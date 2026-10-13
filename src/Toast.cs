using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PingBoost
{
    enum ToastKind { Info, Good, Warn, Bad }

    // Notices as game-chat lines: they stack at the bottom of the window, fade out by themselves and never take
    // the focus or need an OK. Hovering one keeps it; clicking it dismisses it.
    class ChatToasts
    {
        const int MaxShown = 5, Gap = 6, MaxWidth = 540;
        readonly Form owner;
        readonly Func<Rectangle> area; // screen rectangle the lines stack in (bottom-aligned)
        readonly List<ToastForm> live = new List<ToastForm>();

        public ChatToasts(Form owner, Func<Rectangle> area)
        {
            this.owner = owner;
            this.area = area;
            owner.Move += delegate { Reflow(); };
            owner.Resize += delegate { Reflow(); };
        }

        public void Show(string text, ToastKind kind)
        {
            if (owner.IsDisposed || string.IsNullOrEmpty(text)) return;
            var a = area();
            var t = new ToastForm(text.Trim(), kind, Math.Min(MaxWidth, Math.Max(240, a.Width - 32)));
            t.FormClosed += delegate { live.Remove(t); Reflow(); };
            live.Add(t);
            // too many lines: the oldest leaves quickly
            for (int i = 0; i < live.Count - MaxShown; i++) live[i].Dismiss();
            Reflow();
            t.Show(owner);
        }

        void Reflow()
        {
            if (live.Count == 0) return;
            var a = area();
            int y = a.Bottom - 12;
            for (int i = live.Count - 1; i >= 0; i--)
            {
                var t = live[i];
                y -= t.Height;
                t.Location = new Point(Lang.Ar ? a.Right - 16 - t.Width : a.Left + 16, y);
                y -= Gap;
            }
        }

        class ToastForm : Form
        {
            [DllImport("dwmapi.dll")]
            static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

            readonly string text;
            readonly Color bar;
            readonly Timer timer = new Timer { Interval = 30 };
            int stayMs;
            int phase; // 0 fade in, 1 stay, 2 fade out
            const double Full = 0.93;

            public ToastForm(string text, ToastKind kind, int width)
            {
                this.text = text;
                bar = kind == ToastKind.Good ? Theme.Good : kind == ToastKind.Warn ? Theme.Warn : kind == ToastKind.Bad ? Theme.Bad : Theme.Lavender;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                BackColor = Color.FromArgb(14, 10, 24);
                Font = Fonts.Ui(10f);
                Opacity = 0;
                Cursor = Cursors.Hand;
                DoubleBuffered = true;
                var size = TextRenderer.MeasureText(text, Font, new Size(width - 34, 1000), Flags);
                ClientSize = new Size(Math.Min(width, size.Width + 34), size.Height + 18);
                // long lines stay longer: about the time it takes to read them
                stayMs = Math.Min(14000, 3500 + text.Length * 55);
                timer.Tick += delegate { Step(); };
                Click += delegate { Dismiss(); };
            }

            static TextFormatFlags Flags
            {
                get { return TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | (Lang.Ar ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left); }
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.ExStyle |= 0x08000000 | 0x80; // no activation, no taskbar / Alt+Tab entry
                    return cp;
                }
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                int round = 2; // Windows 11 rounded corners (ignored on Windows 10)
                try { DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }
            }

            protected override void OnShown(EventArgs e)
            {
                base.OnShown(e);
                timer.Start();
            }

            public void Dismiss()
            {
                if (phase < 2) { phase = 2; if (!timer.Enabled) timer.Start(); }
            }

            void Step()
            {
                if (phase == 0)
                {
                    Opacity = Math.Min(Full, Opacity + 0.15);
                    if (Opacity >= Full) phase = 1;
                }
                else if (phase == 1)
                {
                    // hovering keeps the line on screen
                    if (!Bounds.Contains(Cursor.Position)) stayMs -= timer.Interval;
                    if (stayMs <= 0) phase = 2;
                }
                else
                {
                    Opacity -= 0.06;
                    if (Opacity <= 0.02) { timer.Stop(); Close(); }
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                using (var b = new SolidBrush(bar))
                    e.Graphics.FillRectangle(b, Lang.Ar ? ClientSize.Width - 4 : 0, 0, 4, ClientSize.Height);
                var rc = new Rectangle(Lang.Ar ? 12 : 18, 9, ClientSize.Width - 30, ClientSize.Height - 18);
                TextRenderer.DrawText(e.Graphics, text, Font, rc, Theme.TextC, Flags);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) timer.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
