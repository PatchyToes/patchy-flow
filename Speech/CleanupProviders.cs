using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Models.Messages;

namespace PatchyFlow;

/// <summary>
/// Raw model output plus token usage. Deliberately dumb: no guard, no dash
/// stripping, no fallback logic. CleanupPass owns all of that so every engine
/// passes through the exact same gate.
/// </summary>
public readonly record struct CleanupRaw(string Text, long InputTokens, long OutputTokens, bool Truncated);

/// <summary>A cleanup backend. Given a system prompt and the user's transcript, return the model's text.</summary>
public interface ICleanupProvider
{
    Task<CleanupRaw> CompleteAsync(string system, string user, AppSettings settings, CancellationToken ct);
}

/// <summary>Claude via the Anthropic SDK. Caches the client, keyed on the API key.</summary>
public sealed class AnthropicCleanupProvider : ICleanupProvider
{
    private AnthropicClient? _client;
    private string _clientKey = "";

    public async Task<CleanupRaw> CompleteAsync(string system, string user, AppSettings settings, CancellationToken ct)
    {
        if (_client == null || _clientKey != settings.AnthropicApiKey)
        {
            _client = new AnthropicClient { ApiKey = settings.AnthropicApiKey };
            _clientKey = settings.AnthropicApiKey;
        }

        var response = await _client.Messages.Create(new MessageCreateParams
        {
            Model = settings.CleanupModel,
            // Headroom, not a budget: you are only billed for tokens actually generated.
            // A cap that the model can bump into silently loses the end of a long dictation.
            MaxTokens = 16000,
            System = system,
            Messages =
            [
                new MessageParam { Role = "user", Content = user },
            ],
        }, ct);

        var sb = new StringBuilder();
        foreach (var block in response.Content)
            if (block.TryPickText(out var textBlock))
                sb.Append(textBlock.Text);

        // Interpolated rather than compared as an enum so this keeps compiling across
        // SDK revisions that reshape the StopReason type.
        var stop = $"{response.StopReason}";
        bool truncated = stop.Contains("max", StringComparison.OrdinalIgnoreCase)
                      && stop.Contains("token", StringComparison.OrdinalIgnoreCase);

        return new CleanupRaw(sb.ToString().Trim(),
            response.Usage?.InputTokens ?? 0, response.Usage?.OutputTokens ?? 0, truncated);
    }
}

/// <summary>
/// Local model via an Ollama server (localhost:11434 by default). Fully offline.
/// Holds no per-settings cache except the shared HttpClient — endpoint and model
/// are read fresh each call, so a live engine/model switch can't go stale.
/// </summary>
public sealed class LocalCleanupProvider : ICleanupProvider
{
    // Shared and long-lived. The real per-call deadline comes from the caller's
    // CancellationToken (CleanupPass's linked timeout+Escape CTS), not this ceiling.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<CleanupRaw> CompleteAsync(string system, string user, AppSettings settings, CancellationToken ct)
    {
        var body = new
        {
            model = settings.LocalModel,
            stream = false,
            // Qwen3 and friends default to thinking-on; a <think> chain is hundreds of
            // words absent from the transcript and would fail the overlap guard every call.
            think = false,
            keep_alive = -1,      // pin in VRAM while PatchyFlow runs; freed on exit
            options = new { temperature = 0.0, num_ctx = 8192 },
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user },
            },
        };

        using var resp = await PostAsync(settings, "/api/chat", body, ct);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var content = root.GetProperty("message").GetProperty("content").GetString() ?? "";
        content = StripThink(content).Trim();

        long inTok = root.TryGetProperty("prompt_eval_count", out var p) ? p.GetInt64() : 0;
        long outTok = root.TryGetProperty("eval_count", out var e) ? e.GetInt64() : 0;
        bool truncated = root.TryGetProperty("done_reason", out var dr)
                      && string.Equals(dr.GetString(), "length", StringComparison.OrdinalIgnoreCase);
        return new CleanupRaw(content, inTok, outTok, truncated);
    }

    /// <summary>
    /// Belt-and-suspenders for C2: strip any reasoning block even though we asked
    /// the model not to emit one. A single leaked &lt;think&gt; block would collapse
    /// the guard's token overlap and force a raw fallback on every dictation.
    /// </summary>
    private static string StripThink(string text) =>
        Regex.Replace(text, @"<think>.*?</think>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase).TrimStart();

    /// <summary>
    /// Load and pin the model so the first real dictation doesn't pay a cold VRAM
    /// load inside the cleanup timeout. Fire-and-forget safe: never throws.
    /// </summary>
    public static async Task WarmAsync(AppSettings settings)
    {
        try
        {
            var body = new
            {
                model = settings.LocalModel,
                stream = false,
                think = false,
                keep_alive = -1,
                messages = new[] { new { role = "user", content = "ok" } },
            };
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            using var resp = await PostAsync(settings, "/api/chat", body, timeout.Token);

            if (resp.IsSuccessStatusCode)
                Log.Write($"Local cleanup model warmed: {settings.LocalModel}");
            else if ((int)resp.StatusCode == 404)
                Log.Write($"Local cleanup model '{settings.LocalModel}' not found — run: ollama pull {settings.LocalModel}");
            else
                Log.Write($"Local cleanup warm failed: HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            Log.Write($"Local cleanup warm failed (is Ollama running?): {ex.Message}");
        }
    }

    /// <summary>Release the model from VRAM when PatchyFlow exits. Best effort.</summary>
    public static async Task UnloadAsync(AppSettings settings)
    {
        try
        {
            var body = new { model = settings.LocalModel, keep_alive = 0, messages = Array.Empty<object>() };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var _ = await PostAsync(settings, "/api/chat", body, timeout.Token);
        }
        catch { /* exiting anyway */ }
    }

    private static Task<HttpResponseMessage> PostAsync(AppSettings settings, string path, object body, CancellationToken ct)
    {
        var url = Endpoint(settings) + path;
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json"),
        };
        return Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static string Endpoint(AppSettings s) =>
        (string.IsNullOrWhiteSpace(s.LocalEndpoint) ? "http://localhost:11434" : s.LocalEndpoint).TrimEnd('/');
}
