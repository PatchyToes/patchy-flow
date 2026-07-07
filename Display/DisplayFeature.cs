using System.Runtime.InteropServices;

namespace PatchyFlow;

/// <summary>
/// Hotkey cycles the primary display through preset resolutions
/// ("2560x1440", "1920x1080@144", ...). Same mechanism as the Windows
/// display panel — instant, reversible, no admin rights.
/// </summary>
public sealed class DisplayFeature : IDisposable
{
    private readonly TapHotkey _hotkey = new();
    private readonly Func<AppSettings> _settings;
    private readonly Action<string, string> _notify;

    private ResolutionWheel? _wheel;

    public DisplayFeature(Func<AppSettings> settings, Action<string, string> notify)
    {
        _settings = settings;
        _notify = notify;
        _hotkey.Pressed += OpenWheel;
    }

    /// <summary>Esports-style radial picker: hold the hotkey, aim, release to apply.</summary>
    private void OpenWheel()
    {
        if (_wheel is { IsVisible: true }) return;
        try
        {
            var s = _settings().Display;
            var current = GetCurrentMode();
            if (current == null)
            {
                _notify("PatchyFlow", "Couldn't read the current display mode.");
                return;
            }

            var items = s.Presets
                .Select(ParsePreset)
                .Where(p => p != null)
                .Select(p => FindSupportedMode(p!))
                .Where(m => m != null)
                .Select(m => m!)
                .DistinctBy(m => (m.Width, m.Height))
                .ToList();
            if (items.Count == 0)
            {
                _notify("PatchyFlow", "No supported display presets (Settings → Display).");
                return;
            }

            int targetVk = Enum.TryParse<System.Windows.Forms.Keys>(s.Hotkey.Key, true, out var k) ? (int)k : 0;

            _wheel = new ResolutionWheel(items, current, targetVk, mode =>
            {
                if (mode.Width == current.Width && mode.Height == current.Height) return;
                if (Apply(mode))
                {
                    Log.Write($"Display: {current.Width}x{current.Height} -> {mode.Width}x{mode.Height}@{mode.Hz}");
                    _notify("Resolution changed", $"{mode.Width}x{mode.Height} @ {mode.Hz}Hz");
                }
                else
                {
                    _notify("PatchyFlow", $"Couldn't switch to {mode.Width}x{mode.Height}.");
                }
            });
            _wheel.Show();
        }
        catch (Exception ex)
        {
            Log.Write($"Resolution wheel failed: {ex}");
        }
    }

    public void ApplySettings()
    {
        var s = _settings().Display;
        if (s.Enabled)
        {
            if (!_hotkey.Register(s.Hotkey))
                _notify("PatchyFlow", $"Display hotkey {s.Hotkey.Describe()} is taken by another app.");
        }
        else
        {
            _hotkey.Unregister();
        }
    }

    internal sealed record Mode(int Width, int Height, int Hz);

    internal static Mode? ParsePreset(string line)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            line.Trim(), @"^(\d{3,5})\s*[xX]\s*(\d{3,5})(?:\s*@\s*(\d{2,3}))?$");
        if (!m.Success) return null;
        return new Mode(
            int.Parse(m.Groups[1].Value),
            int.Parse(m.Groups[2].Value),
            m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
    }

    private static Mode? GetCurrentMode()
    {
        var dm = NewDevMode();
        if (!EnumDisplaySettingsW(null, ENUM_CURRENT_SETTINGS, ref dm)) return null;
        return new Mode((int)dm.dmPelsWidth, (int)dm.dmPelsHeight, (int)dm.dmDisplayFrequency);
    }

    /// <summary>Match the preset against modes the monitor actually supports; prefer highest refresh.</summary>
    private static Mode? FindSupportedMode(Mode wanted)
    {
        Mode? best = null;
        var dm = NewDevMode();
        for (int i = 0; EnumDisplaySettingsW(null, i, ref dm); i++)
        {
            if (dm.dmPelsWidth != wanted.Width || dm.dmPelsHeight != wanted.Height) continue;
            int hz = (int)dm.dmDisplayFrequency;
            if (wanted.Hz > 0)
            {
                if (hz == wanted.Hz) return new Mode(wanted.Width, wanted.Height, hz);
            }
            else if (best == null || hz > best.Hz)
            {
                best = new Mode(wanted.Width, wanted.Height, hz);
            }
        }
        return best;
    }

    private static bool Apply(Mode mode)
    {
        var dm = NewDevMode();
        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
        dm.dmPelsWidth = (uint)mode.Width;
        dm.dmPelsHeight = (uint)mode.Height;
        dm.dmDisplayFrequency = (uint)mode.Hz;
        int result = ChangeDisplaySettingsW(ref dm, CDS_UPDATEREGISTRY);
        if (result != DISP_CHANGE_SUCCESSFUL)
            Log.Write($"ChangeDisplaySettings returned {result}");
        return result == DISP_CHANGE_SUCCESSFUL;
    }

    private const int ENUM_CURRENT_SETTINGS = -1;
    private const uint DM_PELSWIDTH = 0x80000;
    private const uint DM_PELSHEIGHT = 0x100000;
    private const uint DM_DISPLAYFREQUENCY = 0x400000;
    private const uint CDS_UPDATEREGISTRY = 0x1;
    private const int DISP_CHANGE_SUCCESSFUL = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType;
        public uint dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    private static DEVMODE NewDevMode() => new() { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsW(ref DEVMODE lpDevMode, uint dwFlags);

    public void Dispose() => _hotkey.Dispose();
}
