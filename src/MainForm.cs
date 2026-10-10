using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PingBoost
{
    class MainForm : Form
    {
        // Celeste theme: night purple, unicorn pink, lavender, sparkle gold
        public static readonly Color Bg = Color.FromArgb(20, 15, 30);
        public static readonly Color Panel1 = Color.FromArgb(30, 23, 44);
        public static readonly Color Panel2 = Color.FromArgb(46, 35, 68);
        public static readonly Color RowAlt = Color.FromArgb(36, 28, 52);
        public static readonly Color RowSel = Color.FromArgb(74, 40, 92);
        public static readonly Color Accent = Color.FromArgb(255, 111, 200);   // pink
        public static readonly Color Lavender = Color.FromArgb(182, 158, 255);
        public static readonly Color Good = Color.FromArgb(104, 236, 204);     // aqua: good ping
        public static readonly Color TextC = Color.FromArgb(246, 240, 252);
        public static readonly Color Dim = Color.FromArgb(168, 154, 192);
        public static readonly Color Bad = Color.FromArgb(255, 92, 122);
        public static readonly Color Warn = Color.FromArgb(255, 198, 92);      // sparkle gold

        const double RelayMarginMs = 3; // a relay must beat direct by this much to be chosen
        const int CheckArea = 34;       // width of the "play here" tick box in the region column

        // server list columns
        const int ColRegion = 0, ColPing = 1, ColJitter = 2, ColLoss = 3, ColPlayers = 4, ColWait = 5, ColVia = 6, ColRoute = 7;
        static readonly int[] AdvancedWidths = { 250, 84, 80, 66, 96, 110, 96, 150 };
        static readonly int[] SimpleWidths = { 420, 140, 0, 0, 0, 0, 0, 0 };

        // A region heading row in the server list (Deadlock matchmaking region + its activity).
        class GroupHeader
        {
            public string Key;
            public int Matches;
            public bool Top;
        }

        static string T(string en, string ar) { return Lang.T(en, ar); }

        readonly List<Game> games;
        readonly List<Relay> relays;
        bool simple;

        ListBox gameList;
        ListView serverList, relayList;
        ComboBox routeBox;
        Label routeLabel;
        Button btnTest, btnBoost, btnStop, btnLock, btnUnlock, btnDetect;
        Label liveLabel, statusLabel, gameNote, lockLabel, infoLabel;
        Panel pickBar, tabBar, heroCard, bottomPanel;
        Label heroTitle, heroSub;
        Button heroBtn;
        TextBox logBox;
        PingGraph graph;
        Panel[] pages;
        Button[] tabButtons;
        int currentPage;

        bool busy, boosting, clickedCheck;
        volatile bool tunnelActive;
        int monitorGen;
        string lockSummary; // null = no region lock
        Dictionary<string, int> activity; // Deadlock matches/hour per matchmaking region

        Game CurrentGame { get { return gameList.SelectedItem as Game; } }
        bool LockedOut(Game g, Region r) { return lockSummary != null && g != null && g.CanLock && !r.Picked; }

        public MainForm()
        {
            Lang.Ar = Storage.LoadArabic();
            simple = Prefs.Simple;
            games = GameDb.All();
            relays = Storage.LoadRelays();

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1240, 760);
            MinimumSize = new Size(980, 640);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Bg;
            ForeColor = TextC;
            Font = new Font("Segoe UI", 10f);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            ApplyDirection();
            BuildUi();
            ResumeLayout(false);

            RefreshRelays();
            Task.Run(() => ServerLock.Remove()); // leftovers from a crash
            if (!Tunnel.Installed)
                Log(T("WireGuard is not installed, so relay routing is off until you install it (Tools).",
                      "برنامج WireGuard مو مثبّت، فالوسيط مقفّل لين تثبّته (من «أدوات وسجل»)."));
            Shown += delegate
            {
                gameList.SelectedIndex = 0;
                BetaNotice.ShowOnce(this);
                if (Prefs.CheckUpdates) CheckForUpdate();
            };
            FormClosing += delegate
            {
                monitorGen++;
                if (tunnelActive) Tunnel.Stop();
                if (lockSummary != null) ServerLock.Remove();
            };
        }

        void ApplyDirection()
        {
            // LRM first: keeps "2X" in Latin digits inside a right-to-left title bar
            Text = "‎" + Brand.Name + (Brand.IsBeta ? " (Beta)" : "") + "  -  by " + Brand.Developer;
            RightToLeft = Lang.Ar ? RightToLeft.Yes : RightToLeft.No;
            RightToLeftLayout = Lang.Ar;
        }

        // ---------------------------------------------------------------- UI

        void BuildUi()
        {
            var header = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Panel1 };
            header.Controls.Add(liveLabel = new Label
            {
                Text = T("OFF", "مو شغّال"), Font = new Font("Segoe UI Semibold", 17f), ForeColor = Dim, Width = 330,
                Dock = DockStyle.Right, TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 14, 0)
            });
            header.Controls.Add(HeaderButton(T("Settings", "الإعدادات"), 120, TextC, OpenSettings));
            header.Controls.Add(HeaderButton(T("Feedback", "تواصل معنا"), 120, Warn, () => ContactDialog.Show(this, logBox.Text)));

            var logo = new PictureBox
            {
                Image = Brand.Logo, SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(14, 10, 44, 44),
                Cursor = Cursors.Hand, BackColor = Color.Transparent
            };
            logo.Click += delegate { AboutDialog.Show(this); };
            header.Controls.Add(logo);
            header.Controls.Add(new Label
            {
                Text = "2X PING", Font = new Font("Segoe UI Black", 17f), ForeColor = Accent,
                AutoSize = true, Location = new Point(64, 4)
            });
            if (Brand.IsBeta)
                header.Controls.Add(new Label
                {
                    Text = "BETA", Font = new Font("Segoe UI Black", 7.5f), ForeColor = Color.Black, BackColor = Warn,
                    AutoSize = false, Bounds = new Rectangle(178, 11, 42, 18), TextAlign = ContentAlignment.MiddleCenter
                });
            header.Controls.Add(new Label
            {
                Text = T("by " + Brand.Developer + "  ·  lower ping, better games", "بنق أقل، لعب أحلى  ·  من " + Brand.Developer),
                ForeColor = Dim, AutoSize = true, Location = new Point(67, 38), Font = new Font("Segoe UI", 9f)
            });

            statusLabel = new Label
            {
                Dock = DockStyle.Bottom, Height = 28, BackColor = Panel1, ForeColor = Dim,
                TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 12, 0), Text = T("Ready", "جاهز")
            };
            statusLabel.Controls.Add(new Label
            {
                Dock = DockStyle.Right, AutoSize = false, Width = 260, ForeColor = Color.FromArgb(120, 106, 146),
                TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 12, 0),
                Text = "‎" + Brand.Name + " v" + Brand.DisplayVersion + "  ·  " + Brand.Copyright
            });

            var left = new Panel { Dock = DockStyle.Left, Width = 230, BackColor = Panel1, Padding = new Padding(10, 6, 10, 10) };
            gameList = new ListBox
            {
                Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Panel1, ForeColor = TextC,
                DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 40, IntegralHeight = false
            };
            foreach (var g in games) gameList.Items.Add(g);
            gameList.DrawItem += DrawGameItem;
            gameList.SelectedIndexChanged += delegate { OnGameChanged(); };
            left.Controls.Add(gameList);
            left.Controls.Add(new Label
            {
                Text = T("GAMES", "الألعاب"), ForeColor = Dim, Dock = DockStyle.Top, Height = 30,
                Font = new Font("Segoe UI Semibold", 9.5f), TextAlign = ContentAlignment.MiddleLeft
            });

            var center = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 12, 16, 12) };
            var content = new Panel { Dock = DockStyle.Fill };
            tabBar = new Panel { Dock = DockStyle.Top, Height = 46, Visible = !simple };
            center.Controls.Add(content);
            center.Controls.Add(tabBar);

            string[] names = { T("Servers", "السيرفرات"), T("Relays", "سيرفرات الوسيط"), T("Tools && Log", "أدوات وسجل") };
            tabButtons = new Button[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                var b = MakeButton(names[i], Panel2);
                b.Bounds = new Rectangle(i * 156, 0, 148, 34);
                int idx = i;
                b.Click += delegate { ShowPage(idx); };
                tabBar.Controls.Add(b);
                tabButtons[i] = b;
            }

            pages = new[] { BuildServersPage(), BuildRelaysPage(), BuildToolsPage() };
            foreach (var p in pages) { p.Dock = DockStyle.Fill; content.Controls.Add(p); }

            Controls.Add(center);
            Controls.Add(left);
            Controls.Add(header);
            Controls.Add(statusLabel);
            if (Lang.Ar) foreach (Control c in Controls) MirrorChildren(c);
            ShowPage(simple ? 0 : currentPage);
        }

        Panel HeaderButton(string text, int width, Color fore, Action onClick)
        {
            var wrap = new Panel { Dock = DockStyle.Right, Width = width, Padding = new Padding(8, 15, 0, 15) };
            var b = MakeButton(text, Panel2);
            b.ForeColor = fore;
            b.Dock = DockStyle.Fill;
            b.Click += delegate { onClick(); };
            wrap.Controls.Add(b);
            return wrap;
        }

        // The form mirrors only its own children; nested panels are mirrored here for Arabic.
        static void MirrorChildren(Control parent)
        {
            if (parent is ListView || parent is ListBox || parent is ComboBox || parent is TextBox) return;
            var flow = parent as FlowLayoutPanel;
            if (flow != null) { flow.FlowDirection = FlowDirection.RightToLeft; return; }
            foreach (Control c in parent.Controls)
            {
                if (c.Dock == DockStyle.Left) c.Dock = DockStyle.Right;
                else if (c.Dock == DockStyle.Right) c.Dock = DockStyle.Left;
                else if (c.Dock == DockStyle.None)
                {
                    c.Left = parent.ClientSize.Width - c.Right;
                    c.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                }
                MirrorChildren(c);
            }
        }

        // "Your best server" card with a one-click "Play on it".
        Panel BuildHero()
        {
            heroCard = new Panel { Dock = DockStyle.Top, Height = 92, BackColor = Panel2, Padding = new Padding(18, 12, 14, 12) };
            heroBtn = MakeButton(T("Play on it", "العب عليه"), Accent);
            heroBtn.ForeColor = Color.Black;
            heroBtn.Font = new Font("Segoe UI Semibold", 11f);
            heroBtn.Dock = DockStyle.Fill;
            heroBtn.Visible = false;
            var btnWrap = new Panel { Dock = DockStyle.Right, Width = 170, Padding = new Padding(0, 12, 0, 12) };
            btnWrap.Controls.Add(heroBtn);
            heroBtn.Click += delegate { var r = heroBtn.Tag as Region; if (r != null) PlayOn(r); };
            heroSub = new Label { Dock = DockStyle.Fill, ForeColor = Dim, TextAlign = ContentAlignment.TopLeft, AutoEllipsis = true };
            heroTitle = new Label
            {
                Dock = DockStyle.Top, Height = 38, ForeColor = TextC, Font = new Font("Segoe UI Semibold", 15f),
                TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true
            };
            heroCard.Controls.Add(heroSub);
            heroCard.Controls.Add(heroTitle);
            heroCard.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 12 });
            heroCard.Controls.Add(btnWrap);
            return heroCard;
        }

        Panel BuildServersPage()
        {
            var p = new Panel();
            gameNote = new Label { Dock = DockStyle.Top, Height = 32, ForeColor = Dim, TextAlign = ContentAlignment.MiddleLeft, Visible = !simple };

            // region lock bar (Valve games)
            pickBar = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = Panel1, Padding = new Padding(8) };
            btnLock = MakeButton(T("Lock my servers", "ثبّت سيرفراتي"), Accent);
            btnLock.ForeColor = Color.Black;
            btnLock.Font = new Font("Segoe UI Semibold", 10f);
            btnLock.Dock = DockStyle.Right;
            btnLock.Width = 150;
            btnLock.Click += delegate { LockServers(); };
            btnUnlock = MakeButton(T("Unlock", "فك التثبيت"), Panel2);
            btnUnlock.Dock = DockStyle.Right;
            btnUnlock.Width = 120;
            btnUnlock.Click += delegate { UnlockServers(); };
            lockLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 4, 0), AutoEllipsis = true };
            pickBar.Controls.Add(lockLabel);
            pickBar.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
            pickBar.Controls.Add(btnUnlock);
            pickBar.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
            pickBar.Controls.Add(btnLock);

            serverList = MakeListView(new[] { T("Server", "السيرفر"), T("Ping", "البنق"), T("Jitter", "التذبذب"),
                                              T("Loss", "الفقد"), T("Players", "اللاعبين"), T("Wait", "الانتظار"),
                                              T("Via relay", "بالوسيط"), T("Best route", "أحسن طريق") },
                                      simple ? SimpleWidths : AdvancedWidths);
            serverList.DrawSubItem += DrawRegionCheck;
            serverList.MouseDown += (s, e) =>
            {
                clickedCheck = false;
                var item = serverList.GetItemAt(e.X, e.Y);
                var g = CurrentGame;
                if (item == null || g == null || !g.CanLock || !(item.Tag is Region)) return;
                var cell = FirstCell(serverList, item, item.Bounds);
                bool onBox = Lang.Ar ? e.X >= cell.Right - CheckArea && e.X <= cell.Right : e.X >= cell.X && e.X <= cell.X + CheckArea;
                if (!onBox) return;
                clickedCheck = true;
                TogglePick((Region)item.Tag);
            };
            serverList.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Space || CurrentGame == null || !CurrentGame.CanLock) return;
                foreach (ListViewItem item in serverList.SelectedItems) { var r = item.Tag as Region; if (r != null) TogglePick(r); }
                e.Handled = true;
            };
            serverList.DoubleClick += delegate { if (!clickedCheck) Boost(); };

            // in the simple layout the live graph only appears while BOOST is on
            var bottom = bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = simple ? 56 : 200 };
            var controls = new Panel { Dock = DockStyle.Bottom, Height = 56 };
            btnTest = MakeButton(T("Test ping", "افحص البنق"), Panel2);
            btnTest.Bounds = new Rectangle(0, 12, 140, 40);
            btnTest.Click += delegate { RunTest(); };
            routeLabel = new Label { Text = T("Route:", "الطريق:"), ForeColor = Dim, AutoSize = true, Location = new Point(154, 22), Visible = !simple };
            routeBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Panel2,
                ForeColor = TextC, Bounds = new Rectangle(214, 18, 210, 28),
                DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 22, Visible = !simple
            };
            routeBox.DrawItem += (s, e) =>
            {
                if (e.Index < 0) return;
                bool sel = (e.State & DrawItemState.Selected) != 0;
                using (var b = new SolidBrush(sel ? RowSel : Panel2)) e.Graphics.FillRectangle(b, e.Bounds);
                TextRenderer.DrawText(e.Graphics, routeBox.Items[e.Index].ToString(), Font,
                    Rectangle.Inflate(e.Bounds, -4, 0), TextC, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | Lang.Flags);
            };
            int bx = simple ? 154 : 438;
            btnBoost = MakeButton(T("BOOST", "شغّل البوست"), Accent);
            btnBoost.ForeColor = Color.Black;
            btnBoost.Font = new Font("Segoe UI Semibold", 11f);
            btnBoost.Bounds = new Rectangle(bx, 12, 160, 40);
            btnBoost.Click += delegate { Boost(); };
            btnStop = MakeButton(T("Stop", "وقّف"), Panel2);
            btnStop.Bounds = new Rectangle(bx + 170, 12, 100, 40);
            btnStop.Enabled = false;
            btnStop.Click += delegate { StopAll(); };
            controls.Controls.AddRange(new Control[] { btnTest, routeLabel, routeBox, btnBoost, btnStop });

            graph = new PingGraph { Dock = DockStyle.Fill, Visible = !simple };
            bottom.Controls.Add(graph);
            bottom.Controls.Add(controls);

            p.Controls.Add(serverList);
            p.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 8 });
            p.Controls.Add(pickBar);
            p.Controls.Add(BuildInfoBar());
            p.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 6 });
            p.Controls.Add(BuildHero());
            p.Controls.Add(bottom);
            p.Controls.Add(gameNote);
            return p;
        }

        Panel BuildRelaysPage()
        {
            var p = new Panel();
            var info = new Label
            {
                Dock = DockStyle.Top, Height = 64, ForeColor = Dim,
                Text = T("A relay is a VPS of yours running WireGuard (set it up with setup-relay.sh). When the route through it is faster, " +
                         "ONLY the game's traffic goes through it; browsing, downloads and Discord stay on your normal connection.",
                         "الوسيط سيرفر VPS حقك عليه WireGuard (تجهّزه بسكربت setup-relay.sh). لو طلع الطريق عن طريقه أسرع، " +
                         "ترافيك اللعبة بس هو اللي يمشي منه، والباقي (المتصفح، التحميل، ديسكورد) يمشي على نتك العادي.")
            };
            relayList = MakeListView(new[] { T("Name", "الاسم"), T("Server", "السيرفر"), T("Ping", "البنق"), T("Agent", "أداة القياس") },
                                     new[] { 180, 220, 90, 300 });

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(0, 10, 0, 0) };
            bar.Controls.Add(ToolButton(T("Add relay (.conf)", "ضيف وسيط (.conf)"), AddRelay));
            bar.Controls.Add(ToolButton(T("Remove", "احذف"), RemoveRelay));
            bar.Controls.Add(ToolButton(T("Test relays", "افحص الوسطاء"), TestRelays));

            p.Controls.Add(relayList);
            p.Controls.Add(bar);
            p.Controls.Add(info);
            return p;
        }

        Panel BuildToolsPage()
        {
            var p = new Panel();
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 56 };
            bar.Controls.Add(ToolButton(T("Flush DNS", "امسح كاش DNS"), FlushDns));
            bar.Controls.Add(ToolButton(T("Raise game priority", "ارفع أولوية اللعبة"), delegate { RaisePriority(CurrentGame); }));
            bar.Controls.Add(ToolButton(T("Add a server", "ضيف سيرفر"), AddCustomServer));
            bar.Controls.Add(ToolButton(T("Get WireGuard", "نزّل WireGuard"), delegate { Brand.Open("https://www.wireguard.com/install/"); }));
            bar.Controls.Add(ToolButton(T("Data folder", "مجلد البيانات"), delegate { Brand.Open(Storage.Dir); }));
            var reset = ToolButton(T("Put everything back", "رجّع كل شي زي ما كان"), ResetEverything);
            reset.ForeColor = Bad;
            bar.Controls.Add(reset);

            string oldLog = logBox != null ? logBox.Text : "";
            logBox = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                BackColor = Panel1, ForeColor = TextC, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 9.5f),
                Text = oldLog
            };
            p.Controls.Add(logBox);
            p.Controls.Add(bar);
            return p;
        }

        Button ToolButton(string text, Action onClick)
        {
            var b = MakeButton(text, Panel2);
            b.AutoSize = true;
            b.Padding = new Padding(10, 4, 10, 4);
            b.Margin = new Padding(0, 0, 8, 0);
            b.Height = 38;
            b.Click += delegate { onClick(); };
            return b;
        }

        static Button MakeButton(string text, Color bg)
        {
            var b = new Button { Text = text, FlatStyle = FlatStyle.Flat, BackColor = bg, ForeColor = TextC, Cursor = Cursors.Hand };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(bg, 0.25f);
            return b;
        }

        static Color RowBack(ListViewItem item, int index)
        {
            return item.Selected ? RowSel : (index % 2 == 0 ? Panel1 : RowAlt);
        }

        ListView MakeListView(string[] cols, int[] widths)
        {
            var lv = new ListView
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
                HideSelection = false, BorderStyle = BorderStyle.None, BackColor = Panel1, ForeColor = TextC,
                OwnerDraw = true, HeaderStyle = ColumnHeaderStyle.Nonclickable,
                // Kept left-to-right internally; for Arabic the column order is reversed and text is right-aligned.
                RightToLeft = RightToLeft.No, RightToLeftLayout = false,
                SmallImageList = new ImageList { ImageSize = new Size(1, 30) }
            };
            typeof(Control).GetProperty("DoubleBuffered", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(lv, true, null);
            for (int i = 0; i < cols.Length; i++) lv.Columns.Add(cols[i], widths[i]);
            if (Lang.Ar)
                for (int i = 0; i < cols.Length; i++) lv.Columns[i].DisplayIndex = cols.Length - 1 - i;

            // hidden columns (width 0 in the simple layout) cannot be dragged open
            lv.ColumnWidthChanging += (s, e) =>
            {
                if (widths[e.ColumnIndex] == 0) { e.Cancel = true; e.NewWidth = 0; }
            };
            // the first column (server name) takes the remaining width
            lv.Resize += delegate
            {
                int used = 0;
                for (int i = 1; i < lv.Columns.Count; i++) used += lv.Columns[i].Width;
                lv.Columns[0].Width = Math.Max(widths[0], lv.ClientSize.Width - used);
            };

            lv.DrawColumnHeader += (s, e) =>
            {
                if (e.Bounds.Width <= 0) return;
                using (var b = new SolidBrush(Panel2)) e.Graphics.FillRectangle(b, e.Bounds);
                TextRenderer.DrawText(e.Graphics, e.Header.Text, Font, Rectangle.Inflate(e.Bounds, -8, 0), Dim,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | Align);
            };
            lv.DrawItem += (s, e) => { };
            lv.DrawSubItem += (s, e) =>
            {
                if (lv == serverList && (e.ColumnIndex == 0 || e.Item.Tag is GroupHeader)) return; // drawn by DrawRegionCheck
                var rc = e.ColumnIndex == 0 ? FirstCell(lv, e.Item, e.Bounds) : e.Bounds;
                if (rc.Width <= 0) return;
                using (var b = new SolidBrush(RowBack(e.Item, e.ItemIndex))) e.Graphics.FillRectangle(b, rc);
                TextRenderer.DrawText(e.Graphics, e.SubItem.Text, Font, Rectangle.Inflate(rc, -8, 0), e.SubItem.ForeColor,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | Align);
            };
            return lv;
        }

        // Text alignment inside owner-drawn lists (right-aligned, RTL reading for Arabic).
        static TextFormatFlags Align
        {
            get { return Lang.Ar ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left; }
        }

        // Bounds of the first column (ListView reports the whole row for column 0, and it may not be displayed first).
        static Rectangle FirstCell(ListView lv, ListViewItem item, Rectangle row)
        {
            int x = row.X;
            for (int i = 1; i < lv.Columns.Count; i++)
                if (lv.Columns[i].DisplayIndex < lv.Columns[0].DisplayIndex) x += lv.Columns[i].Width;
            return new Rectangle(x, row.Y, lv.Columns[0].Width, row.Height);
        }

        static Rectangle CheckBox(Rectangle cell)
        {
            int x = Lang.Ar ? cell.Right - 9 - 16 : cell.X + 9;
            return new Rectangle(x, cell.Y + (cell.Height - 16) / 2, 16, 16);
        }

        // First column of the server list: optional tick box + server name.
        void DrawRegionCheck(object sender, DrawListViewSubItemEventArgs e)
        {
            if (e.ColumnIndex != 0) return;
            var h = e.Item.Tag as GroupHeader;
            if (h != null) { DrawGroupHeader(e, h); return; }
            var rc = FirstCell(serverList, e.Item, e.Bounds);
            using (var b = new SolidBrush(RowBack(e.Item, e.ItemIndex))) e.Graphics.FillRectangle(b, rc);
            var g = CurrentGame;
            var textRc = Rectangle.Inflate(rc, -8, 0);
            if (g != null && g.CanLock)
            {
                var r = (Region)e.Item.Tag;
                var box = CheckBox(rc);
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                if (r.Picked)
                {
                    using (var b = new SolidBrush(Accent)) e.Graphics.FillRectangle(b, box);
                    using (var pen = new Pen(Color.Black, 2f))
                        e.Graphics.DrawLines(pen, new[] { new Point(box.X + 3, box.Y + 8), new Point(box.X + 7, box.Y + 12), new Point(box.X + 13, box.Y + 4) });
                }
                else using (var pen = new Pen(Dim, 1.5f)) e.Graphics.DrawRectangle(pen, box);
                textRc = Lang.Ar ? new Rectangle(rc.X + 4, rc.Y, rc.Width - CheckArea - 4, rc.Height)
                                 : new Rectangle(rc.X + CheckArea, rc.Y, rc.Width - CheckArea - 4, rc.Height);
            }
            TextRenderer.DrawText(e.Graphics, e.SubItem.Text, Font, textRc,
                e.SubItem.ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | Align);
        }

        void ShowPage(int idx)
        {
            currentPage = idx;
            for (int i = 0; i < pages.Length; i++)
            {
                pages[i].Visible = i == idx;
                tabButtons[i].BackColor = i == idx ? RowSel : Panel2;
                tabButtons[i].ForeColor = i == idx ? Accent : TextC;
            }
        }

        void DrawGameItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var g = (Game)gameList.Items[e.Index];
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var b = new SolidBrush(sel ? Panel2 : Panel1)) e.Graphics.FillRectangle(b, e.Bounds);
            if (sel) using (var b = new SolidBrush(Accent)) e.Graphics.FillRectangle(b, e.Bounds.X, e.Bounds.Y + 8, 3, e.Bounds.Height - 16);
            string name = g.Provider == Provider.Custom ? T("Custom servers", "سيرفرات من عندك") : g.Name;
            TextRenderer.DrawText(e.Graphics, name, Font,
                new Rectangle(e.Bounds.X + 14, e.Bounds.Y, e.Bounds.Width - 18, e.Bounds.Height),
                sel ? TextC : Color.FromArgb(206, 196, 226), TextFormatFlags.VerticalCenter | Align);
        }

        void OpenSettings()
        {
            if (busy || boosting)
            {
                Msg(T("Stop the boost and let the ping test finish first.", "وقّف البوست واستنى الفحص يخلص، وبعدين افتح الإعدادات."));
                return;
            }
            bool resetAsked;
            if (SettingsDialog.Show(this, out resetAsked)) RebuildUi();
            if (resetAsked) ResetEverything();
        }

        // Rebuilds the whole window after a language or layout change.
        void RebuildUi()
        {
            simple = Prefs.Simple;
            var g = CurrentGame;
            SuspendLayout();
            var old = Controls.Cast<Control>().ToArray();
            Controls.Clear();
            ApplyDirection();
            BuildUi();
            foreach (var c in old) c.Dispose();
            ResumeLayout(true);
            RefreshRelays();
            gameList.SelectedItem = g ?? games[0];
        }

        // ---------------------------------------------------------------- helpers

        void Ui(Action a)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke(a); } catch (InvalidOperationException) { } }
            else a();
        }

        void Log(string msg)
        {
            Ui(() => logBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + "   " + msg + Environment.NewLine));
        }

        void SetStatus(string msg) { Ui(() => statusLabel.Text = msg); }

        void Msg(string text)
        {
            MessageBox.Show(this, text, "‎" + Brand.Name, MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1,
                Lang.Ar ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : 0);
        }

        bool Ask(string text)
        {
            return MessageBox.Show(this, text, "‎" + Brand.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1,
                Lang.Ar ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : 0) == DialogResult.Yes;
        }

        // LRM marks keep "74 ms" in order inside right-to-left text.
        static string Ms(double v) { return v < 0 ? T("no reply", "ما يرد") : "‎" + Math.Round(v) + " ms‎"; }

        static string ShortName(Region r) { return r.Name.Split(new[] { "  [", "  (" }, StringSplitOptions.None)[0]; }

        static Color PingColor(double v)
        {
            if (v < 0) return Bad;
            if (v < 60) return Good;
            return v < 120 ? Warn : Bad;
        }

        static void SetSub(ListViewItem item, int i, string text, Color color)
        {
            item.SubItems[i].Text = text;
            item.SubItems[i].ForeColor = color;
        }

        static KeyValuePair<string, double>? BestRelay(Region r)
        {
            if (r.ViaRelay.Count == 0) return null;
            return r.ViaRelay.OrderBy(kv => kv.Value).First();
        }

        // Relay to use for this region, or null when direct is best / unknown.
        Relay AutoRelay(Region r)
        {
            var best = BestRelay(r);
            if (best == null) return null;
            bool directOk = r.Direct != null && r.Direct.Ok;
            if (directOk && best.Value.Value > r.Direct.Avg - RelayMarginMs) return null;
            return relays.FirstOrDefault(x => x.Name == best.Value.Key);
        }

        static double BestLatency(Region r)
        {
            double d = r.Direct != null && r.Direct.Ok ? r.Direct.Avg : double.MaxValue;
            var b = BestRelay(r);
            return b != null ? Math.Min(d, b.Value.Value) : d;
        }

        // ---------------------------------------------------------------- servers

        void OnGameChanged()
        {
            var g = CurrentGame;
            if (g == null) return;
            gameNote.Text = g.Note;
            pickBar.Visible = g.CanLock;
            UpdateLockBar();
            serverList.Items.Clear();
            SetStatus(T("Getting the servers of ", "لحظة، أجيب سيرفرات ") + g.Name + "...");
            Task.Run(() =>
            {
                string err = null;
                try { GameDb.Load(g); } catch (Exception ex) { err = ex.Message; }
                Ui(() =>
                {
                    if (CurrentGame != g) return;
                    if (err != null)
                    {
                        SetStatus(T("Couldn't get the servers: ", "ما قدرت أجيب السيرفرات: ") + err);
                        Log(g.Name + ": " + err);
                        return;
                    }
                    FillServers(g);
                    UpdateLockBar();
                    LoadStats(g);
                    if (g.Regions.Count == 0)
                        SetStatus(T("No servers yet. Add one from Tools > Add a server.", "ما في سيرفرات للحين. ضيف سيرفر من «أدوات وسجل»."));
                    else if (Prefs.AutoTest && g.Regions.All(r => r.Direct == null))
                        RunTest();
                    else
                        SetStatus(g.Regions.Count + T(" regions ready. Press Test ping.", " منطقة جاهزة، اضغط «افحص البنق»."));
                });
            });
        }

        // Deadlock: servers are grouped under their matchmaking region, busiest region first.
        bool Grouped(Game g) { return g != null && g.AppId == "1422450" && activity != null && activity.Count > 0; }

        string RegionKey(Region r)
        {
            string mm;
            return Stats.PopToMm.TryGetValue(r.Id, out mm) && activity != null && activity.ContainsKey(mm) ? mm : "";
        }

        int MatchesOf(Region r)
        {
            string k = RegionKey(r);
            return k == "" ? -1 : activity[k];
        }

        void FillServers(Game g)
        {
            serverList.BeginUpdate();
            serverList.Items.Clear();
            if (Grouped(g))
            {
                var groups = g.Regions.GroupBy(RegionKey)
                    .OrderBy(gr => gr.Key == "" ? 1 : 0)
                    .ThenByDescending(gr => gr.Key == "" ? 0 : activity[gr.Key]).ToList();
                bool first = true;
                foreach (var gr in groups)
                {
                    var h = new GroupHeader { Key = gr.Key, Matches = gr.Key == "" ? 0 : activity[gr.Key], Top = first && gr.Key != "" };
                    first = false;
                    var hi = new ListViewItem("") { Tag = h };
                    for (int i = 0; i < 7; i++) hi.SubItems.Add("");
                    serverList.Items.Add(hi);
                    foreach (var r in gr.OrderBy(x => LockedOut(g, x) ? 1 : 0).ThenBy(BestLatency)) AddRegionRow(r);
                }
            }
            else
                foreach (var r in g.Regions) AddRegionRow(r);
            serverList.EndUpdate();
            UpdateHero();
        }

        void AddRegionRow(Region r)
        {
            var item = new ListViewItem(r.Name) { UseItemStyleForSubItems = false, Tag = r };
            item.ForeColor = TextC;
            for (int i = 0; i < 7; i++) item.SubItems.Add("");
            serverList.Items.Add(item);
            UpdateRow(item);
        }

        string HeaderText(GroupHeader h)
        {
            if (h.Key == "") return T("Other regions (no player data)", "مناطق ثانية (ما عندنا أرقامها)");
            string name = Stats.MmRegions.ContainsKey(h.Key) ? Lang.T(Stats.MmRegions[h.Key][0], Stats.MmRegions[h.Key][1]) : h.Key;
            return name + "   ·   " + Stats.Approx(Stats.EstimatePlayers(h.Matches)) + T(" players", " لاعب") +
                   "   ·   " + T("wait ", "الانتظار ") + Stats.EstimateWait(h.Matches);
        }

        void DrawGroupHeader(DrawListViewSubItemEventArgs e, GroupHeader h)
        {
            var row = e.Item.Bounds;
            using (var b = new SolidBrush(Color.FromArgb(40, 30, 60))) e.Graphics.FillRectangle(b, row);
            var font = new Font("Segoe UI Semibold", 10.5f);
            var text = Rectangle.Inflate(row, -14, 0);
            TextRenderer.DrawText(e.Graphics, HeaderText(h), font, text, h.Key == "" ? Dim : Lavender,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | Align);
            if (h.Top)
            {
                // the busiest region gets a gold sparkle badge on the opposite side
                var flags = TextFormatFlags.VerticalCenter | (Lang.Ar ? TextFormatFlags.Left : TextFormatFlags.Right);
                TextRenderer.DrawText(e.Graphics, T("✦ most players", "✦ الأكثر لاعبين"), font, text, Warn, flags | Lang.Flags);
            }
            font.Dispose();
        }

        // Best server = lowest ping; for Deadlock only among regions busy enough for quick matches.
        Region BestServer(Game g)
        {
            var cands = g.Regions.Where(r => !LockedOut(g, r) && BestLatency(r) < double.MaxValue).ToList();
            if (Grouped(g))
            {
                var busy = cands.Where(r => MatchesOf(r) >= 40).ToList();
                if (busy.Count > 0) cands = busy;
            }
            return cands.OrderBy(BestLatency).FirstOrDefault();
        }

        void UpdateHero()
        {
            var g = CurrentGame;
            if (heroCard == null || g == null) return;
            var best = BestServer(g);
            if (best == null)
            {
                heroTitle.Text = busy ? T("Looking for your best server...", "لحظة، ألقى لك أحسن سيرفر...")
                                      : T("Press Test ping and we'll find your best server", "اضغط «افحص البنق» ونلقى لك أحسن سيرفر");
                heroSub.Text = "";
                heroBtn.Visible = false;
                return;
            }
            heroTitle.Text = "✦ " + T("Your best server: ", "أحسن سيرفر لك: ") + ShortName(best) + "   " + Ms(BestLatency(best));
            int m = MatchesOf(best);
            if (m >= 0)
            {
                string mm = RegionKey(best);
                string reg = Stats.MmRegions.ContainsKey(mm) ? Lang.T(Stats.MmRegions[mm][0], Stats.MmRegions[mm][1]) : mm;
                heroSub.Text = T("Region ", "منطقة ") + reg + "  ·  " + Stats.Approx(Stats.EstimatePlayers(m)) + T(" players", " لاعب") +
                               "  ·  " + T("wait ", "الانتظار ") + Stats.EstimateWait(m) + "  ·  " + T("lowest ping with enough players", "أقل بنق وفيه لاعبين كفاية");
            }
            else heroSub.Text = T("The lowest ping you have right now.", "أقل بنق عندك دحين.");

            heroBtn.Tag = best;
            heroBtn.Visible = g.CanLock;
            bool already = lockSummary != null && best.Picked && g.Regions.Count(r => r.Picked) == 1;
            heroBtn.Enabled = !already;
            heroBtn.Text = already ? T("Locked ✓", "مثبّت ✓") : T("Play on it", "العب عليه");
        }

        // One click: pick only this server and lock it.
        void PlayOn(Region region)
        {
            var g = CurrentGame;
            if (g == null || !g.CanLock) return;
            foreach (var r in g.Regions) r.Picked = r == region;
            Storage.SavePicks(g.AppId, new[] { region.Id });
            FillServers(g);
            UpdateLockBar();
            LockServers();
        }

        void UpdateRow(ListViewItem item)
        {
            var r = item.Tag as Region;
            if (r == null) return;
            var g = CurrentGame;
            item.SubItems[ColRegion].Text = (r.Here ? T("» ", "« ") : "") + r.Name + (r.Here ? T("  (you are here)", "  (إنت هنا)") : "");
            item.SubItems[ColRegion].ForeColor = r.Here || r.Picked ? Accent : (r.Detected ? Warn : TextC);

            string mm;
            int matches;
            if (g != null && g.AppId == "1422450" && activity != null && Stats.PopToMm.TryGetValue(r.Id, out mm) &&
                activity.TryGetValue(mm, out matches))
            {
                SetSub(item, ColPlayers, Stats.Approx(Stats.EstimatePlayers(matches)), TextC);
                SetSub(item, ColWait, Stats.EstimateWait(matches), matches >= 100 ? Accent : matches >= 40 ? Warn : Bad);
            }
            else
            {
                SetSub(item, ColPlayers, "-", Dim);
                SetSub(item, ColWait, "-", Dim);
            }

            if (LockedOut(g, r))
            {
                SetSub(item, ColPing, T("blocked", "محجوب"), Dim);
                foreach (int i in new[] { ColJitter, ColLoss, ColVia, ColRoute }) SetSub(item, i, "-", Dim);
                return;
            }
            if (r.Direct == null)
            {
                foreach (int i in new[] { ColPing, ColJitter, ColLoss, ColVia, ColRoute }) SetSub(item, i, "-", Dim);
                return;
            }
            SetSub(item, ColPing, Ms(r.Direct.Avg), PingColor(r.Direct.Avg));
            SetSub(item, ColJitter, r.Direct.Ok ? "‎" + Math.Round(r.Direct.Jitter, 1) + " ms‎" : "-", r.Direct.Jitter > 10 ? Warn : Dim);
            SetSub(item, ColLoss, "‎" + Math.Round(r.Direct.Loss) + "%‎", r.Direct.Loss > 0 ? Bad : Dim);

            var best = BestRelay(r);
            SetSub(item, ColVia, best == null ? "-" : Ms(best.Value.Value), best == null ? Dim : PingColor(best.Value.Value));

            var relay = AutoRelay(r);
            if (relay != null)
            {
                string gain = r.Direct.Ok ? "  (-" + Math.Round(r.Direct.Avg - best.Value.Value) + " ms)" : "";
                SetSub(item, ColRoute, T("via ", "عن طريق ") + relay.Name + gain, Accent);
            }
            else SetSub(item, ColRoute, r.Direct.Ok ? T("Direct", "مباشر") : "-", r.Direct.Ok ? TextC : Dim);
        }

        void UpdateRegion(Region r)
        {
            foreach (ListViewItem item in serverList.Items)
                if (item.Tag == r) { UpdateRow(item); serverList.Invalidate(item.Bounds); return; }
        }

        void RunTest()
        {
            var g = CurrentGame;
            if (g == null || busy) return;
            if (g.Regions.Count == 0) { SetStatus(T("No servers to test.", "ما في سيرفرات أفحصها.")); return; }
            busy = true;
            btnTest.Enabled = false;
            // regions blocked by the lock can't answer anyway, so skip them
            var regions = g.Regions.Where(r => !LockedOut(g, r)).ToList();
            var rels = relays.ToList();
            SetStatus(rels.Count > 0 ? T("Testing relays...", "أفحص سيرفرات الوسيط...") : T("Testing...", "أفحص..."));
            UpdateHero();

            Task.Run(() =>
            {
                foreach (var rel in rels) rel.Ping = rel.MeasurePing();
                Ui(RefreshRelays);

                int done = 0;
                Parallel.ForEach(regions, new ParallelOptions { MaxDegreeOfParallelism = 8 }, r =>
                {
                    PingResult best = null;
                    Endpoint bestEp = null;
                    foreach (var ep in r.Endpoints)
                    {
                        var res = Pinger.Measure(ep, 4, 1200);
                        if (best == null || (res.Ok && (!best.Ok || res.Avg < best.Avg))) { best = res; bestEp = ep; }
                    }
                    r.Direct = best ?? new PingResult();
                    r.BestEndpoint = best != null && best.Ok ? bestEp : null;

                    var via = new Dictionary<string, double>();
                    var target = r.Target;
                    foreach (var rel in rels)
                    {
                        if (rel.Ping < 0 || !rel.HasAgent || target == null) continue;
                        double v = rel.ProbeFromRelay(target);
                        if (v >= 0) via[rel.Name] = rel.Ping + v;
                    }
                    r.ViaRelay = via;

                    int n = Interlocked.Increment(ref done);
                    Ui(() => { UpdateRegion(r); SetStatus(T("Testing... ", "أفحص... ") + n + "/" + regions.Count); });
                });

                Ui(() =>
                {
                    busy = false;
                    btnTest.Enabled = true;
                    var top = regions.Where(r => BestLatency(r) < double.MaxValue).OrderBy(BestLatency).FirstOrDefault();
                    if (top == null)
                    {
                        SetStatus(T("No server replied. Check your internet or firewall.", "ولا سيرفر رد. شيّك على النت أو الجدار الناري."));
                        return;
                    }
                    if (CurrentGame == g)
                    {
                        // closest first; regions blocked by the lock go to the end
                        g.Regions = g.Regions.OrderBy(r => LockedOut(g, r) ? 1 : 0).ThenBy(BestLatency).ToList();
                        FillServers(g);
                    }
                    SetStatus(T("Done! Your best region: ", "خلصنا! أحسن منطقة لك: ") + ShortName(top) + "  " + Ms(BestLatency(top)));
                    Log(g.Name + ": " + T("best region ", "أحسن منطقة ") + top.Name + " " + Ms(BestLatency(top)));
                });
            });
        }

        void CheckForUpdate()
        {
            Task.Run(() =>
            {
                string tag = Updater.NewerVersion();
                if (tag == null) return;
                Ui(() =>
                {
                    Log(T("New version available: ", "في إصدار جديد: ") + tag);
                    if (Ask(T("A new version of 2X Ping is out (" + tag + "). Open the download page?",
                              "نزل إصدار جديد من البرنامج (" + tag + ")! تبغى أفتح لك صفحة التحميل؟")))
                        Brand.Open(Brand.ReleasesUrl);
                });
            });
        }

        // ---------------------------------------------------------------- players & "which server am I on?"

        Panel BuildInfoBar()
        {
            var bar = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(0, 4, 0, 8) };
            btnDetect = MakeButton(T("Which server am I on?", "على أي سيرفر أنا؟"), Panel2);
            btnDetect.Dock = DockStyle.Right;
            btnDetect.Width = 180;
            btnDetect.Click += delegate { DetectServer(); };
            infoLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = TextC, AutoEllipsis = true };
            bar.Controls.Add(infoLabel);
            bar.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
            bar.Controls.Add(btnDetect);
            return bar;
        }

        void LoadStats(Game g)
        {
            infoLabel.Text = T("Getting player numbers...", "لحظة، أجيب عدد اللاعبين...");
            infoLabel.ForeColor = Dim;
            Task.Run(() =>
            {
                Dictionary<string, int> act = null;
                if (g.AppId == "1422450") { try { act = Stats.DeadlockActivity(); } catch { } }
                int steam = Stats.SteamPlayers(g.SteamAppId);
                Ui(() =>
                {
                    if (CurrentGame != g) return;
                    activity = act;
                    string text;
                    if (act != null && act.Count > 0)
                    {
                        int total = act.Values.Sum(m => Stats.EstimatePlayers(m));
                        text = T("Playing right now (est.): ", "يلعبون دحين تقريباً: ") + Stats.Approx(total) + T("", " لاعب") + "   |   " +
                               string.Join("   ", act.OrderByDescending(kv => kv.Value).Take(4).Select(kv =>
                                   (Stats.MmRegions.ContainsKey(kv.Key) ? Lang.T(Stats.MmRegions[kv.Key][0], Stats.MmRegions[kv.Key][1]) : kv.Key) +
                                   " " + Stats.Approx(Stats.EstimatePlayers(kv.Value))));
                    }
                    else if (steam >= 0)
                        text = T("Online now on Steam: ", "أونلاين دحين على Steam: ") + steam.ToString("N0") +
                               T(" players (all regions together)", " لاعب (كل المناطق مع بعض)");
                    else if (g.Provider == Provider.Custom)
                        text = "";
                    else
                        text = T("Player numbers aren't available for this game.", "عدد اللاعبين مو متوفر لهادي اللعبة.");
                    infoLabel.Text = text;
                    infoLabel.ForeColor = TextC;
                    FillServers(g); // regroup now that the activity numbers are in
                });
            });
        }

        // Region of a Valve game that owns this IP (relay or game server range), or null.
        static Region ValveRegionOf(Game g, string ip)
        {
            return g.Regions.FirstOrDefault(r => r.Cidrs.Any(c => Detector.InCidr(ip, c)));
        }

        void DetectServer()
        {
            var g = CurrentGame;
            if (g == null || busy) return;
            if (g.Processes.Length > 0 && !Detector.IsRunning(g.Processes))
            {
                Msg(T(g.Name + " isn't running. Open it, join a match, then press this again.",
                      "اللعبة مو مفتوحة. افتح " + g.Name + " وادخل مباراة، وبعدين اضغط الزر مرة ثانية."));
                return;
            }
            if (!Ask(T("Join a real match first (not the menu or practice).\n\n2X Ping will watch the game's traffic for 15 seconds and tell you which server you are on. Start?",
                       "ادخل مباراة حقيقية أول (مو القائمة ولا التدريب).\n\nبراقب ترافيك اللعبة 15 ثانية وأقولك على أي سيرفر إنت. نبدأ؟")))
                return;
            busy = true;
            btnDetect.Enabled = false;
            SetStatus(T("Watching the game's traffic... (15 s)", "أراقب ترافيك اللعبة... (15 ثانية)"));
            Task.Run(() =>
            {
                try
                {
                    bool matched;
                    var hits = Detector.Capture(g.Processes, 15, out matched);
                    if (g.Provider == Provider.Valve) Ui(() => ShowValveResult(g, hits));
                    else ShowOtherResult(g, hits);
                }
                catch (Exception ex)
                {
                    Log(T("Server check failed: ", "ما زبط الفحص: ") + ex.Message);
                    Ui(() => { busy = false; btnDetect.Enabled = true; Msg(T("Server check failed: ", "ما زبط الفحص: ") + ex.Message); });
                }
            });
        }

        void NothingFound()
        {
            SetStatus(T("No game traffic found.", "ما لقيت ترافيك لعبة."));
            Msg(T("I didn't see any game server traffic.\n\nMake sure you are inside a real match (not the menu or practice) and try again.",
                  "ما شفت أي ترافيك من سيرفر لعبة.\n\nتأكد إنك داخل مباراة حقيقية (مو القائمة ولا التدريب) وجرّب مرة ثانية."));
        }

        // Valve: the address is one of Valve's relays/servers, so we can name the region and check it against the lock.
        void ShowValveResult(Game g, List<Detector.Hit> hits)
        {
            busy = false;
            btnDetect.Enabled = true;
            foreach (var r in g.Regions) r.Here = false;
            var found = hits.Select(h => ValveRegionOf(g, h.Ip)).Where(r => r != null).Distinct().ToList();
            foreach (var h in hits) Log(T("Game traffic: ", "ترافيك اللعبة: ") + h.Ip + ":" + h.Port + "  (" + h.Packets + ")");
            if (found.Count == 0)
            {
                if (CurrentGame == g) FillServers(g);
                NothingFound();
                return;
            }
            foreach (var r in found) r.Here = true;
            if (CurrentGame == g)
            {
                FillServers(g);
                foreach (ListViewItem item in serverList.Items)
                    if (item.Tag == found[0]) { item.Selected = true; item.EnsureVisible(); }
            }
            string names = string.Join("، ", found.Select(ShortName));
            SetStatus(T("You are on: ", "إنت دحين على: ") + names);

            string verdict = "";
            if (lockSummary != null)
                verdict = found.All(r => r.Picked)
                    ? T("\n\n✓ That's one of your picked servers. The lock is working.", "\n\n✓ هادا من السيرفرات اللي اخترتها، يعني التثبيت شغّال صح.")
                    : T("\n\n⚠ That's NOT one of your picked servers. Restart the game and queue again.",
                        "\n\n⚠ هادا مو من السيرفرات اللي اخترتها! طلّع من اللعبة ورجّع ادخل، وبعدين دوّر مباراة.");
            Msg(T("You are playing on:\n", "إنت تلعب دحين على:\n") + names + verdict);
        }

        // Other games: save the found server so it can be pinged and boosted later.
        void ShowOtherResult(Game g, List<Detector.Hit> hits)
        {
            var found = new List<Region>();
            foreach (var h in hits.Take(3))
            {
                string loc = Stats.Locate(h.Ip);
                Storage.AddDetected(g.Name, h.Ip, loc);
                var r = Storage.MakeDetected(h.Ip, loc);
                r.Direct = Pinger.Measure(r.Endpoints[0], 4, 1200);
                r.BestEndpoint = r.Direct.Ok ? r.Endpoints[0] : null;
                found.Add(r);
                Log(T("Game server: ", "سيرفر اللعبة: ") + h.Ip + ":" + h.Port + "  " + (loc ?? "") + "  (" + h.Packets + ")");
            }
            Ui(() =>
            {
                busy = false;
                btnDetect.Enabled = true;
                if (found.Count == 0) { NothingFound(); return; }
                foreach (var r in found)
                {
                    g.Regions.RemoveAll(x => x.Id == r.Id);
                    g.Regions.Insert(0, r);
                }
                if (CurrentGame == g) FillServers(g);
                var top = found[0];
                SetStatus(T("You are on: ", "إنت دحين على: ") + top.Name);
                Msg(T("You are playing on:\n", "إنت تلعب دحين على:\n") + top.Name + "\n" +
                    (top.Direct.Ok ? T("Ping: ", "البنق: ") + Ms(top.Direct.Avg)
                                   : T("This server doesn't answer ping (normal for some games).", "السيرفر ما يرد على البنق (عادي في بعض الألعاب).")) +
                    T("\n\nIt's saved at the top of the list.", "\n\nحفظته فوق في القائمة."));
            });
        }

        // ---------------------------------------------------------------- server lock (Valve)

        void TogglePick(Region r)
        {
            var g = CurrentGame;
            r.Picked = !r.Picked;
            Storage.SavePicks(g.AppId, g.Regions.Where(x => x.Picked).Select(x => x.Id));
            UpdateRegion(r);
            UpdateLockBar();
        }

        void UpdateLockBar()
        {
            var g = CurrentGame;
            if (g == null || !g.CanLock) return;
            int picked = g.Regions.Count(r => r.Picked);
            if (lockSummary != null)
            {
                lockLabel.Text = T("Locked: you will only play on ", "مثبّت: ما راح تلعب إلا على ") + lockSummary;
                lockLabel.ForeColor = Accent;
            }
            else
            {
                lockLabel.Text = picked == 0
                    ? T("Tick the servers you want, then press Lock my servers.", "علّم (✓) على السيرفرات اللي تبغاها، وبعدين اضغط «ثبّت سيرفراتي».")
                    : T("You picked " + picked + ". Press Lock my servers to play only there.",
                        "اخترت " + picked + (picked == 1 ? " منطقة" : " مناطق") + "، اضغط «ثبّت سيرفراتي» عشان ما تلعب إلا عليها.");
                lockLabel.ForeColor = Dim;
            }
            btnUnlock.Enabled = lockSummary != null;
        }

        void LockServers()
        {
            var g = CurrentGame;
            if (g == null || !g.CanLock) return;
            var picked = g.Regions.Where(r => r.Picked).ToList();
            if (picked.Count == 0)
            {
                Msg(T("Pick at least one server first (click the box next to its name).",
                      "اختار سيرفر واحد على الأقل (اضغط المربع اللي جنب اسمه)."));
                return;
            }
            var keep = new HashSet<string>(picked.SelectMany(r => r.Cidrs));
            var block = g.Regions.Where(r => !r.Picked && !r.Detected).SelectMany(r => r.Cidrs).Where(c => !keep.Contains(c)).ToList();
            string names = string.Join("، ", picked.Select(ShortName));
            btnLock.Enabled = false;
            SetStatus(T("Locking your servers...", "لحظة، أثبّت سيرفراتك..."));
            Task.Run(() =>
            {
                try
                {
                    int n = ServerLock.Apply(block);
                    Ui(() =>
                    {
                        lockSummary = names;
                        btnLock.Enabled = true;
                        UpdateLockBar();
                        FillServers(g);
                        Log(T("Lock ON (", "التثبيت شغّال (") + g.Name + "): " + names + "  (" + n + ")");
                        SetStatus(T("Locked: ", "مثبّت على: ") + names);
                        Msg(T("Done! You will only be matched on: " + names +
                              "\n\nIf the game is open, quit and reopen it (or wait a minute) before you queue." +
                              "\nTo double-check, join a match and press Which server am I on?" +
                              "\n\nThe lock is removed when you press Unlock or close 2X Ping.",
                              "تمام! دحين ما راح تلعب إلا على: " + names +
                              "\n\nلو اللعبة مفتوحة، طلّع منها ورجّع افتحها (أو استنى دقيقة) قبل ما تدوّر مباراة." +
                              "\nوعشان تتأكد بنفسك: ادخل مباراة واضغط «على أي سيرفر أنا؟»." +
                              "\n\nالتثبيت ينشال لحاله لما تضغط «فك التثبيت» أو تقفل البرنامج."));
                    });
                }
                catch (Exception ex)
                {
                    Ui(() =>
                    {
                        btnLock.Enabled = true;
                        Log(T("Lock failed: ", "ما زبط التثبيت: ") + ex.Message);
                        Msg(T("Lock failed: ", "ما زبط التثبيت: ") + ex.Message);
                    });
                }
            });
        }

        void UnlockServers()
        {
            if (lockSummary == null) return;
            lockSummary = null;
            UpdateLockBar();
            var g = CurrentGame;
            if (g != null) FillServers(g);
            Task.Run(() =>
            {
                ServerLock.Remove();
                Log(T("Lock removed, all servers are back.", "فكّينا التثبيت، وكل السيرفرات رجعت."));
                SetStatus(T("Lock removed.", "فكّينا التثبيت."));
            });
        }

        // Undo everything 2X Ping changed on this PC, then verify nothing is left.
        public void ResetEverything()
        {
            if (busy)
            {
                Msg(T("Wait for the current test to finish first.", "استنى الفحص الحالي يخلص أول."));
                return;
            }
            if (!Ask(T("This puts your PC back exactly as it was before 2X Ping:\n\n" +
                       "- removes the server lock from Windows Firewall\n" +
                       "- stops the boost and any relay route\n" +
                       "- sets the game priority back to normal\n" +
                       "- clears the servers you ticked\n\nContinue?",
                       "بيرجّع جهازك زي ما كان قبل البرنامج بالضبط:\n\n" +
                       "- يشيل تثبيت السيرفرات من جدار حماية ويندوز\n" +
                       "- يوقف البوست وطريق الوسيط\n" +
                       "- يرجّع أولوية اللعبة عادية\n" +
                       "- يمسح السيرفرات اللي علّمت عليها\n\nنكمّل؟")))
                return;

            StopAll();
            lockSummary = null;
            foreach (var g in games) foreach (var r in g.Regions) { r.Picked = false; r.Here = false; }
            Storage.ClearPicks();
            busy = true;
            SetStatus(T("Putting everything back...", "لحظة، أرجّع كل شي زي ما كان..."));
            Task.Run(() =>
            {
                ServerLock.Remove();
                Tunnel.Stop();
                Detector.Cleanup();
                foreach (var g in games)
                    foreach (var p in g.Processes.SelectMany(n => Process.GetProcessesByName(n)))
                        try { p.PriorityClass = ProcessPriorityClass.Normal; } catch { }
                bool left = ServerLock.Exists();
                Ui(() =>
                {
                    busy = false;
                    var cur = CurrentGame;
                    if (cur != null) FillServers(cur);
                    UpdateLockBar();
                    if (left)
                    {
                        Log(T("Some firewall rules could not be removed.", "في قواعد بجدار الحماية ما قدرت أشيلها."));
                        Msg(T("Almost done, but some firewall rules are still there. Close 2X Ping and open it again, it cleans them on start.",
                              "تقريباً خلصنا، بس في قواعد بجدار الحماية لسا موجودة. قفّل البرنامج وافتحه مرة ثانية، وهو ينظفها لحاله أول ما يفتح."));
                    }
                    else
                    {
                        Log(T("Everything is back to normal.", "رجّعنا كل شي زي ما كان."));
                        SetStatus(T("Everything is back to normal.", "رجّعنا كل شي زي ما كان ✓"));
                        Msg(T("Done! Everything is back to how it was.\nNothing from 2X Ping is left on your PC.",
                              "تمام! رجّعنا كل شي زي ما كان ✓\nما في أي تغيير من البرنامج باقي على جهازك."));
                    }
                });
            });
        }

        // ---------------------------------------------------------------- boost

        void Boost()
        {
            var g = CurrentGame;
            if (g == null || !btnBoost.Enabled) return;
            Region region = serverList.SelectedItems.Count > 0 ? serverList.SelectedItems[0].Tag as Region : null;
            if (region == null || LockedOut(g, region))
            {
                region = g.Regions.Where(r => !LockedOut(g, r) && BestLatency(r) < double.MaxValue).OrderBy(BestLatency).FirstOrDefault();
                if (region == null) { Msg(T("Press Test ping first, or pick a server from the list.", "اضغط «افحص البنق» أول، أو اختار سيرفر من القائمة.")); return; }
            }
            var target = region.Target;
            if (target == null) return;

            Relay relay = null;
            if (!simple && routeBox.SelectedIndex >= 2) relay = relays[routeBox.SelectedIndex - 2];
            else if (simple || routeBox.SelectedIndex == 0) relay = AutoRelay(region);

            boosting = true;
            btnBoost.Enabled = false;
            btnStop.Enabled = true;

            if (relay == null)
            {
                Log(region.Name + ": " + T("direct route", "طريق مباشر") +
                    (simple || routeBox.SelectedIndex == 0 ? T(" (already the fastest)", " (هو الأسرع أصلاً)") : ""));
                RaisePriority(g);
                StartMonitor(target, ShortName(region));
                return;
            }

            if (!Tunnel.Installed)
            {
                if (Ask(T("Relays need WireGuard for Windows (free). Open the download page?",
                          "عشان تستخدم الوسيط لازم برنامج WireGuard (مجاني). أفتح لك صفحة التحميل؟")))
                    Brand.Open("https://www.wireguard.com/install/");
                StopAll();
                return;
            }

            SetStatus(T("Building the route through ", "أجهّز الطريق عن طريق ") + relay.Name + "...");
            Task.Run(() =>
            {
                try
                {
                    var cidrs = new List<string>();
                    if (g.Provider == Provider.Aws) cidrs.AddRange(GameDb.AwsCidrs(region.Id));
                    else cidrs.AddRange(region.Cidrs);
                    foreach (var ep in region.Endpoints)
                    {
                        var ip = Pinger.TryResolve(ep.Host);
                        if (ip != null) cidrs.Add(ip + "/32");
                    }
                    cidrs = cidrs.Distinct().ToList();
                    Tunnel.Start(Tunnel.BuildConf(relay, cidrs));
                    tunnelActive = true;
                    Log(T("Route ready through ", "الطريق جاهز عن طريق ") + relay.Name + " - " + region.Name + " (" + cidrs.Count + ")");
                    Ui(() => { RaisePriority(g); StartMonitor(target, ShortName(region) + T(" via ", " عن طريق ") + relay.Name); });
                }
                catch (Exception ex)
                {
                    Log(T("Boost failed: ", "ما زبط البوست: ") + ex.Message);
                    Ui(() => { StopAll(); Msg(ex.Message); });
                }
            });
        }

        void StartMonitor(Endpoint target, string label)
        {
            int gen = ++monitorGen;
            graph.Clear();
            if (simple) { graph.Visible = true; bottomPanel.Height = 200; }
            graph.Label = label;
            SetStatus(T("BOOST is on: ", "البوست شغّال: ") + label);
            var t = new Thread(() =>
            {
                while (gen == monitorGen)
                {
                    double v = Pinger.Once(target, 1500);
                    if (gen != monitorGen) break;
                    Ui(() =>
                    {
                        if (gen != monitorGen) return;
                        liveLabel.Text = Ms(v) + "  ·  " + label;
                        liveLabel.ForeColor = PingColor(v);
                        graph.Add(v);
                    });
                    Thread.Sleep(1000);
                }
            }) { IsBackground = true };
            t.Start();
        }

        void StopAll()
        {
            monitorGen++;
            boosting = false;
            liveLabel.Text = T("OFF", "مو شغّال");
            liveLabel.ForeColor = Dim;
            if (simple) { graph.Visible = false; bottomPanel.Height = 56; }
            btnBoost.Enabled = true;
            btnStop.Enabled = false;
            SetStatus(T("Stopped.", "وقّفناه."));
            if (tunnelActive)
            {
                tunnelActive = false;
                Task.Run(() =>
                {
                    Tunnel.Stop();
                    Log(T("Relay route removed, everything is direct again.", "شلنا طريق الوسيط، وكل شي رجع مباشر."));
                });
            }
        }

        void RaisePriority(Game g)
        {
            if (g == null || g.Processes.Length == 0) return;
            var ps = g.Processes.SelectMany(n => Process.GetProcessesByName(n)).ToList();
            if (ps.Count == 0) { Log(T(g.Name + " isn't running, so its priority wasn't changed.", g.Name + " مو مفتوحة، فما غيّرت أولويتها.")); return; }
            foreach (var p in ps)
            {
                try
                {
                    p.PriorityClass = ProcessPriorityClass.High;
                    Log(T("Raised " + g.Name + " priority to High", "رفعت أولوية " + g.Name + " لعالية") + " (pid " + p.Id + ")");
                }
                catch (Exception ex) { Log(T("Couldn't change the priority: ", "ما قدرت أغيّر الأولوية: ") + ex.Message); }
            }
        }

        // ---------------------------------------------------------------- relays

        void RefreshRelays()
        {
            relayList.BeginUpdate();
            relayList.Items.Clear();
            foreach (var r in relays)
            {
                var item = new ListViewItem(r.Name) { UseItemStyleForSubItems = false, Tag = r, ForeColor = TextC };
                item.SubItems.Add(r.EndpointHost ?? T("(config file missing)", "(ملف الإعداد مو موجود)")).ForeColor = TextC;
                item.SubItems.Add(r.Ping < 0 ? "-" : Ms(r.Ping)).ForeColor = r.Ping < 0 ? Dim : PingColor(r.Ping);
                item.SubItems.Add(r.HasAgent ? r.AgentUrl : T("none (no ping estimate)", "ما في (بدون تقدير للبنق)")).ForeColor = Dim;
                relayList.Items.Add(item);
            }
            relayList.EndUpdate();

            int sel = routeBox.SelectedIndex;
            routeBox.Items.Clear();
            routeBox.Items.Add(T("Auto (fastest)", "تلقائي (الأسرع)"));
            routeBox.Items.Add(T("Direct (no relay)", "مباشر بدون وسيط"));
            foreach (var r in relays) routeBox.Items.Add(T("via ", "عن طريق ") + r.Name);
            routeBox.SelectedIndex = sel >= 0 && sel < routeBox.Items.Count ? sel : 0;
        }

        void AddRelay()
        {
            string path;
            using (var dlg = new OpenFileDialog { Filter = "WireGuard config (*.conf)|*.conf", Title = T("Pick the relay's client.conf", "اختار ملف client.conf حق الوسيط") })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                path = dlg.FileName;
            }
            var probe = new Relay { ConfPath = path };
            string host = probe.EndpointHost;
            if (host == null) { Msg(T("This file has no 'Endpoint =' line.", "الملف هادا ما فيه سطر 'Endpoint ='.")); return; }

            var vals = InputDialog.Show(this, T("Add a relay", "ضيف وسيط"),
                new[] { T("Name", "الاسم"), T("Agent link (from setup-relay.sh, optional)", "رابط أداة القياس (من setup-relay.sh، مو ضروري)"), T("Agent token", "رمز أداة القياس (Token)") },
                new[] { Path.GetFileNameWithoutExtension(path), "http://" + host + ":8787", "" });
            if (vals == null || vals[0] == "") return;
            if (relays.Any(r => r.Name.Equals(vals[0], StringComparison.OrdinalIgnoreCase)))
            {
                Msg(T("There's already a relay with that name.", "في وسيط بنفس الاسم."));
                return;
            }

            string safe = new string(vals[0].Select(c => char.IsLetterOrDigit(c) && c < 128 ? c : '_').ToArray());
            string dir = Path.Combine(Storage.Dir, "relays");
            Directory.CreateDirectory(dir);
            string dest = Path.Combine(dir, safe + ".conf");
            File.Copy(path, dest, true);

            relays.Add(new Relay { Name = vals[0], ConfPath = dest, AgentUrl = vals[1], Token = vals[2] });
            Storage.SaveRelays(relays);
            RefreshRelays();
            Log(T("Relay added: ", "ضفنا الوسيط: ") + vals[0] + " (" + host + ")");
        }

        void RemoveRelay()
        {
            if (relayList.SelectedItems.Count == 0) return;
            var r = (Relay)relayList.SelectedItems[0].Tag;
            relays.Remove(r);
            Storage.SaveRelays(relays);
            RefreshRelays();
            Log(T("Relay removed: ", "حذفنا الوسيط: ") + r.Name);
        }

        void TestRelays()
        {
            SetStatus(T("Testing relays...", "أفحص سيرفرات الوسيط..."));
            var rels = relays.ToList();
            Task.Run(() =>
            {
                foreach (var r in rels)
                {
                    r.Ping = r.MeasurePing();
                    string agent = !r.HasAgent ? T("no agent", "بدون أداة قياس")
                        : r.ProbeFromRelay(new Endpoint("1.1.1.1", 443)) >= 0 ? T("agent OK", "أداة القياس شغّالة") : T("agent not reachable", "أداة القياس ما ترد");
                    Log(T("Relay ", "الوسيط ") + r.Name + ": " + Ms(r.Ping) + "، " + agent);
                }
                Ui(() => { RefreshRelays(); SetStatus(T("Relay test done, details in Tools & Log.", "خلص فحص الوسطاء، التفاصيل في «أدوات وسجل».")); });
            });
        }

        // ---------------------------------------------------------------- tools

        void FlushDns()
        {
            var psi = new ProcessStartInfo("ipconfig", "/flushdns") { UseShellExecute = false, CreateNoWindow = true };
            using (var p = Process.Start(psi))
            {
                p.WaitForExit(10000);
                Log(p.ExitCode == 0 ? T("DNS cache cleared.", "مسحنا كاش DNS.") : T("Couldn't clear the DNS cache.", "ما قدرت أمسح كاش DNS."));
            }
        }

        void AddCustomServer()
        {
            var vals = InputDialog.Show(this, T("Add a server", "ضيف سيرفر"),
                new[] { T("Name (e.g. My Rust server)", "الاسم (مثلاً: سيرفر راست حقي)"),
                        T("Servers: host or host:port, comma separated (no port = normal ping)", "السيرفرات: IP أو IP:منفذ، وافصل بينهم بفاصلة (بدون منفذ = بنق عادي)"),
                        T("IP ranges to send through a relay (optional, e.g. 1.2.3.0/24)", "نطاقات IP تمشي عن طريق الوسيط (مو ضروري، مثلاً 1.2.3.0/24)") },
                new[] { "", "", "" });
            if (vals == null || vals[0] == "" || vals[1] == "") return;
            Storage.AddCustom(vals[0], vals[1], vals[2]);
            Log(T("Server added: ", "ضفنا السيرفر: ") + vals[0]);
            var custom = games.First(x => x.Provider == Provider.Custom);
            custom.Loaded = false;
            if (CurrentGame == custom) OnGameChanged(); else gameList.SelectedItem = custom;
        }
    }

    static class InputDialog
    {
        public static string[] Show(IWin32Window owner, string title, string[] labels, string[] defaults)
        {
            using (var f = new Form())
            {
                f.Text = title;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.MinimizeBox = f.MaximizeBox = false;
                f.BackColor = MainForm.Bg;
                f.ForeColor = MainForm.TextC;
                f.Font = new Font("Segoe UI", 10f);
                f.RightToLeft = Lang.Ar ? RightToLeft.Yes : RightToLeft.No;
                f.ClientSize = new Size(520, 24 + labels.Length * 62 + 56);

                var boxes = new TextBox[labels.Length];
                for (int i = 0; i < labels.Length; i++)
                {
                    f.Controls.Add(new Label { Text = labels[i], ForeColor = MainForm.Dim, Bounds = new Rectangle(16, 14 + i * 62, 488, 22) });
                    boxes[i] = new TextBox
                    {
                        Text = defaults[i], Bounds = new Rectangle(16, 38 + i * 62, 488, 26), RightToLeft = RightToLeft.No,
                        BackColor = MainForm.Panel2, ForeColor = MainForm.TextC, BorderStyle = BorderStyle.FixedSingle
                    };
                    f.Controls.Add(boxes[i]);
                }
                int y = 24 + labels.Length * 62 + 8;
                var ok = new Button { Text = Lang.T("OK", "تمام"), DialogResult = DialogResult.OK, Bounds = new Rectangle(Lang.Ar ? 112 : 320, y, 88, 34), FlatStyle = FlatStyle.Flat, BackColor = MainForm.Accent, ForeColor = Color.Black };
                var cancel = new Button { Text = Lang.T("Cancel", "إلغاء"), DialogResult = DialogResult.Cancel, Bounds = new Rectangle(Lang.Ar ? 16 : 416, y, 88, 34), FlatStyle = FlatStyle.Flat, BackColor = MainForm.Panel2 };
                ok.FlatAppearance.BorderSize = cancel.FlatAppearance.BorderSize = 0;
                f.Controls.Add(ok);
                f.Controls.Add(cancel);
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                return f.ShowDialog(owner) == DialogResult.OK ? boxes.Select(b => b.Text.Trim()).ToArray() : null;
            }
        }
    }

    class PingGraph : Control
    {
        const int Max = 90;
        readonly List<double> samples = new List<double>();
        public string Label = "";

        public PingGraph()
        {
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            BackColor = MainForm.Panel1;
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
                    TextRenderer.DrawText(g, Math.Round(max * i / 4).ToString(), Font, new Point(6, y - 8), MainForm.Dim);
                }

            if (samples.Count == 0)
            {
                TextRenderer.DrawText(g, Lang.T("Your live ping shows up here once BOOST is on", "البنق المباشر حقك يطلع هنا لما تشغّل البوست"),
                    Font, rc, MainForm.Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | Lang.Flags);
                return;
            }

            float step = rc.Width / (float)(Max - 1);
            float x0 = rc.Right - (samples.Count - 1) * step;
            using (var pen = new Pen(MainForm.Accent, 2f))
            using (var bad = new SolidBrush(MainForm.Bad))
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
            TextRenderer.DrawText(g, stats, Font, new Point(rc.Left, 4), MainForm.TextC, Lang.Flags);
        }
    }
}
