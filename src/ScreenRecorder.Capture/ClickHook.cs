using System.ComponentModel;
using System.Runtime.InteropServices;
using ScreenRecorder.Core;
using static ScreenRecorder.Capture.NativeMethods;

namespace ScreenRecorder.Capture;

// The recent clicks and the pointer, kept apart from the hook so tests can fill it.
class ClickBuffer
{
    readonly Click[] _clicks = new Click[32];
    int _next;
    (int X, int Y) _pointer;

    // Clicks made while paused are not stored, so nothing outlives 1 second during a long pause.
    volatile bool _paused;
    public bool Paused { set => _paused = value; }

    public (int X, int Y) Pointer { get { lock (_clicks) return _pointer; } }

    public void Add(int x, int y, ClickButton? button, long now)
    {
        lock (_clicks)
        {
            _pointer = (x, y);
            if (button is not { } b || _paused) return;
            _clicks[_next] = new(x, y, b, now);
            _next = (_next + 1) % _clicks.Length;
        }
    }

    public int Read(Span<Click> into) => Read(into, Environment.TickCount64);

    public int Read(Span<Click> into, long now)
    {
        int n = 0;
        lock (_clicks)
            for (int i = 0; i < _clicks.Length; i++)
            {
                ref var c = ref _clicks[(_next + i) % _clicks.Length];
                if (c.Ticks == 0) continue;
                if (now - c.Ticks >= 1000) c = default; // nothing is kept past 1 second
                else if (n < into.Length) into[n++] = c;
            }
        return n;
    }
}

// A low-level mouse hook on its own thread with a message loop. Windows waits for the callback
// on every mouse event of the PC, so a slow callback makes the whole mouse lag: it only stores
// the event under a short lock.
sealed class ClickHook : ClickBuffer, IClickSource
{
    const int WH_MOUSE_LL = 14, WM_LBUTTONDOWN = 0x201, WM_RBUTTONDOWN = 0x204, WM_MBUTTONDOWN = 0x207;
    const uint WM_QUIT = 0x12;
    readonly HookProc _proc; // Windows holds this delegate, so it must stay alive
    readonly Thread _thread;
    uint _threadId;

    public ClickHook()
    {
        _proc = OnMouse;
        if (GetCursorPos(out var p)) Add(p.X, p.Y, null, 0);
        Exception? error = null;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            var hook = SetWindowsHookExW(WH_MOUSE_LL, _proc, GetModuleHandleW(null), 0);
            if (hook == 0) error = new Win32Exception();
            ready.Set();
            if (hook == 0) return;
            while (GetMessageW(out _, 0, 0, 0) > 0) { }
            UnhookWindowsHookEx(hook);
        }) { IsBackground = true, Name = "Click hook", Priority = ThreadPriority.Highest };
        _thread.Start();
        ready.Wait();
        if (error != null) throw error;
    }

    nint OnMouse(int code, nint message, nint info)
    {
        if (code >= 0)
        {
            // MSLLHOOKSTRUCT starts with the point.
            Add(Marshal.ReadInt32(info), Marshal.ReadInt32(info, 4), (int)message switch
            {
                WM_LBUTTONDOWN => ClickButton.Left,
                WM_RBUTTONDOWN => ClickButton.Right,
                WM_MBUTTONDOWN => ClickButton.Middle,
                _ => null,
            }, Environment.TickCount64);
        }
        return CallNextHookEx(0, code, message, info);
    }

    public void Dispose()
    {
        if (PostThreadMessageW(_threadId, WM_QUIT, 0, 0)) _thread.Join(2000);
    }
}
