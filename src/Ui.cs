using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PingBoost
{
    // The app's building blocks: rounded cards, buttons that react to the mouse, and status chips with a dot.
    // Spacing follows an 8 px grid; corners are 12 px on cards and 8 px on buttons.
    static class Shapes
    {
        public static GraphicsPath Round(RectangleF r, float radius)
        {
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            var p = new GraphicsPath();
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Color Blend(Color a, Color b, float t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        // The colour behind a control (walks up through containers that don't paint their own).
        public static Color Behind(Control c)
        {
            for (var p = c.Parent; p != null; p = p.Parent)
                if (p.BackColor.A == 255) return p.BackColor;
            return MainForm.Bg;
        }
    }

    // A rounded card. Children inherit its fill as their background, so labels and lists blend in.
    class Card : Panel
    {
        Color fill = MainForm.Panel1;
        public int Radius = 12;
        public Color Border = Color.Empty;

        public Color Fill { get { return fill; } set { fill = value; BackColor = value; Invalidate(); } }

        public Card()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = fill;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Shapes.Behind(this));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0, 0, Width - 1, Height - 1);
            using (var path = Shapes.Round(r, Radius))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                if (Border != Color.Empty) using (var pen = new Pen(Border)) g.DrawPath(pen, path);
            }
        }
    }

    // A rounded button that lightens on hover and darkens when pressed, with a short fade between states.
    // Optional dot on the reading side (live status). Drop-in replacement for Button.
    class RoundButton : Button
    {
        public int Radius = 8;
        Color dot = Color.Empty;
        float hover, hoverTarget;
        bool pressed;
        readonly Timer anim = new Timer { Interval = 15 };

        public Color Dot { get { return dot; } set { dot = value; Invalidate(); } }

        public RoundButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            anim.Tick += delegate
            {
                hover += Math.Sign(hoverTarget - hover) * 0.2f;
                if (Math.Abs(hover - hoverTarget) < 0.2f) { hover = hoverTarget; anim.Stop(); }
                Invalidate();
            };
        }

        void Animate(float target) { hoverTarget = target; anim.Start(); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Animate(1); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); pressed = false; Animate(0); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); pressed = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressed = false; Invalidate(); }
        protected override void Dispose(bool disposing) { if (disposing) anim.Dispose(); base.Dispose(disposing); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var behind = Shapes.Behind(this);
            g.Clear(behind);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = BackColor, text = ForeColor;
            if (!Enabled) { fill = Shapes.Blend(BackColor, behind, 0.55f); text = Shapes.Blend(ForeColor, behind, 0.55f); }
            else if (pressed) fill = Shapes.Blend(BackColor, Color.Black, 0.18f);
            else fill = Shapes.Blend(BackColor, Color.White, 0.12f * hover);
            using (var path = Shapes.Round(new RectangleF(0, 0, Width - 1, Height - 1), Radius))
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            if (Focused && ShowFocusCues)
                using (var path = Shapes.Round(new RectangleF(1, 1, Width - 3, Height - 3), Radius))
                using (var pen = new Pen(Color.FromArgb(160, MainForm.Lavender), 1.5f)) g.DrawPath(pen, path);

            bool rtl = RightToLeft == RightToLeft.Yes;
            // in a mirrored (right-to-left layout) window Windows flips the drawing itself
            bool leadRight = rtl && !IsMirrored;
            var rc = new Rectangle(Padding.Left + 10, 0, Width - Padding.Horizontal - 20, Height);
            if (dot != Color.Empty)
            {
                const int d = 8;
                int dx = leadRight ? rc.Right - d : rc.X;
                using (var b = new SolidBrush(dot)) g.FillEllipse(b, dx, (Height - d) / 2f, d, d);
                rc = leadRight ? new Rectangle(rc.X, 0, rc.Width - d - 8, Height) : new Rectangle(rc.X + d + 8, 0, rc.Width - d - 8, Height);
            }
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            bool lead = TextAlign == ContentAlignment.MiddleLeft || dot != Color.Empty;
            flags |= lead ? (rtl ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left)
                          : TextFormatFlags.HorizontalCenter | (rtl ? TextFormatFlags.RightToLeft : 0);
            TextRenderer.DrawText(g, Text, Font, rc, text, flags);
        }
    }

    // A status chip: rounded background with a coloured dot (the label's ForeColor) before the text.
    class Chip : Label
    {
        public Color Fill = MainForm.Panel2;

        public Chip()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Shapes.Behind(this));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            bool rtl = RightToLeft == RightToLeft.Yes;
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
                        (rtl ? TextFormatFlags.RightToLeft : 0);
            var size = TextRenderer.MeasureText(g, Text, Font, new Size(int.MaxValue, Height), flags);
            int w = Math.Min(Width - 1, size.Width + 40), h = Math.Min(Height - 1, 34);
            // the chip hugs its text on the reading side of its slot
            int x = (TextAlign == ContentAlignment.MiddleRight) != rtl ? Width - 1 - w : 0;
            var box = new RectangleF(x, (Height - h) / 2f, w, h);
            using (var path = Shapes.Round(box, h / 2f))
            using (var b = new SolidBrush(Fill)) g.FillPath(b, path);
            const int d = 9;
            bool leadRight = rtl && !IsMirrored; // mirrored windows flip the drawing themselves
            float dx = leadRight ? box.Right - 14 - d : box.X + 14;
            using (var b = new SolidBrush(ForeColor)) g.FillEllipse(b, dx, box.Y + (h - d) / 2f, d, d);
            var rc = leadRight ? new Rectangle((int)box.X + 10, 0, (int)box.Width - 30 - d, Height)
                               : new Rectangle((int)box.X + 20 + d, 0, (int)box.Width - 30 - d, Height);
            TextRenderer.DrawText(g, Text, Font, rc, MainForm.TextC, flags | (rtl ? TextFormatFlags.Right : TextFormatFlags.Left));
        }
    }

    // Base for the hand-drawn controls: double-buffered, repaints on resize, knows its reading direction
    // and fades a hover value in and out.
    abstract class SoftControl : Control
    {
        protected float hover, hoverTarget;
        readonly Timer anim = new Timer { Interval = 15 };

        protected SoftControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Cursor = Cursors.Hand;
            TabStop = true;
            anim.Tick += delegate
            {
                hover += Math.Sign(hoverTarget - hover) * 0.2f;
                if (Math.Abs(hover - hoverTarget) < 0.2f) { hover = hoverTarget; anim.Stop(); }
                Invalidate();
            };
        }

        // the reading side is on the right in Arabic (mirrored windows flip the drawing themselves)
        protected bool LeadRight { get { return RightToLeft == RightToLeft.Yes && !IsMirrored; } }
        protected TextFormatFlags TextFlags
        {
            get
            {
                return TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
                       (RightToLeft == RightToLeft.Yes ? TextFormatFlags.RightToLeft | TextFormatFlags.Right : TextFormatFlags.Left);
            }
        }

        protected void Fade(float target) { hoverTarget = target; anim.Start(); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Fade(1); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Fade(0); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void Dispose(bool disposing) { if (disposing) anim.Dispose(); base.Dispose(disposing); }

        protected Graphics Begin(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Shapes.Behind(this));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            return g;
        }

        protected void FocusRing(Graphics g, RectangleF r, float radius)
        {
            if (!Focused || !ShowFocusCues) return;
            using (var path = Shapes.Round(r, radius))
            using (var pen = new Pen(Color.FromArgb(170, MainForm.Lavender), 1.5f)) g.DrawPath(pen, path);
        }
    }

    // An on/off switch with its label: the knob slides and the track turns pink when on.
    class Toggle : SoftControl
    {
        bool on;
        float pos;
        readonly Timer slide = new Timer { Interval = 15 };
        public event EventHandler CheckedChanged;

        public bool Checked
        {
            get { return on; }
            set
            {
                if (on == value) return;
                on = value;
                slide.Start();
                var h = CheckedChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        public Toggle()
        {
            Height = 32;
            slide.Tick += delegate
            {
                float target = on ? 1 : 0;
                pos += Math.Sign(target - pos) * 0.17f;
                if (Math.Abs(pos - target) < 0.17f) { pos = target; slide.Stop(); }
                Invalidate();
            };
            Click += delegate { Checked = !Checked; };
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) Checked = !Checked; };
        }

        public void SetInstant(bool value) { on = value; pos = value ? 1 : 0; Invalidate(); }

        protected override void Dispose(bool disposing) { if (disposing) slide.Dispose(); base.Dispose(disposing); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Begin(e);
            const int tw = 42, th = 24;
            float tx = LeadRight ? Width - tw - 2 : 2, ty = (Height - th) / 2f;
            var track = new RectangleF(tx, ty, tw, th);
            var off = Shapes.Blend(MainForm.Panel2, Color.White, 0.08f * hover);
            using (var path = Shapes.Round(track, th / 2f))
            using (var b = new SolidBrush(Shapes.Blend(off, MainForm.Accent, pos))) g.FillPath(b, path);
            float knob = th - 6;
            // the knob travels towards the reading direction's end when on
            float start = tx + 3, end = tx + tw - knob - 3;
            float kx = LeadRight ? end + (start - end) * pos : start + (end - start) * pos;
            using (var b = new SolidBrush(Shapes.Blend(MainForm.Dim, Color.White, pos))) g.FillEllipse(b, kx, ty + 3, knob, knob);
            FocusRing(g, new RectangleF(tx - 2, ty - 2, tw + 4, th + 4), th / 2f + 2);
            var rc = LeadRight ? new Rectangle(0, 0, Width - tw - 14, Height) : new Rectangle(tw + 12, 0, Width - tw - 14, Height);
            TextRenderer.DrawText(g, Text, Font, rc, ForeColor, TextFlags);
        }
    }

    // A segmented choice (e.g. Arabic | English): a rounded track with the chosen option lit up.
    class Segmented : SoftControl
    {
        string[] items = new string[0];
        int selected, hot = -1;
        public event EventHandler SelectedIndexChanged;

        public string[] Items { get { return items; } set { items = value ?? new string[0]; Invalidate(); } }

        public int SelectedIndex
        {
            get { return selected; }
            set
            {
                if (value == selected || value < 0 || value >= items.Length) return;
                selected = value;
                Invalidate();
                var h = SelectedIndexChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        public Segmented()
        {
            Height = 40;
            KeyDown += (s, e) =>
            {
                bool next = (e.KeyCode == Keys.Right) != (RightToLeft == RightToLeft.Yes);
                if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right)
                    SelectedIndex = Math.Max(0, Math.Min(items.Length - 1, selected + (next ? 1 : -1)));
            };
        }

        protected override bool IsInputKey(Keys keyData) { return keyData == Keys.Left || keyData == Keys.Right || base.IsInputKey(keyData); }

        RectangleF Segment(int i)
        {
            float w = (Width - 8f) / Math.Max(1, items.Length);
            int slot = LeadRight ? items.Length - 1 - i : i; // the first option on the reading side
            return new RectangleF(4 + slot * w, 4, w, Height - 8);
        }

        int At(Point p)
        {
            for (int i = 0; i < items.Length; i++) if (Segment(i).Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int h = At(e.Location); if (h != hot) { hot = h; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); int i = At(e.Location); if (i >= 0) SelectedIndex = i; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Begin(e);
            var all = new RectangleF(0, 0, Width - 1, Height - 1);
            using (var path = Shapes.Round(all, 12))
            using (var b = new SolidBrush(MainForm.Panel2)) g.FillPath(b, path);
            for (int i = 0; i < items.Length; i++)
            {
                var r = Segment(i);
                bool sel = i == selected;
                if (sel || i == hot)
                    using (var path = Shapes.Round(r, 9))
                    using (var b = new SolidBrush(sel ? MainForm.Accent : Color.FromArgb(62, 48, 88))) g.FillPath(b, path);
                using (var f = sel ? Fonts.Semi(Font.SizeInPoints / 0.92f) : null)
                    TextRenderer.DrawText(g, items[i], f ?? Font, Rectangle.Round(r), sel ? Color.Black : MainForm.TextC,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            FocusRing(g, new RectangleF(1, 1, Width - 3, Height - 3), 12);
        }
    }

    // A game you can switch on or off: its icon and name, with a round check on the far side. Lit when on.
    class GameTile : SoftControl
    {
        bool on;
        public Image Icon;
        public event EventHandler CheckedChanged;

        public bool Checked
        {
            get { return on; }
            set
            {
                if (on == value) return;
                on = value;
                Invalidate();
                var h = CheckedChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        public GameTile()
        {
            Height = 46;
            Click += delegate { Checked = !Checked; };
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Space) Checked = !Checked; };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Begin(e);
            var box = new RectangleF(0.5f, 0.5f, Width - 2, Height - 2);
            var fill = on ? Color.FromArgb(58, 38, 82) : MainForm.Panel1;
            fill = Shapes.Blend(fill, Color.White, 0.05f * hover);
            using (var path = Shapes.Round(box, 11))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                using (var pen = new Pen(on ? Color.FromArgb(200, MainForm.Accent) : Color.FromArgb(64, 52, 88), on ? 1.6f : 1f)) g.DrawPath(pen, path);
            }
            const int icon = 26, check = 20;
            float ix = LeadRight ? Width - 12 - icon : 12;
            if (Icon != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                using (var ia = new System.Drawing.Imaging.ImageAttributes())
                {
                    if (!on)
                    {
                        // switched-off games look quieter
                        var cm = new System.Drawing.Imaging.ColorMatrix { Matrix33 = 0.45f };
                        ia.SetColorMatrix(cm);
                    }
                    g.DrawImage(Icon, new Rectangle((int)ix, (Height - icon) / 2, icon, icon), 0, 0, Icon.Width, Icon.Height, GraphicsUnit.Pixel, ia);
                }
            }
            float cx = LeadRight ? 12 : Width - 12 - check, cy = (Height - check) / 2f;
            if (on)
            {
                using (var b = new SolidBrush(MainForm.Accent)) g.FillEllipse(b, cx, cy, check, check);
                using (var pen = new Pen(Color.Black, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    g.DrawLines(pen, new[] { new PointF(cx + 5.5f, cy + 10.5f), new PointF(cx + 8.8f, cy + 13.6f), new PointF(cx + 14.6f, cy + 7f) });
            }
            else using (var pen = new Pen(MainForm.Dim, 1.5f)) g.DrawEllipse(pen, cx + 0.75f, cy + 0.75f, check - 1.5f, check - 1.5f);
            FocusRing(g, new RectangleF(1, 1, Width - 3, Height - 3), 11);
            int textStart = 12 + icon + 10, textEnd = 12 + check + 10;
            var rc = LeadRight ? new Rectangle(textEnd, 0, Width - textStart - textEnd, Height) : new Rectangle(textStart, 0, Width - textStart - textEnd, Height);
            // game names are Latin: no right-to-left reordering (it moves the colon of "Arena Breakout: Infinite"),
            // just aligned to the reading side; a long name wraps to two lines instead of being cut
            var flags = TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis |
                        (RightToLeft == RightToLeft.Yes ? TextFormatFlags.Right : TextFormatFlags.Left);
            var color = on ? MainForm.TextC : MainForm.Dim;
            if (TextRenderer.MeasureText(g, Text, Font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width <= rc.Width)
                TextRenderer.DrawText(g, Text, Font, rc, color, flags | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            else
                using (var small = Fonts.Ui(Font.SizeInPoints / 0.92f * 0.9f))
                {
                    // still too long for two lines: drop the subtitle ("Arena Breakout: Infinite" -> "Arena Breakout")
                    string text = Text;
                    if (TextRenderer.MeasureText(g, text, small, new Size(rc.Width, 0), flags).Height > rc.Height && text.Contains(":"))
                        text = text.Substring(0, text.IndexOf(':'));
                    int h = Math.Min(rc.Height, TextRenderer.MeasureText(g, text, small, new Size(rc.Width, 0), flags).Height);
                    TextRenderer.DrawText(g, text, small, new Rectangle(rc.X, (Height - h) / 2, rc.Width, h), color, flags);
                }
        }
    }

    // A text field in a rounded frame that lights up while typing.
    class InputBox : Card
    {
        public readonly TextBox Box;
        static readonly Color Rest = Color.FromArgb(72, 60, 100);

        public InputBox()
        {
            Fill = MainForm.Panel2;
            Radius = 10;
            Border = Rest;
            Height = 42;
            Padding = new Padding(14, 11, 14, 8);
            Box = new TextBox { BorderStyle = BorderStyle.None, BackColor = MainForm.Panel2, ForeColor = MainForm.TextC, Dock = DockStyle.Fill };
            Box.GotFocus += delegate { Border = MainForm.Accent; Invalidate(); };
            Box.LostFocus += delegate { Border = Rest; Invalidate(); };
            Click += delegate { Box.Focus(); };
            Cursor = Cursors.IBeam;
            Controls.Add(Box);
        }

        public override string Text { get { return Box.Text; } set { Box.Text = value; } }
    }
}
