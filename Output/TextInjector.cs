using System.Runtime.InteropServices;
using System.Windows;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;

namespace PatchyFlow;

/// <summary>
/// Puts transcribed text into whatever field has focus:
/// clipboard swap + synthetic Ctrl+V, then the old clipboard comes back.
/// </summary>
public static class TextInjector
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

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
        [FieldOffset(0)] public MOUSEINPUT mi;
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
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;

    private static readonly int[] ModifierVks = { 0x11, 0x10, 0x12, 0x5B, 0x5C }; // ctrl, shift, alt, lwin, rwin

    public static async Task PasteAsync(string text, bool restoreClipboard)
    {
        // if the user's fingers are still on the chord, our Ctrl+V would become Ctrl+Win+V etc.
        await WaitForModifierReleaseAsync();

        var dispatcher = Application.Current.Dispatcher;

        string? previous = null;
        await dispatcher.InvokeAsync(() =>
        {
            try { if (Clipboard.ContainsText()) previous = Clipboard.GetText(); }
            catch { /* clipboard owned by someone else right now */ }
        });

        bool set = false;
        for (int attempt = 0; attempt < 5 && !set; attempt++)
        {
            await dispatcher.InvokeAsync(() =>
            {
                try
                {
                    Clipboard.SetDataObject(text, true);
                    set = true;
                }
                catch { /* retry */ }
            });
            if (!set) await Task.Delay(50);
        }
        if (!set)
        {
            Log.Write("Clipboard set failed after retries; paste aborted");
            return;
        }

        SendCtrlV();
        await Task.Delay(250); // give the target app time to consume the paste

        if (restoreClipboard && previous != null)
        {
            await dispatcher.InvokeAsync(() =>
            {
                try { Clipboard.SetDataObject(previous, true); }
                catch { Log.Write("Clipboard restore failed"); }
            });
        }
    }

    private static async Task WaitForModifierReleaseAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            bool anyDown = ModifierVks.Any(vk => (GetAsyncKeyState(vk) & 0x8000) != 0);
            if (!anyDown) return;
            await Task.Delay(15);
        }
    }

    private static void SendCtrlV()
    {
        INPUT Key(ushort vk, bool up) => new()
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 } },
        };

        var inputs = new[]
        {
            Key(VK_CONTROL, false),
            Key(VK_V, false),
            Key(VK_V, true),
            Key(VK_CONTROL, true),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }
}
