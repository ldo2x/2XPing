using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace PingBoost
{
    class PingGraph : Control
    {
        const int Max = 90;
        readonly List<double> samples = new List<double>();
        public string Label = "";

        public PingGraph()
        {
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            BackColor = Theme.Panel1;
        }

        public void Add(double v)
        {
            samples.Add(v);
            if (samples.Count > Max) samples.RemoveAt(0);
            Invalidate();
        }

        public void Clear() { samples.Clear(); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var rc = new Rectangle(48, 26, Width - 62, Height - 40);
            if (rc.Width < 20 || rc.Height < 20) return;

            var ok = samples.Where(v => v >= 0).ToList();
            double max = ok.Count > 0 ? Math.Max(50, ok.Max() * 1.25) : 100;

            using (var pen = new Pen(Color.FromArgb(56, 44, 78)))
                for (int i = 0; i <= 4; i++)
                {
                    int y = rc.Bottom - i * rc.Height / 4;
                    g.DrawLine(pen, rc.Left, y, rc.Right, y);
                    TextRenderer.DrawText(g, Math.Round(max * i / 4).ToString(), Font, new Point(6, y - 8), Theme.Dim);
                }

            if (samples.Count == 0)
            {
                TextRenderer.DrawText(g, Lang.T("Your live ping shows up here once Speed up is on", "البنق المباشر حقك يطلع هنا لما تشغّل التسريع"),
                    Font, rc, Theme.Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | Lang.Flags);
                return;
            }

            float step = rc.Width / (float)(Max - 1);
            float x0 = rc.Right - (samples.Count - 1) * step;
            using (var pen = new Pen(Theme.Accent, 2f))
            using (var bad = new SolidBrush(Theme.Bad))
            {
                PointF? prev = null;
                for (int i = 0; i < samples.Count; i++)
                {
                    float x = x0 + i * step;
                    if (samples[i] < 0) { g.FillEllipse(bad, x - 3, rc.Top - 3, 6, 6); prev = null; continue; }
                    var pt = new PointF(x, (float)(rc.Bottom - samples[i] / max * rc.Height));
                    if (prev.HasValue) g.DrawLine(pen, prev.Value, pt);
                    prev = pt;
                }
            }

            string stats = Label;
            if (ok.Count > 0)
                stats += "    " + Lang.T("avg ", "المتوسط ") + Math.Round(ok.Average()) + "   " + Lang.T("min ", "الأقل ") + Math.Round(ok.Min()) +
                         "   " + Lang.T("max ", "الأعلى ") + Math.Round(ok.Max()) + "   " + Lang.T("loss ", "الفقد ") +
                         "‎" + Math.Round(100.0 * (samples.Count - ok.Count) / samples.Count) + "%‎";
            TextRenderer.DrawText(g, stats, Font, new Point(rc.Left, 4), Theme.TextC, Lang.Flags);
        }
    }
}
