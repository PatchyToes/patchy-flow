using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PatchyFlow;

/// <summary>
/// Low-level keyboard hook implementing hold-to-talk:
/// chord down starts, chord up stops, Escape while engaged cancels.
/// The non-modifier chord key is swallowed so it never reaches other apps
/// (critical when the key is LWin — otherwise the Start menu opens).
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    public event Action? ChordDown;
    public event Action? ChordUp;
    public event Action? Cancel;

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const uint LLKHF_INJECTED = 0x10;
    private const uint VK_ESCAPE = 0x1B;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    // keep a reference so the delegate isn't GC'd while the hook lives
    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hookId = IntPtr.Zero;

    private uint _targetVk;
    private bool _needCtrl, _needShift, _needAlt, _needWin;

    private readonly HashSet<uint> _down = new();
    private bool _engaged;
    private bool _suppressTargetUntilUp;
    // true when we swallowed the target key's down event; only then may we swallow its up.
    // If the down leaked to the OS (chord pressed in the other order), swallowing the up
    // would leave Windows thinking the key (e.g. LWin) is held forever.
    private bool _swallowedTargetDown;

    public KeyboardHook(HotkeyConfig config)
    {
        Configure(config);
        _proc = HookProc;
    }

    public void Configure(HotkeyConfig config)
    {
        if (!Enum.TryParse<Keys>(config.Key, ignoreCase: true, out var key))
        {
            Log.Write($"Unknown hotkey key '{config.Key}', falling back to LWin");
            key = Keys.LWin;
        }
        _targetVk = (uint)key;
        _needCtrl = config.Ctrl;
        _needShift = config.Shift;
        _needAlt = config.Alt;
        _needWin = config.Win;
    }

    public void Install()
    {
        if (_hookId != IntPtr.Zero) return;
        _hookId = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, GetModuleHandleW(null), 0);
        if (_hookId == IntPtr.Zero)
            throw new InvalidOperationException($"SetWindowsHookEx failed: {Marshal.GetLastWin32Error()}");
    }

    private bool IsDownEither(uint l, uint r) => _down.Contains(l) || _down.Contains(r);

    private bool ModsSatisfied()
    {
        if (_needCtrl && !IsDownEither(0xA2, 0xA3)) return false;
        if (_needShift && !IsDownEither(0xA0, 0xA1)) return false;
        if (_needAlt && !IsDownEither(0xA4, 0xA5)) return false;
        if (_needWin && !IsDownEither(0x5B, 0x5C)) return false;
        return true;
    }

    private bool IsRequiredModVk(uint vk) =>
        (_needCtrl && vk is 0xA2 or 0xA3) ||
        (_needShift && vk is 0xA0 or 0xA1) ||
        (_needAlt && vk is 0xA4 or 0xA5) ||
        (_needWin && vk is 0x5B or 0x5C);

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return CallNextHookEx(_hookId, nCode, wParam, lParam);

        var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

        // ignore our own synthetic Ctrl+V from the paste step
        if ((info.flags & LLKHF_INJECTED) != 0)
            return CallNextHookEx(_hookId, nCode, wParam, lParam);

        int msg = wParam.ToInt32();
        bool isDown = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
        bool isUp = msg is WM_KEYUP or WM_SYSKEYUP;
        uint vk = info.vkCode;

        if (isDown) _down.Add(vk);
        else if (isUp) _down.Remove(vk);

        if (isDown)
        {
            if (vk == _targetVk)
            {
                if (_engaged) return (IntPtr)1; // key repeat while engaged
                if (_suppressTargetUntilUp) return (IntPtr)1;
                if (ModsSatisfied())
                {
                    _engaged = true;
                    _swallowedTargetDown = true;
                    ChordDown?.Invoke();
                    return (IntPtr)1;
                }
            }
            else if (!_engaged && !_suppressTargetUntilUp
                     && IsRequiredModVk(vk) && _down.Contains(_targetVk) && ModsSatisfied())
            {
                // chord pressed in the other order (target key first, modifier second);
                // the target key's down already reached the OS, so its up must too
                _engaged = true;
                _swallowedTargetDown = false;
                ChordDown?.Invoke();
            }
            else if (_engaged && vk == VK_ESCAPE)
            {
                _engaged = false;
                _suppressTargetUntilUp = _down.Contains(_targetVk);
                Cancel?.Invoke();
                return (IntPtr)1;
            }
        }
        else if (isUp)
        {
            if (vk == _targetVk)
            {
                if (_engaged)
                {
                    _engaged = false;
                    ChordUp?.Invoke();
                    if (_swallowedTargetDown) return (IntPtr)1;
                }
                else if (_suppressTargetUntilUp)
                {
                    _suppressTargetUntilUp = false;
                    if (_swallowedTargetDown) return (IntPtr)1;
                }
            }
            else if (_engaged && IsRequiredModVk(vk) && !ModsSatisfied())
            {
                // released a required modifier — chord over
                _engaged = false;
                _suppressTargetUntilUp = _down.Contains(_targetVk);
                ChordUp?.Invoke();
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }
}
