using System.Text.Json;
using System.Text.Json.Serialization;

namespace PatchyFlow;

public sealed class HotkeyConfig
{
    // No modifier defaults to true — every hotkey must opt in to its modifiers
    // explicitly, or they'd all inherit a phantom Ctrl.
    public bool Ctrl { get; set; }
    public bool Shift { get; set; }
    public bool Alt { get; set; }
    public bool Win { get; set; }
    public string Key { get; set; } = "Space";

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
    // single dead key: hold-aim-release with one finger. (Ctrl+Alt+R was taken, Ctrl+Alt+D was awkward.)
    public HotkeyConfig Hotkey { get; set; } = new() { Key = "Scroll" };

    /// <summary>Resolutions to cycle through: "2560x1440" or "1920x1080@144". Unsupported ones are skipped.</summary>
    public List<string> Presets { get; set; } = new() { "3840x2160", "2560x1440", "1920x1080" };
}

public sealed class HistorySettings
{
    /// <summary>Hold-free hotkey that re-pastes the most recent dictation.</summary>
    public bool RepasteEnabled { get; set; } = true;

    /// <summary>Wispr Flow muscle memory: Alt+Shift+Z.</summary>
    public HotkeyConfig RepasteHotkey { get; set; } = new() { Alt = true, Shift = true, Key = "Z" };

    public int KeepLast { get; set; } = 500;
}

public sealed class AppSettings
{
    /// <summary>Master switch for push-to-talk dictation.</summary>
    public bool DictationEnabled { get; set; } = true;

    // Mike's real Wispr binding: hold Ctrl+Win.
    public HotkeyConfig Hotkey { get; set; } = new() { Ctrl = true, Key = "LWin" };

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
    public int MaxRecordingSeconds { get; set; } = 900;

    /// <summary>The old 5-minute ceiling, bumped once on load. See Load().</summary>
    private const int LegacyMaxRecordingSeconds = 300;

    /// <summary>Anthropic API key for the correction pass. Empty = pass disabled, raw transcripts.</summary>
    public string AnthropicApiKey { get; set; } = "";

    /// <summary>Delete-only AI cleanup of transcripts.</summary>
    public bool CleanupEnabled { get; set; } = true;

    /// <summary>Which backend runs the cleanup pass: "Anthropic" (cloud) or "Local" (Ollama, fully offline).</summary>
    public string CleanupEngine { get; set; } = "Anthropic";

    /// <summary>Anthropic model for the cloud engine.</summary>
    public string CleanupModel { get; set; } = "claude-haiku-4-5";

    /// <summary>Ollama base URL for the Local engine. Never leaves the machine.</summary>
    public string LocalEndpoint { get; set; } = "http://localhost:11434";

    /// <summary>Ollama model tag for the Local engine, e.g. "qwen3:8b". Must be pulled in Ollama.</summary>
    public string LocalModel { get; set; } = "qwen3:8b";

    /// <summary>Local cleanup timeout. Longer than the cloud path: a cold model load can be slow.</summary>
    public int LocalTimeoutSeconds { get; set; } = 30;

    /// <summary>Feed the active app name + window title to transcription and cleanup.</summary>
    public bool ContextAwareness { get; set; } = true;

    public ScreenshotSettings Screenshot { get; set; } = new();

    public DisplaySettings Display { get; set; } = new();

    public HistorySettings History { get; set; } = new();

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
                if (loaded != null)
                {
                    // The 5-minute ceiling was a safety valve against a stuck hotkey, but
                    // long dictations were hitting it and silently losing everything said
                    // after it fired. Anyone still on the old default gets the new one.
                    if (loaded.MaxRecordingSeconds == LegacyMaxRecordingSeconds)
                    {
                        loaded.MaxRecordingSeconds = new AppSettings().MaxRecordingSeconds;
                        loaded.Save();
                        Log.Write($"Raised max recording length to {loaded.MaxRecordingSeconds}s (was the old {LegacyMaxRecordingSeconds}s default)");
                    }
                    return loaded;
                }
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
