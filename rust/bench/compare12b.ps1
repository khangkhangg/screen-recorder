# Second measurement pass: screenrec against the C# engine. Windows PowerShell 5.1.
#   powershell -ExecutionPolicy Bypass -File rust\bench\compare12b.ps1 -Parts setup,A,B,C
# A: 3840x2160 at 30, busy, 10 runs per engine.  B: Rust stop time by length.  C: memory at 1920x1080 at 60.
# Recordings go to -Work\rec and are deleted after they are read. Nothing is sent to the desktop.
param(
    [string]$Parts = 'setup,A,B,C',
    [string]$Work = "$env:TEMP\screenrec-bench12b",
    [int]$ARuns = 10, [int]$ASeconds = 120, [int]$BRuns = 3, [string]$BLengths = '60,120,300,600',
    [int]$CRuns = 3, [int]$CSeconds = 120, [int]$Pause = 10
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot\..\..").Path
$rec = "$repo\rust\target\release\screenrec.exe"
$bench = "$Work\cs\bin\Release\net10.0-windows10.0.19041.0\win-x64\Bench.exe"
$res = "$Work\results"
New-Item -ItemType Directory -Force $res, "$Work\rec" | Out-Null

function Median($xs) {
    $s = @($xs | Sort-Object); if (-not $s.Count) { return $null }
    if ($s.Count % 2) { $s[[int][math]::Floor($s.Count / 2)] } else { ($s[$s.Count / 2 - 1] + $s[$s.Count / 2]) / 2 }
}
function Save($name, $o) { $o | ConvertTo-Json -Depth 8 | Set-Content "$res\$name.json"; Write-Host "== $name"; Write-Host ($o | ConvertTo-Json -Depth 8) }
# The user's own C# app must never be touched; this only looks.
function AppState { if (Get-Process -Name ScreenRecorder -ErrorAction SilentlyContinue) { 'running' } else { 'not running' } }
$safety = @()
function Mark($part, $when) { $script:safety += [pscustomobject]@{ part = $part; when = $when; ScreenRecorder = (AppState); at = (Get-Date).ToString('s') }; Write-Host "[$part $when] ScreenRecorder $(AppState)" }

function Start-Proc($exe, [string[]]$argv) {
    $si = New-Object System.Diagnostics.ProcessStartInfo $exe
    $si.Arguments = ($argv | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' '
    $si.UseShellExecute = $false; $si.RedirectStandardInput = $true; $si.RedirectStandardOutput = $true; $si.RedirectStandardError = $true
    [System.Diagnostics.Process]::Start($si)
}
function Helper([string[]]$argv) { (& $bench @argv) | ConvertFrom-Json }

# A busy window of the helper, in screen pixels. Optional log gets one line per second.
function Show-Window($mode, $x, $y, $w, $h, $log) {
    $a = @('window', $mode, $x, $y, $w, $h); if ($log) { $a += $log }
    $p = Start-Proc $bench $a
    [void]$p.StandardOutput.ReadLine(); Start-Sleep -Milliseconds 500; $p
}

# GPU memory counters of one process, summed over its adapters. Null when the system has none.
function Open-Gpu($procId) {
    try {
        if (-not [Diagnostics.PerformanceCounterCategory]::Exists('GPU Process Memory')) { return $null }
        $cat = New-Object Diagnostics.PerformanceCounterCategory 'GPU Process Memory'
        $inst = $cat.GetInstanceNames() | Where-Object { $_ -like "pid_${procId}_*" }
        if (-not $inst) { return @() }
        @($inst | ForEach-Object { $i = $_; foreach ($n in 'Dedicated Usage', 'Shared Usage') { $c = New-Object Diagnostics.PerformanceCounter 'GPU Process Memory', $n, $i, $true; [void]$c.NextValue(); $c } })
    } catch { $null }
}

# Runs one recording and samples the process every 500 ms from its start until it exits.
function Record($engine, $target, $fps, $seconds, [switch]$Mic, [switch]$Sys, $OnStart, $OnStop) {
    $out = "$Work\rec\$engine-" + [guid]::NewGuid().ToString('N').Substring(0, 8)
    New-Item -ItemType Directory -Force $out | Out-Null
    if ($engine -eq 'rust') {
        $a = @('record'); if ($target -eq 'monitor') { $a += '--monitor', '1' } else { $a += '--region', $target.Substring(7) }
        $a += '--out', $out, '--fps', $fps; if ($Mic) { $a += '--mic' }; if ($Sys) { $a += '--system-audio' }
        $p = Start-Proc $rec $a
    } else {
        $a = @('record', $out, $target, $fps); if ($Mic) { $a += 'mic' }; if ($Sys) { $a += 'sys' }
        $p = Start-Proc $bench $a
    }
    $samples = New-Object System.Collections.ArrayList
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $m = @{ gpu = $null; tried = 0; ws = 0; priv = 0; pk = 0; ded = 0; sh = 0; seen = $false }
    function Sample {
        if ($p.HasExited) { return }
        try { $p.Refresh(); $ws = $p.WorkingSet64; $priv = $p.PrivateMemorySize64; $pk = $p.PeakWorkingSet64 } catch { return }
        $m.ws = [math]::Max($m.ws, $ws); $m.priv = [math]::Max($m.priv, $priv); $m.pk = [math]::Max($m.pk, $pk)
        if ($null -eq $m.gpu -or ($m.gpu.Count -eq 0 -and $m.tried -lt 20)) { $m.gpu = Open-Gpu $p.Id; $m.tried++ }
        if ($m.gpu -and $m.gpu.Count) {
            $d = 0; $s = 0
            foreach ($c in $m.gpu) { try { $v = $c.NextValue() } catch { $v = 0 }; if ($c.CounterName -eq 'Dedicated Usage') { $d += $v } else { $s += $v } }
            $m.ded = [math]::Max($m.ded, $d); $m.sh = [math]::Max($m.sh, $s); $m.seen = $true
        }
        [void]$samples.Add([pscustomobject]@{ t = [math]::Round($clock.Elapsed.TotalSeconds, 1); wsMB = [math]::Round($ws / 1MB, 1); privMB = [math]::Round($priv / 1MB, 1) })
    }
    $notes = @(); $errTask = $null
    $line = $p.StandardError.ReadLineAsync()
    while ($true) {
        while (-not $line.Wait(500)) { Sample; if ($p.HasExited -and $line.IsCompleted) { break } }
        $l = $line.Result
        if ($null -eq $l) { throw "The engine ended before recording: $($notes -join ' ') $($p.StandardOutput.ReadToEnd())" }
        if ($l -eq 'recording') { break }
        $notes += $l; $line = $p.StandardError.ReadLineAsync()
    }
    $errTask = $p.StandardError.ReadToEndAsync()
    $tStart = [DateTime]::UtcNow
    if ($OnStart) { & $OnStart }
    $cpu0 = $p.TotalProcessorTime
    $end = [Diagnostics.Stopwatch]::StartNew()
    while ($end.Elapsed.TotalSeconds -lt $seconds) { Sample; Start-Sleep -Milliseconds 500 }
    $cpu = ($p.TotalProcessorTime - $cpu0).TotalSeconds / $end.Elapsed.TotalSeconds / [Environment]::ProcessorCount * 100
    $tStop = [DateTime]::UtcNow
    $stop = [Diagnostics.Stopwatch]::StartNew()
    $p.StandardInput.WriteLine('stop'); $p.StandardInput.Flush()
    # The stop is sampled too, until the process is gone.
    while (-not $p.HasExited) { Sample; [void]$p.WaitForExit(500) }
    $stop.Stop()
    $json = $p.StandardOutput.ReadToEnd() | ConvertFrom-Json
    if ($OnStop) { & $OnStop }
    [pscustomobject]@{
        engine = $engine; folder = (Get-ChildItem $out -Directory | Select-Object -First 1).FullName; root = $out
        droppedFrames = $json.droppedFrames; frameRate = $json.frameRate
        notes = @($notes) + @($json.notes) + @($errTask.Result.Trim() | Where-Object { $_ })
        stopSeconds = [math]::Round($stop.Elapsed.TotalSeconds, 3)
        peakWsMB = [math]::Round($m.ws / 1MB, 1); peakPrivMB = [math]::Round($m.priv / 1MB, 1); peakWsCounterMB = [math]::Round($m.pk / 1MB, 1)
        gpuDedicatedPeakMB = $(if ($m.seen) { [math]::Round($m.ded / 1MB, 1) } else { $null }); gpuSharedPeakMB = $(if ($m.seen) { [math]::Round($m.sh / 1MB, 1) } else { $null })
        cpuPercent = [math]::Round($cpu, 2); samples = $samples; tRecording = $tStart; tStop = $tStop
    }
}

function Probe-File($path) { Helper @('probe', $path) }
function Wait-Between { Start-Sleep -Seconds $Pause }

foreach ($step in $Parts -split ',') {
    switch ($step.Trim()) {
        'setup' {
            cmd /c "cd /d `"$repo\rust`" && cargo build --release -p recorder-cli 2>nul" | Out-Null; if ($LASTEXITCODE) { throw 'screenrec did not build.' }
            $head = (git -C $repo rev-parse --short HEAD); $dirty = (git -C $repo status --porcelain rust) -join ';'
            New-Item -ItemType Directory -Force "$Work\cs" | Out-Null; Copy-Item "$PSScriptRoot\cs\*" "$Work\cs" -Force
            dotnet build "$Work\cs\Bench.csproj" -c Release "-p:RepoRoot=$repo" | Out-Null
            if ($LASTEXITCODE) { throw 'The C# helper did not build.' }
            Save 'setup' ([pscustomobject]@{ head = $head; dirtyRust = $dirty; exeBytes = (Get-Item $rec).Length; app = (AppState) })
        }
        'A' {
            Mark 'A' 'start'
            $runs = @()
            foreach ($i in 1..$ARuns) {
                foreach ($e in 'rust', 'cs') {
                    $log = "$Work\busy-$e-$i.log"; Remove-Item $log -ErrorAction SilentlyContinue
                    $win = Show-Window busy 0 0 3840 2160 $log
                    try {
                        $r = Record $e 'monitor' 30 $ASeconds -Sys
                        $final = Probe-File "$($r.folder)\final.mp4"; $screen = Probe-File "$($r.folder)\screen.mp4"
                        $sess = Get-Content "$($r.folder)\session.json" -Raw | ConvertFrom-Json
                        Remove-Item $r.root -Recurse -Force
                    } finally { if (-not $win.HasExited) { $win.Kill() }; $win.WaitForExit() }
                    # Window frames per second, only for whole seconds inside the recording.
                    $rates = @()
                    foreach ($l in (Get-Content $log)) {
                        $f = $l -split ' '; $endT = [DateTime]::new([long]$f[2], 'Utc')
                        if ($endT -ge $r.tRecording.AddSeconds(1) -and $endT -le $r.tStop) { $rates += [double]$f[0] / [double]$f[1] }
                    }
                    $runs += [pscustomobject]@{
                        engine = $e; run = $i; dropped = $r.droppedFrames; sessionDropped = $sess.droppedFrames; stopSeconds = $r.stopSeconds
                        finalFrames = $final.frameCount; finalCfr = $final.constantFrameRate; finalSize = "$($final.width)x$($final.height)"; finalSeconds = $final.duration
                        screenFrames = $screen.frameCount; screenCfr = $screen.constantFrameRate; screenSize = "$($screen.width)x$($screen.height)"
                        windowFpsMedian = [math]::Round((Median $rates), 1); windowFpsLowest = [math]::Round(($rates | Measure-Object -Minimum).Minimum, 1); windowSeconds = $rates.Count
                        notes = $r.notes -join ' | '
                    }
                    Write-Host ($runs[-1] | ConvertTo-Json -Compress)
                    Save 'A-partial' $runs
                    Wait-Between
                }
            }
            $all = Median ($runs | ForEach-Object { $_.windowFpsMedian })
            foreach ($r in $runs) { $r | Add-Member -NotePropertyName windowMarked -NotePropertyValue ([math]::Abs($r.windowFpsMedian - $all) / $all -gt 0.10) }
            Save 'A' ([pscustomobject]@{ windowMedianOfMedians = $all; runs = $runs })
            Mark 'A' 'end'
        }
        'B' {
            Mark 'B' 'start'
            $runs = @()
            foreach ($len in ($BLengths -split ',' | ForEach-Object { [int]$_ })) {
                foreach ($i in 1..$BRuns) {
                    $out = "$Work\rec\stop-$len-$i"; New-Item -ItemType Directory -Force $out | Out-Null
                    try {
                        $o = Helper @('stoptime', $rec, $out, $len, '0,0,1920,1080', 60)
                        $folder = (Get-ChildItem $out -Directory | Select-Object -First 1).FullName
                        $final = if ($folder) { Probe-File "$folder\final.mp4" } else { $null }
                        $sess = if ($folder) { Get-Content "$folder\session.json" -Raw | ConvertFrom-Json } else { $null }
                    } finally { Remove-Item $out, "$Work\rec\stdout.txt", "$Work\rec\stderr.txt" -Recurse -Force -ErrorAction SilentlyContinue }
                    $runs += [pscustomobject]@{ length = $len; run = $i; stopSeconds = [math]::Round($o.stopMs / 1000, 3); exitCode = $o.exitCode; toolSeconds = ($o.stdout | ConvertFrom-Json).seconds; finalFrames = $final.frameCount; finalSeconds = $final.duration; dropped = $sess.droppedFrames; files = $o.files }
                    Write-Host ($runs[-1] | Select-Object length, run, stopSeconds, exitCode, toolSeconds, finalFrames | ConvertTo-Json -Compress)
                    Save 'B-partial' $runs
                    Wait-Between
                }
            }
            $med = foreach ($len in ($BLengths -split ',')) { [pscustomobject]@{ length = [int]$len; values = @($runs | Where-Object length -eq $len | ForEach-Object stopSeconds); median = (Median ($runs | Where-Object length -eq $len | ForEach-Object stopSeconds)) } }
            Save 'B' ([pscustomobject]@{ medians = $med; runs = $runs })
            Mark 'B' 'end'
        }
        'C' {
            Mark 'C' 'start'
            $runs = @()
            foreach ($i in 1..$CRuns) {
                foreach ($e in 'rust', 'cs') {
                    $win = Show-Window busy 0 0 1920 1080
                    try {
                        $r = Record $e 'region:0,0,1920,1080' 60 $CSeconds -Sys
                        Remove-Item $r.root -Recurse -Force
                    } finally { if (-not $win.HasExited) { $win.Kill() }; $win.WaitForExit() }
                    $runs += $r | Select-Object engine, peakWsMB, peakPrivMB, peakWsCounterMB, gpuDedicatedPeakMB, gpuSharedPeakMB, cpuPercent, stopSeconds, droppedFrames, samples
                    $runs[-1] | Add-Member -NotePropertyName run -NotePropertyValue $i
                    Write-Host ($runs[-1] | Select-Object engine, run, peakWsMB, peakPrivMB, peakWsCounterMB, gpuDedicatedPeakMB, gpuSharedPeakMB | ConvertTo-Json -Compress)
                    Save 'C-partial' $runs
                    Wait-Between
                }
            }
            Save 'C' $runs
            Mark 'C' 'end'
        }
    }
}
$safety | ConvertTo-Json | Set-Content "$res\safety-$($Parts -replace '[^\w]', '_').json"
