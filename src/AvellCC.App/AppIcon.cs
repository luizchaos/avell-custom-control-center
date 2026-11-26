using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using AvellCC.Core.Control;

namespace AvellCC.App;

/// <summary>
/// The AvellCC icon, drawn in code so the exe icon (Assets\AvellCC.ico) and the tray icon come from one design:
/// a blue fan inside an RGB ring (fans + keyboard lighting) on a dark rounded tile.
/// Regenerate the .ico after changing the drawing: <c>AvellCC.exe --export-icon src\AvellCC.App\Assets\AvellCC.ico</c>.
/// </summary>
public static class AppIcon
{
    public static readonly int[] IcoSizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

    /// <summary>Renders the icon at <paramref name="size"/> px; with a state, adds the tray status dot.</summary>
    public static Bitmap Render(int size, LoopState? state = null)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        var small = size <= 24;
        g.ScaleTransform(size / 256f, size / 256f);    // design grid: 256 × 256

        // Tile
        var tileInset = small ? 0 : 8;
        using (var tile = RoundedRect(tileInset, tileInset, 256 - 2 * tileInset, 256 - 2 * tileInset, small ? 48 : 56))
        using (var fill = new LinearGradientBrush(new Point(0, 0), new Point(0, 256), Color.FromArgb(0x2A, 0x31, 0x3D), Color.FromArgb(0x12, 0x15, 0x1B)))
        {
            g.FillPath(fill, tile);
        }

        // RGB ring: short arcs walking around the hue circle
        var ringWidth = Math.Max(14f, 1.6f * 256 / size);
        var ringRadius = small ? 100f : 92f;
        const int segments = 48;
        for (var i = 0; i < segments; i++)
        {
            using var pen = new Pen(FromHue(i * 360.0 / segments + 200), ringWidth);
            g.DrawArc(pen, 128 - ringRadius, 128 - ringRadius, 2 * ringRadius, 2 * ringRadius, i * 360f / segments - 90, 360f / segments + 1.5f);
        }

        // Fan: five swept blades with one gradient across the whole rotor
        var bladeScale = small ? 1.12f : 1f;
        using (var blades = new LinearGradientBrush(new Point(40, 40), new Point(216, 216), Color.FromArgb(0x93, 0xC5, 0xFD), Color.FromArgb(0x25, 0x63, 0xEB)))
        {
            for (var i = 0; i < 5; i++)
            {
                using var blade = new GraphicsPath();
                // swept blade: wide leading edge curving out to a tip that leans clockwise
                blade.AddBezier(-16, -20, -40, -48, -14, -84, 34, -76);
                blade.AddBezier(34, -76, 16, -62, 12, -42, 18, -16);
                blade.CloseFigure();
                using var m = new Matrix();
                m.Translate(128, 128);
                m.Rotate(i * 72 + 8);
                m.Scale(bladeScale, bladeScale);
                blade.Transform(m);
                g.FillPath(blades, blade);
            }
        }

        // Hub
        using (var hub = new SolidBrush(Color.FromArgb(0x16, 0x1A, 0x21))) g.FillEllipse(hub, 128 - 26, 128 - 26, 52, 52);
        using (var cap = new SolidBrush(Color.FromArgb(0xE6, 0xE8, 0xEC))) g.FillEllipse(cap, 128 - 11, 128 - 11, 22, 22);

