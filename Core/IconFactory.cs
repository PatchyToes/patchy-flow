using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace PatchyFlow;

/// <summary>
/// Renders the app icon: dark rounded tile, teal waveform bars —
/// the same visual language as the overlay pill and admin panel.
/// Writes a multi-size PNG-compressed .ico (16..256).
/// </summary>
internal static class IconFactory
{
    private static readonly int[] Sizes = { 16, 24, 32, 48, 64, 128, 256 };

    public static void WriteIco(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pngs = Sizes.Select(RenderPng).ToArray();

        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((ushort)0);              // reserved
        w.Write((ushort)1);              // type: icon
        w.Write((ushort)Sizes.Length);

        int offset = 6 + 16 * Sizes.Length;
        for (int i = 0; i < Sizes.Length; i++)
        {
            int s = Sizes[i];
            w.Write((byte)(s == 256 ? 0 : s));
            w.Write((byte)(s == 256 ? 0 : s));
            w.Write((byte)0);            // palette
            w.Write((byte)0);            // reserved
            w.Write((ushort)1);          // planes
            w.Write((ushort)32);         // bpp
            w.Write(pngs[i].Length);
            w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var png in pngs) w.Write(png);
    }

    private static byte[] RenderPng(int size)
    {
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        // dark rounded tile with a subtle vertical gradient
        float radius = size * 0.22f;
        using var tile = RoundedRect(0.5f, 0.5f, size - 1f, size - 1f, radius);
        using var bg = new LinearGradientBrush(
            new RectangleF(0, 0, size, size),
            Color.FromArgb(255, 38, 38, 42),
            Color.FromArgb(255, 16, 16, 18),
            LinearGradientMode.Vertical);
        g.FillPath(bg, tile);

        // teal waveform bars (capsules via round-cap lines)
        var teal = Color.FromArgb(255, 78, 201, 176);
        float barWidth = Math.Max(1.5f, size * 0.095f);
        float gap = size * 0.075f;
        float[] heights = { 0.30f, 0.55f, 0.80f, 0.55f, 0.30f };

        using var pen = new Pen(teal, barWidth)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };

        float totalWidth = 5 * barWidth + 4 * gap;
        float x = (size - totalWidth) / 2 + barWidth / 2;
        foreach (var h in heights)
        {
            float barHeight = Math.Max(barWidth, size * h * 0.74f);
            float y1 = (size - barHeight) / 2 + barWidth / 2;
            float y2 = (size + barHeight) / 2 - barWidth / 2;
            g.DrawLine(pen, x, y1, x, y2);
            x += barWidth + gap;
        }

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        float d = 2 * r;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
