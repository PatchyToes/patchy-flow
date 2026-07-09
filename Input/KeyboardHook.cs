using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PatchyFlow;

/// <summary>
/// Low-level keyboard hook implementing hold-to-talk:
/// chord down starts, chord up stops, Escape while engaged cancels.
///
/// The hook runs on its own dedicated thread with its own message pump. A
/// WH_KEYBOARD_LL callback fires on the thread that installed it, and Windows
/// silently skips the hook for any event that thread doesn't service within
/// LowLevelHooksTimeout (~300ms). Running it on the UI thread meant it got
/// starved during transcription/paste — exactly when the chord's key-up lands —
/// so releases went missing and the key got left stuck. A dedicated thread is
/// never blocked by UI work, so it sees every event.
///
/// A Windows-key target inside a modifier chord (e.g. Ctrl+Win) is NOT swallowed:
/// the modifier already masks the Start menu, and passing the key through lets the
/// OS own its state, so a dropped key-up can't leave Windows thinking it's held.
/// See <see cref="ShouldSwallowTarget"/>.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    public event Action? ChordDown;
    public event Action? ChordUp;
    public event Action? Cancel;

    /// <summary>
    /// Fired when Escape is pressed while NOT holding the chord (e.g. during
    /// transcription/paste). Return true to also swallow the Escape so it
    /// doesn't reach the foreground app. Return false to let it pass through.
    /// </summary>
    public Func<bool>? EscapeWhileIdle;

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    // message pump for the dedicated hook thread
    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private const uint WM_QUIT = 0x0012;

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    // virtual-key codes for the chord participants we may need to force-release
    private const ushort VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private const ushort VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_CONTROL = 0x11;
    private const ushort VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_SHIFT = 0x10;
    private const ushort VK_LMENU = 0xA4, VK_RMENU = 0xA5, VK_MENU = 0x12;

    private static bool IsWinKey(uint vk) => vk is VK_LWIN or VK_RWIN;
    private static bool OsThinksDown(uint vk) => (GetAsyncKeyState((int)vk) & 0x8000) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    // keep a reference so the delegate isn't GC'd while the hook lives
    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hookId = IntPtr.Zero;

    // the hook lives on its own thread with its own message pump (see class summary)
    private Thread? _hookThread;
    private uint _hookThreadId;
    private readonly ManualResetEventSlim _installGate = new(false);
    private string? _installError;

    private uint _targetVk;
    private bool _needCtrl, _needShift, _needAlt, _needWin;

    private readonly HashSet<uint> _down = new();
    private bool _engaged;
    private bool _suppressTargetUntilUp;
    // Whether we swallowed the target key's down; only then may we swallow its up, so the
    // two stay symmetric. For a Windows target in a modifier chord we intentionally do NOT
    // swallow (see ShouldSwallowTarget) — passing the key through lets the OS own its state,
    // so a missed key-up can never leave Windows thinking the key is held.
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
        if (_hookThread != null) return;

        _hookThread = new Thread(HookThreadMain)
        {
            IsBackground = true,
            Name = "PatchyFlow-KeyboardHook",
        };
        _hookThread.Start();

        // Block until the hook is actually installed (or failed) so callers keep the
        // old synchronous semantics: after Install() returns, the hook is live.
        _installGate.Wait();
        if (_installError != null)
            throw new InvalidOperationException($"SetWindowsHookEx failed: {_installError}");
    }

    private void HookThreadMain()
    {
        _hookThreadId = GetCurrentThreadId();

        try
        {
            _hookId = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, GetModuleHandleW(null), 0);
            if (_hookId == IntPtr.Zero)
                _installError = Marshal.GetLastWin32Error().ToString();
        }
        catch (Exception ex) { _installError = ex.Message; }
        finally { _installGate.Set(); }

        if (_hookId == IntPtr.Zero) return;

        // Pump messages so the low-level hook callback is serviced on THIS thread and
        // never starved by the UI thread. GetMessage blocks (no busy-spin) and returns
        // 0 when Dispose posts WM_QUIT, which ends the loop and tears the hook down.
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
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

    // We swallow the target key to keep it out of the focused app during a hold. The
    // one case we must NOT swallow is a Windows key inside a chord that already has
    // another modifier: Ctrl/Shift/Alt naturally mask the Start menu, and swallowing a
    // Windows key is exactly what turns a dropped key-up into a stuck-key catastrophe.
    // A non-Win target is always safe to swallow (a stuck F-key does nothing); a bare
    // Windows-key hotkey has no masking modifier, so it must still be swallowed.
    private bool ShouldSwallowTarget =>
        !IsWinKey(_targetVk) || !(_needCtrl || _needShift || _needAlt);

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
            // Self-heal: if the Windows chord key is the target and the OS still
            // thinks it's held while we're idle (a phantom left by a missed key-up
            // on a leaked down), clear it before this keystroke becomes a Win+key
            // shortcut. Guarded on our own view of the key being physically up, so
            // we never fight a chord that's legitimately forming.
            if (!_engaged && !_suppressTargetUntilUp && vk != _targetVk
                && IsWinKey(_targetVk) && !_down.Contains(_targetVk) && OsThinksDown(_targetVk))
            {
                SendKeyUps(new[] { (ushort)_targetVk });
            }

            if (vk == _targetVk)
            {
                // Key repeat while engaged, or a lingering target-down after a cancel:
                // never re-trigger. Swallow only if we swallowed the original down, so
                // down and up stay symmetric.
                if (_engaged || _suppressTargetUntilUp)
                    return _swallowedTargetDown
                        ? (IntPtr)1
                        : CallNextHookEx(_hookId, nCode, wParam, lParam);

                if (ModsSatisfied())
                {
                    _engaged = true;
                    // Only swallow when we must. For a Windows target inside a modifier
                    // chord we pass it through and let the modifier mask the Start menu —
                    // that's what stops a missed key-up from becoming a stuck Windows key.
                    _swallowedTargetDown = ShouldSwallowTarget;
                    ChordDown?.Invoke();
                    if (_swallowedTargetDown) return (IntPtr)1;
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
                ForceReleaseChordKeys();
                return (IntPtr)1;
            }
            else if (!_engaged && vk == VK_ESCAPE && EscapeWhileIdle != null)
            {
                // Escape during processing (chord already released) aborts the pipeline.
                // Only swallow it if the controller actually acted on it.
                if (EscapeWhileIdle.Invoke()) return (IntPtr)1;
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
                    ForceReleaseChordKeys();
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
                ForceReleaseChordKeys();
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    /// <summary>
    /// Safety net the controller calls whenever a dictation fully ends — even if
    /// the balancing key-up was never observed by this hook (hook timeout, secure
    /// desktop, focus change). By the time a dictation has finished processing the
    /// user has certainly released the chord, so any chord key the OS still reports
    /// as held is a phantom and gets cleared. Decoupling the release from observing
    /// the up event is the whole robustness win — the old code could only fire from
    /// branches that processed an up, which are exactly the events that go missing.
    /// </summary>
    public void ReleaseStuckChordKeys() => ForceReleaseChordKeys();

    /// <summary>
    /// Force-release every chord key so Windows is never left thinking Ctrl/Win/etc.
    /// is still held (which turns normal typing into Win+key shortcuts).
    ///
    /// Plain modifiers (Ctrl/Shift/Alt) are released unconditionally: a stray keyup
    /// for one that isn't down is a harmless no-op. The Windows key is different — a
    /// lone injected LWin keyup when the OS doesn't think it's down can itself pop
    /// the Start menu, so it's released ONLY when the OS actually reports it held.
    /// Injected events carry LLKHF_INJECTED and are ignored by our own hook, so this
    /// does not recurse.
    /// </summary>
    private void ForceReleaseChordKeys()
    {
        var vks = new List<ushort>();

        // target key: gate Win keys on real OS state; release anything else outright
        if (!IsWinKey(_targetVk) || OsThinksDown(_targetVk)) vks.Add((ushort)_targetVk);

        if (_needCtrl) { vks.Add(VK_LCONTROL); vks.Add(VK_RCONTROL); vks.Add(VK_CONTROL); }
        if (_needShift) { vks.Add(VK_LSHIFT); vks.Add(VK_RSHIFT); vks.Add(VK_SHIFT); }
        if (_needAlt) { vks.Add(VK_LMENU); vks.Add(VK_RMENU); vks.Add(VK_MENU); }
        if (_needWin)
        {
            if (OsThinksDown(VK_LWIN)) vks.Add(VK_LWIN);
            if (OsThinksDown(VK_RWIN)) vks.Add(VK_RWIN);
        }

        SendKeyUps(vks);
    }

    private static void SendKeyUps(IReadOnlyCollection<ushort> vks)
    {
        if (vks.Count == 0) return;

        // Windows opens the Start menu / Quick Settings flyout (the "sound menu")
        // whenever it sees a Windows-key press-and-release with no other key in
        // between. A phantom-held Win key means the OS already saw the down, so the
        // lone keyup we inject to clear it completes that press/release pair and
        // pops the menu — exactly the symptom that shows up as a dictation ends.
        // Mask it the way the OS does internally (and AutoHotkey via A_MenuMaskKey):
        // inject an inert Ctrl tap first so the Win release reads as part of a chord,
        // not a tap. Ctrl is the safe mask — Win+Ctrl and Alt+Ctrl trigger nothing.
        bool maskWin = vks.Any(vk => vk is VK_LWIN or VK_RWIN);

        var inputs = new List<INPUT>(vks.Count + 2);
        if (maskWin)
        {
            inputs.Add(KeyEvent(VK_LCONTROL, down: true));
            inputs.Add(KeyEvent(VK_LCONTROL, down: false));
        }
        foreach (var vk in vks)
            inputs.Add(KeyEvent(vk, down: false));

        try { SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>()); }
        catch { /* best effort — a stuck key is worse than a failed release */ }
    }

    private static INPUT KeyEvent(ushort vk, bool down) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = down ? 0u : KEYEVENTF_KEYUP } },
    };

    public void Dispose()
    {
        var thread = _hookThread;
        if (thread != null)
        {
            // Wake the hook thread's GetMessage and tell it to exit; it unhooks itself.
            if (_hookThreadId != 0)
                PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            thread.Join(TimeSpan.FromSeconds(2));
            _hookThread = null;
        }
        _installGate.Dispose();
    }
}
