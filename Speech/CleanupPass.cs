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
        "You clean up and lightly format dictated speech-to-text transcripts.\n" +
        "REMOVE (delete-only):\n" +
        "- Filler words (um, uh, and 'you know'/'like' when used as pure filler).\n" +
        "- False starts and the abandoned half of self-corrections, keeping only the speaker's " +
        "final version. Example: 'send it Tuesday, no wait, Thursday' becomes 'send it Thursday'.\n" +
        "- Duplicated sentences or phrases that are obvious transcription glitches (the same line " +
        "repeated back-to-back, exactly or near-exactly; keep one copy).\n" +
        "PUNCTUATION & STRUCTURE (whitespace and punctuation only, never words):\n" +
        "- Fix punctuation and capitalization. Break long run-on stream-of-consciousness passages " +
        "into sentences and paragraphs for readability.\n" +
        "- Question detection: the speaker often thinks out loud and a single question can span " +
        "several sentences. When the overall intent is a question or a request (common when they're " +
        "prompting an AI), end it with a question mark. Be conservative — only when it genuinely reads " +
        "as a question; when unsure, leave it as a statement.\n" +
        "- When the speaker clearly enumerates items, format them as a simple bulleted or numbered list.\n" +
        "- NEVER use em dashes or en dashes. Use commas, periods, or parentheses instead.\n" +
        "HARD RULE: never rephrase, reorder, summarize, expand, or substitute the speaker's words. " +
        "Keep their exact wording and word order. You may only delete filler/false-starts, fix " +
        "punctuation and capitalization, and add whitespace or list structure. If it needs nothing, " +
        "return it unchanged. Output only the cleaned text — no commentary, no quotes.";

    private static AnthropicClient? _client;
    private static string _clientKey = "";

    public static async Task<string> ApplyAsync(string raw, ForegroundInfo? context, AppSettings settings,
        CancellationToken cancel = default)
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

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancel);
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

            Stats.RecordAiCall(response.Usage?.InputTokens ?? 0, response.Usage?.OutputTokens ?? 0);

            var sb = new System.Text.StringBuilder();
            foreach (var block in response.Content)
                if (block.TryPickText(out var textBlock))
                    sb.Append(textBlock.Text);
            var cleaned = StripDashes(sb.ToString().Trim());

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
            Stats.RecordAiFailure();
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
        // compare word-character length only, so added list bullets / newlines / paragraph
        // breaks don't trip the growth guard (they carry no words). The token-overlap check
        // below is what actually prevents the model from adding or changing words.
        if (WordCharLen(cleaned) > WordCharLen(raw) * 1.15) return false;

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

    private static List<string> Tokenize(string text)
    {
        // Drop line-leading list markers ("1.", "-", "•") so added list formatting
        // doesn't count as new words against the overlap guard.
        text = Regex.Replace(text, @"(?m)^\s*(?:\d+[.)]|[-*•])\s+", "");
        return Regex.Matches(text.ToLowerInvariant(), @"[\w']+").Select(m => m.Value).ToList();
    }

    private static int CountWords(string text) =>
        Regex.Matches(text, @"\S+").Count;

    private static int WordCharLen(string text) =>
        Regex.Matches(text, @"[\w']").Count;

    /// <summary>Mike never wants em/en dashes. Turn them into commas, tidy spacing.</summary>
    private static string StripDashes(string text)
    {
        text = Regex.Replace(text, @"\s*[—–]\s*", ", ");
        text = Regex.Replace(text, @",\s*,", ",");   // ", ," -> ","
        text = Regex.Replace(text, @"\s+,", ",");     // " ," -> ","
        return text;
    }
}
