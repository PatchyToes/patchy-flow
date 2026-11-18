using System.Text.RegularExpressions;

namespace PatchyFlow;

/// <summary>
/// Optional delete-only correction pass. Removes filler and the abandoned half of
/// self-corrections, never rephrases. Engine-agnostic: the Anthropic and Local
/// backends are dumb text generators; the system prompt, similarity guard, dash
/// stripping, cancellation, and stats all live here and run identically for both.
/// Every failure path falls back to the raw transcript.
/// </summary>
public static class CleanupPass
{
    private const string SystemPrompt =
        "You reformat dictated speech-to-text for readability. Your job is to make the transcript " +
        "easier to READ, not to edit what was said.\n" +
        "PRESERVE EVERYTHING THE SPEAKER SAYS. Keep every point, aside, tangent, and repetition, in " +
        "the original order and the speaker's own words. The speaker is usually thinking out loud, and " +
        "the full ramble is the value, not noise to be trimmed. Never condense it. Do NOT drop a " +
        "sentence or idea because it seems redundant, long-winded, off-topic, or unimportant. When you " +
        "are unsure whether something belongs, keep it. Err toward keeping too much.\n" +
        "REMOVE only true verbal disfluencies:\n" +
        "- Filler sounds: um, uh, er, ah, hmm.\n" +
        "- 'you know', 'like', 'I mean', 'sort of', 'kind of' ONLY when used as empty filler, never " +
        "when they carry meaning.\n" +
        "- An immediately repeated (stuttered) word, or a sentence transcribed back-to-back twice; " +
        "keep one copy.\n" +
        "- The abandoned half of an explicit, immediate self-correction, ONLY when it is unmistakable. " +
        "Example: 'send it Tuesday, no wait, Thursday' becomes 'send it Thursday'. If it might instead " +
        "be the speaker developing, restating, or circling back on a thought, KEEP both.\n" +
        "PUNCTUATION & STRUCTURE (whitespace and punctuation only, never words):\n" +
        "- Fix punctuation and capitalization. Break long run-on stream-of-consciousness passages " +
        "into sentences and paragraphs for readability.\n" +
        "- Question detection: the speaker often thinks out loud and a single question can span " +
        "several sentences. When the overall intent is a question or a request (common when they're " +
        "prompting an AI), end it with a question mark. Be conservative — only when it genuinely reads " +
        "as a question; when unsure, leave it as a statement.\n" +
        "- When the speaker clearly enumerates items, format them as a simple bulleted or numbered list.\n" +
        "- NEVER use em dashes or en dashes. Use commas, periods, or parentheses instead.\n" +
        "HARD RULE: never summarize, shorten, rephrase, reorder, expand, or substitute the speaker's " +
        "words. Keep their exact wording and word order. You may ONLY delete the disfluencies listed " +
        "above, fix punctuation and capitalization, and add whitespace or list structure. If it needs " +
        "nothing, return it unchanged. Output only the cleaned text — no commentary, no quotes.";

    // A deliberately smaller job for the local engine. Small models drift when asked
    // to exercise judgment (collapsing self-corrections, inferring questions, "fixing"
    // grammar), so we take the judgment away and ask only for the safe, mechanical part.
    // Haiku gets the full SystemPrompt above; local is the offline fallback, scoped to
    // what an 8B can do without putting words in your mouth.
    private const string LocalSystemPrompt =
        "You remove speech disfluencies from a dictated transcript. Do the MINIMUM.\n" +
        "ONLY remove, and only when clearly used as filler:\n" +
        "- um, uh, er, ah, hmm.\n" +
        "- 'you know', 'like', 'I mean', 'sort of', 'kind of' when they carry no meaning.\n" +
        "- an immediately repeated (stuttered) word or phrase — keep one copy.\n" +
        "Then fix capitalization and add sentence-ending punctuation where a sentence clearly ends.\n" +
        "NEVER do anything else. Specifically:\n" +
        "- Never add, delete, reorder, or substitute any real (non-filler) word — not even to fix " +
        "grammar, spelling, or an apparent transcription error. If a word looks wrong, LEAVE IT.\n" +
        "- Never rewrite a self-correction; leave what was said exactly as said.\n" +
        "- Never make a list. Never use em dashes or en dashes.\n" +
        "When in doubt, change nothing. Output only the cleaned text — no commentary, no quotes.";

    private static readonly AnthropicCleanupProvider AnthropicProvider = new();
    private static readonly LocalCleanupProvider LocalProvider = new();

    private static bool IsLocal(AppSettings s) =>
        string.Equals(s.CleanupEngine, "Local", StringComparison.OrdinalIgnoreCase);

    public static async Task<string> ApplyAsync(string raw, ForegroundInfo? context, AppSettings settings,
        CancellationToken cancel = default)
    {
        if (!settings.CleanupEnabled)
            return raw;
        if (CountWords(raw) < 4)
            return raw; // nothing worth a model call

        bool local = IsLocal(settings);

        // Per-engine readiness. Critically, the Local path must NOT be gated on an
        // Anthropic key — an offline user won't have one, and that's the whole point.
        if (local)
        {
            if (string.IsNullOrWhiteSpace(settings.LocalModel)) return raw;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(settings.AnthropicApiKey)) return raw;
        }

        ICleanupProvider provider = local ? LocalProvider : AnthropicProvider;
        int timeoutSec = local ? Math.Max(1, settings.LocalTimeoutSeconds) : 8;

