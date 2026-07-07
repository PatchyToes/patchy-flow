using System.Windows.Controls;
using System.Windows.Input;
using TextBox = System.Windows.Controls.TextBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace PatchyFlow;

/// <summary>
/// Click, press a key combo, done. Handles modifier-as-target chords
/// (e.g. Ctrl+LWin, where LWin is the "key") because that's Mike's actual hotkey.
/// </summary>
public sealed class HotkeyCaptureBox : TextBox
{
    public HotkeyConfig Value { get; private set; } = new();

    public HotkeyCaptureBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Focusable = true;
    }

    public void Load(HotkeyConfig config)
    {
        Value = new HotkeyConfig
        {
            Ctrl = config.Ctrl, Shift = config.Shift, Alt = config.Alt, Win = config.Win, Key = config.Key,
        };
        Text = Value.Describe();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Tab) { e.Handled = false; return; } // keep keyboard nav working

        bool keyIsCtrl = key is Key.LeftCtrl or Key.RightCtrl;
        bool keyIsShift = key is Key.LeftShift or Key.RightShift;
        bool keyIsAlt = key is Key.LeftAlt or Key.RightAlt;
        bool keyIsWin = key is Key.LWin or Key.RWin;

        var mods = Keyboard.Modifiers;
        var config = new HotkeyConfig
        {
            // a modifier can BE the target key; don't also count it as a held modifier
            Ctrl = mods.HasFlag(ModifierKeys.Control) && !keyIsCtrl,
            Shift = mods.HasFlag(ModifierKeys.Shift) && !keyIsShift,
            Alt = mods.HasFlag(ModifierKeys.Alt) && !keyIsAlt,
            Win = mods.HasFlag(ModifierKeys.Windows) && !keyIsWin,
            Key = ((System.Windows.Forms.Keys)KeyInterop.VirtualKeyFromKey(key)).ToString(),
        };

        Value = config;
        Text = config.Describe();
    }
}
