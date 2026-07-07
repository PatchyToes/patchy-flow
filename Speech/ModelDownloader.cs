namespace PatchyFlow;

/// <summary>First-run download of the Whisper model from Hugging Face.</summary>
public static class ModelDownloader
{
    private const string BaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

    public static string ModelPath(string modelFileName) => Path.Combine(Paths.ModelsDir, modelFileName);

    public static bool Exists(string modelFileName) => File.Exists(ModelPath(modelFileName));

    public static async Task DownloadAsync(string modelFileName, IProgress<double>? progress = null)
    {
        Paths.EnsureCreated();
        var target = ModelPath(modelFileName);
        var temp = target + ".partial";

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        using var response = await http.GetAsync(BaseUrl + modelFileName, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? -1;
        await using (var src = await response.Content.ReadAsStreamAsync())
        await using (var dst = File.Create(temp))
        {
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read));
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        File.Move(temp, target, overwrite: true);
        Log.Write($"Model downloaded: {modelFileName}");
    }
}
