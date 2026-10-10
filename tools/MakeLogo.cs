// Draws the 2X Ping logo (signal bars + "2X") and writes assets\logo.png and a multi-size assets\app.ico.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

static class MakeLogo
{
    static readonly Color Green = Color.FromArgb(0, 210, 145);
    static readonly Color GreenDark = Color.FromArgb(0, 140, 100);

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

    static Bitmap Draw(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        float s = size / 256f;
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            // tile
            var tile = new RectangleF(4 * s, 4 * s, 248 * s, 248 * s);
            using (var path = Rounded(tile, 56 * s))
            using (var bg = new LinearGradientBrush(tile, Color.FromArgb(34, 38, 50), Color.FromArgb(14, 16, 22), 90f))
            using (var border = new Pen(Color.FromArgb(120, Green), Math.Max(1f, 6 * s)))
            {
                g.FillPath(bg, path);
                g.DrawPath(border, path);
            }

            // signal bars (bottom right), rising left to right
            float[] heights = { 46, 82, 118, 154 };
            float barW = 30 * s, gap = 12 * s, baseY = 214 * s, x = 76 * s;
            for (int i = 0; i < heights.Length; i++)
            {
                var r = new RectangleF(x, baseY - heights[i] * s, barW, heights[i] * s);
                using (var path = Rounded(r, 8 * s))
                using (var b = new LinearGradientBrush(r, Green, GreenDark, 90f))
                    g.FillPath(b, path);
                x += barW + gap;
            }

            // "2X" badge (top left)
            if (size >= 24)
            {
                using (var f = new Font("Segoe UI Black", 92 * s, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var shadow = new SolidBrush(Color.FromArgb(160, 0, 0, 0)))
                using (var white = new SolidBrush(Color.White))
                {
                    var pt = new PointF(22 * s, 6 * s);
                    g.DrawString("2X", f, shadow, pt.X + 4 * s, pt.Y + 4 * s);
                    g.DrawString("2X", f, white, pt);
                }
            }
            else
            {
                // too small for text: a bright dot marks the brand
                using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, 30 * s, 30 * s, 70 * s, 70 * s);
            }
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
        Console.WriteLine("Logo written to " + dir);
    }
}
