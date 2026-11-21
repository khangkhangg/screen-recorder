# Measures screenrec against the C# engine on this PC. Windows PowerShell 5.1.
#   powershell -ExecutionPolicy Bypass -File rust\bench\compare.ps1 [-Steps size,start,same,...]
# Steps: size start same long 4k crash hdr camera start4k sync. Recordings go to -Work and
# are deleted after they are read. Results are printed and saved as JSON in -Work\results.
# Busy steps open full-screen or large topmost-free windows of their own for minutes.
param(
    [string]$Steps = 'size,start,same,long,4k,crash,hdr,camera,start4k,sync',
    [string]$Work = "$env:TEMP\screenrec-bench",
    [int]$LongSeconds = 300,
    [int]$BusySeconds = 120
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot\..\..").Path
$rec = "$repo\rust\target\release\screenrec.exe"
New-Item -ItemType Directory -Force "$Work\results", "$Work\cs" | Out-Null

cmd /c "cd /d `"$repo\rust`" && cargo build --release -p recorder-cli 2>nul"
if ($LASTEXITCODE) { throw 'screenrec did not build.' }
Copy-Item "$PSScriptRoot\cs\*" "$Work\cs" -Force
dotnet build "$Work\cs\Bench.csproj" -c Release "-p:RepoRoot=$repo" | Out-Null
if ($LASTEXITCODE) { throw 'The C# helper did not build.' }
$bench = "$Work\cs\bin\Release\net10.0-windows10.0.19041.0\win-x64\Bench.exe"
$logo = "$Work\logo.png"
& $bench logo $logo

function Start-Proc($exe, [string[]]$argv) {
    $si = New-Object System.Diagnostics.ProcessStartInfo $exe
    $si.Arguments = ($argv | ForEach-Object { if ($_ -match '[\s,]') { "`"$_`"" } else { $_ } }) -join ' '
    $si.UseShellExecute = $false
    $si.RedirectStandardInput = $true; $si.RedirectStandardOutput = $true; $si.RedirectStandardError = $true
    [System.Diagnostics.Process]::Start($si)
}

function Helper([string[]]$argv) { (& $bench @argv) | ConvertFrom-Json }

# Shows a window of the helper and waits until it is on screen.
function Show-Window($mode, $x, $y, $w, $h) {
    $p = Start-Proc $bench @('window', $mode, $x, $y, $w, $h)
    [void]$p.StandardOutput.ReadLine()
    Start-Sleep -Milliseconds 300
    $p
}

# Records with either engine. Target is monitor, region:X,Y,W,H or window:TITLE.
# Measures from outside: peak working set and private bytes, processor use while recording,
# and stop time from the "stop" line to process exit. -Kill ends the process instead.
function Record($engine, $target, $fps, $seconds, [switch]$Mic, [switch]$Sys, $Logo, [switch]$Kill, $OnStart) {
    $out = "$Work\rec\$engine-" + [guid]::NewGuid().ToString('N').Substring(0, 8)
    New-Item -ItemType Directory -Force $out | Out-Null
    if ($engine -eq 'rust') {
        $a = @('record')
        if ($target -eq 'monitor') { $a += '--monitor', '1' }
        elseif ($target -like 'region:*') { $a += '--region', $target.Substring(7) }
        else { $a += '--window', $target.Substring(7) }
        $a += '--out', $out, '--fps', $fps
        if ($Mic) { $a += '--mic' }; if ($Sys) { $a += '--system-audio' }; if ($Logo) { $a += '--logo', $Logo }
        $p = Start-Proc $rec $a
    } else {
        $a = @('record', $out, $target, $fps)
        if ($Mic) { $a += 'mic' }; if ($Sys) { $a += 'sys' }; if ($Logo) { $a += "logo=$Logo" }
        $p = Start-Proc $bench $a
    }
    $notes = @()
    while ($true) {
        $line = $p.StandardError.ReadLine()
        if ($null -eq $line) { throw "The $engine engine ended before recording: $($notes -join ' ') $($p.StandardOutput.ReadToEnd())" }
        if ($line -eq 'recording') { break }
        $notes += $line
    }
    $errTask = $p.StandardError.ReadToEndAsync()
    if ($OnStart) { & $OnStart }
    $cpu0 = $p.TotalProcessorTime; $clock = [Diagnostics.Stopwatch]::StartNew()
    $ws = 0; $priv = 0
    while ($clock.Elapsed.TotalSeconds -lt $seconds) {
        $p.Refresh()
        $ws = [Math]::Max($ws, $p.WorkingSet64); $priv = [Math]::Max($priv, $p.PrivateMemorySize64)
        Start-Sleep -Milliseconds 250
    }
    $p.Refresh(); $ws = [Math]::Max($ws, $p.PeakWorkingSet64)
    $cpu = ($p.TotalProcessorTime - $cpu0).TotalSeconds / $clock.Elapsed.TotalSeconds / [Environment]::ProcessorCount * 100
    if ($Kill) {
        $p.Kill(); $p.WaitForExit()
        return [pscustomobject]@{ engine = $engine; folder = (Get-ChildItem $out -Directory | Select-Object -First 1).FullName }
    }
    $stop = [Diagnostics.Stopwatch]::StartNew()
    $p.StandardInput.WriteLine('stop'); $p.StandardInput.Flush()
    $json = $p.StandardOutput.ReadToEnd()
    $p.WaitForExit(); $stop.Stop()
    $r = $json | ConvertFrom-Json
    [pscustomobject]@{
        engine = $engine; folder = $r.folder; droppedFrames = $r.droppedFrames; frameRate = $r.frameRate
        notes = @($r.notes) + $notes + @($errTask.Result.Trim() | Where-Object { $_ })
        stopSeconds = [Math]::Round($stop.Elapsed.TotalSeconds, 3)
        peakWorkingSetMB = [Math]::Round($ws / 1MB, 1); peakPrivateMB = [Math]::Round($priv / 1MB, 1)
        cpuPercent = [Math]::Round($cpu, 2)
    }
}

