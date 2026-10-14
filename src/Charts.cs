using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace PingBoost
{
    // Small GDI+ charts in the Celeste theme for the developer dashboard: a line/area chart for daily numbers,
    // horizontal bars for shares, a donut, a scatter of two values and plain columns. Each draws its own card,
    // writes a short line when it has nothing to show, and puts its title and labels on the reading side.

    // The axis arithmetic (pure, tested): round tick values and value -> pixel.
    static class ChartScale
    {
        // Round ticks (steps of 1, 2 or 5 times a power of ten) covering min..max, at most about maxTicks of them.
        // wholeNumbers: never a step below 1 (counts). An empty or flat range still gives a usable axis.
        public static double[] NiceTicks(double min, double max, int maxTicks, bool wholeNumbers)
        {
            if (double.IsNaN(min) || double.IsNaN(max) || double.IsInfinity(min) || double.IsInfinity(max)) { min = 0; max = 1; }
            if (min > max) { double t = min; min = max; max = t; }
            if (max - min < 1e-9)
            {
                if (max == 0) max = 1;
                else if (max > 0) min = Math.Min(0, min);
                else max = 0;
                if (max - min < 1e-9) max = min + 1;
            }
            maxTicks = Math.Max(2, maxTicks);
            double step = NiceStep((max - min) / (maxTicks - 1));
            if (wholeNumbers) step = Math.Max(1, Math.Round(step));
            double lo = Math.Floor(min / step) * step, hi = Math.Ceiling(max / step) * step;
            var l = new List<double>();
            for (double v = lo; v <= hi + step * 0.5 && l.Count < 100; v += step) l.Add(Math.Round(v / step) * step);
            if (l.Count < 2) l.Add(lo + step);
            return l.ToArray();
        }

        // the closest of 1, 2, 5, 10, 20, 50... (times a power of ten) to raw (Heckbert's nice numbers)
        public static double NiceStep(double raw)
        {
            if (raw <= 0 || double.IsNaN(raw) || double.IsInfinity(raw)) return 1;
            double p = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double f = raw / p;
            double nice = f < 1.5 ? 1 : f < 3 ? 2 : f < 7 ? 5 : 10;
            return nice * p;
        }

        // value -> pixel between lo and hi (hi may be less than lo, e.g. y grows upwards); a flat range maps to lo
        public static float Map(double v, double min, double max, float lo, float hi)
        {
            if (max - min < 1e-12) return lo;
            return (float)(lo + (v - min) / (max - min) * (hi - lo));
        }

        // a tick's text: 1200 -> "1.2k", 5 -> "5", 0.5 -> "0.5"
        public static string TickText(double v)
        {
            if (Math.Abs(v) >= 10000) return DashFmt.Count((long)Math.Round(v));
            if (Math.Abs(v - Math.Round(v)) < 1e-9) return ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }

    // The common part: the card, the title, the "nothing yet" line and the reading side.
    abstract class ChartBase : Control
    {
        public string Title = "";
        // a short number line on the far side of the title ("+6 ms"), drawn left to right so its sign and unit keep
        // their order in Arabic too
        public string Note = "";
        public string EmptyText { get; set; }
        protected const int Pad = 14, TitleH = 30;

        protected ChartBase()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            ForeColor = Theme.TextC;
            Font = Fonts.Ui(8.5f);
        }

        protected bool Rtl { get { return RightToLeft == RightToLeft.Yes; } }
        protected abstract bool IsEmpty { get; }
        protected abstract void Draw(Graphics g, Rectangle plot);

        // the text side flags: right-aligned and right-to-left in Arabic
        protected TextFormatFlags Lead
        {
            get
            {
                return TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter |
                       (Rtl ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left);
            }
        }

        // Text on the reading side, cut with "..." only when it doesn't fit: GDI's ellipsis reorders mixed Arabic and
        // numbers ("+6 ms" came out as "ms 6+") even when nothing is cut.
        public static void DrawFit(Graphics g, string text, Font font, Rectangle r, Color color, TextFormatFlags flags)
        {
            if (TextRenderer.MeasureText(g, text ?? "", font, new Size(int.MaxValue, r.Height), flags).Width > r.Width)
                flags |= TextFormatFlags.EndEllipsis;
            TextRenderer.DrawText(g, text, font, r, color, flags);
        }

        protected void DrawLead(Graphics g, string text, Font font, Rectangle r, Color color) { DrawFit(g, text, font, r, color, Lead); }

        protected TextFormatFlags Trail
        {
            get
            {
                return TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter |
                       (Rtl ? TextFormatFlags.Left | TextFormatFlags.RightToLeft : TextFormatFlags.Right);
            }
        }

        protected static readonly TextFormatFlags Center = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix |
                                                          TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Shapes.Behind(this));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Shapes.Round(new RectangleF(0, 0, Width - 1, Height - 1), 12))
            using (var b = new SolidBrush(Theme.Panel1)) g.FillPath(b, path);
            int noteW = string.IsNullOrEmpty(Note) ? 0 : TextRenderer.MeasureText(g, Note, Font).Width + 12;
            using (var f = Fonts.Semi(10f))
                DrawLead(g, Title, f, Rtl ? new Rectangle(Pad + noteW, 6, Width - 2 * Pad - noteW, TitleH - 6)
                                          : new Rectangle(Pad, 6, Width - 2 * Pad - noteW, TitleH - 6), Theme.TextC);
            if (noteW > 0)
                TextRenderer.DrawText(g, Note, Font, new Rectangle(Pad, 6, Width - 2 * Pad, TitleH - 6), Theme.Warn,
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | (Rtl ? TextFormatFlags.Left : TextFormatFlags.Right));
            var plot = new Rectangle(Pad, TitleH + 4, Width - 2 * Pad, Height - TitleH - 4 - Pad / 2 - 4);
            if (plot.Width < 20 || plot.Height < 20) return;
            if (IsEmpty)
            {
                TextRenderer.DrawText(g, EmptyText ?? Lang.T("No data yet", "ما فيه بيانات لسا"), Font, plot, Theme.Dim,
                                      Center | (Rtl ? TextFormatFlags.RightToLeft : 0));
                return;
            }
            Draw(g, plot);
        }

        // horizontal grid lines, each with its value on the axis side (left in English, right in Arabic)
        protected void YGrid(Graphics g, Rectangle area, double[] ticks, int axisW, Func<double, float> y)
        {
            using (var pen = new Pen(Color.FromArgb(48, Theme.Lavender)))
                foreach (var t in ticks)
                {
                    float py = y(t);
                    g.DrawLine(pen, area.Left, py, area.Right, py);
                    var r = Rtl ? new Rectangle(area.Right + 4, (int)py - 9, axisW - 4, 18) : new Rectangle(area.Left - axisW, (int)py - 9, axisW - 4, 18);
                    TextRenderer.DrawText(g, ChartScale.TickText(t), Font, r, Theme.Dim,
                        TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | (Rtl ? TextFormatFlags.Left : TextFormatFlags.Right));
                }
        }

        // the legend: coloured dots and names, on the side opposite the title
        protected void Legend(Graphics g, IList<string> names, IList<Color> colors)
        {
            int x = Rtl ? Pad : Width - Pad;
            for (int i = names.Count - 1; i >= 0; i--)
            {
                int w = TextRenderer.MeasureText(g, names[i], Font).Width;
                int item = w + 14;
                int left = Rtl ? x : x - item;
                using (var b = new SolidBrush(colors[i])) g.FillEllipse(b, left, 13, 8, 8);
                TextRenderer.DrawText(g, names[i], Font, new Point(left + 11, 9), Theme.Dim, TextFormatFlags.NoPrefix);
                x = Rtl ? x + item + 10 : x - item - 10;
            }
        }
    }

    // Daily numbers: one line per series, the first one filled under it. Hover shows the day's values.
    class LineChart : ChartBase
    {
        public class Series
        {
            public string Name;
            public Color Color;
            public double[] Values = new double[0];
        }

        public List<Series> Lines = new List<Series>();
        public string[] XLabels = new string[0];  // one per value (short dates)
        int hot = -1;
        Rectangle lastArea;

        protected override bool IsEmpty { get { return Lines.Count == 0 || Lines.All(s => s.Values.Length == 0 || s.Values.All(v => v == 0)); } }

        int Count { get { return Lines.Count == 0 ? 0 : Lines.Max(s => s.Values.Length); } }

        float X(int i, Rectangle a)
        {
            int n = Count;
            // time runs left to right in both languages (like the calendar on a phone's chart)
            return n <= 1 ? a.Left + a.Width / 2f : ChartScale.Map(i, 0, n - 1, a.Left, a.Right);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int n = Count, h = -1;
            if (n > 0 && lastArea.Width > 0 && e.X >= lastArea.Left - 6 && e.X <= lastArea.Right + 6)
                h = n == 1 ? 0 : Math.Max(0, Math.Min(n - 1, (int)Math.Round((e.X - lastArea.Left) * (n - 1.0) / lastArea.Width)));
            if (h != hot) { hot = h; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hot != -1) { hot = -1; Invalidate(); } }

        protected override void Draw(Graphics g, Rectangle plot)
        {
            Legend(g, Lines.Select(s => s.Name).ToList(), Lines.Select(s => s.Color).ToList());
            const int axisW = 40, xLabelH = 18;
            var a = Rtl ? new Rectangle(plot.Left + 4, plot.Top + 6, plot.Width - axisW - 8, plot.Height - xLabelH - 8)
                        : new Rectangle(plot.Left + axisW, plot.Top + 6, plot.Width - axisW - 8, plot.Height - xLabelH - 8);
            lastArea = a;
            double max = Lines.SelectMany(s => s.Values).DefaultIfEmpty(0).Max();
            var ticks = ChartScale.NiceTicks(0, max, Math.Max(2, Math.Min(5, a.Height / 28)), true);
            double top = ticks[ticks.Length - 1];
            Func<double, float> y = v => ChartScale.Map(v, 0, top, a.Bottom, a.Top);
            YGrid(g, a, ticks, axisW, y);

            int n = Count;
            // x labels: as many as fit, evenly spread, always the first and the last
            if (XLabels.Length == n && n > 0)
            {
                int every = Math.Max(1, (int)Math.Ceiling(n / Math.Max(1.0, a.Width / 64.0)));
                for (int i = 0; i < n; i++)
                {
                    if (i % every != 0 && i != n - 1) continue;
                    if (i != n - 1 && n - 1 - i < every && i != 0) continue; // keep room for the last one
                    float px = X(i, a);
                    TextRenderer.DrawText(g, XLabels[i], Font, new Rectangle((int)px - 30, a.Bottom + 3, 60, xLabelH), Theme.Dim, Center);
                }
            }

            for (int k = Lines.Count - 1; k >= 0; k--)
            {
                var s = Lines[k];
                if (s.Values.Length == 0) continue;
                var pts = new PointF[s.Values.Length];
                for (int i = 0; i < pts.Length; i++) pts[i] = new PointF(X(i, a), y(s.Values[i]));
                if (k == 0 && pts.Length > 1)
                {
                    var area = new List<PointF>(pts) { new PointF(pts[pts.Length - 1].X, a.Bottom), new PointF(pts[0].X, a.Bottom) };
                    using (var br = new LinearGradientBrush(new RectangleF(a.Left, a.Top - 1, a.Width, a.Height + 2),
                                                            Color.FromArgb(90, s.Color), Color.FromArgb(8, s.Color), 90f))
                        g.FillPolygon(br, area.ToArray());
                }
                using (var pen = new Pen(s.Color, 2f) { LineJoin = LineJoin.Round })
                    if (pts.Length > 1) g.DrawLines(pen, pts);
                if (pts.Length <= 31)
                    using (var b = new SolidBrush(s.Color))
                        foreach (var p in pts) g.FillEllipse(b, p.X - 2.5f, p.Y - 2.5f, 5, 5);
            }

            if (hot >= 0 && hot < n) Tooltip(g, a, hot, y);
        }

        void Tooltip(Graphics g, Rectangle a, int i, Func<double, float> y)
        {
            float px = X(i, a);
            using (var pen = new Pen(Color.FromArgb(120, Theme.Lavender)) { DashStyle = DashStyle.Dot }) g.DrawLine(pen, px, a.Top, px, a.Bottom);
            var lines = new List<string>();
            if (i < XLabels.Length) lines.Add(XLabels[i]);
            foreach (var s in Lines) if (i < s.Values.Length) lines.Add(s.Name + ": " + ChartScale.TickText(s.Values[i]));
            int w = lines.Max(l => TextRenderer.MeasureText(g, l, Font).Width) + 16, h = lines.Count * 17 + 8;
            float bx = px + 10 + w > a.Right ? px - 10 - w : px + 10;
            var box = new RectangleF(bx, a.Top + 4, w, h);
            using (var path = Shapes.Round(box, 6))
            {
                using (var b = new SolidBrush(Color.FromArgb(240, Theme.Panel2))) g.FillPath(b, path);
                using (var pen = new Pen(Color.FromArgb(90, Theme.Lavender))) g.DrawPath(pen, path);
            }
            for (int k = 0; k < lines.Count; k++)
                DrawLead(g, lines[k], Font, new Rectangle((int)box.X + 8, (int)box.Y + 4 + k * 17, w - 12, 17),
                         k == 0 ? Theme.Dim : Lines[k - 1].Color);
            foreach (var s in Lines)
                if (i < s.Values.Length)
                    using (var b = new SolidBrush(s.Color)) g.FillEllipse(b, px - 4, y(s.Values[i]) - 4, 8, 8);
        }
    }

    // Shares as horizontal bars: the name on the reading side, the bar, then the number.
    class BarList : ChartBase
    {
        public class Bar
        {
            public string Label, ValueText;
            public double Value;
            public Color Color = Theme.Accent;
        }

        public List<Bar> Bars = new List<Bar>();
        public double Max = double.NaN;   // NaN = the largest bar fills the row (100 for percentages)
        public int RowH = 22;
        public int LabelW = 130;
        public int ValueW = 64;

        protected override bool IsEmpty { get { return Bars.Count == 0; } }

        // the height that shows every row
        public int PreferredHeightFor(int rows) { return TitleH + 10 + Math.Max(1, rows) * RowH + Pad; }

        protected override void Draw(Graphics g, Rectangle plot)
        {
            double max = double.IsNaN(Max) ? Bars.Max(b => b.Value) : Max;
            if (max <= 0) max = 1;
            int labelW = Math.Min(LabelW, plot.Width / 2), valueW = ValueW;
            int rows = Math.Max(1, plot.Height / RowH);
            for (int i = 0; i < Bars.Count && i < rows; i++)
            {
                var b = Bars[i];
                int top = plot.Top + 2 + i * RowH;
                var lr = Rtl ? new Rectangle(plot.Right - labelW, top, labelW, RowH) : new Rectangle(plot.Left, top, labelW, RowH);
                DrawLead(g, b.Label, Font, lr, Theme.TextC);
                int trackW = plot.Width - labelW - valueW - 12;
                if (trackW < 10) continue;
                float w = (float)Math.Max(b.Value > 0 ? 3 : 0, Math.Min(1, b.Value / max) * trackW);
                float tx = Rtl ? plot.Right - labelW - 6 - trackW : plot.Left + labelW + 6;
                var track = new RectangleF(tx, top + 5, trackW, RowH - 10);
                using (var path = Shapes.Round(track, 4))
                using (var br = new SolidBrush(Color.FromArgb(40, Theme.Lavender))) g.FillPath(br, path);
                if (w > 0)
                {
                    var bar = Rtl ? new RectangleF(track.Right - w, track.Y, w, track.Height) : new RectangleF(track.X, track.Y, w, track.Height);
                    using (var path = Shapes.Round(bar, 4))
                    using (var br = new SolidBrush(b.Color)) g.FillPath(br, path);
                }
                var vr = Rtl ? new Rectangle(plot.Left, top, valueW, RowH) : new Rectangle(plot.Right - valueW, top, valueW, RowH);
                TextRenderer.DrawText(g, b.ValueText ?? "", Font, vr, Theme.Dim, Trail);
            }
        }
    }

    // Parts of a whole: a ring with the total in the middle and a legend beside it.
    class DonutChart : ChartBase
    {
        public class Slice
        {
            public string Label;
            public double Value;
            public Color Color;
        }

        public List<Slice> Slices = new List<Slice>();
        public string CenterText = "", CenterSub = "";

        protected override bool IsEmpty { get { return Slices.Sum(s => s.Value) <= 0; } }

        protected override void Draw(Graphics g, Rectangle plot)
        {
            double total = Slices.Sum(s => s.Value);
            int d = Math.Min(plot.Height - 4, Math.Max(60, plot.Width / 2 - 10));
            d = Math.Min(d, plot.Height - 4);
            var ring = Rtl ? new Rectangle(plot.Right - d, plot.Top + (plot.Height - d) / 2, d, d)
                           : new Rectangle(plot.Left, plot.Top + (plot.Height - d) / 2, d, d);
            float thick = Math.Max(10, d / 6f);
            var rr = new RectangleF(ring.X + thick / 2, ring.Y + thick / 2, ring.Width - thick, ring.Height - thick);
            float start = -90;
            foreach (var s in Slices)
            {
                if (s.Value <= 0) continue;
                float sweep = (float)(360.0 * s.Value / total);
                using (var pen = new Pen(s.Color, thick)) g.DrawArc(pen, rr, start, Math.Max(0.5f, sweep - (Slices.Count(x => x.Value > 0) > 1 ? 1.2f : 0)));
                start += sweep;
            }
            using (var f = Fonts.Semi(13f))
                TextRenderer.DrawText(g, CenterText, f, new Rectangle(ring.X, ring.Y + ring.Height / 2 - 20, ring.Width, 24), Theme.TextC, Center);
            TextRenderer.DrawText(g, CenterSub, Font, new Rectangle(ring.X, ring.Y + ring.Height / 2 + 4, ring.Width, 18), Theme.Dim,
                                  Center | (Rtl ? TextFormatFlags.RightToLeft : 0));

            // the legend
            int lx = Rtl ? plot.Left : ring.Right + 14, lw = plot.Width - d - 14;
            int rowH = 20, y0 = plot.Top + Math.Max(0, (plot.Height - Slices.Count * rowH) / 2);
            for (int i = 0; i < Slices.Count; i++)
            {
                var s = Slices[i];
                int y = y0 + i * rowH;
                if (y + rowH > plot.Bottom + 4) break;
                int dot = Rtl ? lx + lw - 10 : lx;
                using (var b = new SolidBrush(s.Color)) g.FillEllipse(b, dot, y + 6, 9, 9);
                var r = Rtl ? new Rectangle(lx, y, lw - 16, rowH) : new Rectangle(lx + 16, y, lw - 16, rowH);
                // the name on the reading side, the share and count at the far end (never mixed in one string)
                const int valueW = 84;
                var nr = Rtl ? new Rectangle(r.X + valueW, y, r.Width - valueW, rowH) : new Rectangle(r.X, y, r.Width - valueW, rowH);
                var vr = Rtl ? new Rectangle(r.X, y, valueW, rowH) : new Rectangle(r.Right - valueW, y, valueW, rowH);
                DrawLead(g, s.Label, Font, nr, Theme.TextC);
                TextRenderer.DrawText(g, DashFmt.Pct(DashMath.Percent((long)s.Value, (long)total)) + "  " + DashFmt.Count((long)s.Value),
                                      Font, vr, Theme.Dim, Trail);
            }
        }
    }

    // Two values per point (expected vs actual ping), with the "spot on" diagonal.
    class ScatterChart : ChartBase
    {
        public List<PointF> Points = new List<PointF>();
        public string XName = "", YName = "";
        public Color Dot = Theme.Lavender;

        protected override bool IsEmpty { get { return Points.Count == 0; } }

        protected override void Draw(Graphics g, Rectangle plot)
        {
            const int axisW = 40, xLabelH = 34;
            var a = Rtl ? new Rectangle(plot.Left + 4, plot.Top + 4, plot.Width - axisW - 8, plot.Height - xLabelH - 4)
                        : new Rectangle(plot.Left + axisW, plot.Top + 4, plot.Width - axisW - 8, plot.Height - xLabelH - 4);
            double max = Math.Max(Points.Max(p => p.X), Points.Max(p => p.Y));
            var ticks = ChartScale.NiceTicks(0, max, Math.Max(2, Math.Min(6, a.Height / 28)), true);
            double top = ticks[ticks.Length - 1];
            Func<double, float> y = v => ChartScale.Map(v, 0, top, a.Bottom, a.Top);
            Func<double, float> x = v => ChartScale.Map(v, 0, top, a.Left, a.Right);
            YGrid(g, a, ticks, axisW, y);
            foreach (var t in ticks)
                TextRenderer.DrawText(g, ChartScale.TickText(t), Font, new Rectangle((int)x(t) - 24, a.Bottom + 2, 48, 16), Theme.Dim, Center);
            TextRenderer.DrawText(g, XName, Font, new Rectangle(a.Left, a.Bottom + 17, a.Width, 16), Theme.Dim, Center | (Rtl ? TextFormatFlags.RightToLeft : 0));
            DrawLead(g, YName, Font, new Rectangle(a.Left + 6, a.Top, a.Width - 12, 16), Theme.Dim);
            using (var pen = new Pen(Color.FromArgb(150, Theme.Good), 1.5f) { DashStyle = DashStyle.Dash })
                g.DrawLine(pen, x(0), y(0), x(top), y(top));
            using (var b = new SolidBrush(Color.FromArgb(170, Dot)))
                foreach (var p in Points)
                {
                    // above the diagonal = slower than expected
                    var c = p.Y - p.X > 20 ? Color.FromArgb(200, Theme.Bad) : p.Y - p.X > 5 ? Color.FromArgb(200, Theme.Warn) : Color.FromArgb(190, Dot);
                    b.Color = c;
                    g.FillEllipse(b, x(p.X) - 3, y(p.Y) - 3, 6, 6);
                }
        }
    }

    // Counts per category as vertical columns (e.g. how far off the ping guess was).
    class ColumnChart : ChartBase
    {
        public string[] Labels = new string[0];
        public double[] Values = new double[0];
        public Color[] Colors;  // optional, one per column

        protected override bool IsEmpty { get { return Values.Length == 0 || Values.All(v => v <= 0); } }

        protected override void Draw(Graphics g, Rectangle plot)
        {
            const int axisW = 36, xLabelH = 20;
            var a = Rtl ? new Rectangle(plot.Left + 4, plot.Top + 14, plot.Width - axisW - 8, plot.Height - xLabelH - 14)
                        : new Rectangle(plot.Left + axisW, plot.Top + 14, plot.Width - axisW - 8, plot.Height - xLabelH - 14);
            var ticks = ChartScale.NiceTicks(0, Values.Max(), Math.Max(2, Math.Min(5, a.Height / 28)), true);
            double top = ticks[ticks.Length - 1];
            Func<double, float> y = v => ChartScale.Map(v, 0, top, a.Bottom, a.Top);
            YGrid(g, a, ticks, axisW, y);
            int n = Values.Length;
            float slot = a.Width / (float)n, bw = Math.Max(4, slot * 0.62f);
            for (int i = 0; i < n; i++)
            {
                // the columns keep their order left to right (a number line), in both languages
                float cx = a.Left + slot * (i + 0.5f);
                float top2 = y(Values[i]);
                var r = new RectangleF(cx - bw / 2, top2, bw, a.Bottom - top2);
                var color = Colors != null && i < Colors.Length ? Colors[i] : Theme.Accent;
                if (r.Height > 0)
                    using (var path = Shapes.Round(r, Math.Min(4, r.Height / 2)))
                    using (var b = new SolidBrush(color)) g.FillPath(b, path);
                if (Values[i] > 0)
                    TextRenderer.DrawText(g, ChartScale.TickText(Values[i]), Font, new Rectangle((int)(cx - slot / 2), (int)top2 - 16, (int)slot, 15), Theme.Dim, Center);
                if (i < Labels.Length)
                    TextRenderer.DrawText(g, "\u200E" + Labels[i], Font, new Rectangle((int)(cx - slot / 2), a.Bottom + 2, (int)slot, xLabelH), Theme.Dim, Center);
            }
        }
    }
}
