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
            EnsureCudaRuntimeDiscoverable();

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

    /// <summary>
    /// Whisper's CUDA backend (ggml-cuda-whisper.dll) is ~2x faster than Vulkan on an
    /// NVIDIA card, but it needs the CUDA 12 runtime (cudart/cublas) on the DLL search
    /// path — which we don't ship. Ollama does, though, and it's part of this app's local
    /// stack. So if the runtime isn't already next to the exe, borrow Ollama's copy by
    /// prepending its folder to PATH. If Ollama isn't installed, this no-ops and the
    /// runtime order below falls back to Vulkan — identical to the old behavior, no risk.
    /// </summary>
    private static void EnsureCudaRuntimeDiscoverable()
    {
        try
        {
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, "cudart64_12.dll")))
                return; // already resolvable next to the exe

            var ollamaRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Ollama");
            if (!Directory.Exists(ollamaRoot)) return;

            var cudart = Directory
                .EnumerateFiles(ollamaRoot, "cudart64_12.dll", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (cudart == null) return;

            var cudaDir = Path.GetDirectoryName(cudart)!;
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            Environment.SetEnvironmentVariable("PATH", cudaDir + Path.PathSeparator + path);
            Log.Write($"CUDA runtime for Whisper sourced from: {cudaDir}");
        }
        catch (Exception ex)
        {
            Log.Write($"CUDA runtime probe failed (falling back to Vulkan): {ex.Message}");
        }
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
        {
            if (IsHallucination(segment, samples)) continue;
            sb.Append(segment.Text);
        }

        var text = CleanUp(sb.ToString(), prompt);
        Log.Write($"Transcribed {samples.Length / 16000.0:0.0}s of audio in {sw.ElapsedMilliseconds}ms -> {text.Length} chars");
        return text;
    }

    // Stock caption boilerplate from Whisper's subtitle training data. These are what it
    // emits when asked to transcribe something that isn't speech. Only ever used to judge
    // a segment whose audio is already measurably quiet — never as a blanket text filter,
    // because every one of these is also a thing a person can genuinely say.
    private static readonly HashSet<string> StockCaptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "thank you", "thank you.", "thanks", "thank you very much", "thank you so much",
        "thanks for watching", "thank you for watching", "thanks for watching!",
        "thanks for listening", "please subscribe", "subscribe", "like and subscribe",
        "see you next time", "i'll see you next time", "see you in the next video",
        "bye", "bye bye", "goodbye", "you", "the end", "amen", "okay", "oh",
        "transcription by castingwords", "subtitles by the amara.org community",
    };

    /// <summary>
    /// Whisper invents text when it is fed something that isn't speech — trailing silence,
    /// the zero-padding it adds to reach a full 30s window, or a pause between sentences.
    /// That is where the stray "Thank you" endings and random names come from.
    ///
    /// The model's own no-speech score can't catch this: whisper.cpp computes it once per
    /// 30s window and stamps every segment in that window with it, so a hallucination sitting
    /// right after real speech inherits the real speech's confident score. (Confirmed: the
    /// old >=0.8 check never fired once across thousands of dictations.)
    ///
    /// So measure the actual audio under the segment instead. Two tiers, both conservative:
    /// dead air can't have produced any words, and audio that is merely quiet is only
    /// overruled when the text is known subtitle boilerplate.
    /// </summary>
    private static bool IsHallucination(SegmentData segment, float[] samples)
    {
        var text = segment.Text.Trim();
        if (text.Length == 0) return false;

        // Whisper's timestamps drift by a few hundred ms, so widen the span before
        // measuring. Any real speech leaking into the window keeps the segment.
        const float Guard = 0.35f;
        int from = (int)((segment.Start.TotalSeconds - Guard) * 16000);
        int to = (int)((segment.End.TotalSeconds + Guard) * 16000);
        from = Math.Clamp(from, 0, samples.Length);
        to = Math.Clamp(to, 0, samples.Length);

        // Segment lands entirely past the end of the recording: it was generated from the
        // zero-padding whisper.cpp appends to fill out the last window. Always fake.
        if (to <= from)
        {
            Log.Write($"Dropped segment from silent padding past end of audio: \"{text}\"");
            return true;
        }

        float peak = 0f, sumSq = 0f;
        for (int i = from; i < to; i++)
        {
            float a = Math.Abs(samples[i]);
            if (a > peak) peak = a;
            sumSq += samples[i] * samples[i];
        }
        float rms = (float)Math.Sqrt(sumSq / (to - from));

        // Tier 1 — dead air. Nothing was said here, whatever the model wrote.
        if (peak < 0.02f && rms < 0.005f)
        {
            Log.Write($"Dropped hallucinated segment on silence (peak {peak:0.000}, rms {rms:0.000}): \"{text}\"");
            return true;
        }

        // Tier 2 — audible but far below speech level (breath, keyboard, room tone).
        // Only drop it if the model wrote known caption boilerplate.
        var bare = Regex.Replace(text, @"[^\w' ]", "").Trim();
        if (rms < 0.015f && StockCaptions.Contains(bare))
        {
            Log.Write($"Dropped stock caption over non-speech (peak {peak:0.000}, rms {rms:0.000}): \"{text}\"");
            return true;
        }

        return false;
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
