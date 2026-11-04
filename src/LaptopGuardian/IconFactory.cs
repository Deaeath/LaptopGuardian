using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Drawing.Imaging;
using LaptopGuardian.Core;

namespace LaptopGuardian;

/// <summary>Draws the shield-and-pulse icon at any size and colour, so the tray icon can show the laptop's state.</summary>
internal static class IconFactory
{
    public static readonly Color Calm = Color.FromArgb(0x2F, 0x8F, 0xE8);
    public static readonly Color Good = Color.FromArgb(0x2E, 0xB8, 0x6E);
    public static readonly Color Warn = Color.FromArgb(0xE8, 0xA3, 0x2A);
    public static readonly Color Bad = Color.FromArgb(0xE5, 0x48, 0x45);

    public static Color For(Severity level) => level switch
    {
        Severity.Critical => Bad,
        Severity.Warning => Warn,
        _ => Good,
    };

    public static Bitmap Render(int size, Color color)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        float s = size;
        PointF P(float x, float y) => new(x * s, y * s);
        using var shield = new GraphicsPath();
        shield.AddBezier(P(0.5f, 0.04f), P(0.63f, 0.13f), P(0.76f, 0.16f), P(0.90f, 0.17f));
        shield.AddLine(P(0.90f, 0.17f), P(0.90f, 0.47f));
        shield.AddBezier(P(0.90f, 0.47f), P(0.90f, 0.73f), P(0.71f, 0.87f), P(0.5f, 0.97f));
        shield.AddBezier(P(0.5f, 0.97f), P(0.29f, 0.87f), P(0.10f, 0.73f), P(0.10f, 0.47f));
        shield.AddLine(P(0.10f, 0.47f), P(0.10f, 0.17f));
        shield.AddBezier(P(0.10f, 0.17f), P(0.24f, 0.16f), P(0.37f, 0.13f), P(0.5f, 0.04f));
        shield.CloseFigure();

        var top = Lighten(color, 0.28f);
        using (var fill = new LinearGradientBrush(new RectangleF(0, 0, s, s), top, color, LinearGradientMode.Vertical))
            g.FillPath(fill, shield);

        float w = Math.Max(1.5f, s * 0.085f);
        using var pen = new Pen(Color.White, w) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(pen, new[]
        {
            P(0.22f, 0.53f), P(0.37f, 0.53f), P(0.45f, 0.35f), P(0.55f, 0.70f), P(0.63f, 0.53f), P(0.78f, 0.53f),
        });
        return bmp;
    }

    static Color Lighten(Color c, float f) =>
        Color.FromArgb(c.A, (int)(c.R + (255 - c.R) * f), (int)(c.G + (255 - c.G) * f), (int)(c.B + (255 - c.B) * f));

    /// <summary>A multi-size .ico with PNG frames (Windows Vista and later).</summary>
    public static byte[] Ico(Color color, params int[] sizes)
    {
        var frames = sizes.Select(sz =>
        {
            using var bmp = Render(sz, color);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return (Size: sz, Png: ms.ToArray());
        }).ToList();

        using var o = new MemoryStream();
        using var w = new BinaryWriter(o);
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)frames.Count);
        int offset = 6 + 16 * frames.Count;
        foreach (var f in frames)
        {
            w.Write((byte)(f.Size >= 256 ? 0 : f.Size));
            w.Write((byte)(f.Size >= 256 ? 0 : f.Size));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((ushort)1); w.Write((ushort)32);
            w.Write(f.Png.Length); w.Write(offset);
            offset += f.Png.Length;
        }
        foreach (var f in frames) w.Write(f.Png);
        return o.ToArray();
    }

    public static Icon TrayIcon(Color color) =>
        new(new MemoryStream(Ico(color, 16, 20, 24, 32, 40, 48, 64)), System.Windows.Forms.SystemInformation.SmallIconSize);
}
