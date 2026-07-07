using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Media.Imaging;
using Windows.Media.Ocr;
using BitmapDecoder = Windows.Graphics.Imaging.BitmapDecoder;
using Clipboard = System.Windows.Clipboard;
using DataObject = System.Windows.DataObject;

namespace PatchyFlow;

/// <summary>
/// Snip a region → the clipboard carries BOTH the image and its text at once.
/// Paste into a vision AI and it takes the image; paste into a terminal,
/// text-only AI, or doc and it takes the text. Local Windows OCR by default
/// (perfect for consoles); optional Claude vision describe for structure.
/// </summary>
public sealed class ScreenshotFeature : IDisposable
{
    private readonly TapHotkey _hotkey = new();
    private Func<AppSettings> _settings;
    private readonly Action<string, string> _notify;
    private bool _snipInProgress;

    public ScreenshotFeature(Func<AppSettings> settings, Action<string, string> notify)
    {
        _settings = settings;
        _notify = notify;
        _hotkey.Pressed += () => _ = OnHotkeyAsync();
    }

    public void ApplySettings()
    {
        var s = _settings().Screenshot;
        if (s.Enabled)
        {
            if (!_hotkey.Register(s.Hotkey))
                _notify("PatchyFlow", $"Screenshot hotkey {s.Hotkey.Describe()} is taken by another app.");
        }
        else
        {
            _hotkey.Unregister();
        }
    }

    private async Task OnHotkeyAsync()
    {
        if (_snipInProgress) return;
        _snipInProgress = true;
        try
        {
            await SnipAsync();
        }
        catch (Exception ex)
        {
            Log.Write($"Screenshot failed: {ex}");
            _notify("PatchyFlow — screenshot failed", "Check the log (tray menu).");
        }
        finally
        {
            _snipInProgress = false;
        }
    }

    private async Task SnipAsync()
    {
        var s = _settings();

        // freeze the whole virtual desktop first so the snip UI never appears in the capture
        var vs = System.Windows.Forms.SystemInformation.VirtualScreen;
        using var frozen = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(frozen))
            g.CopyFromScreen(vs.Left, vs.Top, 0, 0, frozen.Size);

        var snip = new SnipWindow(ToBitmapSource(frozen), vs.Left, vs.Top, vs.Width, vs.Height);
        snip.ShowDialog();
        if (snip.SelectedRegion is not { } region) return;

        using var crop = frozen.Clone(
            new Rectangle(region.X - vs.Left, region.Y - vs.Top, region.Width, region.Height),
            PixelFormat.Format32bppArgb);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        string text = await OcrAsync(crop);

        if (s.Screenshot.SmartDescribe)
        {
            var described = await DescribeAsync(crop, text, s);
            if (!string.IsNullOrWhiteSpace(described)) text = described;
        }

        var imageSource = ToBitmapSource(crop);
        var data = new DataObject();
        data.SetImage(imageSource);
        if (!string.IsNullOrWhiteSpace(text))
            data.SetText(text);
        Clipboard.SetDataObject(data, true);

        if (!string.IsNullOrEmpty(s.Screenshot.SavePngTo))
            SavePng(crop, s.Screenshot.SavePngTo);

        Log.Write($"Snip {region.Width}x{region.Height}: {text.Length} chars of text in {sw.ElapsedMilliseconds}ms");
        _notify("Copied: image + text",
            text.Length > 0 ? $"{region.Width}x{region.Height} px, {text.Length} chars of text on the clipboard."
                            : $"{region.Width}x{region.Height} px (no text detected).");
    }

    internal static async Task<string> OcrAsync(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        stream.Position = 0;

        var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
        using var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine == null)
        {
            Log.Write("OCR engine unavailable for profile languages");
            return "";
        }

        var result = await engine.RecognizeAsync(softwareBitmap);
        return string.Join(Environment.NewLine, result.Lines.Select(l => l.Text));
    }

    private static async Task<string> DescribeAsync(Bitmap bitmap, string ocrText, AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.AnthropicApiKey)) return "";
        try
        {
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            var b64 = Convert.ToBase64String(stream.ToArray());

            var client = new Anthropic.AnthropicClient { ApiKey = settings.AnthropicApiKey };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var response = await client.Messages.Create(new Anthropic.Models.Messages.MessageCreateParams
            {
                Model = settings.CleanupModel,
                MaxTokens = 2048,
                System = "Convert this screenshot into faithful, structured plain text for a text-only reader: " +
                         "transcribe visible text exactly, preserve structure (tables as aligned text, workflow steps " +
                         "in order, console output verbatim), and add at most one leading sentence saying what is on " +
                         "screen. No commentary.",
                Messages =
                [
                    new Anthropic.Models.Messages.MessageParam
                    {
                        Role = "user",
                        Content = new List<Anthropic.Models.Messages.ContentBlockParam>
                        {
                            new Anthropic.Models.Messages.ImageBlockParam
                            {
                                Source = new Anthropic.Models.Messages.Base64ImageSource
                                {
                                    Data = b64,
                                    MediaType = "image/png",
                                },
                            },
                        },
                    },
                ],
            }, cts.Token);

            var sb = new System.Text.StringBuilder();
            foreach (var block in response.Content)
                if (block.TryPickText(out var t))
                    sb.Append(t.Text);
            return sb.ToString().Trim();
        }
        catch (Exception ex)
        {
            Log.Write($"Smart describe failed (falling back to OCR text): {ex.Message}");
            return "";
        }
    }

    private static void SavePng(Bitmap bitmap, string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            bitmap.Save(Path.Combine(folder, $"snip-{DateTime.Now:yyyyMMdd-HHmmss}.png"), ImageFormat.Png);
        }
        catch (Exception ex)
        {
            Log.Write($"Snip save failed: {ex.Message}");
        }
    }

    private static BitmapSource ToBitmapSource(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    public void Dispose() => _hotkey.Dispose();
}