        // Tray status dot (bottom right)
        if (state is { } s)
        {
            var color = s switch
            {
                LoopState.Curve => Color.FromArgb(0x22, 0xC5, 0x5E),
                LoopState.Firmware => Color.FromArgb(0x9A, 0xA1, 0xAD),
                LoopState.Emergency or LoopState.Error => Color.FromArgb(0xEF, 0x44, 0x44),
                _ => Color.FromArgb(0xF5, 0x9E, 0x0B),
            };
            using var outline = new SolidBrush(Color.FromArgb(0x12, 0x15, 0x1B));
            using var dot = new SolidBrush(color);
            g.FillEllipse(outline, 150, 150, 106, 106);
            g.FillEllipse(dot, 164, 164, 78, 78);
        }
        return bmp;
    }

    /// <summary>Tray / window icon for the current fan-control state.</summary>
    public static Icon Create(LoopState state, int size = 32)
    {
        using var bmp = Render(size, state);
        var handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    /// <summary>Preview sheet: every .ico size plus the tray states, on dark and light backgrounds.</summary>
    public static void ExportPreview(string path)
    {
        LoopState[] states = [LoopState.Curve, LoopState.Firmware, LoopState.BlockedByVendor, LoopState.Error];
        var width = IcoSizes.Sum() + 12 * IcoSizes.Length + 12;
        using var sheet = new Bitmap(width, 2 * (256 + 24) + 2 * 64, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(sheet);
        g.Clear(Color.FromArgb(0x20, 0x23, 0x2A));
        g.FillRectangle(Brushes.WhiteSmoke, 0, 256 + 24, width, 256 + 24);
        foreach (var row in new[] { 12, 256 + 36 })
        {
            var x = 12;
            foreach (var size in IcoSizes)
            {
                using var bmp = Render(size);
                g.DrawImage(bmp, x, row + 256 - size, size, size);
                x += size + 12;
            }
        }
        var tx = 12;
        foreach (var state in states)
        foreach (var size in new[] { 16, 24, 32 })
        {
            using var bmp = Render(size, state);
            g.DrawImage(bmp, tx, 2 * (256 + 24) + 16, size, size);
            g.FillRectangle(Brushes.WhiteSmoke, tx, 2 * (256 + 24) + 64, 40, 40);
            g.DrawImage(bmp, tx, 2 * (256 + 24) + 68, size, size);
            tx += 44;
        }
        sheet.Save(path, ImageFormat.Png);
    }

    /// <summary>Writes a multi-size .ico (PNG frames, Windows Vista+).</summary>
    public static void ExportIco(string path)
    {
        var frames = IcoSizes.Select(size =>
        {
            using var bmp = Render(size);
            using var png = new MemoryStream();
            bmp.Save(png, ImageFormat.Png);
            return (size, data: png.ToArray());
        }).ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var file = File.Create(path);
        using var w = new BinaryWriter(file);
        w.Write((ushort)0);              // reserved
        w.Write((ushort)1);              // type: icon
        w.Write((ushort)frames.Count);
        var offset = 6 + 16 * frames.Count;
        foreach (var (size, data) in frames)
        {
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);            // palette
            w.Write((byte)0);            // reserved
            w.Write((ushort)1);          // planes
            w.Write((ushort)32);         // bits per pixel
            w.Write(data.Length);
            w.Write(offset);
            offset += data.Length;
        }
        foreach (var (_, data) in frames) w.Write(data);
    }

    private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        p.AddArc(x, y, 2 * r, 2 * r, 180, 90);
        p.AddArc(x + w - 2 * r, y, 2 * r, 2 * r, 270, 90);
        p.AddArc(x + w - 2 * r, y + h - 2 * r, 2 * r, 2 * r, 0, 90);
        p.AddArc(x, y + h - 2 * r, 2 * r, 2 * r, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Saturated color from a hue in degrees.</summary>
    private static Color FromHue(double hue)
    {
        hue = (hue % 360 + 360) % 360 / 60;
        var x = 1 - Math.Abs(hue % 2 - 1);
        var (r, g, b) = (int)hue switch
        {
            0 => (1.0, x, 0.0), 1 => (x, 1.0, 0.0), 2 => (0.0, 1.0, x), 3 => (0.0, x, 1.0), 4 => (x, 0.0, 1.0), _ => (1.0, 0.0, x),
        };
        static int C(double v) => (int)Math.Round(40 + v * 215);   // keep it bright but not pure neon
        return Color.FromArgb(C(r), C(g), C(b));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
