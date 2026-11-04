using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LaptopGuardian.Core;

/// <summary>
/// Filters keyboard chatter: a worn switch that registers one press as two (or more).
/// A key-down that arrives within <see cref="ThresholdMs"/> of the same key's last key-up is swallowed,
/// along with its matching key-up. Holding a key still auto-repeats normally, and input injected by
/// other software (macros, remappers) is never touched.
///
/// The hook runs on its own thread with its own message loop. Windows silently removes a low-level hook
/// whose thread is too busy to answer in time, so it must never share a thread with the user interface.
/// </summary>
public sealed class KeyboardDebouncer : IDisposable
{
    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105, WM_QUIT = 0x0012;
    const uint LLKHF_INJECTED = 0x10;

    delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public UIntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public UIntPtr wParam; public IntPtr lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)] static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint threadId, int msg, UIntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);

    // Kept in a field so the GC never collects the delegate while Windows holds it.
    readonly LowLevelKeyboardProc _proc;
    IntPtr _hook = IntPtr.Zero;
    Thread? _thread;
    uint _threadId;

    readonly uint[] _lastUp = new uint[256];
    readonly bool[] _hasLastUp = new bool[256];
    readonly bool[] _isDown = new bool[256];
    readonly bool[] _suppressed = new bool[256];
    int _count;

    public int ThresholdMs { get; set; } = 50;
    public bool Enabled { get; set; } = true;
    public int SuppressedCount => Volatile.Read(ref _count);
    public bool Running => _hook != IntPtr.Zero;

    /// <summary>Raised on the hook thread with the virtual-key code of each swallowed bounce. Keep handlers quick.</summary>
    public event Action<int>? BounceSuppressed;

    public KeyboardDebouncer() => _proc = HookCallback;

    public void Start()
    {
        if (_thread != null) return;
        Exception? error = null;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) error = new Win32Exception(Marshal.GetLastWin32Error(), "Could not install the keyboard hook.");
            ready.Set();
            if (_hook == IntPtr.Zero) return;
            while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        })
        { IsBackground = true, Name = "Keyboard filter", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
        if (error != null) { _thread = null; throw error; }
    }

    static bool IsModifier(uint vk) =>
        vk is 0x10 or 0x11 or 0x12 or >= 0xA0 and <= 0xA5 or 0x5B or 0x5C;

    IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            uint vk = kb.vkCode & 0xFF;
            int msg = wParam.ToInt32();
            bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            bool up = msg is WM_KEYUP or WM_SYSKEYUP;

            if ((kb.flags & LLKHF_INJECTED) == 0 && !IsModifier(vk) && (down || up))
            {
                if (down)
                {
                    if (_isDown[vk])
                    {
                        // Auto-repeat. If we swallowed the first press but the key is really being held, let it through from here on.
                        _suppressed[vk] = false;
                    }
                    else
                    {
                        _isDown[vk] = true;
                        if (Enabled && _hasLastUp[vk] && unchecked(kb.time - _lastUp[vk]) < (uint)ThresholdMs)
                        {
                            _suppressed[vk] = true;
                            Interlocked.Increment(ref _count);
                            BounceSuppressed?.Invoke((int)vk);
                            return 1;
                        }
                    }
                }
                else
                {
                    _isDown[vk] = false;
                    if (_suppressed[vk])
                    {
                        // Swallow the release of a swallowed press. Keep _lastUp at the original release so a run of bounces all measure from it.
                        _suppressed[vk] = false;
                        return 1;
                    }
                    _lastUp[vk] = kb.time;
                    _hasLastUp[vk] = true;
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_thread == null) return;
        PostThreadMessage(_threadId, WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }
}
