using System.Text.Json;

namespace PatchyFlow;

/// <summary>
/// Lightweight running counters (stats.json) for the History window's health strip:
/// how much you dictate, whether AI calls are failing, and a rough cost estimate.
/// </summary>
public sealed class Stats
{
    public int Dictations { get; set; }
    public long Words { get; set; }
    public int AiCalls { get; set; }
    public int AiFailures { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }

    // Haiku 4.5 list price: $1 / 1M input, $5 / 1M output.
    public double EstimatedCostUsd => InputTokens / 1_000_000.0 * 1.0 + OutputTokens / 1_000_000.0 * 5.0;

    private static readonly object Gate = new();
    private static string FilePath => Path.Combine(Paths.DataDir, "stats.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static Stats Load()
    {
        try
        {
            lock (Gate)
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<Stats>(File.ReadAllText(FilePath)) ?? new Stats();
        }
        catch (Exception ex) { Log.Write($"Stats load failed: {ex.Message}"); }
        return new Stats();
    }

    private static void Mutate(Action<Stats> change)
    {
        try
        {
            lock (Gate)
            {
                var s = Load();
                change(s);
                Paths.EnsureCreated();
                File.WriteAllText(FilePath, JsonSerializer.Serialize(s, JsonOpts));
            }
        }
        catch (Exception ex) { Log.Write($"Stats save failed: {ex.Message}"); }
    }

    public static void RecordDictation(int words) =>
        Mutate(s => { s.Dictations++; s.Words += words; });

    public static void RecordAiCall(long inputTokens, long outputTokens) =>
        Mutate(s => { s.AiCalls++; s.InputTokens += inputTokens; s.OutputTokens += outputTokens; });

    public static void RecordAiFailure() =>
        Mutate(s => { s.AiCalls++; s.AiFailures++; });
}