# Reads every file of a recording folder, then deletes the folder.
function Probe-Folder($folder, [switch]$Keep) {
    $files = [ordered]@{}
    foreach ($f in Get-ChildItem $folder -File | Sort-Object Name) {
        if ($f.Extension -in '.mp4', '.wav') { $files[$f.Name] = Helper @('probe', $f.FullName) }
        elseif ($f.Name -eq 'session.json') { $files[$f.Name] = Get-Content $f.FullName -Raw | ConvertFrom-Json }
        else { $files[$f.Name] = $f.Length }
    }
    if (-not $Keep) { Remove-Item (Split-Path $folder) -Recurse -Force }
    [pscustomobject]$files
}

function Flatten($o, $prefix = '') {
    $h = [ordered]@{}
    if ($o -is [pscustomobject]) {
        foreach ($p in $o.PSObject.Properties) { (Flatten $p.Value "$prefix$($p.Name).").GetEnumerator() | ForEach-Object { $h[$_.Key] = $_.Value } }
    } else { $h[$prefix.TrimEnd('.')] = ($o | ConvertTo-Json -Compress -Depth 5) }
    $h
}

function Save($name, $o) {
    $o | ConvertTo-Json -Depth 8 | Set-Content "$Work\results\$name.json"
    Write-Host "== $name"; Write-Host ($o | ConvertTo-Json -Depth 8)
}

function Median($xs) { $s = @($xs | Sort-Object); if ($s.Count % 2) { $s[[int][Math]::Floor($s.Count / 2)] } else { ($s[$s.Count / 2 - 1] + $s[$s.Count / 2]) / 2 } }

