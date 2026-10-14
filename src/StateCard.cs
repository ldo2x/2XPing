using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace PingBoost
{
    // The main screen's card: says the situation in plain words (CardView from CardLogic.Decide), offers region tiles
    // to choose from (the featured two or three, or every region in rows of three after "Other regions") with an
    // optional footnote under them, one main button and up to three small links. Everything is centred; in Arabic the first tile and the "Other regions" link
    // sit on the right. The links flank the button, or sit in a row under it where "Test ping" is offered.
    class StateCard : Card
    {
        const int Pad = 24, TileGap = 14, RowGap = 12, PerRow = 3, TileMaxW = 236, LinkGap = 28;

        CardView view;
        readonly List<RegionTile> tiles = new List<RegionTile>();
        readonly RoundButton button;
        readonly CardLink other, manual, test, hint;
        readonly Font titleFont = Fonts.Semi(16f), subFont = Fonts.Ui(10f), noteFont = Fonts.Ui(9f);
        readonly Timer dots = new Timer { Interval = 380 };
        int dotPhase;
        bool hintOpen; // the hint link was pressed: its steps show under it
        bool laying, hasButton, hasLinks, hasTest, showTiles, otherAvailable = true, manualAvailable = true, testAvailable = true; // Visible reads false until the window shows, so the layout keeps its own flags
        Rectangle titleRect, subRect, dotsRect, noteRect, stepsRect;

        // HintToggled: the hint's steps opened or closed, so the card wants a different height
        public event EventHandler ButtonPressed, OtherRegions, Manual, TestPing, SelectionChanged, HintToggled;

        public StateCard()
        {
            Radius = 16;
            Fill = Theme.Panel1;
            button = new RoundButton
            {
                BackColor = Theme.Accent, ForeColor = Color.Black, Font = Fonts.Semi(11f), Radius = 10, Height = 40, Visible = false
            };
            button.Click += delegate { Raise(ButtonPressed); };
            other = new CardLink { Text = Lang.T("Other regions", "مناطق ثانية"), Visible = false };
            other.Click += delegate { Raise(OtherRegions); };
            manual = new CardLink { Text = Lang.T("Pick by hand", "اختيار يدوي"), Visible = false };
            manual.Click += delegate { Raise(Manual); };
            test = new CardLink { Text = Lang.T("Test ping", "افحص البنق"), Visible = false };
            test.Click += delegate { Raise(TestPing); };
            hint = new CardLink { Font = Fonts.Ui(9f), Visible = false };
            hint.Click += delegate
            {
                hintOpen = !hintOpen;
                PerformLayout();
                Invalidate();
                Raise(HintToggled);
            };
            Controls.Add(button);
            Controls.Add(hint);
            Controls.Add(other);
            Controls.Add(test);
            Controls.Add(manual);
            dots.Tick += delegate { dotPhase = (dotPhase + 1) % 3; Invalidate(dotsRect); };
        }

        public CardView View { get { return view; } }

        // "Other regions" only shows when there are more regions than the featured ones (once open it reads "Fewer
        // regions"); "Pick by hand" and "Test ping" only where the server list and the test button are hidden.
        public void SetLinks(bool otherRegions, bool expanded, bool pickByHand, bool testPing)
        {
            otherAvailable = otherRegions;
            manualAvailable = pickByHand;
            testAvailable = testPing;
            other.Text = expanded ? Lang.T("Fewer regions", "مناطق أقل") : Lang.T("Other regions", "مناطق ثانية");
            other.Visible = hasLinks && otherAvailable;
            manual.Visible = hasLinks && manualAvailable;
            hasTest = view != null && view.ShowTest && testAvailable;
            test.Visible = hasTest;
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
            button.BackColor = secondary ? Theme.Panel2 : Theme.Accent;
            button.ForeColor = secondary ? Theme.TextC : Color.Black;
            button.Visible = hasButton;
            hasLinks = v != null && v.ShowLinks;
            other.Visible = hasLinks && otherAvailable;
            manual.Visible = hasLinks && manualAvailable;
            hasTest = v != null && v.ShowTest && testAvailable;
            test.Visible = hasTest;
            bool link = v != null && v.HintLink != null;
            if (!link) hintOpen = false; // a link shown again later starts closed
            hint.Visible = link;
            if (link) hint.Text = v.HintLink;

            var accent = v != null ? v.Accent : Theme.Accent;
            Border = accent == Theme.Good || accent == Theme.Warn ? Shapes.Blend(accent, Theme.Panel1, 0.6f) : Color.Empty;

            if (Waiting) dots.Start(); else dots.Stop();
            PerformLayout();
            Invalidate();
        }

        // Loading, searching and locking show three soft dots instead of a sub-line.
        bool Waiting
        {
            get { return view != null && (view.Kind == CardKind.Loading || view.Kind == CardKind.Testing || view.Kind == CardKind.Locking) && view.Button == null && view.Sub == null; }
        }

        // The links shown, in reading order.
        List<CardLink> Links()
        {
            var list = new List<CardLink>();
            if (hasLinks && otherAvailable) list.Add(other);
            if (hasTest) list.Add(test);
            if (hasLinks && manualAvailable) list.Add(manual);
            return list;
        }

        // Where "Test ping" is offered (the simple layout) the links sit in a row under the button, also while a test
        // runs and the link is away, so the card doesn't jump; otherwise up to two links flank the button.
        bool LinksBelow { get { return hasButton && hasLinks && testAvailable && Links().Count > 0; } }

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
                if (view == null) return Theme.TextC;
                if (view.Accent == Theme.Good) return Theme.Good;
                if (view.Accent == Theme.Warn) return Theme.Warn;
                return Theme.TextC;
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
            titleH = Measure(view.Title, titleFont, w) + PingLineH(w);
            subH = view.Sub != null ? Measure(view.Sub, subFont, w) : Waiting ? 14 : 0;
            tileH = TilesHeight(Rows());
            bool actions = hasButton || Links().Count > 0;
            int noteH = NoteHeight(w, tileH);
            return titleH + (subH > 0 ? 6 + subH : 0) + (tileH > 0 ? 16 + tileH : 0) + (noteH > 0 ? 10 + noteH : 0) +
                   (actions ? 18 + button.Height : 0) + (LinksBelow ? button.Height : 0) + HintHeight(w);
        }

        // The "-condebug" hint at the bottom: a small link (its steps under it once pressed), or the steps alone.
        bool HintLinkShown { get { return view != null && view.HintLink != null; } }
        bool StepsShown { get { return view != null && view.HintSteps != null && (!HintLinkShown || hintOpen); } }
        int HintLinkH { get { return hint.Font.Height + 12; } }

        int HintHeight(int w)
        {
            int h = 0;
            if (HintLinkShown) h += 10 + HintLinkH;
            if (StepsShown) h += (HintLinkShown ? 2 : 14) + Measure(view.HintSteps, noteFont, w);
            return h;
        }

        // The lock's expected ping: one line right under the title (counted in the title's height).
        int PingLineH(int w) { return view.PingLine != null ? 4 + Measure(view.PingLine, subFont, w) : 0; }

        // The footnote sits under the tiles, so it only shows with them.
        int NoteHeight(int w, int tileH) { return tileH > 0 && view.Note != null ? Measure(view.Note, noteFont, w) : 0; }

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
            var links = Links();
            bool actions = hasButton || links.Count > 0;
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
            noteRect = Rectangle.Empty;
            int noteH = NoteHeight(w, tileH);
            if (noteH > 0)
            {
                y += 10;
                noteRect = new Rectangle(Pad, y, w, noteH);
                y += noteH;
            }

            if (actions)
            {
                y += 18;
                Actions(links, y, w);
                y += button.Height + (LinksBelow ? button.Height : 0);
            }

            stepsRect = Rectangle.Empty;
            if (HintLinkShown)
            {
                y += 10;
                int lw = LinkWidth(hint);
                hint.Bounds = new Rectangle((ClientSize.Width - lw) / 2, y, lw, HintLinkH);
                y += HintLinkH;
            }
            if (StepsShown)
            {
                y += HintLinkShown ? 2 : 14;
                int sh = Measure(view.HintSteps, noteFont, w);
                stepsRect = new Rectangle(Pad, y, w, sh);
                y += sh;
            }
        }

        // The button and the links at y.
        void Actions(List<CardLink> links, int y, int w)
        {
            if (!hasButton || LinksBelow)
            {
                if (hasButton)
                {
                    int bw = Math.Min(w, Math.Max(200, TextRenderer.MeasureText(button.Text, button.Font).Width + 64));
                    button.Bounds = new Rectangle((ClientSize.Width - bw) / 2, y, bw, button.Height);
                    y += button.Height;
                }
                LinkRow(links, y);
                return;
            }
            // the button with up to two links: the first before it in reading order, the second after it
            CardLink first = links.Count > 0 ? links[0] : null, second = links.Count > 1 ? links[1] : null;
            int side = Math.Max(first != null ? LinkWidth(first) : 0, second != null ? LinkWidth(second) : 0);
            int bw2 = TextRenderer.MeasureText(button.Text, button.Font).Width + 64;
            int room = w - (side > 0 ? 2 * (side + LinkGap) : 0);
            bw2 = Math.Max(Math.Min(200, room), Math.Min(bw2, room));
            int bx = (ClientSize.Width - bw2) / 2;
            button.Bounds = new Rectangle(bx, y, bw2, button.Height);
            CardLink left = LeadRight ? second : first, right = LeadRight ? first : second;
            if (left != null) left.Bounds = new Rectangle(bx - LinkGap - LinkWidth(left), y, LinkWidth(left), button.Height);
            if (right != null) right.Bounds = new Rectangle(bx + bw2 + LinkGap, y, LinkWidth(right), button.Height);
        }

        // Links side by side and centred, the first on the reading side.
        void LinkRow(List<CardLink> links, int y)
        {
            int x = (ClientSize.Width - (links.Sum(l => LinkWidth(l)) + LinkGap * Math.Max(0, links.Count - 1))) / 2;
            var order = LeadRight ? Enumerable.Reverse(links).ToList() : links;
            foreach (var l in order)
            {
                int lw = LinkWidth(l);
                l.Bounds = new Rectangle(x, y, lw, button.Height);
                x += lw + LinkGap;
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
            int lineH = PingLineH(titleRect.Width);
            var nameRect = new Rectangle(titleRect.X, titleRect.Y, titleRect.Width, titleRect.Height - lineH);
            TextRenderer.DrawText(g, view.Title, titleFont, nameRect, TitleColor, Flags);
            if (lineH > 0)
                TextRenderer.DrawText(g, view.PingLine, subFont, new Rectangle(titleRect.X, nameRect.Bottom + 4, titleRect.Width, lineH - 4),
                                      view.PingLineColor, Flags);
            if (view.Sub != null) TextRenderer.DrawText(g, view.Sub, subFont, subRect, Theme.Dim, Flags);
            if (noteRect != Rectangle.Empty) TextRenderer.DrawText(g, view.Note, noteFont, noteRect, Theme.Dim, Flags);
            if (stepsRect != Rectangle.Empty) TextRenderer.DrawText(g, view.HintSteps, noteFont, stepsRect, Theme.Dim, Flags);
            if (dotsRect != Rectangle.Empty)
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                const int d = 7, gap = 9;
                float x = dotsRect.X + (dotsRect.Width - (3 * d + 2 * gap)) / 2f, y = dotsRect.Y + (dotsRect.Height - d) / 2f;
                for (int i = 0; i < 3; i++)
                {
                    int k = LeadRight ? 2 - i : i; // the pulse runs in the reading direction
                    var c = k == dotPhase ? Theme.Accent : Shapes.Blend(Theme.Dim, Theme.Panel1, 0.45f);
                    using (var b = new SolidBrush(c)) g.FillEllipse(b, x + i * (d + gap), y, d, d);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { dots.Dispose(); titleFont.Dispose(); subFont.Dispose(); noteFont.Dispose(); }
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
            if (choice.Tag == "queue") return Lang.T("Shortest wait", "أقصر انتظار");
            if (choice.Tag == "players") return Lang.T("Most players", "أكثر لاعبين");
            return null;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Begin(e);
            if (choice == null) return;
            bool on = choice.Selected;
            var box = new RectangleF(0.75f, Lip + 0.75f, Width - 2.5f, Height - Lip - 2.5f);
            var rest = Shapes.Blend(Theme.Panel1, Theme.Panel2, 0.55f);
            var fill = Shapes.Blend(on ? Color.FromArgb(62, 38, 86) : rest, Color.White, 0.05f * hover);
            using (var path = Shapes.Round(box, 12))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                var edge = on ? Theme.Accent : Shapes.Blend(Color.FromArgb(70, 56, 98), Theme.Lavender, 0.25f * hover);
                using (var pen = new Pen(edge, on ? 1.8f : 1f)) g.DrawPath(pen, path);
            }
            FocusRing(g, new RectangleF(box.X + 2, box.Y + 2, box.Width - 4, box.Height - 4), 10);

            // the round check on the reading side, the name next to it
            float cy = Lip + 16 + (NameH - CheckD) / 2f;
            float cx = LeadRight ? Width - In - CheckD : In;
            if (on)
            {
                using (var b = new SolidBrush(Theme.Accent)) g.FillEllipse(b, cx, cy, CheckD, CheckD);
                using (var pen = new Pen(Color.Black, 2f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
                    g.DrawLines(pen, new[] { new PointF(cx + 5f, cy + 9.5f), new PointF(cx + 8f, cy + 12.4f), new PointF(cx + 13.2f, cy + 6.4f) });
            }
            else using (var pen = new Pen(Theme.Dim, 1.5f)) g.DrawEllipse(pen, cx + 0.75f, cy + 0.75f, CheckD - 1.5f, CheckD - 1.5f);

            int textX = In + CheckD + 10, textW = Width - textX - In;
            int y = Lip + 16;
            TextRenderer.DrawText(g, choice.Name, nameFont, Row(textX, y, textW, NameH), on ? Theme.TextC : Shapes.Blend(Theme.TextC, Theme.Dim, 0.35f), TextFlags);
            y += NameH + 6;

            bool known = choice.Ping < double.MaxValue;
            string ping = known ? Lang.T("Ping ", "بنق ") + (int)Math.Round(choice.Ping) : Lang.T("One moment...", "لحظة...");
            // Valve: the route enters at a relay ("via Dubai"), right after the number in the small font. Both are drawn
            // without padding (the number keeps its place: it starts where the padded text did), so no gap opens.
            if (known && choice.Via != null)
            {
                var nopad = TextFlags | TextFormatFlags.NoPadding;
                var area = new Size(textW, LineH);
                int pad = (TextRenderer.MeasureText(g, ping, pingFont, area, TextFlags).Width - TextRenderer.MeasureText(g, ping, pingFont, area, nopad).Width) / 2;
                int pw = TextRenderer.MeasureText(g, ping, pingFont, area, nopad).Width;
                TextRenderer.DrawText(g, ping, pingFont, Row(textX + pad, y, textW - pad, LineH), Fmt.PingColor(choice.Ping), nopad);
                if (pad + pw < textW)
                    TextRenderer.DrawText(g, " · " + Lang.T("via ", "عن طريق ") + choice.Via, lineFont, Row(textX + pad + pw, y, textW - pad - pw, LineH), Theme.Dim, nopad);
            }
            else TextRenderer.DrawText(g, ping, known ? pingFont : lineFont, Row(textX, y, textW, LineH), Fmt.PingColor(choice.Ping), TextFlags);
            y += LineH;
            if (HasQueue(choice))
            {
                TextRenderer.DrawText(g, Lang.T("Wait ", "الانتظار ") + Stats.EstimateWait(choice.Matches), lineFont, Row(textX, y, textW, LineH), Theme.Dim, TextFlags);
                y += LineH;
            }
            if (HasPlayers(choice))
                TextRenderer.DrawText(g, RegionGroups.Busy(choice.Players) + " · " + Stats.Approx(choice.Players), lineFont, Row(textX, y, textW, LineH), Theme.Dim, TextFlags);

            // the tag sits on the top edge, over the check
            string tag = TagText();
            if (tag != null)
            {
                int tw = TextRenderer.MeasureText(tag, badgeFont).Width + 18;
                float bx = LeadRight ? Width - In + 4 - tw : In - 4;
                var pill = new RectangleF(bx, 0, tw, BadgeH);
                Color pf = choice.Tag == "best" ? Theme.Accent : choice.Tag == "ping" ? Theme.Good : Theme.Lavender;
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
            var color = Shapes.Blend(Theme.Lavender, Color.White, 0.3f * hover);
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
