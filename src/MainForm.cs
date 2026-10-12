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

        static string StatusOff { get { return T("Status: ready", "الحالة: جاهز"); } }

        readonly List<Game> allGames; // every game the app knows
        List<Game> games;              // the ones shown: released games, plus the rest in developer mode
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
        readonly HashSet<Game> tested = new HashSet<Game>(); // games tested since the app opened
        volatile bool tunnelActive;
        int monitorGen;
        string lockSummary; // null = no region lock
        Dictionary<string, int> activity; // Deadlock matches/hour per matchmaking region

        Game CurrentGame { get { return gameList.SelectedItem as Game; } }
        bool LockedOut(Game g, Region r) { return lockSummary != null && g != null && g.CanLock && !r.Picked && g.Hosts(r); }

        public MainForm()
        {
            Lang.Ar = Storage.LoadArabic();
            simple = Prefs.Simple;
            devMode = DevModeSaved;
            allGames = GameDb.All();
            GameDb.ShowUnreleased = devMode;
            games = GameDb.Available(allGames);
            // developer mode: every problem the app detects shows up right away
            Issues.Reported += r =>
            {
                if (!devMode) return;
                Ui(() => { Msg("Problem · " + r.Code + ": " + r.Detail, ToastKind.Warn); DevOut("problem  " + r.Code + ": " + r.Detail); });
            };
            relays = Storage.LoadRelays();

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1240, 760);
            MinimumSize = new Size(980, 640);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Bg;
            ForeColor = TextC;
            Font = Fonts.Ui(10f);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            ApplyDirection();
            BuildUi();
            ResumeLayout(false);

            RefreshRelays();
            Task.Run(() =>
            {
                // leftovers from a crash, and a firewall that's off (locks can't work then)
                if (ServerLock.Exists()) { Issues.Report("leftover_lock", "rules from a previous session were still there"); ServerLock.Remove(); }
                if (!ServerLock.FirewallOn()) Issues.Report("firewall_off", "Windows Firewall is off for the current network profile");
            });
            if (!Tunnel.Installed)
                Log(T("WireGuard is not installed, so relay routing is off until you install it (Tools).",
                      "برنامج WireGuard مو مثبّت، فالوسيط مقفّل لين تثبّته (من «أدوات وسجل»)."));
            BuildTray();
            Shown += delegate
            {
                // first launch: name + games + statistics notice; using the beta requires accepting it
                if (!Telemetry.HasConsent)
                {
                    if (!WelcomeDialog.Show(this)) { Close(); return; }
                    FillGames(); // the games picked on the welcome screen
                }
                long startupMs = 0;
                try { startupMs = (long)(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds; } catch { }
                Telemetry.Start(allGames, startupMs);
                if (startupMs > 8000) Issues.Report("slow_startup", startupMs + " ms from launch to the window",
                    new Dictionary<string, object> { { "ms", startupMs } });
                SelectStartGame();
                StartAutoDetect();
                // first launch only: two short lines about the beta
                if (string.IsNullOrEmpty(Storage.GetSetting("beta_ack")))
                {
                    Storage.SetSetting("beta_ack", Brand.Version);
                    Msg(T("This is a beta, so you may run into small bugs. Found one, or have an idea? Press Feedback at the top.",
                          "هادي نسخة بيتا، فممكن تطلع لك أخطاء بسيطة. لقيت شي أو عندك فكرة؟ اضغط «تواصل معنا» فوق."));
                    Msg(T("Player numbers and wait times are estimates, and 2X Ping never touches game files.",
                          "عدد اللاعبين ووقت الانتظار تقريبية، والبرنامج ما يلمس ملفات اللعبة."));
                }
                if (Updater.JustUpdated) ShowUpdatedNote();
                CheckForUpdate(true);
                // keep checking while the app stays open (it can run for days next to the clock)
                var updates = new System.Windows.Forms.Timer { Interval = 3 * 60 * 60 * 1000 };
                updates.Tick += delegate { CheckForUpdate(false); };
                updates.Start();
            };
            FormClosing += delegate
            {
                monitorGen++;
                if (tunnelActive) Tunnel.Stop();
                if (lockSummary != null) ServerLock.Remove();
                tray.Visible = false;
                Telemetry.Exit();
            };
        }

        // The game that is running right now, otherwise the one used last time, otherwise the first.
        void SelectStartGame()
        {
            if (gameList.Items.Count == 0) return;
            var list = gameList.Items.Cast<Game>().ToList();
            var pick = list.FirstOrDefault(g => g.Processes.Length > 0 && Detector.IsRunning(g.Processes));
            if (pick != null) Log(T(pick.Name + " is running, so it's selected.", pick.Name + " مفتوحة، فاخترتها لك."));
            else pick = list.FirstOrDefault(g => g.Icon == Storage.GetSetting("last_game"));
            gameList.SelectedItem = pick ?? list[0];
        }

        // ---------------------------------------------------------------- system tray

        NotifyIcon tray;

        void BuildTray()
        {
            tray = new NotifyIcon { Icon = Icon, Text = Brand.Name, Visible = false };
            tray.DoubleClick += delegate { RestoreFromTray(); };
            tray.BalloonTipClicked += delegate { RestoreFromTray(); };
            BuildTrayMenu();
            Resize += delegate
            {
                if (WindowState != FormWindowState.Minimized || tray.Visible) return;
                // minimized: live next to the clock, so the lock keeps working without the window in the way
                Hide();
                tray.Visible = true;
                Telemetry.Send("use", null, -1, "tray");
                if (Storage.GetSetting("tray_tip") != "1")
                {
                    Storage.SetSetting("tray_tip", "1");
                    tray.ShowBalloonTip(5000, Brand.Name, T("Still running here. Double-click to open it.",
                                                             "البرنامج شغّال هنا. دبل كليك عشان يفتح."), ToolTipIcon.Info);
                }
            };
            Disposed += delegate { tray.Dispose(); };
            // while the player is in the game (this window not in front), the live ping and the automatic server
            // check pause, so nothing runs next to the match; coming back refreshes them right away
            Activated += delegate
            {
                if (DateTime.Now - lastInFront < TimeSpan.FromSeconds(20)) return; // only after a real absence
                lastAutoDetect = DateTime.MinValue;
                RefreshStats(TimeSpan.FromMinutes(1));
                if (autoDetectTimer != null) AutoDetectTick();
            };
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        static readonly uint myPid = (uint)Process.GetCurrentProcess().Id;
        DateTime lastInFront = DateTime.Now;

        // Is one of 2X Ping's windows in front? Asked of Windows each time (focus events can be missed,
        // e.g. when a notice or a dialog takes the focus), so the app never stays paused while it's looked at.
        bool appActive
        {
            get
            {
                uint pid;
                GetWindowThreadProcessId(GetForegroundWindow(), out pid);
                bool front = pid == myPid;
                if (front) lastInFront = DateTime.Now;
                return front;
            }
        }
        DateTime statsAt = DateTime.MinValue;

        // Player numbers: every 5 minutes while the window is in front (they're shared by all copies through
        // Supabase, so this costs deadlock-api nothing); "fresh" = how old they may be before refreshing
        void RefreshStats(TimeSpan fresh)
        {
            var g = CurrentGame;
            if (g == null || !g.Loaded || busy || DateTime.Now - statsAt < fresh) return;
            LoadStats(g, true);
        }

        void BuildTrayMenu()
        {
            var menu = new ContextMenuStrip { RightToLeft = Lang.Ar ? RightToLeft.Yes : RightToLeft.No, Font = Fonts.Ui(10f) };
            menu.Items.Add(T("Open 2X Ping", "افتح البرنامج"), null, delegate { RestoreFromTray(); });
            menu.Items.Add(T("Close 2X Ping", "اقفل البرنامج"), null, delegate { Close(); });
            var old = tray.ContextMenuStrip;
            tray.ContextMenuStrip = menu;
            if (old != null) old.Dispose();
        }

        // Someone opened 2X Ping again while it runs: show this window on top (also from the tray).
        public void BringUp()
        {
            if (IsDisposed) return;
            RestoreFromTray();
            TopMost = true; // a short TopMost flip gets it past the foreground lock
            TopMost = false;
            Activate();
        }

        void RestoreFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            tray.Visible = false;
            Activate();
        }

        // Tooltip of the tray icon (at most 63 characters).
        void SetTrayText(string text)
        {
            text = Brand.Name + " · " + text;
            tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }

        void ApplyDirection()
        {
            // LRM first: keeps "2X" in Latin digits inside a right-to-left title bar
            Text = Brand.Caption + (Brand.IsBeta ? " (Beta)" : "") + "  -  by " + Brand.Developer + (devMode ? "   [DEV]" : "");
            RightToLeft = Lang.Ar ? RightToLeft.Yes : RightToLeft.No;
            RightToLeftLayout = Lang.Ar;
        }

        // ---------------------------------------------------------------- UI

        void BuildUi()
        {
            var header = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Panel1 };
            header.Controls.Add(liveLabel = new Chip
            {
                Text = StatusOff, Font = Fonts.Semi(11f), ForeColor = Dim, Width = 430,
                Dock = DockStyle.Right, TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 14, 0)
            });
            header.Controls.Add(HeaderButton(T("Settings", "الإعدادات"), 120, TextC, OpenSettings));
            header.Controls.Add(HeaderButton(T("Feedback", "تواصل معنا"), 120, Warn, () =>
            {
                Telemetry.Send("use", null, -1, "feedback");
                ContactDialog.Show(this, logBox.Text);
            }));

            var logo = new PictureBox
            {
                Image = Brand.Logo, SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(14, 10, 44, 44),
                Cursor = Cursors.Hand, BackColor = Color.Transparent
            };
            // 5 quick clicks on the logo open the developer dashboard (MouseDown: rapid clicks would merge into double-clicks)
            int logoClicks = 0;
            DateTime lastClick = DateTime.MinValue;
            logo.MouseDown += delegate
            {
                logoClicks = DateTime.Now - lastClick < TimeSpan.FromSeconds(1) ? logoClicks + 1 : 1;
                lastClick = DateTime.Now;
                if (logoClicks >= 5) { logoClicks = 0; ToggleDevMode(); }
            };
            header.Controls.Add(logo);
            header.Controls.Add(new Label
            {
                Text = "2X PING", Font = Fonts.Bold(17f), ForeColor = Accent,
                AutoSize = true, Location = new Point(64, 4)
            });
            if (Brand.IsBeta)
                header.Controls.Add(new Label
                {
                    Text = "BETA", Font = Fonts.Bold(7.5f), ForeColor = Color.Black, BackColor = Warn,
                    AutoSize = false, Bounds = new Rectangle(178, 11, 42, 18), TextAlign = ContentAlignment.MiddleCenter
                });
            if (devMode)
                header.Controls.Add(new Label
                {
                    Text = "DEV", Font = Fonts.Bold(7.5f), ForeColor = Color.Black, BackColor = DevAccent,
                    AutoSize = false, Bounds = new Rectangle(226, 11, 38, 18), TextAlign = ContentAlignment.MiddleCenter
                });
            header.Controls.Add(new Label
            {
                Text = T("by " + Brand.Developer + "  ·  lower ping, better games", "بنق أقل، لعب أحلى  ·  من " + Brand.Developer),
                ForeColor = Dim, AutoSize = true, Location = new Point(67, 38), Font = Fonts.Ui(9f)
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

            var left = new Panel { Dock = DockStyle.Left, Width = 252, BackColor = Panel1, Padding = new Padding(10, 6, 10, 10) };
            gameList = new ListBox
            {
                Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Panel1, ForeColor = TextC,
                DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 40, IntegralHeight = false
            };
            FillGames();
            gameList.DrawItem += DrawGameItem;
            gameList.MouseMove += (s, e) =>
            {
                int i = gameList.IndexFromPoint(e.Location);
                if (i == hotGame) return;
                hotGame = i;
                gameList.Invalidate();
            };
            gameList.MouseLeave += delegate { hotGame = -1; gameList.Invalidate(); };
            gameList.SelectedIndexChanged += delegate { OnGameChanged(); };
            left.Controls.Add(gameList);
            left.Controls.Add(new Label
            {
                Text = T("GAMES", "الألعاب"), ForeColor = Dim, Dock = DockStyle.Top, Height = 30,
                Font = Fonts.Semi(9.5f), TextAlign = ContentAlignment.MiddleLeft
            });

            var center = centerPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 16, 20, 16) };
            var content = new Panel { Dock = DockStyle.Fill };
            tabBar = new Panel { Dock = DockStyle.Top, Height = 46, Visible = !simple || devMode };
            center.Controls.Add(content);
            center.Controls.Add(tabBar);

            var names = new List<string> { T("Servers", "السيرفرات"), T("Relays", "سيرفرات الوسيط"), T("Tools & Log", "أدوات وسجل") };
            if (devMode) names.Add(T("Developer", "المطوّر"));
            tabButtons = new Button[names.Count];
            for (int i = 0; i < names.Count; i++)
            {
                var b = MakeButton(names[i], Panel2);
                b.Bounds = new Rectangle(i * 156, 0, 148, 34);
                int idx = i;
                b.Click += delegate { ShowPage(idx); };
                tabBar.Controls.Add(b);
                tabButtons[i] = b;
            }

            var pageList = new List<Panel> { BuildServersPage(), BuildRelaysPage(), BuildToolsPage() };
            if (devMode) pageList.Add(BuildDevPage());
            pages = pageList.ToArray();
            foreach (var p in pages) { p.Dock = DockStyle.Fill; content.Controls.Add(p); }

            // "a new version is out" strip under the header (hidden until there is one)
            updateBar = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Color.FromArgb(58, 44, 30), Padding = new Padding(14, 7, 14, 7), Visible = false };
            updateBtn = MakeButton(T("Update now", "حدّث دحين"), Warn);
            updateBtn.ForeColor = Color.Black;
            updateBtn.Font = Fonts.Semi(10f);
            updateBtn.Dock = DockStyle.Right;
            updateBtn.Width = 130;
            updateBtn.Click += delegate { if (pendingUpdate != null && UpdateDialog.Show(this, pendingUpdate, false)) CloseForUpdate(); };
            updateLabel = new Label { Dock = DockStyle.Fill, ForeColor = Warn, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            updateBar.Controls.Add(updateLabel);
            updateBar.Controls.Add(updateBtn);

            Controls.Add(center);
            Controls.Add(left);
            Controls.Add(updateBar);
            if (devMode) Controls.Add(BuildDevStrip());
            Controls.Add(header);
            Controls.Add(statusLabel);
            if (Lang.Ar) foreach (Control c in Controls) MirrorChildren(c);
            ShowPage(simple && !devMode ? 0 : currentPage);
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
            heroCard = new Card { Dock = DockStyle.Top, Height = 96, Fill = Panel2, Radius = 14, Padding = new Padding(20, 14, 16, 14) };
            heroBtn = MakeButton(T("Play on it", "العب عليه"), Accent);
            heroBtn.ForeColor = Color.Black;
            heroBtn.Font = Fonts.Semi(11f);
            heroBtn.Dock = DockStyle.Fill;
            heroBtn.Visible = false;
            var btnWrap = new Panel { Dock = DockStyle.Right, Width = 170, Padding = new Padding(0, 12, 0, 12) };
            btnWrap.Controls.Add(heroBtn);
            heroBtn.Click += delegate { var r = heroBtn.Tag as Region; if (r != null) PlayOn(r); };
            heroSub = new Label { Dock = DockStyle.Fill, ForeColor = Dim, TextAlign = ContentAlignment.TopLeft, AutoEllipsis = true };
            heroTitle = new Label
            {
                Dock = DockStyle.Top, Height = 38, ForeColor = TextC, Font = Fonts.Semi(15f),
                TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true
            };
            heroCard.Controls.Add(heroSub);
            heroCard.Controls.Add(heroTitle);
            heroCard.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 12 });
            if (simple)
            {
                // simple layout: the only other action, "Test ping", sits next to "Play on it"
                var testWrap = new Panel { Dock = DockStyle.Right, Width = 140, Padding = new Padding(0, 12, 0, 12) };
                btnTest.Dock = DockStyle.Fill;
                btnTest.BackColor = Bg; // darker than the card, so it reads as a button
                testWrap.Controls.Add(btnTest);
                heroCard.Controls.Add(testWrap);
                heroCard.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 10 });
            }
            heroCard.Controls.Add(btnWrap);
            return heroCard;
        }

        Panel BuildServersPage()
        {
            var p = new Panel();
            gameNote = new Label { Dock = DockStyle.Top, Height = 32, ForeColor = Dim, TextAlign = ContentAlignment.MiddleLeft, Visible = !simple };

            // region lock bar (Valve games)
            pickBar = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(4, 0, 4, 8) };
            btnLock = MakeButton(T("Lock my servers", "ثبّت سيرفراتي"), Accent);
            btnLock.ForeColor = Color.Black;
            btnLock.Font = Fonts.Semi(10f);
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
            serverList.DoubleClick += delegate { if (!clickedCheck && !simple) Boost(); };

            // in the simple layout the live graph only appears while BOOST is on
            // the simple layout has no bottom bar: no BOOST (it only matters with a relay), graph hidden
            var bottom = bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 200, Visible = !simple };
            var controls = new Panel { Dock = DockStyle.Bottom, Height = 56 };
            btnTest = MakeButton(T("Test ping", "افحص البنق"), Panel2);
            btnTest.Bounds = new Rectangle(0, 12, 140, 40);
            btnTest.Click += delegate { Telemetry.Send("use", null, -1, "test button"); RunTest(); };
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
            btnBoost.Font = Fonts.Semi(11f);
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

            // one card holds the lock bar and the server list: picking and locking read as one task
            var listCard = new Card { Dock = DockStyle.Fill, Fill = Panel1, Radius = 14, Padding = new Padding(12, 12, 12, 12) };
            listCard.Controls.Add(serverList);
            listCard.Controls.Add(pickBar);
            p.Controls.Add(listCard);
            p.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 12 });
            p.Controls.Add(BuildInfoBar());
            p.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 12 });
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
            bar.Controls.Add(ToolButton(T("Get WireGuard", "نزّل WireGuard"), delegate { Brand.Open("https://www.wireguard.com/install/"); }));
            bar.Controls.Add(ToolButton(T("Data folder", "مجلد البيانات"), delegate { Brand.Open(Storage.Dir); }));
            bar.Controls.Add(ToolButton(T("Back to normal", "رجّع كل شي طبيعي"), ResetEverything));

            string oldLog = logBox != null ? logBox.Text : "";
            logBox = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                BackColor = Panel1, ForeColor = TextC, BorderStyle = BorderStyle.None, Font = Fonts.Ui(9.5f),
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

        static RoundButton MakeButton(string text, Color bg)
        {
            return new RoundButton { Text = text, BackColor = bg, ForeColor = TextC };
        }

        static readonly Color RowHover = Color.FromArgb(54, 42, 78);
        static ListViewItem hotRow; // the row under the mouse

        static Color RowBack(ListViewItem item, int index)
        {
            if (item.Selected) return RowSel;
            if (item == hotRow) return RowHover;
            return index % 2 == 0 ? Panel1 : RowAlt;
        }

        ListView MakeListView(string[] cols, int[] widths)
        {
            var lv = new ListView
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
                HideSelection = false, BorderStyle = BorderStyle.None, BackColor = Panel1, ForeColor = TextC,
                OwnerDraw = true, HeaderStyle = ColumnHeaderStyle.Nonclickable,
                // with an embedded (private) font WinForms stops owner-drawing the header, so the control keeps a
                // system font; all text is still drawn with the form's font (Readex Pro) in the Draw* handlers below
                Font = new Font("Segoe UI", 10f),
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
            lv.MouseMove += (s, e) =>
            {
                var item = lv.GetItemAt(e.X, e.Y);
                if (item == hotRow) return;
                var old = hotRow;
                hotRow = item;
                if (old != null && old.ListView == lv) lv.Invalidate(old.Bounds);
                if (item != null) lv.Invalidate(item.Bounds);
            };
            lv.MouseLeave += delegate { var old = hotRow; hotRow = null; if (old != null && old.ListView == lv) lv.Invalidate(old.Bounds); };
            lv.DrawSubItem += (s, e) =>
            {
                if (lv == serverList && (e.ColumnIndex == 0 || e.Item.Tag is GroupHeader)) return; // drawn by DrawRegionCheck
                var rc = e.ColumnIndex == 0 ? FirstCell(lv, e.Item, e.Bounds) : e.Bounds;
                if (rc.Width <= 0) return;
                using (var b = new SolidBrush(RowBack(e.Item, e.ItemIndex))) e.Graphics.FillRectangle(b, rc);
                var textRc = Rectangle.Inflate(rc, -8, 0);
                if (lv == serverList && e.ColumnIndex == ColPing && e.SubItem.Text.Contains("ms"))
                {
                    // a dot in the ping colour, on the reading side of the number
                    const int d = 8;
                    var size = TextRenderer.MeasureText(e.SubItem.Text, Font);
                    int x = Lang.Ar ? textRc.Right - size.Width - d - 6 : textRc.X;
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using (var b = new SolidBrush(e.SubItem.ForeColor)) e.Graphics.FillEllipse(b, x, rc.Y + (rc.Height - d) / 2f, d, d);
                    if (!Lang.Ar) textRc = new Rectangle(textRc.X + d + 6, textRc.Y, textRc.Width - d - 6, textRc.Height);
                }
                TextRenderer.DrawText(e.Graphics, e.SubItem.Text, Font, textRc, e.SubItem.ForeColor,
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
            int x = Lang.Ar ? cell.Right - 8 - 18 : cell.X + 8;
            return new Rectangle(x, cell.Y + (cell.Height - 18) / 2, 18, 18);
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
            if (g != null && g.CanLock && g.Hosts((Region)e.Item.Tag))
            {
                var r = (Region)e.Item.Tag;
                var box = CheckBox(rc);
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                // the same round pink check as the game tiles
                if (r.Picked)
                {
                    using (var b = new SolidBrush(Accent)) e.Graphics.FillEllipse(b, box);
                    using (var pen = new Pen(Color.Black, 2f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round, LineJoin = System.Drawing.Drawing2D.LineJoin.Round })
                        e.Graphics.DrawLines(pen, new[] { new PointF(box.X + 5f, box.Y + 9.5f), new PointF(box.X + 8f, box.Y + 12.4f), new PointF(box.X + 13.2f, box.Y + 6.3f) });
                }
                else
                {
                    using (var b = new SolidBrush(Panel2)) e.Graphics.FillEllipse(b, box);
                    using (var pen = new Pen(Color.FromArgb(150, Lavender), 1.5f)) e.Graphics.DrawEllipse(pen, box.X + 0.75f, box.Y + 0.75f, box.Width - 1.5f, box.Height - 1.5f);
                }
                textRc = Lang.Ar ? new Rectangle(rc.X + 4, rc.Y, rc.Width - CheckArea - 4, rc.Height)
                                 : new Rectangle(rc.X + CheckArea, rc.Y, rc.Width - CheckArea - 4, rc.Height);
            }

            // country flag (image) on the leading side of the name
            var flag = Brand.Flag(((Region)e.Item.Tag).Flag);
            if (flag != null)
            {
                const int fw = 24, fh = 16, gap = 8;
                int fx = Lang.Ar ? textRc.Right - fw : textRc.X;
                e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                e.Graphics.DrawImage(flag, new Rectangle(fx, rc.Y + (rc.Height - fh) / 2, fw, fh));
                textRc = Lang.Ar ? new Rectangle(textRc.X, textRc.Y, textRc.Width - fw - gap, textRc.Height)
                                 : new Rectangle(textRc.X + fw + gap, textRc.Y, textRc.Width - fw - gap, textRc.Height);
            }
            TextRenderer.DrawText(e.Graphics, e.SubItem.Text, Font, textRc,
                e.SubItem.ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | Align);
        }

        void ShowPage(int idx)
        {
            if (idx < 0 || idx >= pages.Length) idx = 0;
            currentPage = idx;
            for (int i = 0; i < pages.Length; i++)
            {
                pages[i].Visible = i == idx;
                tabButtons[i].BackColor = i == idx ? RowSel : Panel2;
                tabButtons[i].ForeColor = i == idx ? Accent : TextC;
            }
        }

        // only the games the player chose (all of them if none are chosen)
        void FillGames()
        {
            var hidden = Prefs.HiddenGames;
            var shown = games.Where(g => !hidden.Contains(g.Icon)).ToList();
            gameList.Items.Clear();
            foreach (var g in shown.Count > 0 ? shown : games) gameList.Items.Add(g);
        }

        int hotGame = -1; // the game under the mouse

        void DrawGameItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var g = (Game)gameList.Items[e.Index];
            bool sel = (e.State & DrawItemState.Selected) != 0, hot = e.Index == hotGame;
            using (var b = new SolidBrush(Panel1)) e.Graphics.FillRectangle(b, e.Bounds);
            if (sel || hot)
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var pill = new RectangleF(e.Bounds.X + 2, e.Bounds.Y + 3, e.Bounds.Width - 4, e.Bounds.Height - 6);
                using (var path = Shapes.Round(pill, 10))
                using (var b = new SolidBrush(sel ? Panel2 : Color.FromArgb(38, 30, 56))) e.Graphics.FillPath(b, path);
                if (sel)
                    using (var b = new SolidBrush(Accent))
                        e.Graphics.FillRectangle(b, Lang.Ar ? e.Bounds.Right - 5 : e.Bounds.X + 2, e.Bounds.Y + 12, 3, e.Bounds.Height - 24);
            }
            // icon on the leading side, then the name
            const int iconSize = 26;
            var icon = Brand.GameIcon(g.Icon);
            int iconX = Lang.Ar ? e.Bounds.Right - 12 - iconSize : e.Bounds.X + 12;
            if (icon != null)
            {
                e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                e.Graphics.DrawImage(icon, new Rectangle(iconX, e.Bounds.Y + (e.Bounds.Height - iconSize) / 2, iconSize, iconSize));
            }
            var textRc = Lang.Ar ? new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - iconSize - 30, e.Bounds.Height)
                                 : new Rectangle(e.Bounds.X + iconSize + 22, e.Bounds.Y, e.Bounds.Width - iconSize - 28, e.Bounds.Height);
            if (!g.Released)
            {
                // developer mode only: a small "trial" tag on games that aren't in the public version yet
                string tag = T("TRIAL", "تجريبي");
                using (var f = Fonts.Semi(7.5f))
                {
                    var ts = TextRenderer.MeasureText(tag, f);
                    var tagRc = new Rectangle(Lang.Ar ? textRc.X : textRc.Right - ts.Width - 8, e.Bounds.Y + (e.Bounds.Height - 18) / 2, ts.Width + 8, 18);
                    using (var path = Shapes.Round(tagRc, 6))
                    using (var b = new SolidBrush(Color.FromArgb(28, 70, 74))) e.Graphics.FillPath(b, path);
                    TextRenderer.DrawText(e.Graphics, tag, f, tagRc, DevAccent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    textRc = Lang.Ar ? new Rectangle(textRc.X + tagRc.Width + 6, textRc.Y, textRc.Width - tagRc.Width - 6, textRc.Height)
                                     : new Rectangle(textRc.X, textRc.Y, textRc.Width - tagRc.Width - 6, textRc.Height);
                }
            }
            TextRenderer.DrawText(e.Graphics, g.Name, Font, textRc,
                sel ? TextC : Color.FromArgb(206, 196, 226), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | Align);
        }

        bool pendingRebuild; // settings changed while a ping test was running: rebuild when it ends

        void OpenSettings()
        {
            Telemetry.Send("use", null, -1, "settings");
            bool resetAsked;
            if (SettingsDialog.Show(this, out resetAsked))
            {
                Telemetry.Send("settings", null, -1, (Prefs.Simple ? "simple" : "advanced") + ", " + (Lang.Ar ? "ar" : "en"),
                    new Dictionary<string, object> { { "mode", Prefs.Simple ? "simple" : "advanced" }, { "lang", Lang.Ar ? "ar" : "en" },
                                                     { "hidden_games", string.Join(",", Prefs.HiddenGames) }, { "autotest", Prefs.AutoTest } });
                if (busy) pendingRebuild = true;
                else RebuildUi();
            }
            if (resetAsked) ResetEverything();
        }

        // Rebuilds the whole window after a language or layout change.
        void RebuildUi()
        {
            simple = Prefs.Simple;
            devMode = DevModeSaved;
            GameDb.ShowUnreleased = devMode;
            games = GameDb.Available(allGames);
            var g = CurrentGame;
            SuspendLayout();
            var old = Controls.Cast<Control>().ToArray();
            Controls.Clear();
            ApplyDirection();
            BuildUi();
            foreach (var c in old) c.Dispose();
            ResumeLayout(true);
            ShowUpdateBar();
            BuildTrayMenu();
            RefreshRelays();
            btnBoost.Enabled = !boosting;
            btnStop.Enabled = boosting;
            if (g != null && gameList.Items.Contains(g)) gameList.SelectedItem = g;
            else if (gameList.Items.Count > 0) gameList.SelectedIndex = 0; // the open game was hidden in Settings
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

        ChatToasts chat;
        Panel centerPanel;

        // Notices appear as chat lines at the bottom of the window and fade out (no popups, no OK button).
        // While the window is next to the clock, the important ones become a tray balloon instead.
        void Msg(string text, ToastKind kind = ToastKind.Info)
        {
            Ui(() =>
            {
                if (!Visible || WindowState == FormWindowState.Minimized)
                {
                    if (kind != ToastKind.Info && tray.Visible)
                        tray.ShowBalloonTip(5000, Brand.Name, text,
                            kind == ToastKind.Bad ? ToolTipIcon.Error : kind == ToastKind.Warn ? ToolTipIcon.Warning : ToolTipIcon.Info);
                    return;
                }
                if (chat == null) chat = new ChatToasts(this, () => centerPanel.RectangleToScreen(centerPanel.ClientRectangle));
                chat.Show(text, kind);
            });
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
            // the simple layout hides the note line, so the first visit to each game shows it as a chat line
            if (simple && Storage.GetSetting("tip_" + g.Icon) != "1")
            {
                Storage.SetSetting("tip_" + g.Icon, "1");
                Msg(g.Note);
            }
            Telemetry.Game = g.Name;
            Telemetry.Server = null;
            Storage.SetSetting("last_game", g.Icon);
            ResetDetectButton();
            pickBar.Visible = g.CanLock;
            UpdateLockBar();
            serverList.Items.Clear();
            SetStatus(T("Getting the servers of ", "أجيب سيرفرات ") + g.Name + "...");
            Task.Run(() =>
            {
                string err = null;
                // one quiet retry: a slow answer from Steam now and then shouldn't show up as an error
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try { GameDb.Load(g); err = null; break; }
                    catch (Exception ex) { err = ex.Message; Thread.Sleep(1500); }
                }
                Ui(() =>
                {
                    if (CurrentGame != g) return;
                    if (err != null)
                    {
                        SetStatus(T("Couldn't get the servers. Check your internet, then pick the game again.",
                                    "ما قدرت أجيب السيرفرات. شيّك على النت، وبعدين اختار اللعبة مرة ثانية."));
                        Log(g.Name + ": " + err);
                        Issues.Report("servers_load_failed", g.Name + ": " + err, null, g.Icon);
                        return;
                    }
                    // last time's results show up straight away; the new test then refreshes them
                    if (g.Regions.All(r => r.Direct == null))
                    {
                        Storage.LoadLast(g);
                        if (g.Regions.Any(r => r.Direct != null))
                            g.Regions = g.Regions.OrderBy(r => LockedOut(g, r) ? 1 : 0).ThenBy(BestLatency).ToList();
                    }
                    FillServers(g);
                    UpdateLockBar();
                    LoadStats(g);
                    if (g.Regions.Count == 0)
                        SetStatus(T("No servers for this game right now. Try again in a bit.", "ما لقيت سيرفرات لهادي اللعبة دحين، جرّب بعد شوي."));
                    else if (Prefs.AutoTest && !tested.Contains(g))
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

        // Deadlock players online (in matches, the queue, the menus or away), -1 when unknown
        int deadlockOnline = -1;

        // A region's players: the online total split by the region's share of the live matches.
        // Without the total, the old estimate from the live-match sample (far below the real number).
        int PlayersOf(int matches)
        {
            int all = activity != null ? activity.Values.Sum() : 0;
            if (deadlockOnline > 0 && all > 0) return (int)Math.Round((double)deadlockOnline * matches / all);
            return Stats.EstimatePlayers(matches);
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
            return name + "   ·   " + Stats.Approx(PlayersOf(h.Matches)) + T(" online", " أونلاين") +
                   "   ·   " + T("wait ", "الانتظار ") + Stats.EstimateWait(h.Matches);
        }

        void DrawGroupHeader(DrawListViewSubItemEventArgs e, GroupHeader h)
        {
            var row = e.Item.Bounds;
            using (var b = new SolidBrush(Color.FromArgb(40, 30, 60))) e.Graphics.FillRectangle(b, row);
            var font = Fonts.Semi(10.5f);
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
            var cands = g.Regions.Where(r => g.Hosts(r) && !LockedOut(g, r) && BestLatency(r) < double.MaxValue).ToList();
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
                heroTitle.Text = busy ? T("Looking for your best server...", "أدوّر لك أحسن سيرفر...")
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
                heroSub.Text = T("Region ", "منطقة ") + reg + "  ·  " + Stats.Approx(PlayersOf(m)) + T(" online", " أونلاين") +
                               "  ·  " + T("wait ", "الانتظار ") + Stats.EstimateWait(m) + "  ·  " + T("lowest ping with enough players", "أقل بنق وفيه لاعبين كفاية");
            }
            else heroSub.Text = T("The lowest ping you have right now.", "أقل بنق عندك دحين.");

            heroBtn.Tag = best;
            heroBtn.Visible = g.CanLock;
            bool already = lockSummary != null && best.Picked;
            heroBtn.Enabled = !already;
            heroBtn.Text = already ? T("Locked ✓", "مثبّت ✓") : T("Play on it", "العب عليه");
        }

        // One click: pick only this server and lock it.
        void PlayOn(Region region)
        {
            var g = CurrentGame;
            if (g == null || !g.CanLock) return;
            Telemetry.Send("use", region.NameEn, -1, "play on it");
            // games with region groups (Overwatch): the best server's whole region, so queues stay short
            string[] group;
            var ids = g.LockGroups != null && g.LockGroups.TryGetValue(region.Id, out group) ? group : new[] { region.Id };
            foreach (var r in g.Regions) r.Picked = ids.Contains(r.Id);
            Storage.SavePicks(g.LockKey, ids);
            FillServers(g);
            UpdateLockBar();
            LockServers();
        }

        void UpdateRow(ListViewItem item)
        {
            var r = item.Tag as Region;
            if (r == null) return;
            var g = CurrentGame;
            item.SubItems[ColRegion].Text = (r.Here ? T("» ", "« ") : "") + r.Name + (r.Here ? T("  (you are here)", "  (إنت هنا)") : "") +
                                            (g != null && !g.Hosts(r) ? T("   ·  relay only", "   ·  وسيط بس") : "") +
                                            (devMode ? "   ·  ‎" + r.Cidrs.Count + " ranges‎" : "");
            item.SubItems[ColRegion].ForeColor = r.Here || r.Picked ? Accent : (r.Detected ? Warn : TextC);

            string mm;
            int matches;
            if (g != null && g.AppId == "1422450" && activity != null && Stats.PopToMm.TryGetValue(r.Id, out mm) &&
                activity.TryGetValue(mm, out matches))
            {
                SetSub(item, ColPlayers, Stats.Approx(PlayersOf(matches)), TextC);
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
            var watch = Stopwatch.StartNew();

            Task.Run(() =>
            {
                foreach (var rel in rels) rel.Ping = rel.MeasurePing();
                Ui(RefreshRelays);

                int done = 0;
                // every region (and each of its addresses) at once: the whole test takes about as long as the slowest server
                Parallel.ForEach(regions, new ParallelOptions { MaxDegreeOfParallelism = 64 }, r =>
                {
                    var results = r.Endpoints.AsParallel().AsOrdered().Select(ep => Pinger.Measure(ep, 3, 1000)).ToList();
                    PingResult best = null;
                    Endpoint bestEp = null;
                    for (int i = 0; i < results.Count; i++)
                    {
                        var res = results[i];
                        if (best == null || (res.Ok && (!best.Ok || res.Avg < best.Avg))) { best = res; bestEp = r.Endpoints[i]; }
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

                long ms = watch.ElapsedMilliseconds;
                Ui(() =>
                {
                    busy = false;
                    btnTest.Enabled = true;
                    tested.Add(g);
                    Storage.SaveLast(g);
                    if (pendingRebuild) { pendingRebuild = false; RebuildUi(); return; }
                    // the user switched games while this test ran: test the game they are looking at now
                    var cur = CurrentGame;
                    if (cur != g && cur != null && Prefs.AutoTest && cur.Regions.Count > 0 && !tested.Contains(cur))
                    {
                        RunTest();
                        return;
                    }
                    var top = regions.Where(r => BestLatency(r) < double.MaxValue).OrderBy(BestLatency).FirstOrDefault();
                    // every result, so ping can be compared by city, ISP and connection type
                    var results = new Dictionary<string, object>();
                    foreach (var r in regions.Where(r => r.Direct != null))
                        results[r.Id] = new object[] { r.Direct.Ok ? (object)Math.Round(r.Direct.Avg) : null, Math.Round(r.Direct.Loss), Math.Round(r.Direct.Jitter, 1) };
                    var data = new Dictionary<string, object> { { "ms", ms }, { "regions", results }, { "best", top != null ? top.Id : null } };
                    if (top == null)
                    {
                        SetStatus(T("No server replied. Check your internet or firewall.", "ولا سيرفر رد، شيّك على النت أو جدار الحماية."));
                        Telemetry.Send("test", null, -1, "no reply, " + regions.Count + " regions", data);
                        Issues.Report("all_down", g.Name + ": none of " + regions.Count + " regions answered", data, g.Icon);
                        return;
                    }
                    if (CurrentGame == g)
                    {
                        // closest first; regions blocked by the lock go to the end
                        g.Regions = g.Regions.OrderBy(r => LockedOut(g, r) ? 1 : 0).ThenBy(BestLatency).ToList();
                        FillServers(g);
                    }
                    SetStatus(T("Done. Your best region: ", "خلص الفحص. أحسن منطقة لك: ") + ShortName(top) + "  " + Ms(BestLatency(top)));
                    Telemetry.Send("test", top.NameEn, (int)Math.Round(BestLatency(top)), regions.Count + " regions, " + ms + " ms", data);
                    var down = regions.Where(r => r.Direct != null && !r.Direct.Ok && !r.Detected).Select(r => r.Id).ToList();
                    if (down.Count > 0)
                        Issues.Report("region_down", g.Name + ": " + string.Join(", ", down),
                            new Dictionary<string, object> { { "game", g.Name }, { "regions", down.ToArray() } }, g.Icon + string.Join(",", down));
                    Log(g.Name + ": " + T("best region ", "أحسن منطقة ") + top.Name + " " + Ms(BestLatency(top)));
                });
            });
        }

        // ---------------------------------------------------------------- updates (required)

        Panel updateBar;
        Label updateLabel;
        Button updateBtn;
        Release pendingUpdate;
        bool updatePrompted, closingForUpdate;

        void CheckForUpdate(bool atStart)
        {
            Task.Run(() =>
            {
                var r = Updater.Latest();
                Ui(() =>
                {
                    pendingUpdate = r;
                    ShowUpdateBar();
                    if (r == null || closingForUpdate) return;
                    Log(T("New version available: ", "في نسخة جديدة: ") + r.Ver.ToString(3));
                    if (Updater.Forced(r))
                    {
                        // don't pull the rug in the middle of a match: force it on the next launch instead
                        if (!atStart && games.Any(g => g.Processes.Length > 0 && Detector.IsRunning(g.Processes))) return;
                        ForceUpdate(r);
                    }
                    else if (!updatePrompted)
                    {
                        // a chat line, not a popup: the bar at the top has the button
                        updatePrompted = true;
                        Msg(T("A new version is out (" + r.Ver.ToString(3) + "). Press Update now at the top, it takes a few seconds.",
                              "في نسخة جديدة (‎" + r.Ver.ToString(3) + "‎). اضغط «حدّث دحين» فوق، ياخذ كم ثانية."), ToastKind.Warn);
                    }
                });
            });
        }

        void ShowUpdateBar()
        {
            if (updateBar == null) return;
            var r = pendingUpdate;
            updateBar.Visible = r != null;
            updateBtn.Visible = r != null;
            if (r == null) return;
            string v = "‎" + r.Ver.ToString(3) + "‎";
            updateLabel.ForeColor = Warn;
            updateLabel.Text = r.Optional
                ? T("A new version is out (" + v + ").", "في نسخة جديدة (" + v + ").")
                : Updater.Forced(r)
                    ? T("This version is too old. Update to keep using 2X Ping.", "هالنسخة قديمة، حدّث عشان تكمل تستخدم البرنامج.")
                    : T("A new version is out (" + v + "). Update within " + Updater.LeftText(r) + " to keep using 2X Ping.",
                        "في نسخة جديدة (" + v + "). حدّث خلال " + Updater.LeftText(r) + " عشان يكمل البرنامج يشتغل.");
        }

        // Right after a self-update: a short green note in the update strip.
        void ShowUpdatedNote()
        {
            updateBar.Visible = true;
            updateBtn.Visible = false;
            updateLabel.ForeColor = Good;
            updateLabel.Text = T("Updated to " + Brand.Version + " ✓", "تحدّث البرنامج لـ ‎" + Brand.Version + "‎ ✓");
            var t = new System.Windows.Forms.Timer { Interval = 10000 };
            t.Tick += delegate { t.Stop(); t.Dispose(); if (pendingUpdate == null) updateBar.Visible = false; };
            t.Start();
        }

        // The grace period is over: update or close (closing removes the lock and the relay route, as always).
        void ForceUpdate(Release r)
        {
            if (!Visible) RestoreFromTray();
            if (UpdateDialog.Show(this, r, true)) CloseForUpdate();
            else Close();
        }

        void CloseForUpdate()
        {
            closingForUpdate = true;
            Close();
        }

        // ---------------------------------------------------------------- players & "which server am I on?"

        Panel BuildInfoBar()
        {
            var bar = new Panel { Dock = DockStyle.Top, Height = 40 };
            btnDetect = MakeButton(T("Which server am I on?", "على أي سيرفر أنا؟"), Panel2);
            btnDetect.Dock = DockStyle.Right;
            btnDetect.Width = 240;
            btnDetect.Click += delegate { DetectServer(); };
            infoLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = TextC, AutoEllipsis = true };
            bar.Controls.Add(infoLabel);
            bar.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
            bar.Controls.Add(btnDetect);
            return bar;
        }

        void LoadStats(Game g, bool quiet = false)
        {
            statsAt = DateTime.Now;
            if (!quiet)
            {
                infoLabel.Text = T("Getting player numbers...", "لحظة، أجيب عدد اللاعبين...");
                infoLabel.ForeColor = Dim;
            }
            Task.Run(() =>
            {
                Dictionary<string, int> act = null;
                int online = -1;
                if (g.AppId == "1422450")
                {
                    try { act = Stats.DeadlockActivity(); } catch { }
                    try { online = Stats.DeadlockOnline(); } catch { }
                }
                int steam = Stats.SteamPlayers(g.SteamAppId);
                Ui(() =>
                {
                    if (CurrentGame != g) return;
                    activity = act;
                    deadlockOnline = online;
                    string text;
                    if (act != null && act.Count > 0)
                    {
                        int total = act.Values.Sum(m => PlayersOf(m));
                        text = T("Online now (est.): ", "أونلاين دحين تقريباً: ") + Stats.Approx(total) + T("", " لاعب") + "   |   " +
                               string.Join("   ", act.OrderByDescending(kv => kv.Value).Take(4).Select(kv =>
                                   (Stats.MmRegions.ContainsKey(kv.Key) ? Lang.T(Stats.MmRegions[kv.Key][0], Stats.MmRegions[kv.Key][1]) : kv.Key) +
                                   " " + Stats.Approx(PlayersOf(kv.Value))));
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
                    // regroup now that the activity numbers are in, keeping the list where the player left it
                    int top = serverList.TopItem != null ? serverList.TopItem.Index : 0;
                    FillServers(g);
                    if (quiet && top > 0 && top < serverList.Items.Count) serverList.TopItem = serverList.Items[top];
                });
            });
        }

        // Region of a Valve game that owns this IP (relay or game server range), or null.
        static Region ValveRegionOf(Game g, string ip)
        {
            return g.Regions.FirstOrDefault(r => r.Cidrs.Any(c => Detector.InCidr(ip, c)));
        }

        // The datacenter the game is on, from the addresses it talks to: one that hosts matches, or null when the game
        // only talks to a relay (e.g. Dubai), which hides the match server behind it.
        static Region HostRegionOf(Game g, List<Detector.Hit> hits)
        {
            return hits.Select(h => ValveRegionOf(g, h.Ip)).FirstOrDefault(r => r != null && g.Hosts(r));
        }

        static Region RelayRegionOf(Game g, List<Detector.Hit> hits)
        {
            return hits.Select(h => ValveRegionOf(g, h.Ip)).FirstOrDefault(r => r != null && !g.Hosts(r));
        }

        void DetectServer()
        {
            var g = CurrentGame;
            if (g == null || busy) return;
            if (autoDetecting)
            {
                Msg(T("Checking your server right now, the answer shows on the button in a few seconds.",
                      "أشيّك على سيرفرك دحين، والجواب يطلع على الزر بعد ثواني."));
                return;
            }
            if (g.Processes.Length > 0 && !Detector.IsRunning(g.Processes))
            {
                Msg(T(g.Name + " isn't running. Open it, join a match, then press this again.",
                      g.Name + " مو مفتوحة. افتحها وادخل مباراة، وبعدين اضغط الزر مرة ثانية."), ToastKind.Warn);
                return;
            }
            Msg(T("Watching the game's traffic for 15 seconds. Stay in a real match (not the menu or practice).",
                  "أراقب ترافيك اللعبة 15 ثانية. خلّك داخل مباراة حقيقية (مو القائمة ولا التدريب)."));
            busy = true;
            btnDetect.Enabled = false;
            SetStatus(T("Watching the game's traffic... (15 s)", "أراقب ترافيك اللعبة... (15 ثانية)"));
            Task.Run(() =>
            {
                try
                {
                    bool matched;
                    var hits = Detector.Capture(g.Processes, 15, out matched);
                    // a local record too (traffic.txt in the data folder), for diagnosing locks that block too much
                    try
                    {
                        File.AppendAllLines(Path.Combine(Storage.Dir, "traffic.txt"), hits.Select(h =>
                        {
                            var dc = g.CanLock ? ValveRegionOf(g, h.Ip) : null;
                            return string.Join("\t", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), g.Name, h.Ip + ":" + h.Port,
                                               h.Packets + " packets", dc != null ? dc.Id : "-", lockSummary != null ? "locked: " + lockSummary : "not locked");
                        }));
                    }
                    catch { }
                    // every address the game talked to, so the server lists can grow for games without official ones
                    Telemetry.Send("traffic", null, -1, hits.Count + " addresses", new Dictionary<string, object>
                    {
                        { "hits", hits.Take(8).Select(h => h.Ip + ":" + h.Port + " x" + h.Packets).ToArray() }
                    });
                    // games with known datacenters (Valve, Overwatch): name the datacenter and check it against the lock
                    if (g.Provider == Provider.Valve || (g.CanLock && hits.Any(h => ValveRegionOf(g, h.Ip) != null)))
                        Ui(() => ShowValveResult(g, hits));
                    else ShowOtherResult(g, hits);
                }
                catch (Exception ex)
                {
                    Log(T("Server check failed: ", "ما زبط الفحص: ") + ex.Message);
                    Issues.Report("detect_failed", "manual check: " + ex.Message);
                    Ui(() => { busy = false; btnDetect.Enabled = true; Msg(T("Server check failed: ", "ما زبط الفحص: ") + ex.Message, ToastKind.Bad); });
                }
            });
        }

        void NothingFound()
        {
            SetStatus(T("No game traffic found.", "ما لقيت ترافيك لعبة."));
            Msg(T("I didn't see any game server traffic. Make sure you're inside a real match (not the menu or practice) and try again.",
                  "ما شفت ترافيك من سيرفر لعبة. تأكد إنك داخل مباراة حقيقية (مو القائمة ولا التدريب) وجرّب مرة ثانية."), ToastKind.Warn);
        }

        // Valve: the address is one of Valve's relays/servers, so we can name the region and check it against the lock.
        void ShowValveResult(Game g, List<Detector.Hit> hits)
        {
            busy = false;
            btnDetect.Enabled = true;
            foreach (var r in g.Regions) r.Here = false;
            var found = hits.Select(h => ValveRegionOf(g, h.Ip)).Where(r => r != null && g.Hosts(r)).Distinct().ToList();
            foreach (var h in hits) Log(T("Game traffic: ", "ترافيك اللعبة: ") + h.Ip + ":" + h.Port + "  (" + h.Packets + ")");
            var relay = RelayRegionOf(g, hits);
            if (found.Count == 0 && relay != null)
            {
                // the game goes through a nearby Valve relay, which hides which datacenter the match is on
                if (CurrentGame == g) FillServers(g);
                SetStatus(T("Through Valve's relay in ", "داخل شبكة Valve من وسيط ") + ShortName(relay));
                Telemetry.Send("detect", relay.NameEn, -1, "through relay");
                Msg(T("Your game goes through Valve's relay in " + ShortName(relay) + ", the shortest way into their network, so the match server itself is hidden. " +
                      (lockSummary != null ? "Your lock still only allows the servers you picked." : ""),
                      "لعبتك داخلة شبكة Valve من وسيط " + ShortName(relay) + "، وهو أقصر طريق لها، فسيرفر المباراة نفسه ما يبان. " +
                      (lockSummary != null ? "والتثبيت ما يسمح إلا بالسيرفرات اللي اخترتها." : "")), ToastKind.Good);
                return;
            }
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
            Telemetry.Server = string.Join(", ", found.Select(r => r.NameEn));
            Telemetry.Send("detect", Telemetry.Server, -1,
                lockSummary == null ? "no lock" : found.All(r => r.Picked) ? "lock OK" : "NOT on picked servers");
            if (lockSummary != null && !found.All(r => r.Picked))
                Issues.Report("lock_outside", g.Name + " on " + string.Join(", ", found.Select(r => r.Id)) + " while locked on " + lockSummary,
                    new Dictionary<string, object> { { "game", g.Name }, { "ips", hits.Take(3).Select(h => h.Ip).ToArray() }, { "locked", lockSummary } },
                    g.Icon + string.Join(",", found.Select(r => r.Id)));

            bool onPicked = lockSummary == null || found.All(r => r.Picked);
            Msg(T("You're playing on: ", "إنت تلعب دحين على: ") + names, onPicked ? ToastKind.Good : ToastKind.Warn);
            if (lockSummary != null)
                Msg(onPicked
                    ? T("✓ That's one of your picked servers, so the lock is working.", "هادا من السيرفرات اللي اخترتها، يعني التثبيت شغّال صح ✓")
                    : T("That's not one of your picked servers. Restart the game and queue again.",
                        "هادا مو من السيرفرات اللي اخترتها. اقفل اللعبة وافتحها من جديد، وبعدين دوّر مباراة."),
                    onPicked ? ToastKind.Good : ToastKind.Warn);
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
                Telemetry.Server = top.NameEn;
                Telemetry.Send("detect", top.NameEn, top.Direct.Ok ? (int)Math.Round(top.Direct.Avg) : -1, null);
                Msg(T("You're playing on: ", "إنت تلعب دحين على: ") + top.Name, ToastKind.Good);
                Msg((top.Direct.Ok ? T("Ping: ", "البنق: ") + Ms(top.Direct.Avg) + T(". ", "، ")
                                   : T("It doesn't answer ping (normal for some games). ", "ما يرد على البنق (عادي في بعض الألعاب)، ")) +
                    T("It's saved at the top of the list.", "وحفظته فوق في القائمة."));
            });
        }

        // ---------------------------------------------------------------- automatic "which server am I on?"

        // While the selected game runs, a short look at its traffic puts the server on the button itself
        // ("On: Dammam ✓"): every 30 s for 10 minutes after the lock changes, so the player sees whether it worked,
        // then every 90 s. Problems found on the way are reported silently (see Issues).
        System.Windows.Forms.Timer autoDetectTimer;
        bool autoDetecting, matchSinceLock;
        DateTime lastAutoDetect = DateTime.MinValue, lockChangedAt = DateTime.MinValue, lastRulesCheck = DateTime.MinValue;
        string lastAutoServer;

        bool Verifying { get { return DateTime.Now - lockChangedAt < TimeSpan.FromMinutes(10); } }

        void StartAutoDetect()
        {
            if (autoDetectTimer != null) return;
            autoDetectTimer = new System.Windows.Forms.Timer { Interval = 10000 };
            autoDetectTimer.Tick += delegate { AutoDetectTick(); if (appActive) RefreshStats(TimeSpan.FromMinutes(5)); };
            autoDetectTimer.Start();
        }

        void SetDetect(string text, Color dot)
        {
            btnDetect.Text = text;
            btnDetect.ForeColor = TextC;
            ((RoundButton)btnDetect).Dot = dot;
        }

        void ResetDetectButton()
        {
            if (btnDetect == null) return;
            SetDetect(T("Which server am I on?", "على أي سيرفر أنا؟"), Color.Empty);
            lastAutoDetect = DateTime.MinValue;
            lastAutoServer = null;
        }

        // Called after locking or unlocking: check right away and keep checking often for a while.
        void LockChanged(Game g)
        {
            lockChangedAt = DateTime.Now;
            matchSinceLock = false;
            lastAutoServer = null;
            lastAutoDetect = DateTime.MinValue;
            if (btnDetect == null) return;
            if (lockSummary != null)
            {
                SetDetect(T("Checking at your next match...", "بنتأكد في مباراتك الجاية..."), Lavender);
            }
            else ResetDetectButton();
        }

        void AutoDetectTick()
        {
            var g = CurrentGame;
            CheckLockRules();
            if (g == null || busy || autoDetecting || !appActive || g.Processes.Length == 0) return;
            if (DateTime.Now - lastAutoDetect < TimeSpan.FromSeconds(Verifying ? 30 : 90)) return;
            autoDetecting = true;
            lastAutoDetect = DateTime.Now;
            Task.Run(() =>
            {
                List<Detector.Hit> hits = null;
                string where = null;
                bool running = Detector.IsRunning(g.Processes);
                if (running)
                    try
                    {
                        bool matched;
                        hits = Detector.Capture(g.Processes, 8, out matched);
                        // games without a datacenter list: the main server's city
                        if (hits.Count > 0 && !hits.Any(h => ValveRegionOf(g, h.Ip) != null)) where = Stats.Locate(hits[0].Ip);
                    }
                    catch (Exception ex)
                    {
                        hits = null;
                        Issues.Report("detect_failed", "automatic check: " + ex.Message);
                    }
                Ui(() =>
                {
                    autoDetecting = false;
                    if (CurrentGame != g) return;
                    if (!running) { if (lastAutoServer != null) ResetDetectButton(); return; }
                    if (hits != null) ShowAutoDetect(g, hits, where);
                });
            });
        }

        // The lock's firewall rules can vanish (antivirus, another tool, a firewall reset): check every 2 minutes.
        void CheckLockRules()
        {
            if (lockSummary == null || DateTime.Now - lastRulesCheck < TimeSpan.FromMinutes(2)) return;
            lastRulesCheck = DateTime.Now;
            string locked = lockSummary;
            Task.Run(() =>
            {
                if (ServerLock.Exists()) return;
                Issues.Report("lock_rules_missing", "lock on " + locked + " but no firewall rules");
                Ui(() =>
                {
                    if (lockSummary != locked) return;
                    Msg(T("Your server lock was removed from Windows Firewall (maybe by a security app). Press Lock my servers again.",
                          "تثبيت السيرفرات انشال من جدار حماية ويندوز (يمكن برنامج حماية). اضغط «ثبّت سيرفراتي» مرة ثانية."), ToastKind.Warn);
                });
            });
        }

        void ShowAutoDetect(Game g, List<Detector.Hit> hits, string where)
        {
            if (hits.Count == 0)
            {
                bool waiting = lockSummary != null && !matchSinceLock;
                SetDetect(waiting ? T("Checking at your next match...", "بنتأكد في مباراتك الجاية...") : T("Not in a match", "مو داخل مباراة"),
                          waiting ? Lavender : Dim);
                // locked a while ago and still no match: the lock may be blocking more than it should
                if (waiting && DateTime.Now - lockChangedAt > TimeSpan.FromMinutes(12))
                    Issues.Report("queue_long", g.Name + ", locked on " + lockSummary + ", no match for " +
                        (int)(DateTime.Now - lockChangedAt).TotalMinutes + " min (maybe just in the menu)",
                        new Dictionary<string, object> { { "game", g.Name }, { "locked", lockSummary } }, lockChangedAt.Ticks.ToString());
                return;
            }
            matchSinceLock = true;
            var top = hits[0];
            var dc = HostRegionOf(g, hits);
            var relay = dc == null ? RelayRegionOf(g, hits) : null;
            if (relay != null)
            {
                // only a relay in sight: the match server is hidden behind it, so there's nothing to check or report
                SetDetect(T("Via relay: ", "عن طريق وسيط: ") + ShortName(relay), Lavender);
                lastAutoServer = relay.Id;
                return;
            }
            string name = dc != null ? ShortName(dc) : (where ?? top.Ip);
            bool ok = dc == null ? lockSummary == null : (lockSummary == null || dc.Picked);
            SetDetect(T("On: ", "إنت على: ") + name, ok ? Good : Warn);
            if (dc != null && !dc.Here)
            {
                foreach (var r in g.Regions) r.Here = r == dc;
                FillServers(g);
            }
            var data = new Dictionary<string, object>
            {
                { "game", g.Name }, { "ip", top.Ip + ":" + top.Port }, { "packets", top.Packets }, { "where", where },
                { "datacenter", dc != null ? dc.Id : null }, { "locked", lockSummary }
            };
            string net24 = top.Ip.Substring(0, top.Ip.LastIndexOf('.'));
            if (dc == null)
                Issues.Report("unknown_server", g.Name + " on " + top.Ip + " (" + (where ?? "?") + ")", data, g.Icon + net24);
            else if (!ok)
                Issues.Report("lock_outside", g.Name + " on " + dc.Id + " (" + top.Ip + ") while locked on " + lockSummary, data, g.Icon + dc.Id);
            if (!ok && dc != null && name != lastAutoServer)
                Msg(T("You're on " + name + ", which isn't one of your picked servers. Restart the game and queue again.",
                      "إنت على " + name + "، وهو مو من السيرفرات اللي اخترتها. اقفل اللعبة وافتحها من جديد، وبعدين دوّر مباراة."), ToastKind.Warn);
            if (name != lastAutoServer)
                Telemetry.Send("detect", dc != null ? dc.NameEn : top.Ip, -1, "auto, " + (ok ? "ok" : "outside lock"));
            lastAutoServer = name;
        }

        // ---------------------------------------------------------------- server lock (Valve)

        void TogglePick(Region r)
        {
            var g = CurrentGame;
            if (g == null || !g.Hosts(r)) return; // a relay only: no matches are played there
            r.Picked = !r.Picked;
            Storage.SavePicks(g.LockKey, g.Regions.Where(x => x.Picked).Select(x => x.Id));
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
                      "اختار سيرفر واحد على الأقل (اضغط المربع اللي جنب اسمه)."), ToastKind.Warn);
                return;
            }
            string names = string.Join("، ", picked.Select(ShortName));
            // other games: only the game's own UDP (its match traffic) is blocked, so login, voice chat and the
            // rest of the PC are untouched; the exe must be known for that
            string exe = null;
            if (g.LockSpace != null)
            {
                exe = SystemInfo.FindExe(g);
                if (exe == null)
                {
                    Issues.Report("exe_not_found", g.Name, null, g.Icon);
                    Msg(T("Open " + g.Name + " once (the main menu is enough) so 2X Ping can find it, then press Lock my servers again.",
                          "افتح " + g.Name + " مرة وحدة (القائمة الرئيسية تكفي) عشان البرنامج يعرف مكانها، وبعدين اضغط «ثبّت سيرفراتي» مرة ثانية."), ToastKind.Warn);
                    return;
                }
            }
            WarnLongerRoute(g, picked);
            btnLock.Enabled = false;
            SetStatus(T("Locking your servers...", "أثبّت سيرفراتك..."));
            Task.Run(() =>
            {
                try
                {
                    List<string> block;
                    if (g.LockSpace != null)
                        // everything the game's servers can live on, except the picked datacenters (new servers included)
                        block = IpLists.Subtract(IpLists.Resolve(g.LockSpace),
                            picked.SelectMany(r => r.Cidrs).Concat(IpLists.Resolve(g.LockAllow ?? new string[0])));
                    else
                    {
                        var keep = new HashSet<string>(picked.SelectMany(r => r.Cidrs));
                        // relay-only datacenters stay open: the game enters Valve's network through them
                        block = g.Regions.Where(r => !r.Picked && !r.Detected && g.Hosts(r)).SelectMany(r => r.Cidrs).Where(c => !keep.Contains(c)).ToList();
                    }
                    int n = ServerLock.Apply(block, exe, exe != null ? "UDP" : null, g.LockPorts);
                    bool firewallOn = ServerLock.FirewallOn();
                    if (!firewallOn) Issues.Report("firewall_off", "while locking " + g.Name);
                    Ui(() =>
                    {
                        lockSummary = names;
                        LockChanged(g);
                        Telemetry.Server = string.Join(", ", picked.Select(r => r.NameEn));
                        Telemetry.Send("lock", Telemetry.Server, -1, n + " ranges blocked");
                        btnLock.Enabled = true;
                        UpdateLockBar();
                        FillServers(g);
                        Log(T("Lock ON (", "التثبيت شغّال (") + g.Name + "): " + names + "  (" + n + ")");
                        SetStatus(T("Locked: ", "مثبّت على: ") + names);
                        StartLockMonitor();
                        Msg(T("Locked. You'll only be matched on: ", "تم التثبيت. ما بتلعب إلا على: ") + names, ToastKind.Good);
                        if (g.LockSpace != null)
                            Msg(T("Try a casual match first (not ranked): if " + g.Name + " still picks a blocked server, the match won't load, and in ranked that counts as leaving.",
                                  "جرّبه في مباراة عادية أول (مو رانكد): لو اللعبة اختارت سيرفر محجوب المباراة ما تفتح، وفي الرانكد تنحسب منسحب."), ToastKind.Warn);
                        Msg(T("If the game is open, restart it (or wait a minute) before you queue.",
                              "لو اللعبة مفتوحة، اقفلها وافتحها من جديد (أو استنى دقيقة) قبل ما تدوّر مباراة."));
                        if (Storage.GetSetting("tip_tray") != "1")
                        {
                            Storage.SetSetting("tip_tray", "1");
                            Msg(T("Minimize 2X Ping and it waits next to the clock; the lock stays on until you unlock or close it.",
                                  "لو صغّرت البرنامج يروح جنب الساعة، والتثبيت يبقى شغّال لين تفكّه أو تقفل البرنامج."));
                        }
                        // the lock can't work with the firewall off: this one the player has to know
                        if (!firewallOn)
                            Msg(T("Windows Firewall is off, so the lock can't block anything. Turn it on in Windows Security > Firewall.",
                                  "جدار حماية ويندوز مطفي، فالتثبيت ما يقدر يحجب شي. شغّله من أمان ويندوز ← جدار الحماية."), ToastKind.Bad);
                    });
                }
                catch (Exception ex)
                {
                    Ui(() =>
                    {
                        btnLock.Enabled = true;
                        Log(T("Lock failed: ", "ما زبط التثبيت: ") + ex.Message);
                        Issues.Report("lock_apply_failed", g.Name + ": " + ex.Message, null, g.Icon);
                        Msg(T("Lock failed: ", "ما زبط التثبيت: ") + ex.Message, ToastKind.Bad);
                    });
                }
            });
        }

        // Valve games enter Valve's network through the closest datacenter and ride it to the match server.
        // If that closest one hosts matches too, the lock has to block it, and the route gets longer: say so,
        // with the ping it costs and the fix (tick it as well), from the player's own pings and Valve's.
        void WarnLongerRoute(Game g, List<Region> picked)
        {
            if (g.Provider != Provider.Valve || g.Backbone.Count == 0) return;
            Func<Region, double> ping = r => r.Direct != null && r.Direct.Ok ? r.Direct.Avg : double.MaxValue;
            var open = g.Regions.Where(r => r.Picked || !g.Hosts(r)).ToList();
            Region worstX = null, worstP = null;
            double worstGain = 0;
            foreach (var p in picked)
            {
                // the best way to this server that stays open
                double best = double.MaxValue;
                foreach (var o in open)
                {
                    int hop = g.Between(o.Id, p.Id);
                    if (hop >= 0 && ping(o) < double.MaxValue) best = Math.Min(best, ping(o) + hop);
                }
                if (best == double.MaxValue) continue;
                // ...and through each datacenter the lock blocks
                foreach (var x in g.Regions.Where(r => !r.Picked && g.Hosts(r)))
                {
                    int hop = g.Between(x.Id, p.Id);
                    if (hop < 0 || ping(x) == double.MaxValue) continue;
                    double gain = best - (ping(x) + hop);
                    if (gain > worstGain) { worstGain = gain; worstX = x; worstP = p; }
                }
            }
            if (worstX == null || worstGain < 10) return;
            int ms = (int)Math.Round(worstGain);
            Telemetry.Send("use", worstX.NameEn, ms, "longer route warning, " + worstP.Id);
            Msg(T("Heads up: " + ShortName(worstX) + " is your shortest way into Valve's network to " + ShortName(worstP) +
                  ", so blocking it can add about " + ms + " ms. Tick " + ShortName(worstX) + " too to keep it.",
                  "تنبيه: " + ShortName(worstX) + " هي أقصر طريق لك لشبكة Valve عشان توصل " + ShortName(worstP) +
                  "، وحجبها ممكن يزيد بنقك حوالي " + ms + " ms. علّم على " + ShortName(worstX) + " بعد عشان تبقى مفتوحة."),
                ToastKind.Warn);
        }

        void UnlockServers()
        {
            if (lockSummary == null) return;
            lockSummary = null;
            if (!boosting) StopMonitor();
            Telemetry.Server = null;
            Telemetry.Send("unlock", null, -1, null);
            LockChanged(CurrentGame);
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
                Msg(T("Wait for the current test to finish first.", "استنى الفحص الحالي يخلص أول."), ToastKind.Warn);
                return;
            }

            lockSummary = null;
            StopAll();
            foreach (var g in games) foreach (var r in g.Regions) { r.Picked = false; r.Here = false; }
            Storage.ClearPicks();
            Telemetry.Server = null;
            Telemetry.Send("reset", null, -1, null);
            busy = true;
            SetStatus(T("Putting everything back...", "أرجّع كل شي زي ما كان..."));
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
                              "تقريباً خلصنا، بس في قواعد بجدار الحماية لسا موجودة. اقفل البرنامج وافتحه مرة ثانية، وهو ينظفها أول ما يفتح."), ToastKind.Warn);
                    }
                    else
                    {
                        Log(T("Everything is back to normal.", "رجّعنا كل شي زي ما كان."));
                        SetStatus(T("Everything is back to normal.", "رجّعنا كل شي زي ما كان ✓"));
                        Msg(T("Everything is back to normal ✓ Nothing from 2X Ping is left on your PC.",
                              "رجّعنا كل شي طبيعي ✓ ما بقى أي تغيير من البرنامج على جهازك."), ToastKind.Good);
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
                if (region == null) { Msg(T("Press Test ping first, or pick a server from the list.", "اضغط «افحص البنق» أول، أو اختار سيرفر من القائمة."), ToastKind.Warn); return; }
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
                StartMonitor(target, T("BOOST · ", "بوست · ") + ShortName(region));
                Telemetry.Send("boost", region.NameEn, -1, "direct");
                return;
            }

            if (!Tunnel.Installed)
            {
                Msg(T("Relays need WireGuard for Windows (free). Get it from Tools & Log > Get WireGuard.",
                      "الوسيط يحتاج برنامج WireGuard (مجاني). تلقاه في «أدوات وسجل» ← «نزّل WireGuard»."), ToastKind.Warn);
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
                    Telemetry.Send("boost", region.NameEn, -1, "relay " + relay.Name);
                    Ui(() => { RaisePriority(g); StartMonitor(target, T("BOOST · ", "بوست · ") + ShortName(region) + T(" via ", " عن طريق ") + relay.Name); });
                }
                catch (Exception ex)
                {
                    Log(T("Boost failed: ", "ما زبط البوست: ") + ex.Message);
                    Telemetry.Send("error", region.NameEn, -1, "boost via " + relay.Name + ": " + ex);
                    Ui(() => { StopAll(); Msg(T("Boost failed: ", "ما زبط البوست: ") + ex.Message, ToastKind.Bad); });
                }
            });
        }

        // Live ping in the status bar (and the graph in the advanced layout), every second.
        void StartMonitor(Endpoint target, string label)
        {
            int gen = ++monitorGen;
            graph.Clear();
            graph.Label = label;
            SetStatus(label);
            var t = new Thread(() =>
            {
                var last = DateTime.MinValue;
                while (gen == monitorGen)
                {
                    // while the game is in front: one ping every 15 s, just for the tray icon
                    if (!appActive && DateTime.Now - last < TimeSpan.FromSeconds(15)) { Thread.Sleep(500); continue; }
                    last = DateTime.Now;
                    double v = Pinger.Once(target, 1500);
                    if (gen != monitorGen) break;
                    Ui(() =>
                    {
                        if (gen != monitorGen) return;
                        liveLabel.Text = T("Status: ", "الحالة: ") + label + "  ·  " + Ms(v);
                        liveLabel.ForeColor = PingColor(v);
                        SetTrayText(label + " · " + Ms(v));
                        Telemetry.Ping = v < 0 ? -1 : (int)Math.Round(v);
                        graph.Add(v);
                        TrackLoss(label, v);
                    });
                    Thread.Sleep(1000);
                }
            }) { IsBackground = true };
            t.Start();
        }

        // Packet loss to the locked/boosted server over the last minute: heavy loss is reported (once per server).
        readonly Queue<bool> lossWindow = new Queue<bool>();
        string lossLabel;

        void TrackLoss(string label, double v)
        {
            if (label != lossLabel) { lossLabel = label; lossWindow.Clear(); }
            lossWindow.Enqueue(v < 0);
            if (lossWindow.Count > 60) lossWindow.Dequeue();
            if (lossWindow.Count < 60) return;
            int lost = lossWindow.Count(x => x);
            if (lost * 100 / lossWindow.Count >= 15)
                Issues.Report("connection_loss", label + ": " + lost + " of the last 60 pings lost",
                    new Dictionary<string, object> { { "server", label }, { "lost", lost } }, label);
        }

        void StopMonitor()
        {
            monitorGen++;
            Telemetry.Ping = -1;
            liveLabel.Text = StatusOff;
            liveLabel.ForeColor = Dim;
            SetTrayText(T("ready", "جاهز"));
        }

        // While servers are locked, the status bar follows the ping of the locked server.
        void StartLockMonitor()
        {
            var g = CurrentGame;
            if (lockSummary == null || boosting || g == null) return;
            var r = g.Regions.Where(x => x.Picked && x.Target != null).OrderBy(BestLatency).FirstOrDefault();
            if (r != null) StartMonitor(r.Target, T("locked on ", "مثبّت على ") + ShortName(r));
        }

        void StopAll()
        {
            StopMonitor();
            boosting = false;
            btnBoost.Enabled = true;
            btnStop.Enabled = false;
            SetStatus(T("Stopped.", "وقّفناه."));
            StartLockMonitor();
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
            if (host == null) { Msg(T("This file has no 'Endpoint =' line.", "الملف هادا ما فيه سطر 'Endpoint ='."), ToastKind.Warn); return; }

            var vals = InputDialog.Show(this, T("Add a relay", "ضيف وسيط"),
                new[] { T("Name", "الاسم"), T("Agent link (from setup-relay.sh, optional)", "رابط أداة القياس (من setup-relay.sh، مو ضروري)"), T("Agent token", "رمز أداة القياس (Token)") },
                new[] { Path.GetFileNameWithoutExtension(path), "http://" + host + ":8787", "" });
            if (vals == null || vals[0] == "") return;
            if (relays.Any(r => r.Name.Equals(vals[0], StringComparison.OrdinalIgnoreCase)))
            {
                Msg(T("There's already a relay with that name.", "في وسيط بنفس الاسم."), ToastKind.Warn);
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

        // ---------------------------------------------------------------- developer mode

        // Five quick clicks on the logo turn the whole app into its developer version (with the secret key in dev.key):
        // DEV badge and strip, a Developer tab with live statistics and tools, extra detail in the lists, and every
        // problem the app detects shown right away. The same five clicks (or the strip's button) turn it off.
        bool devMode;
        TextBox devOut;

        static bool DevModeSaved { get { return Storage.GetSetting("dev_mode") == "1" && !string.IsNullOrEmpty(Telemetry.DevKey); } }

        void ToggleDevMode()
        {
            if (devMode) { SetDevMode(false); return; }
            if (!Telemetry.Configured) { Msg("Statistics backend isn't configured (Telemetry.SupabaseUrl / SupabaseKey).", ToastKind.Bad); return; }
            if (string.IsNullOrEmpty(Telemetry.DevKey))
            {
                var v = InputDialog.Show(this, "Developer key", new[] { "Supabase secret key (stays only on this PC, in dev.key)" }, new[] { "" });
                if (v == null || v[0] == "") return;
                Telemetry.DevKey = v[0];
            }
            SetDevMode(true);
        }

        void SetDevMode(bool on)
        {
            Storage.SetSetting("dev_mode", on ? "1" : "0");
            if (on) currentPage = 3;
            else if (currentPage == 3) currentPage = 0;
            if (busy) pendingRebuild = true; else RebuildUi();
            Msg(on ? "Developer mode ON" : "Developer mode off", on ? ToastKind.Good : ToastKind.Info);
        }

        // The strip under the header: always-visible quick tools, in a colour no player ever sees.
        static readonly Color DevColor = Color.FromArgb(18, 48, 52), DevAccent = Color.FromArgb(104, 236, 204);

        Panel BuildDevStrip()
        {
            var strip = new Panel { Dock = DockStyle.Top, Height = 42, BackColor = DevColor, Padding = new Padding(12, 6, 12, 6), RightToLeft = RightToLeft.No };
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, RightToLeft = RightToLeft.No };
            flow.Controls.Add(new Label { Text = "DEVELOPER MODE", ForeColor = DevAccent, Font = Fonts.Bold(9f), AutoSize = true, Margin = new Padding(0, 7, 16, 0) });
            Action<string, Action> add = (text, act) =>
            {
                var b = MakeButton(text, Color.FromArgb(28, 70, 74));
                b.ForeColor = Color.White;
                b.AutoSize = false;
                b.Size = new Size(TextRenderer.MeasureText(text, Font).Width + 26, 30);
                b.Margin = new Padding(0, 0, 8, 0);
                b.Click += delegate { act(); };
                flow.Controls.Add(b);
            };
            add("Developer tab", () => ShowPage(3));
            add("Self-test", DevSelfTest);
            add("Firewall rules", DevShowRules);
            add("Test notices", DevTestToasts);
            add("Data folder", () => Brand.Open(Storage.Dir));
            add("Exit developer mode", () => SetDevMode(false));
            strip.Controls.Add(flow);
            return strip;
        }

        Panel BuildDevPage()
        {
            var p = new Panel { RightToLeft = RightToLeft.No };
            var dash = new DevPanel { Dock = DockStyle.Fill };
            dash.ForgetKey += () => SetDevMode(false);

            var tools = new Card { Dock = DockStyle.Right, Width = 340, Fill = Panel1, Padding = new Padding(12), RightToLeft = RightToLeft.No };
            var flow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 330, FlowDirection = FlowDirection.LeftToRight, RightToLeft = RightToLeft.No };
            Action<string, Action> add = (text, act) =>
            {
                var b = MakeButton(text, Panel2);
                b.Size = new Size(150, 32);
                b.Margin = new Padding(0, 0, 8, 8);
                b.Click += delegate { try { act(); } catch (Exception ex) { DevOut("! " + ex.Message); } };
                flow.Controls.Add(b);
            };
            add("Self-test", DevSelfTest);
            add("Firewall rules", DevShowRules);
            add("Refresh IP lists", DevRefreshLists);
            add("Server ranges", DevShowRanges);
            add("Test notices", DevTestToasts);
            add("Fake update bar", DevFakeUpdateBar);
            add("Forced update", () => UpdateDialog.Show(this, new Release { Tag = "v9.9.9-beta", Ver = new Version(9, 9, 9) }, true));
            add("Send test problem", () => { Issues.Report("crash", "developer test problem " + DateTime.Now.ToString("HH:mm:ss")); DevOut("sent a test problem"); });
            add("issues.txt", () => DevOpenFile("issues.txt"));
            add("traffic.txt", () => DevOpenFile("traffic.txt"));
            add("crash.log", () => DevOpenFile("crash.log"));
            add("Copy diagnostics", () => { Clipboard.SetText(ContactDialog.Diagnostics(logBox.Text)); DevOut("diagnostics copied"); });
            add("Reset first-run", DevResetFirstRun);
            add("Exit dev mode", () => SetDevMode(false));
            devOut = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
                BackColor = Bg, ForeColor = DevAccent, Font = new Font("Consolas", 9f), RightToLeft = RightToLeft.No
            };
            tools.Controls.Add(devOut);
            tools.Controls.Add(new Label { Text = "Output", Dock = DockStyle.Top, Height = 22, ForeColor = Dim });
            tools.Controls.Add(flow);
            tools.Controls.Add(new Label { Text = "TOOLS", Dock = DockStyle.Top, Height = 26, ForeColor = DevAccent, Font = Fonts.Bold(9f) });

            p.Controls.Add(dash);
            p.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 12 });
            p.Controls.Add(tools);
            foreach (var r in Issues.Recent) DevOut(r.At.ToString("HH:mm:ss") + "  problem  " + r.Code + ": " + r.Detail);
            return p;
        }

        void DevOut(string line)
        {
            Ui(() => { if (devOut != null && !devOut.IsDisposed) devOut.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + line + Environment.NewLine); });
        }

        // Everything the app depends on, checked in one go.
        void DevSelfTest()
        {
            ShowPage(3);
            DevOut("---- self-test");
            Task.Run(() =>
            {
                Action<string, Func<string>> check = (name, f) =>
                {
                    string r;
                    try { r = f(); } catch (Exception ex) { r = "FAIL " + ex.Message; }
                    DevOut(name.PadRight(22) + r);
                };
                check("Version", () => Brand.DisplayVersion + ", install " + Telemetry.InstallId.Substring(0, 8));
                check("Windows Firewall", () => ServerLock.FirewallOn() ? "on" : "OFF (locks can't work)");
                check("Lock rules", () => ServerLock.Exists() ? "present" : "none");
                check("pktmon", () => File.Exists(Path.Combine(Environment.SystemDirectory, "pktmon.exe")) ? "available" : "MISSING");
                check("WireGuard", () => Tunnel.Installed ? "installed" : "not installed");
                check("GitHub", () => { Http.Get("https://api.github.com/repos/" + Brand.Repo, 8000); return "ok"; });
                check("Statistics backend", () => { Telemetry.Query("installs?select=install_id&limit=1"); return "ok (secret key works)"; });
                check("Google list", () => IpLists.Resolve("gcp:me-central2").Count + " Dammam ranges");
                check("Amazon list", () => IpLists.Resolve("aws:eu-central-1").Count + " Frankfurt ranges");
                check("Blizzard network", () => IpLists.Resolve("asn:57976").Count + " blocks");
                foreach (var g in games.Where(x => x.LockSpace != null))
                    check("Exe " + g.Name, () => SystemInfo.FindExe(g) ?? "not found");
                check("Problems this session", () => Issues.Recent.Count.ToString());
                DevOut("---- done");
            });
        }

        void DevShowRules()
        {
            ShowPage(3);
            Task.Run(() =>
            {
                var psi = new ProcessStartInfo("netsh", "advfirewall firewall show rule name=\"2XPing-RegionLock\" verbose")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                string text;
                using (var proc = Process.Start(psi)) { text = proc.StandardOutput.ReadToEnd(); proc.WaitForExit(15000); }
                var rules = text.Split(new[] { "Rule Name:" }, StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
                DevOut("---- firewall rules: " + rules.Count);
                foreach (var r in rules)
                {
                    Func<string, string> field = k =>
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(r, k + @":\s*(.+)");
                        return m.Success ? m.Groups[1].Value.Trim() : "-";
                    };
                    string ips = field("RemoteIP");
                    DevOut(field("Direction") + " " + field("Protocol") + " ports " + field("RemotePort") + "  " +
                           (ips.Length > 0 ? ips.Split(',').Length + " ranges" : "") + "  " + Path.GetFileName(field("Program")));
                }
            });
        }

        void DevRefreshLists()
        {
            try { Directory.Delete(Path.Combine(Storage.Dir, "cache"), true); } catch { }
            foreach (var g in games) g.Loaded = false;
            DevOut("IP list cache cleared, reloading the current game");
            OnGameChanged();
        }

        void DevShowRanges()
        {
            var g = CurrentGame;
            if (g == null) return;
            ShowPage(3);
            DevOut("---- " + g.Name + " datacenters");
            foreach (var r in g.Regions)
                DevOut(r.Id.PadRight(16) + r.Cidrs.Count.ToString().PadLeft(4) + " ranges  ping " + (r.Target != null ? r.Target.ToString() : "-"));
            if (g.LockSpace != null) DevOut("lock space: " + string.Join(", ", g.LockSpace.Take(6)) + (g.LockSpace.Length > 6 ? " ..." : ""));
        }

        void DevTestToasts()
        {
            Msg("Info notice: this is how a normal message looks.");
            Msg("Good notice: locked on Dammam ✓", ToastKind.Good);
            Msg("Warning notice: you're on a server outside your lock.", ToastKind.Warn);
            Msg("Error notice: couldn't add the firewall rules.", ToastKind.Bad);
        }

        void DevFakeUpdateBar()
        {
            Storage.SetSetting("update_seen", "v9.9.9-beta|" + DateTime.UtcNow.AddDays(-1).Ticks);
            pendingUpdate = new Release { Tag = "v9.9.9-beta", Ver = new Version(9, 9, 9) };
            ShowUpdateBar();
            DevOut("showing a fake 9.9.9 update bar (restart the app to clear it)");
        }

        void DevOpenFile(string name)
        {
            string path = Path.Combine(Storage.Dir, name);
            if (File.Exists(path)) Brand.Open(path); else DevOut(name + " doesn't exist yet");
        }

        void DevResetFirstRun()
        {
            foreach (var k in new[] { "beta_ack", "tray_tip", "tip_tray" }) Storage.SetSetting(k, "");
            foreach (var g in games) Storage.SetSetting("tip_" + g.Icon, "");
            DevOut("first-run notices and tips will show again (the welcome screen needs consent=0 in settings.txt)");
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
                f.Font = Fonts.Ui(10f);
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
                var ok = new RoundButton { Text = Lang.T("OK", "تمام"), DialogResult = DialogResult.OK, Bounds = new Rectangle(Lang.Ar ? 112 : 320, y, 88, 34), FlatStyle = FlatStyle.Flat, BackColor = MainForm.Accent, ForeColor = Color.Black };
                var cancel = new RoundButton { Text = Lang.T("Cancel", "إلغاء"), DialogResult = DialogResult.Cancel, Bounds = new Rectangle(Lang.Ar ? 16 : 416, y, 88, 34), FlatStyle = FlatStyle.Flat, BackColor = MainForm.Panel2 };
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
