using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;
using Poe2Pricer.Ui;

namespace Poe2Pricer.App;

// Low-level keyboard hook on a thread of its own: Windows holds every key press until the
// hook returns, so the callback only compares keys and hands the work to the UI thread
// (with the hook on the busy UI thread, the whole system lags). Bound keys are
// swallowed, everything else, Esc included, passes to the game. Esc is reported (Escape)
// on its way: the remnant list closes on it without a choice (OverlayEngine.Escaped).
public sealed class KeyboardHook : IDisposable
{
    private readonly Dispatcher ui;
    private readonly Thread thread;
    private LowLevelKeyboardProc? proc;
    private nint hook;
    private uint threadId;
    private volatile (HotKey Binding, Action Action)[] bindings = [];
    private readonly HeldKey held = new(TimeProvider.System);

    // Windows' error code when it refused the hook (security software, a restricted desktop):
    // no key will ever arrive; the user is told.
    public int? Error { get; private set; }

    // Only while this returns true (game in front) keys are taken. Must be cheap.
    public Func<bool> Active { get; set; } = () => true;
    // Told of every Esc pressed while Active; cheap, on the hook's thread. Nothing is stored.
    public Action? Escape { get; set; }

    public KeyboardHook(Dispatcher ui)
    {
        this.ui = ui;
        using var ready = new ManualResetEventSlim();
        thread = new Thread(() =>
        {
            threadId = GetCurrentThreadId();
            proc = Callback;
            using var module = Process.GetCurrentProcess().MainModule!;
            hook = SetWindowsHookExW(13, proc, GetModuleHandleW(module.ModuleName), 0);
            if (hook == 0)
                Error = Marshal.GetLastWin32Error();
            ready.Set();
            while (GetMessageW(out var msg, 0, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
            UnhookWindowsHookEx(hook);
        })
        {
            IsBackground = true,
            Name = "Keyboard hook",
        };
        thread.Start();
        ready.Wait();
    }

    public void Bind(string text, Action action) => bindings = [.. bindings, (HotKey.Parse(text), action)];

    public void Clear() => bindings = [];

    private nint Callback(int code, nint wParam, nint lParam)
    {
        // WM_KEYDOWN or WM_SYSKEYDOWN (Alt combinations)
        if (code >= 0 && (wParam == 0x100 || wParam == 0x104))
        {
            var key = KeyInterop.KeyFromVirtualKey(Marshal.ReadInt32(lParam));
            bool alt = Down(0x12), ctrl = Down(0x11), shift = Down(0x10);
            if (key == Key.Escape && Active())
                Escape?.Invoke();
            foreach (var (b, action) in bindings)
                if (b.Key == key && b.Alt == alt && b.Ctrl == ctrl && b.Shift == shift && Active())
                {
                    // Swallowed either way; the repeats of a held key do nothing more.
                    if (held.Pressed(key))
                        ui.BeginInvoke(action);
                    return 1;
                }
        }
        // WM_KEYUP or WM_SYSKEYUP
        else if (code >= 0 && (wParam == 0x101 || wParam == 0x105))
            held.Released(KeyInterop.KeyFromVirtualKey(Marshal.ReadInt32(lParam)));
        return CallNextHookEx(hook, code, wParam, lParam);
    }

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public void Dispose() => PostThreadMessageW(threadId, 0x0012, 0, 0); // WM_QUIT

    private delegate nint LowLevelKeyboardProc(int code, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam, LParam;
        public uint Time;
        public int X, Y;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookExW(int id, LowLevelKeyboardProc proc, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] private static extern int GetMessageW(out MSG msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern nint DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint thread, uint msg, nint w, nint l);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern nint GetModuleHandleW(string name);
}
