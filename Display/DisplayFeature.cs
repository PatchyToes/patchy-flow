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

    public DisplayFeature(Func<AppSettings> settings, Action<string, string> notify)
    {
        _settings = settings;
        _notify = notify;
        _hotkey.Pressed += CycleResolution;
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

    private void CycleResolution()
    {
        try
        {
            var presets = _settings().Display.Presets
                .Select(ParsePreset)
                .Where(p => p != null)
                .Select(p => p!)
                .ToList();
            if (presets.Count == 0)
            {
                _notify("PatchyFlow", "No display presets configured (Settings → Display).");
                return;
            }

            var current = GetCurrentMode();
            if (current == null)
            {
                _notify("PatchyFlow", "Couldn't read the current display mode.");
                return;
            }

            // find where we are in the cycle; unknown resolution = jump to first preset
            int index = presets.FindIndex(p => p.Width == current.Width && p.Height == current.Height);
            for (int attempt = 1; attempt <= presets.Count; attempt++)
            {
                var next = presets[(index + attempt) % presets.Count];
                if (next.Width == current.Width && next.Height == current.Height) continue;

                var supported = FindSupportedMode(next);
                if (supported == null)
                {
                    Log.Write($"Display preset {next.Width}x{next.Height}@{next.Hz} not supported by this monitor");
                    continue;
                }

                if (Apply(supported))
                {
                    Log.Write($"Display: {current.Width}x{current.Height} -> {supported.Width}x{supported.Height}@{supported.Hz}");
                    _notify("Resolution changed", $"{supported.Width}x{supported.Height} @ {supported.Hz}Hz");
                    return;
                }
            }
            _notify("PatchyFlow", "No other preset applied (all unsupported or already active).");
        }
        catch (Exception ex)
        {
            Log.Write($"Display cycle failed: {ex}");
            _notify("PatchyFlow — display change failed", "Check the log (tray menu).");
        }
    }

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
