// Draws the 2X Ping mark ("Quiet Signal", see docs/brand/logo-philosophy.md) and writes assets\logo.png and a
// multi-size assets\app.ico. A glowing core sends out mirrored pairs of ping waves inside a night-violet tile,
// with one gold spark. Each size is drawn on its own: the small sizes drop detail and thicken strokes.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

static class MakeLogo
{
    // Celeste palette
    static readonly Color Pink = Color.FromArgb(255, 111, 200);
    static readonly Color Lavender = Color.FromArgb(172, 146, 255);
    static readonly Color Gold = Color.FromArgb(255, 210, 120);
    static readonly Color CoreLight = Color.FromArgb(255, 238, 214);
    static readonly Color NightTop = Color.FromArgb(46, 30, 76);
    static readonly Color NightBottom = Color.FromArgb(17, 11, 29);

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // four-point spark with slightly concave sides
    static void Spark(Graphics g, float cx, float cy, float r, Color c)
    {
        float k = r * 0.2f;
        var pts = new[]
        {
            new PointF(cx, cy - r), new PointF(cx + k, cy - k), new PointF(cx + r, cy), new PointF(cx + k, cy + k),
            new PointF(cx, cy + r), new PointF(cx - k, cy + k), new PointF(cx - r, cy), new PointF(cx - k, cy - k)
        };
        using (var b = new SolidBrush(c)) g.FillPolygon(b, pts);
    }

    // a mirrored pair of arcs around (cx, cy): one opening to the left, one to the right
    static void Waves(Graphics g, float cx, float cy, float radius, float sweep, float width, Color c)
    {
        using (var pen = new Pen(c, width) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            var box = new RectangleF(cx - radius, cy - radius, radius * 2, radius * 2);
            g.DrawArc(pen, box, -sweep, sweep * 2);         // right
            g.DrawArc(pen, box, 180 - sweep, sweep * 2);    // left
        }
    }

    static Bitmap Draw(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        float s = size / 256f;
        bool tiny = size <= 20, small = size <= 32;
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            g.ScaleTransform(s, s);

            // the night tile
            var tile = tiny ? new RectangleF(0, 0, 256, 256) : new RectangleF(6, 6, 244, 244);
            using (var path = Rounded(tile, tiny ? 64 : 58))
            using (var bg = new LinearGradientBrush(tile, NightTop, NightBottom, 90f))
            {
                g.FillPath(bg, path);
                if (!tiny)
                    using (var border = new Pen(Color.FromArgb(small ? 140 : 110, Lavender), small ? 10 : 3)) g.DrawPath(border, path);
            }

            const float cx = 128, cy = 132;
            if (tiny)
            {
                // tray icon: the core and one pair of waves, heavy enough to survive 16 px
                Waves(g, cx, cy, 78, 42, 36, Pink);
                using (var b = new SolidBrush(CoreLight)) g.FillEllipse(b, cx - 30, cy - 30, 60, 60);
                return bmp;
            }

            // a faint glow behind the core
            if (!small)
                using (var glow = new GraphicsPath())
                {
                    glow.AddEllipse(cx - 58, cy - 58, 116, 116);
                    using (var b = new PathGradientBrush(glow) { CenterColor = Color.FromArgb(70, Pink), SurroundColors = new[] { Color.FromArgb(0, Pink) } })
                        g.FillPath(b, glow);
                }

            float stroke = small ? 22 : 16;
            Waves(g, cx, cy, 80, 34, stroke, Lavender);   // far wave, cooler
            Waves(g, cx, cy, 48, 40, stroke, Pink);       // near wave, warmer

            // the core: warm light at the centre, rose at the rim
            float cr = small ? 22 : 19;
            using (var core = new GraphicsPath())
            {
                core.AddEllipse(cx - cr, cy - cr, cr * 2, cr * 2);
                using (var b = new PathGradientBrush(core) { CenterColor = CoreLight, SurroundColors = new[] { Color.FromArgb(255, 160, 214) } })
                    g.FillPath(b, core);
            }

            // the one asymmetric note
            if (!small) Spark(g, 200, 54, 19, Gold);
            else if (size > 24) Spark(g, 204, 50, 24, Gold); // at 24 px it would only be a speck
        }
        return bmp;
    }

    static void Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : "assets";
        Directory.CreateDirectory(dir);
        using (var big = Draw(512)) big.Save(Path.Combine(dir, "logo.png"), ImageFormat.Png);

        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
        var images = new List<byte[]>();
        foreach (int sz in sizes)
            using (var b = Draw(sz))
            using (var ms = new MemoryStream())
            {
                b.Save(ms, ImageFormat.Png);
                images.Add(ms.ToArray());
            }

        using (var fs = File.Create(Path.Combine(dir, "app.ico")))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(images[i].Length); w.Write(offset);
                offset += images[i].Length;
            }
            foreach (var img in images) w.Write(img);
        }

        // a review sheet: every size side by side, on dark and on light, at true pixels and enlarged
        if (args.Length > 1) Sheet(args[1]);
        Console.WriteLine("Logo written to " + dir);
    }

    static void Sheet(string path)
    {
        int[] sizes = { 256, 64, 48, 32, 24, 16 };
        using (var sheet = new Bitmap(1100, 620))
        using (var g = Graphics.FromImage(sheet))
        {
            g.Clear(Color.FromArgb(20, 15, 30));
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            using (var light = new SolidBrush(Color.FromArgb(236, 233, 242))) g.FillRectangle(light, 0, 330, 1100, 290);
            int x = 30;
            foreach (int sz in sizes)
            {
                using (var b = Draw(sz))
                {
                    g.DrawImage(b, x, 30 + (256 - sz) / 2, sz, sz);           // true size, dark
                    g.DrawImage(b, x, 350 + (256 - sz) / 2, sz, sz);          // true size, light
                }
                x += sz + 40;
            }
            // tray-size icons enlarged 6x (pixel view)
            int ex = 680;
            foreach (int sz in new[] { 32, 16 })
            {
                using (var b = Draw(sz))
                {
                    g.DrawImage(b, ex, 40, sz * (sz == 16 ? 12 : 6), sz * (sz == 16 ? 12 : 6));
                    g.DrawImage(b, ex, 360, sz * (sz == 16 ? 12 : 6), sz * (sz == 16 ? 12 : 6));
                }
                ex += 210;
            }
            sheet.Save(path, ImageFormat.Png);
        }
    }
}
