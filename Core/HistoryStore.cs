using System.Text;
using System.Text.Json;

namespace PatchyFlow;

public sealed record HistoryEntry(DateTime Ts, string App, string Text);

/// <summary>
/// Every finalized dictation lands here (history.jsonl) so a bad paste
/// never loses words: browse and copy from Settings, repaste-last by hotkey.
/// </summary>
public static class HistoryStore
{
    private static readonly object Gate = new();
    private static string FilePath => Path.Combine(Paths.DataDir, "history.jsonl");

    /// <summary>Raised after a dictation is appended, so an open history view can refresh live.</summary>
    public static event Action? Changed;

    public static void Append(string text, string app, int keepLast)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            lock (Gate)
            {
                Paths.EnsureCreated();
                var line = JsonSerializer.Serialize(new { ts = DateTime.Now, app, text = text.Trim() });
                File.AppendAllText(FilePath, line + Environment.NewLine, Encoding.UTF8);
                TrimIfNeeded(keepLast);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"History append failed: {ex.Message}");
            return;
        }
        Changed?.Invoke();
    }

    public static List<HistoryEntry> ReadRecent(int count)
    {
        try
        {
            lock (Gate)
            {
                if (!File.Exists(FilePath)) return new List<HistoryEntry>();
                return File.ReadAllLines(FilePath)
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Select(Parse)
                    .Where(e => e != null)
                    .Select(e => e!)
                    .TakeLast(count)
                    .Reverse() // newest first
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            Log.Write($"History read failed: {ex.Message}");
            return new List<HistoryEntry>();
        }
    }

    public static string? LastText() => ReadRecent(1).FirstOrDefault()?.Text;

    private static HistoryEntry? Parse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            return new HistoryEntry(
                r.GetProperty("ts").GetDateTime(),
                r.TryGetProperty("app", out var a) ? a.GetString() ?? "" : "",
                r.GetProperty("text").GetString() ?? "");
        }
        catch
        {
            return null; // one corrupt line shouldn't kill the history
        }
    }

    private static void TrimIfNeeded(int keepLast)
    {
        var lines = File.ReadAllLines(FilePath);
        if (lines.Length > keepLast * 2)
            File.WriteAllLines(FilePath, lines.TakeLast(keepLast));
    }
}