        try
        {
            // Local gets the stripped-down prompt with no context hint — telling a small
            // model to "resolve ambiguous words" is exactly the invitation to substitute
            // that we're trying to avoid. Haiku gets the full prompt plus context.
            var system = local ? LocalSystemPrompt : SystemPrompt;
            if (!local && context != null && settings.ContextAwareness)
            {
                system += $"\nThe user is dictating into: {context.Describe()}. " +
                          "Use this only to resolve ambiguous words; never change tone or content because of it.";
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancel);
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var result = await provider.CompleteAsync(system, raw, settings, cts.Token);

            // Count the call (with real token usage) before the guard, matching the
            // original behavior: you "spent" the call even if the guard rejects it.
            // Local tokens are non-billable so they never inflate the cost estimate.
            Stats.RecordAiCall(result.InputTokens, result.OutputTokens, billable: !local);

            if (result.Truncated)
            {
                Log.Write($"Cleanup output hit the model's token ceiling and lost its tail ({settings.CleanupEngine}); using raw");
                return raw;
            }

            var cleaned = StripDashes(result.Text.Trim());

            if (!PassesGuard(raw, cleaned))
            {
                Log.Write($"Cleanup guard rejected output ({settings.CleanupEngine}, {raw.Length} -> {cleaned.Length} chars, {sw.ElapsedMilliseconds}ms); using raw");
                return raw;
            }

            Log.Write($"Cleanup ({settings.CleanupEngine}): {raw.Length} -> {cleaned.Length} chars in {sw.ElapsedMilliseconds}ms");
            return cleaned;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // User hit Escape — an abort, not a failure. Let FinishDictationAsync handle it
            // (skip paste, play the cancel blip) and don't record an AI failure.
            throw;
        }
        catch (Exception ex)
        {
            Stats.RecordAiFailure();
            Log.Write($"Cleanup failed ({settings.CleanupEngine}, using raw transcript): {ex.Message}");
            return raw;
        }
    }

    // Filler removal legitimately trims some words, but cleaned text that drops
    // below this fraction of the original word count means the model summarized
    // instead of cleaning. The ramble is the value, so reject and fall back to raw.
    private const double RetentionFloor = 0.70;

    // Escape hatch for the floor: word count alone can't tell summarizing from
    // removing a large Whisper duplication glitch (same block transcribed twice).
    // Deleting a duplicate removes no vocabulary — every distinct word still
    // appears elsewhere — while summarizing guts it. So a low word-count ratio is
    // still accepted when nearly all distinct words survive.
    private const double DedupTypeCoverage = 0.90;

    /// <summary>
    /// Undercorrection as a guarantee: the cleaned text must be made of the
    /// speaker's own words (>=88% token overlap), must not grow, and must not
    /// shrink below the retention floor (which would mean summarizing, not cleaning).
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

        // Reject over-deletion: too many of the speaker's words are gone — unless
        // the vocabulary survived, which means duplicates were removed, not content.
        if (rawTokens.Count > 0 && (double)cleanedTokens.Count / rawTokens.Count < RetentionFloor)
        {
            var rawTypes = new HashSet<string>(rawTokens);
            var cleanedTypes = new HashSet<string>(cleanedTokens);
            int keptTypes = rawTypes.Count(cleanedTypes.Contains);
            double coverage = (double)keptTypes / rawTypes.Count;
            if (coverage < DedupTypeCoverage)
            {
                Log.Write($"Guard floor: kept {(double)cleanedTokens.Count / rawTokens.Count:P0} of words, {coverage:P0} of vocabulary");
                return false;
            }
        }

        if (!EndSurvived(rawTokens, cleanedTokens)) return false;

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

    /// <summary>
    /// Catches the one failure the other checks are blind to: the model quietly stopping
    /// early and dropping the last paragraph or two. Word count still looks fine (the
    /// tail is a small fraction of a long dictation) and every surviving word is the
    /// speaker's, so both the retention floor and the overlap check wave it through.
    ///
    /// Look at the end specifically. Take the distinctive words from the last stretch of
    /// the transcript — ones that appear at most twice overall, so common glue words and
    /// filler can't paper over a missing tail — and require most of them to still be
    /// present somewhere in the output. Checking "somewhere" rather than "at the end"
    /// keeps this safe when the model legitimately collapses a repeated block.
    /// </summary>
    private static bool EndSurvived(List<string> rawTokens, List<string> cleanedTokens)
    {
        const int TailWindow = 30;
        if (rawTokens.Count < TailWindow * 2) return true; // too short for a tail to hide in

        var counts = new Dictionary<string, int>();
        foreach (var t in rawTokens)
            counts[t] = counts.TryGetValue(t, out var n) ? n + 1 : 0 + 1;

        var distinctive = rawTokens
            .Skip(rawTokens.Count - TailWindow)
            .Where(t => t.Length > 2 && counts[t] <= 2)
            .Distinct()
            .ToList();

        if (distinctive.Count < 4) return true; // nothing distinctive enough to judge by

        var cleanedSet = new HashSet<string>(cleanedTokens);
        int kept = distinctive.Count(cleanedSet.Contains);
        double survival = (double)kept / distinctive.Count;

        if (survival < 0.5)
        {
            Log.Write($"Guard tail: cleanup dropped the end of the dictation ({kept}/{distinctive.Count} closing words survived)");
            return false;
        }
        return true;
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
