using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Models.Messages;

namespace PatchyFlow;

/// <summary>
/// Optional Claude Haiku correction pass. Delete-only by design: it removes
/// filler and the abandoned half of self-corrections, never rephrases.
/// A similarity guard rejects any output that drifts from what was said,
/// and every failure path falls back to the raw transcript.
/// </summary>
public static class CleanupPass
{
    private const string SystemPrompt =
        "You clean up dictated speech-to-text transcripts. Your ONLY job is delete-only editing:\n" +
        "- Remove filler words (um, uh, and 'you know'/'like' when used as pure filler).\n" +
        "- Remove false starts and the abandoned half of self-corrections, keeping only the " +
        "speaker's final version. Example: 'send it Tuesday, no wait, Thursday' becomes 'send it Thursday'.\n" +
        "- Fix punctuation and capitalization only where clearly needed.\n" +
        "You must NEVER rephrase, reorder, summarize, expand, or add words the speaker did not say. " +
        "Keep the speaker's exact word choices. If the transcript needs no changes, return it unchanged. " +
        "Output only the cleaned text — no commentary, no quotes.";

    private static AnthropicClient? _client;
    private static string _clientKey = "";

    public static async Task<string> ApplyAsync(string raw, ForegroundInfo? context, AppSettings settings)
    {
        if (!settings.CleanupEnabled || string.IsNullOrWhiteSpace(settings.AnthropicApiKey))
            return raw;
        if (CountWords(raw) < 4)
            return raw; // nothing worth an API call

        try
        {
            if (_client == null || _clientKey != settings.AnthropicApiKey)
            {
                _client = new AnthropicClient { ApiKey = settings.AnthropicApiKey };
                _clientKey = settings.AnthropicApiKey;
            }

            var system = SystemPrompt;
            if (context != null && settings.ContextAwareness)
            {
                system += $"\nThe user is dictating into: {context.Describe()}. " +
                          "Use this only to resolve ambiguous words; never change tone or content because of it.";
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var response = await _client.Messages.Create(new MessageCreateParams
            {
                Model = settings.CleanupModel,
                MaxTokens = 4096,
                System = system,
                Messages =
                [
                    new MessageParam { Role = "user", Content = raw },
                ],
            }, cts.Token);

            var sb = new System.Text.StringBuilder();
            foreach (var block in response.Content)
                if (block.TryPickText(out var textBlock))
                    sb.Append(textBlock.Text);
            var cleaned = sb.ToString().Trim();

            if (!PassesGuard(raw, cleaned))
            {
                Log.Write($"Cleanup guard rejected output ({raw.Length} -> {cleaned.Length} chars, {sw.ElapsedMilliseconds}ms); using raw");
                return raw;
            }

            Log.Write($"Cleanup: {raw.Length} -> {cleaned.Length} chars in {sw.ElapsedMilliseconds}ms");
            return cleaned;
        }
        catch (Exception ex)
        {
            Log.Write($"Cleanup failed (using raw transcript): {ex.Message}");
            return raw;
        }
    }

    /// <summary>
    /// Undercorrection as a guarantee: the cleaned text must be made of the
    /// speaker's own words (>=88% token overlap) and must not grow.
    /// </summary>
    private static bool PassesGuard(string raw, string cleaned)
    {
        if (string.IsNullOrWhiteSpace(cleaned)) return false;
        if (cleaned.Length > raw.Length * 1.15) return false;

        var rawTokens = Tokenize(raw);
        var cleanedTokens = Tokenize(cleaned);
        if (cleanedTokens.Count == 0) return false;

        var pool = new Dictionary<string, int>();
        foreach (var t in rawTokens)
            pool[t] = pool.TryGetValue(t, out var n) ? n + 1 : 1;

        int matched = 0;
        foreach (var t in cleanedTokens)
        {
            if (pool.TryGetValue(t, out var n) && n > 0)
            {
                pool[t] = n - 1;
                matched++;
            }
        }

        return (double)matched / cleanedTokens.Count >= 0.88;
    }

    private static List<string> Tokenize(string text) =>
        Regex.Matches(text.ToLowerInvariant(), @"[\w']+").Select(m => m.Value).ToList();

    private static int CountWords(string text) =>
        Regex.Matches(text, @"\S+").Count;
}
