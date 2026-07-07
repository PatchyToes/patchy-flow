using System.Text;
using System.Text.RegularExpressions;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace PatchyFlow;

/// <summary>
/// Local Whisper transcription via Whisper.net.
/// Runtime preference: CUDA (RTX) -> Vulkan -> CPU.
/// </summary>
public sealed class Transcriber : IDisposable
{
    private WhisperFactory? _factory;

    public bool IsReady => _factory != null;
    public string? LoadedRuntime { get; private set; }

    public async Task InitializeAsync(string modelPath)
    {
        await Task.Run(() =>
        {
            RuntimeOptions.RuntimeLibraryOrder = new List<RuntimeLibrary>
            {
                RuntimeLibrary.Cuda,
                RuntimeLibrary.Vulkan,
                RuntimeLibrary.Cpu,
            };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            _factory = WhisperFactory.FromPath(modelPath);
            LoadedRuntime = RuntimeOptions.LoadedLibrary?.ToString() ?? "unknown";
            Log.Write($"Whisper model loaded in {sw.ElapsedMilliseconds}ms, runtime: {LoadedRuntime}");
        });
    }

    public async Task<string> TranscribeAsync(float[] samples, string language, string? prompt,
        CancellationToken cancel = default)
    {
        if (_factory == null) throw new InvalidOperationException("Model not loaded yet");

        var builder = _factory.CreateBuilder()
            .WithThreads(Math.Max(2, Environment.ProcessorCount / 2))
            // don't condition each 30s window on the previous one — that feedback
            // loop is what makes Whisper repeat a line forever on long dictations
            .WithNoContext();

        if (string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase))
            builder = builder.WithLanguageDetection();
        else
            builder = builder.WithLanguage(language);

        if (!string.IsNullOrWhiteSpace(prompt))
            builder = builder.WithPrompt(prompt);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await using var processor = builder.Build();

        var sb = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(samples, cancel))
            sb.Append(segment.Text);

        var text = CleanUp(sb.ToString(), prompt);
        Log.Write($"Transcribed {samples.Length / 16000.0:0.0}s of audio in {sw.ElapsedMilliseconds}ms -> {text.Length} chars");
        return text;
    }

    private static string CleanUp(string text, string? prompt)
    {
        // whisper noise artifacts like [BLANK_AUDIO], [MUSIC], ♪ — not real speech
        text = Regex.Replace(text, @"\[(?:BLANK_AUDIO|MUSIC|NOISE|SILENCE|INAUDIBLE)[^\]]*\]", "", RegexOptions.IgnoreCase);
        text = text.Replace("♪", "");
        text = Regex.Replace(text, @"\s+", " ");
        text = StripLeakedPrompt(text.Trim(), prompt);
        return text.Trim();
    }

    /// <summary>
    /// Whisper sometimes echoes its initial prompt at the very start of the output.
    /// Strip a leaked "Glossary: …." lead-in, or the prompt verbatim if it leaked whole.
    /// </summary>
    private static string StripLeakedPrompt(string text, string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt) || text.Length == 0) return text;

        // whole-prompt leak (verbatim, case/space-insensitive at the start)
        if (text.StartsWith(prompt, StringComparison.OrdinalIgnoreCase))
            return text[prompt.Length..].TrimStart(' ', ',', '.', '-');

        // partial "Glossary: ...." lead-in (the prompt starts with "Glossary:")
        var m = Regex.Match(text, @"^\s*Glossary:.*?\.\s*", RegexOptions.IgnoreCase);
        if (m.Success && m.Length < text.Length)
            return text[m.Length..];

        return text;
    }

    public void Dispose() => _factory?.Dispose();
}
