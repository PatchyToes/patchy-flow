using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Interop;

namespace PatchyFlow;

/// <summary>
/// Tap-style global hotkey via RegisterHotKey (fires once per press).
/// The hold-to-talk chord uses the low-level hook; everything else uses this.
/// </summary>
public sealed class TapHotkey : IDisposable
{
    public event Action? Pressed;

    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private static int _nextId = 0xB000;

    private readonly int _id;
    private HwndSource? _source;
    private bool _registered;

    public TapHotkey()
    {
        _id = _nextId++;
    }

    /// <summary>Register (or re-register) the hotkey. Returns false if the combo is taken.</summary>
    public bool Register(HotkeyConfig config)
    {
        Unregister();

        if (!Enum.TryParse<Keys>(config.Key, ignoreCase: true, out var key))
        {
            Log.Write($"TapHotkey: unknown key '{config.Key}'");
            return false;
        }

        uint mods = 0;
        if (config.Ctrl) mods |= MOD_CONTROL;
        if (config.Shift) mods |= MOD_SHIFT;
        if (config.Alt) mods |= MOD_ALT;
        if (config.Win) mods |= MOD_WIN;

        // message-only sink window for WM_HOTKEY
        _source ??= new HwndSource(new HwndSourceParameters("PatchyFlowHotkeySink")
        {
            Width = 0, Height = 0,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP
        });
        _source.AddHook(WndProc);

        _registered = RegisterHotKey(_source.Handle, _id, mods, (uint)key);
        Log.Write(_registered
            ? $"Hotkey registered: {config.Describe()}"
            : $"Hotkey FAILED (already taken by another app): {config.Describe()}");
        return _registered;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == _id)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Unregister()
    {
        if (_registered && _source != null)
        {
            UnregisterHotKey(_source.Handle, _id);
            _registered = false;
        }
    }

    public void Dispose()
    {
        Unregister();
        _source?.RemoveHook(WndProc);
        _source?.Dispose();
        _source = null;
    }
}
