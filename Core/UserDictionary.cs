using System.Text;
using System.Text.RegularExpressions;

namespace PatchyFlow;

/// <summary>
/// Plain-text personal dictionary at %APPDATA%\PatchyFlow\dictionary.txt.
/// One entry per line:
///   PatchyHub                  — bias transcription toward this word and fix its casing
///   Agency Unbound => Agency: Unbound   — additionally rewrite the left side to the right side
/// Lines starting with # are comments.
/// </summary>
public sealed class UserDictionary
{
    public sealed record Entry(string Phrase, string? Replacement);

    private List<Entry> _entries = new();

    public IReadOnlyList<Entry> Entries => _entries;

    public void Load()
    {
        try
        {
            Paths.EnsureCreated();
            if (!File.Exists(Paths.DictionaryPath))
            {
                File.WriteAllText(Paths.DictionaryPath,
                    "# PatchyFlow dictionary — one word or phrase per line.\n" +
                    "# Plain line: biases transcription and fixes casing (e.g. PatchyHub).\n" +
                    "# Rewrite: spoken text => written text\n",
                    Encoding.UTF8);
            }

            var list = new List<Entry>();
            foreach (var raw in File.ReadAllLines(Paths.DictionaryPath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;

                var arrow = line.IndexOf("=>", StringComparison.Ordinal);
                if (arrow > 0)
                {
                    var phrase = line[..arrow].Trim();
                    var replacement = line[(arrow + 2)..].Trim();
                    if (phrase.Length > 0 && replacement.Length > 0)
                        list.Add(new Entry(phrase, replacement));
                }
                else
                {
                    list.Add(new Entry(line, null));
                }
            }
            _entries = list;
            Log.Write($"Dictionary loaded: {_entries.Count} entries");
        }
        catch (Exception ex)
        {
            Log.Write($"Dictionary load failed: {ex.Message}");
            _entries = new List<Entry>();
        }
    }

    /// <summary>Vocabulary hint passed to Whisper as the initial prompt.</summary>
    public string? BuildWhisperPrompt()
    {
        if (_entries.Count == 0) return null;
        var words = _entries.Select(e => e.Replacement ?? e.Phrase).Distinct();
        var prompt = "Glossary: " + string.Join(", ", words) + ".";
        return prompt.Length > 700 ? prompt[..700] : prompt;
    }

    /// <summary>Fix casing of known words and apply spoken=>written rewrites.</summary>
    public string Apply(string text)
    {
        foreach (var e in _entries)
        {
            var target = e.Replacement ?? e.Phrase;
            try
            {
                // Lookarounds instead of \b so entries with @ / : / - behave.
                var pattern = @"(?<![\w])" + Regex.Escape(e.Phrase) + @"(?![\w])";
                text = Regex.Replace(text, pattern, target.Replace("$", "$$"), RegexOptions.IgnoreCase);
            }
            catch (Exception ex)
            {
                Log.Write($"Dictionary rule failed for '{e.Phrase}': {ex.Message}");
            }
        }
        return text;
    }
}
