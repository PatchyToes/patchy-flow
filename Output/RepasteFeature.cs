namespace PatchyFlow;

/// <summary>Alt+Shift+Z: paste the most recent dictation again, anywhere.</summary>
public sealed class RepasteFeature : IDisposable
{
    private readonly TapHotkey _hotkey = new();
    private readonly Func<AppSettings> _settings;
    private readonly Action<string, string> _notify;
    private bool _busy;

    public RepasteFeature(Func<AppSettings> settings, Action<string, string> notify)
    {
        _settings = settings;
        _notify = notify;
        _hotkey.Pressed += () => _ = RepasteAsync();
    }

    public void ApplySettings()
    {
        var s = _settings().History;
        if (s.RepasteEnabled)
        {
            if (!_hotkey.Register(s.RepasteHotkey))
                _notify("PatchyFlow", $"Repaste hotkey {s.RepasteHotkey.Describe()} is taken by another app.");
        }
        else
        {
            _hotkey.Unregister();
        }
    }

    private async Task RepasteAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var last = HistoryStore.LastText();
            if (string.IsNullOrEmpty(last))
            {
                _notify("PatchyFlow", "No dictation in history yet.");
                return;
            }
            var s = _settings();
            await TextInjector.PasteAsync(s.AppendSpace ? last + " " : last, s.RestoreClipboard);
            Log.Write($"Repasted last dictation ({last.Length} chars)");
        }
        catch (Exception ex)
        {
            Log.Write($"Repaste failed: {ex}");
        }
        finally
        {
            _busy = false;
        }
    }

    public void Dispose() => _hotkey.Dispose();
}