foreach ($step in $Steps -split ',') {
    switch ($step) {
        'size' {
            $dumpbin = Get-ChildItem 'C:\Program Files*\Microsoft Visual Studio\*\*\VC\Tools\MSVC\*\bin\Hostx64\x64\dumpbin.exe' | Select-Object -First 1
            $dlls = & $dumpbin.FullName /nologo /dependents $rec | Where-Object { $_ -match '^\s+\S+\.dll\s*$' } | ForEach-Object { $_.Trim() }
            $cs = (Get-ChildItem "$repo\dist\ScreenRecorder" -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
            Save 'size' ([pscustomobject]@{ bytes = (Get-Item $rec).Length; dlls = $dlls; csharpDistBytes = $cs })
        }
        'start' {
            $ms = 1..10 | ForEach-Object { (Measure-Command { & $rec list monitors | Out-Null }).TotalMilliseconds }
            Save 'start' ([pscustomobject]@{ runsMs = $ms | ForEach-Object { [Math]::Round($_, 1) }; medianMs = [Math]::Round((Median $ms), 1) })
        }
        'same' {
            $win = Show-Window busy 200 200 1280 720
            try {
                $both = foreach ($e in 'rust', 'cs') {
                    $r = Record $e 'region:200,200,1280,720' 60 10 -Sys
                    $r | Add-Member files (Probe-Folder $r.folder) -PassThru
                }
            } finally { $win.Kill() }
            $a = Flatten $both[0].files; $b = Flatten $both[1].files
            $diff = foreach ($k in @($a.Keys) + @($b.Keys) | Sort-Object -Unique) { if ($a[$k] -ne $b[$k]) { "${k}: rust=$($a[$k]) cs=$($b[$k])" } }
            Save 'same' ([pscustomobject]@{ runs = $both; differences = @($diff) })
        }
        'long' {
            $win = Show-Window busy 300 300 1920 1080
            $title = "screenrec-bench-$($win.Id)"
            try {
                $runs = foreach ($e in 'rust', 'cs') {
                    $r = Record $e "window:$title" 60 $LongSeconds -Mic -Sys -Logo $logo
                    $r | Add-Member files (Probe-Folder $r.folder) -PassThru
                }
            } finally { $win.Kill() }
            Save 'long' $runs
        }
        '4k' {
            $win = Show-Window busy 0 0 3840 2160
            try {
                $runs = foreach ($e in 'rust', 'cs') {
                    foreach ($i in 1..3) {
                        $r = Record $e 'monitor' 30 $BusySeconds
                        $f = Get-Item "$($r.folder)\final.mp4"
                        $sizes = Helper @('sizes', $f.FullName)
                        $files = Probe-Folder $r.folder
                        # Repeated frames encode to tiny samples on a busy screen, so tiny samples per second hint where drops fall.
                        $med = Median ($sizes | ForEach-Object { $_[1] })
                        $tiny = $sizes | Where-Object { $_[1] -lt $med / 20 } | ForEach-Object { [int][Math]::Floor($_[0]) } | Group-Object | ForEach-Object { "$($_.Name)s:$($_.Count)" }
                        $r | Add-Member files $files -PassThru | Add-Member tinySamplesBySecond @($tiny) -PassThru |
                            Add-Member droppedPercent ([Math]::Round(100 * $r.droppedFrames / [Math]::Max(1, $files.'final.mp4'.frameCount), 2)) -PassThru
                    }
                }
            } finally { $win.Kill() }
            Save '4k' $runs
        }
        'crash' {
            $r = Record rust 'monitor' 60 30 -Mic -Sys -Kill
            $files = Probe-Folder $r.folder
            Save 'crash' $files
        }
        'hdr' {
            $win = Show-Window patch 700 500 200 100
            try {
                $runs = foreach ($e in 'rust', 'cs') {
                    $r = Record $e 'region:600,400,640,360' 60 3
                    $px = Helper @('pixels', "$($r.folder)\screen.mp4", '2', '150,150', '250,150')
                    Remove-Item (Split-Path $r.folder) -Recurse -Force
                    [pscustomobject]@{ engine = $e; gray = $px.pixels[0].bgra -join ','; white = $px.pixels[1].bgra -join ','; time = $px.time }
                }
            } finally { $win.Kill() }
            Save 'hdr' $runs
        }
        'camera' { Save 'camera' (& $rec list cameras | ConvertFrom-Json) }
        'start4k' {
            $runs = foreach ($e in 'rust', 'cs') {
                foreach ($i in 1..20) {
                    $r = Record $e 'monitor' 30 10
                    $n = (Probe-Folder $r.folder).'final.mp4'.frameCount
                    [pscustomobject]@{ engine = $e; run = $i; droppedFrames = $r.droppedFrames; frames = $n }
                }
            }
            Save 'start4k' $runs
        }
        'sync' {
            $runs = foreach ($e in 'rust', 'cs') {
                foreach ($i in 1..5) {
                    $win = Show-Window flash 400 400 320 240
                    try {
                        $r = Record $e 'region:400,400,320,240' 60 5 -Sys -OnStart { Start-Sleep 2; $win.StandardInput.WriteLine('flash'); $win.StandardInput.Flush() }
                    } finally { $win.Kill() }
                    $v = (Helper @('firstwhite', "$($r.folder)\screen.mp4", '160,120')).seconds
                    $s = (Helper @('onset', "$($r.folder)\system.wav")).seconds
                    Remove-Item (Split-Path $r.folder) -Recurse -Force
                    $d = if ($null -ne $v -and $null -ne $s) { [Math]::Round(($s - $v) * 1000) } else { $null }
                    [pscustomobject]@{ engine = $e; run = $i; whiteFrameS = $v; beepS = $s; soundMinusPictureMs = $d }
                }
            }
            Save 'sync' $runs
        }
    }
}
Remove-Item "$Work\rec" -Recurse -Force -ErrorAction SilentlyContinue
