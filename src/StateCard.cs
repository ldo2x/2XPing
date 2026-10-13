using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace PingBoost
{
    // The main screen's card: says the situation in plain words (CardView from CardLogic.Decide), offers region tiles
    // to choose from (the featured two or three, or every region in rows of three after "Other regions"), one main
    // button and two small links. Everything is centred; in Arabic the first tile and the "Other regions" link sit on
    // the right.
    class StateCard : Card
    {
        const int Pad = 24, TileGap = 14, RowGap = 12, PerRow = 3, TileMaxW = 236, LinkGap = 28;

        CardView view;
        readonly List<RegionTile> tiles = new List<RegionTile>();
        readonly RoundButton button;
        readonly CardLink other, manual;
        readonly Font titleFont = Fonts.Semi(16f), subFont = Fonts.Ui(10f);
        readonly Timer dots = new Timer { Interval = 380 };
        int dotPhase;
        bool laying, hasButton, hasLinks, showTiles, otherAvailable = true, manualAvailable = true; // Visible reads false until the window shows, so the layout keeps its own flags
        Rectangle titleRect, subRect, dotsRect;

        public event EventHandler ButtonPressed, OtherRegions, Manual, SelectionChanged;

        public StateCard()
        {
            Radius = 16;
            Fill = MainForm.Panel1;
            button = new RoundButton
            {
                BackColor = MainForm.Accent, ForeColor = Color.Black, Font = Fonts.Semi(11f), Radius = 10, Height = 40, Visible = false
            };
            button.Click += delegate { Raise(ButtonPressed); };
            other = new CardLink { Text = Lang.T("Other regions", "مناطق ثانية"), Visible = false };
            other.Click += delegate { Raise(OtherRegions); };
            manual = new CardLink { Text = Lang.T("Pick by hand", "اختيار يدوي"), Visible = false };
            manual.Click += delegate { Raise(Manual); };
            Controls.Add(button);
            Controls.Add(other);
            Controls.Add(manual);
            dots.Tick += delegate { dotPhase = (dotPhase + 1) % 3; Invalidate(dotsRect); };
        }

        public CardView View { get { return view; } }

        // "Other regions" only shows when there are more regions than the featured ones (once open it reads "Fewer
        // regions"); "Pick by hand" only where the server list is hidden.
        public void SetLinks(bool otherRegions, bool expanded, bool pickByHand)
        {
            otherAvailable = otherRegions;
            manualAvailable = pickByHand;
            other.Text = expanded ? Lang.T("Fewer regions", "مناطق أقل") : Lang.T("Other regions", "مناطق ثانية");
            other.Visible = hasLinks && otherAvailable;
            manual.Visible = hasLinks && manualAvailable;
        }

        // Shows a state. choices are the tiles to show, in rows of three (their Selected flags are the selection); the
        // same list shown again keeps its tiles, so re-deciding after a click doesn't flicker.
        public void Show(CardView v, List<RegionChoice> choices)
        {
            view = v;
            var list = (choices ?? new List<RegionChoice>()).ToList();
            bool same = list.Count == tiles.Count && list.Select((c, i) => tiles[i].Choice == c).All(x => x);
            if (!same)
            {
                foreach (var t in tiles) { Controls.Remove(t); t.Dispose(); }
                tiles.Clear();
                foreach (var c in list)
                {
                    var t = new RegionTile { Choice = c };
                    t.Toggled += delegate { Raise(SelectionChanged); };
                    tiles.Add(t);
                    Controls.Add(t);
                }
            }
            showTiles = v != null && v.ShowTiles && tiles.Count > 0;
            foreach (var t in tiles) { t.Visible = showTiles; t.Invalidate(); }

            hasButton = v != null && v.Button != null;
            if (hasButton) button.Text = v.Button;
            // once locked, "Unlock" is a quiet secondary action; pink stays for what we want the player to press
            bool secondary = v != null && (v.Kind == CardKind.Locked || v.Kind == CardKind.InMatchOk || v.Kind == CardKind.InMatchRelay || v.Kind == CardKind.Outside);
            button.BackColor = secondary ? MainForm.Panel2 : MainForm.Accent;
            button.ForeColor = secondary ? MainForm.TextC : Color.Black;
            button.Visible = hasButton;
            hasLinks = v != null && v.ShowLinks;
            other.Visible = hasLinks && otherAvailable;
            manual.Visible = hasLinks && manualAvailable;

            var accent = v != null ? v.Accent : MainForm.Accent;
            Border = accent == MainForm.Good || accent == MainForm.Warn ? Shapes.Blend(accent, MainForm.Panel1, 0.6f) : Color.Empty;

            if (Waiting) dots.Start(); else dots.Stop();
            PerformLayout();
            Invalidate();
        }

        // Loading and searching show three soft dots instead of a sub-line.
        bool Waiting
        {
            get { return view != null && (view.Kind == CardKind.Loading || view.Kind == CardKind.Testing) && view.Button == null && view.Sub == null; }
        }

        bool Rtl { get { return RightToLeft == RightToLeft.Yes; } }
        bool LeadRight { get { return Rtl && !IsMirrored; } }

        TextFormatFlags Flags
        {
            get { return TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | (Rtl ? TextFormatFlags.RightToLeft : 0); }
        }

        Color TitleColor
        {
            get
            {
                if (view == null) return MainForm.TextC;
                if (view.Accent == MainForm.Good) return MainForm.Good;
                if (view.Accent == MainForm.Warn) return MainForm.Warn;
                return MainForm.TextC;
            }
        }

        void Raise(EventHandler h) { if (h != null) h(this, EventArgs.Empty); }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (laying || view == null) return;
            laying = true;
            try { Arrange(); }
            finally { laying = false; }
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); PerformLayout(); Invalidate(); }

        // The tiles in rows of three, in order.
        List<List<RegionTile>> Rows()
        {
            var rows = new List<List<RegionTile>>();
            if (!showTiles) return rows;
            for (int i = 0; i < tiles.Count; i += PerRow) rows.Add(tiles.Skip(i).Take(PerRow).ToList());
            return rows;
        }

        // Height of the tile rows: each row as tall as its tallest needs, a gap between rows.
        int TilesHeight(List<List<RegionTile>> rows)
        {
            int h = 0;
            foreach (var row in rows) h += (h > 0 ? RowGap : 0) + RegionTile.HeightFor(row.Select(t => t.Choice));
            return h;
        }

        int ContentHeight(int w, out int titleH, out int subH, out int tileH)
        {
            titleH = Measure(view.Title, titleFont, w);
            subH = view.Sub != null ? Measure(view.Sub, subFont, w) : Waiting ? 14 : 0;
            tileH = TilesHeight(Rows());
            bool actions = hasButton || hasLinks;
            return titleH + (subH > 0 ? 6 + subH : 0) + (tileH > 0 ? 16 + tileH : 0) + (actions ? 18 + button.Height : 0);
        }

        // The height the card needs at this width to show everything with its normal margins.
        public int PreferredHeight(int width)
        {
            if (view == null) return 120;
            int titleH, subH, tileH;
            return ContentHeight(Math.Max(40, width - 2 * Pad), out titleH, out subH, out tileH) + 2 * (Pad - 4);
        }

        void Arrange()
        {
            int w = Math.Max(40, ClientSize.Width - 2 * Pad);
            var rows = Rows();
            int titleH, subH, tileH;
            int total = ContentHeight(w, out titleH, out subH, out tileH);
            bool actions = hasButton || hasLinks;
            int y = Math.Max(Pad - 6, (ClientSize.Height - total) / 2);

            titleRect = new Rectangle(Pad, y, w, titleH);
            y += titleH;
            subRect = dotsRect = Rectangle.Empty;
            if (subH > 0)
            {
                y += 6;
                if (view.Sub != null) subRect = new Rectangle(Pad, y, w, subH);
                else dotsRect = new Rectangle(ClientSize.Width / 2 - 24, y, 48, subH);
                y += subH;
            }

            if (tileH > 0)
            {
                y += 16;
                int cols = rows[0].Count; // every row uses the first row's tile width, a short last row is centred
                int tw = Math.Min(TileMaxW, (w - TileGap * (cols - 1)) / cols);
                for (int r = 0; r < rows.Count; r++)
                {
                    var row = rows[r];
                    int n = row.Count, rowH = RegionTile.HeightFor(row.Select(t => t.Choice));
                    int x0 = (ClientSize.Width - (n * tw + (n - 1) * TileGap)) / 2;
                    if (r > 0) y += RowGap;
                    for (int i = 0; i < n; i++)
                    {
                        int slot = LeadRight ? n - 1 - i : i; // the first tile on the reading side
                        row[i].Bounds = new Rectangle(x0 + slot * (tw + TileGap), y, tw, rowH);
                    }
                    y += rowH;
                }
            }

            if (actions)
            {
                y += 18;
                int ow = LinkWidth(other), mw = LinkWidth(manual);
                int side = hasLinks ? Math.Max(ow, mw) : 0;
                if (hasButton)
                {
                    int bw = TextRenderer.MeasureText(button.Text, button.Font).Width + 64;
                    int room = w - (hasLinks ? 2 * (side + LinkGap) : 0);
                    bw = Math.Max(Math.Min(200, room), Math.Min(bw, room));
                    int bx = (ClientSize.Width - bw) / 2;
                    button.Bounds = new Rectangle(bx, y, bw, button.Height);
                    if (hasLinks)
                    {
                        // "Other regions" before the button in reading order, "Pick by hand" after it
                        var before = new Rectangle(bx - LinkGap - ow, y, ow, button.Height);
                        var after = new Rectangle(bx + bw + LinkGap, y, mw, button.Height);
                        other.Bounds = LeadRight ? new Rectangle(bx + bw + LinkGap, y, ow, button.Height) : before;
                        manual.Bounds = LeadRight ? new Rectangle(bx - LinkGap - mw, y, mw, button.Height) : after;
                    }
                }
                else if (hasLinks)
                {
                    int cx = ClientSize.Width / 2;
                    // only one of the two links: it sits in the middle
                    if (!otherAvailable) manual.Bounds = new Rectangle(cx - mw / 2, y, mw, button.Height);
                    if (!manualAvailable) other.Bounds = new Rectangle(cx - ow / 2, y, ow, button.Height);
                    if (!otherAvailable || !manualAvailable) return;
                    var near = new Rectangle(cx - LinkGap / 2 - ow, y, ow, button.Height);
                    other.Bounds = LeadRight ? new Rectangle(cx + LinkGap / 2, y, ow, button.Height) : near;
                    manual.Bounds = LeadRight ? new Rectangle(cx - LinkGap / 2 - mw, y, mw, button.Height) : new Rectangle(cx + LinkGap / 2, y, mw, button.Height);
                }
            }
        }

        int Measure(string text, Font f, int w)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return TextRenderer.MeasureText(text, f, new Size(w, int.MaxValue), Flags).Height;
        }

        static int LinkWidth(CardLink l) { return TextRenderer.MeasureText(l.Text, l.Font).Width + 16; }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (view == null) return;
            var g = e.Graphics;
            TextRenderer.DrawText(g, view.Title, titleFont, titleRect, TitleColor, Flags);
            if (view.Sub != null) TextRenderer.DrawText(g, view.Sub, subFont, subRect, MainForm.Dim, Flags);
            if (dotsRect != Rectangle.Empty)
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                const int d = 7, gap = 9;
                float x = dotsRect.X + (dotsRect.Width - (3 * d + 2 * gap)) / 2f, y = dotsRect.Y + (dotsRect.Height - d) / 2f;
                for (int i = 0; i < 3; i++)
                {
                    int k = LeadRight ? 2 - i : i; // the pulse runs in the reading direction
                    var c = k == dotPhase ? MainForm.Accent : Shapes.Blend(MainForm.Dim, MainForm.Panel1, 0.45f);
                    using (var b = new SolidBrush(c)) g.FillEllipse(b, x + i * (d + gap), y, d, d);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { dots.Dispose(); titleFont.Dispose(); subFont.Dispose(); }
            base.Dispose(disposing);
        }
    }

    // One region to play in: name, a tag on the top edge ("Best choice"), the ping and, when known, the queue wait
    // and how busy it is. Click to switch it on or off; several can be on.
    class RegionTile : SoftControl
    {
        const int BadgeH = 22, Lip = BadgeH / 2, In = 14, CheckD = 18;
        static readonly Font nameFont = Fonts.Semi(12f), lineFont = Fonts.Ui(9.5f), pingFont = Fonts.Medium(10f), badgeFont = Fonts.Semi(8.5f);

        RegionChoice choice;
        public event EventHandler Toggled;

        public RegionChoice Choice { get { return choice; } set { choice = value; Invalidate(); } }

        public RegionTile()
        {
            Click += delegate { Toggle(); };
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) Toggle(); };
        }

        void Toggle()
        {
            if (choice == null) return;
            Focus();
            choice.Selected = !choice.Selected;
            Invalidate();
            var h = Toggled;
            if (h != null) h(this, EventArgs.Empty);
        }

        static bool HasQueue(RegionChoice c) { return c.Matches >= 0; }
        static bool HasPlayers(RegionChoice c) { return c.Players >= 0; }

        static int NameH { get { return nameFont.Height + 2; } }
        static int LineH { get { return lineFont.Height + 3; } }

        // Every tile in a row gets the same height: room for the wait and players lines when any tile has them.
        public static int HeightFor(IEnumerable<RegionChoice> row)
        {
            var list = row.ToList();
            int lines = 1 + (list.Any(HasQueue) ? 1 : 0) + (list.Any(HasPlayers) ? 1 : 0);
            return Lip + 16 + NameH + 6 + lines * LineH + 14;
        }

        string TagText()
        {
            if (choice.Tag == "best") return Lang.T("Best choice", "أحسن خيار");
            if (choice.Tag == "ping") return Lang.T("Lowest ping", "أقل بنق");
            if (choice.Tag == "queue") return Lang.T("Fastest queue", "أسرع قيم");
            return null;
        }

        static Color PingColor(double ms)
        {
            if (ms >= double.MaxValue) return MainForm.Dim;
            if (ms < 60) return MainForm.Good;
            return ms < 120 ? MainForm.Warn : MainForm.Bad;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Begin(e);
            if (choice == null) return;
            bool on = choice.Selected;
            var box = new RectangleF(0.75f, Lip + 0.75f, Width - 2.5f, Height - Lip - 2.5f);
            var rest = Shapes.Blend(MainForm.Panel1, MainForm.Panel2, 0.55f);
            var fill = Shapes.Blend(on ? Color.FromArgb(62, 38, 86) : rest, Color.White, 0.05f * hover);
            using (var path = Shapes.Round(box, 12))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                var edge = on ? MainForm.Accent : Shapes.Blend(Color.FromArgb(70, 56, 98), MainForm.Lavender, 0.25f * hover);
                using (var pen = new Pen(edge, on ? 1.8f : 1f)) g.DrawPath(pen, path);
            }
            FocusRing(g, new RectangleF(box.X + 2, box.Y + 2, box.Width - 4, box.Height - 4), 10);

            // the round check on the reading side, the name next to it
            float cy = Lip + 16 + (NameH - CheckD) / 2f;
            float cx = LeadRight ? Width - In - CheckD : In;
            if (on)
            {
                using (var b = new SolidBrush(MainForm.Accent)) g.FillEllipse(b, cx, cy, CheckD, CheckD);
                using (var pen = new Pen(Color.Black, 2f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
                    g.DrawLines(pen, new[] { new PointF(cx + 5f, cy + 9.5f), new PointF(cx + 8f, cy + 12.4f), new PointF(cx + 13.2f, cy + 6.4f) });
            }
            else using (var pen = new Pen(MainForm.Dim, 1.5f)) g.DrawEllipse(pen, cx + 0.75f, cy + 0.75f, CheckD - 1.5f, CheckD - 1.5f);

            int textX = In + CheckD + 10, textW = Width - textX - In;
            int y = Lip + 16;
            TextRenderer.DrawText(g, choice.Name, nameFont, Row(textX, y, textW, NameH), on ? MainForm.TextC : Shapes.Blend(MainForm.TextC, MainForm.Dim, 0.35f), TextFlags);
            y += NameH + 6;

            bool known = choice.Ping < double.MaxValue;
            string ping = known ? Lang.T("Ping ", "بنق ") + (int)Math.Round(choice.Ping) : Lang.T("One moment...", "لحظة...");
            TextRenderer.DrawText(g, ping, known ? pingFont : lineFont, Row(textX, y, textW, LineH), PingColor(choice.Ping), TextFlags);
            y += LineH;
            if (HasQueue(choice))
            {
                TextRenderer.DrawText(g, Lang.T("Wait ", "الانتظار ") + Stats.EstimateWait(choice.Matches), lineFont, Row(textX, y, textW, LineH), MainForm.Dim, TextFlags);
                y += LineH;
            }
            if (HasPlayers(choice))
                TextRenderer.DrawText(g, RegionGroups.Busy(choice.Players) + " · " + Stats.Approx(choice.Players), lineFont, Row(textX, y, textW, LineH), MainForm.Dim, TextFlags);

            // the tag sits on the top edge, over the check
            string tag = TagText();
            if (tag != null)
            {
                int tw = TextRenderer.MeasureText(tag, badgeFont).Width + 18;
                float bx = LeadRight ? Width - In + 4 - tw : In - 4;
                var pill = new RectangleF(bx, 0, tw, BadgeH);
                Color pf = choice.Tag == "best" ? MainForm.Accent : choice.Tag == "ping" ? MainForm.Good : MainForm.Lavender;
                using (var path = Shapes.Round(pill, BadgeH / 2f))
                using (var b = new SolidBrush(pf)) g.FillPath(b, path);
                TextRenderer.DrawText(g, tag, badgeFont, Rectangle.Round(pill), Color.Black,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix |
                    (RightToLeft == RightToLeft.Yes ? TextFormatFlags.RightToLeft : 0));
            }
        }

        // a text row on the reading side of the tile
        Rectangle Row(int x, int y, int w, int h) { return LeadRight ? new Rectangle(Width - x - w, y, w, h) : new Rectangle(x, y, w, h); }
    }

    // A small lavender text link that underlines on hover; Enter or Space presses it.
    class CardLink : SoftControl
    {
        public CardLink()
        {
            Font = Fonts.Ui(10f);
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) OnClick(EventArgs.Empty); };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Begin(e);
            var color = Shapes.Blend(MainForm.Lavender, Color.White, 0.3f * hover);
            var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix |
                        (RightToLeft == RightToLeft.Yes ? TextFormatFlags.RightToLeft : 0);
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, color, flags);
            var size = TextRenderer.MeasureText(g, Text, Font, Size.Empty, flags);
            if (hover > 0)
                using (var pen = new Pen(Color.FromArgb((int)(200 * hover), color)))
                {
                    float y = (Height + size.Height) / 2f - 2, x = (Width - size.Width) / 2f + 6;
                    g.DrawLine(pen, x, y, x + size.Width - 12, y);
                }
            FocusRing(g, new RectangleF(1, 6, Width - 3, Height - 13), 8);
        }
    }
}
