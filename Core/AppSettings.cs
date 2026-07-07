using System.Text.Json;
using System.Text.Json.Serialization;

namespace PatchyFlow;

public sealed class HotkeyConfig
{
    // Default matches Mike's real Wispr Flow binding: hold Ctrl+Win.
    public bool Ctrl { get; set; } = true;
    public bool Shift { get; set; }
    public bool Alt { get; set; }
    public bool Win { get; set; }
    public string Key { get; set; } = "LWin";

    public string Describe()
    {
        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Shift) parts.Add("Shift");
        if (Alt) parts.Add("Alt");
        if (Win) parts.Add("Win");
        parts.Add(Key);
        return string.Join("+", parts);
    }
}

public sealed class ScreenshotSettings
{
    public bool Enabled { get; set; } = true;
    public HotkeyConfig Hotkey { get; set; } = new() { Ctrl = true, Shift = true, Key = "S" };

    /// <summary>Also run Claude vision to describe the snip; otherwise local OCR text only.</summary>
    public bool SmartDescribe { get; set; }

    /// <summary>Folder to also save the PNG into. Empty = clipboard only.</summary>
    public string SavePngTo { get; set; } = "";
}

public sealed class DisplaySettings
{
    public bool Enabled { get; set; } = true;
    public HotkeyConfig Hotkey { get; set; } = new() { Ctrl = true, Alt = true, Key = "R" };

    /// <summary>Resolutions to cycle through: "2560x1440" or "1920x1080@144". Unsupported ones are skipped.</summary>
    public List<string> Presets { get; set; } = new() { "3840x2160", "2560x1440", "1920x1080" };
}

public sealed class AppSettings
{
    /// <summary>Master switch for push-to-talk dictation.</summary>
    public bool DictationEnabled { get; set; } = true;

    public HotkeyConfig Hotkey { get; set; } = new();

    /// <summary>"en", "auto", or any whisper language code.</summary>
    public string Language { get; set; } = "en";

    /// <summary>Model file name inside %APPDATA%\PatchyFlow\models.</summary>
    public string Model { get; set; } = "ggml-large-v3-turbo.bin";

    /// <summary>Holds shorter than this are treated as accidental taps.</summary>
    public int MinHoldMs { get; set; } = 350;

    /// <summary>Extra audio captured after key release so trailing words aren't cut.</summary>
    public int TailCaptureMs { get; set; } = 200;

    /// <summary>Append a trailing space so back-to-back dictations chain naturally.</summary>
    public bool AppendSpace { get; set; } = true;

    /// <summary>Put the previous clipboard content back after pasting.</summary>
    public bool RestoreClipboard { get; set; } = true;

    /// <summary>Substring match against capture device names. Empty = default communications mic.</summary>
    public string MicDeviceName { get; set; } = "";

    /// <summary>Play the start/cancel pings.</summary>
    public bool EnableSounds { get; set; } = true;

    /// <summary>Safety stop for a runaway recording, in seconds.</summary>
    public int MaxRecordingSeconds { get; set; } = 300;

    /// <summary>Anthropic API key for the correction pass. Empty = pass disabled, raw transcripts.</summary>
    public string AnthropicApiKey { get; set; } = "";

    /// <summary>Delete-only AI cleanup of transcripts (needs AnthropicApiKey).</summary>
    public bool CleanupEnabled { get; set; } = true;

    public string CleanupModel { get; set; } = "claude-haiku-4-5";

    /// <summary>Feed the active app name + window title to transcription and cleanup.</summary>
    public bool ContextAwareness { get; set; } = true;

    public ScreenshotSettings Screenshot { get; set; } = new();

    public DisplaySettings Display { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Paths.SettingsPath), JsonOpts);
                if (loaded != null) return loaded;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Settings load failed, using defaults: {ex.Message}");
        }

        var defaults = new AppSettings();
        defaults.Save();
        return defaults;
    }

    public void Save()
    {
        try
        {
            Paths.EnsureCreated();
            File.WriteAllText(Paths.SettingsPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch (Exception ex)
        {
            Log.Write($"Settings save failed: {ex.Message}");
        }
    }
}
