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
        Panel pickBar, tabBar, bottomPanel, infoBar, infoGap, cardHost, listCard, listGap;
        StateCard stateCard;
        TextBox logBox;
        PingGraph graph;
        Panel[] pages;
        Button[] tabButtons;
        int currentPage;

        bool busy, boosting, clickedCheck;
        readonly HashSet<Game> tested = new HashSet<Game>(); // games tested since the app opened
        volatile bool tunnelActive;
        int monitorGen;     // read by the monitor thread: always through Volatile/Interlocked
        int boostGen;       // bumped by Stop: a relay route still being built then goes away instead of starting
        string lockSummary; // null = no region lock
        LockTest.LockInfo lockInfo; // how the current lock was applied (developer lock test)
        HashSet<string> lockClosed; // Valve: the entry relays the current lock closes too (StrictLock, ruling R3)
        RouteNote lockRoute;        // ...and what it costs, as the card tells it (null = nothing to tell)
        Dictionary<string, int> activity; // Deadlock matches/hour per matchmaking region

        Game CurrentGame { get { return gameList.SelectedItem as Game; } }
        bool LockedOut(Game g, Region r)
        {
            if (lockSummary == null || g == null || !g.CanLock || r.Picked || r.NoRelay) return false;
            return g.Hosts(r) || (g == lockGame && lockClosed != null && lockClosed.Contains(r.Id));
        }

        public MainForm()
        {
            uiThread = Thread.CurrentThread.ManagedThreadId;
            HandleCreated += delegate { FlushEarlyUi(); };
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
            Load += delegate { FitAdvancedHeight(true); };

            RefreshRelays();
            // leftovers from a crash: on the firewall worker, so this runs before any lock the player asks for in the
            // first seconds (only the rule and tunnel steps, which must stay in order)
            FirewallWorker.Run(() =>
            {
                bool? present = ServerLock.Present();
                if (present == true) { Issues.Report("leftover_lock", "rules from a previous session were still there"); ServerLock.Remove(); }
                else if (present == false) ServerLock.MayExist = false; // unknown (netsh didn't answer): closing still checks
                // a relay tunnel left installed by a crash (the service would start again at every boot)
                if (Tunnel.ServiceInstalled && Tunnel.Installed)
                {
                    Issues.Report("leftover_tunnel", "relay tunnel service from a previous session was still installed");
                    Tunnel.Stop();
                }
            });
            Task.Run(() =>
            {
                // a capture of ours left running if the app closed mid-way (e.g. during a lock test); never someone else's
                try { if (Detector.Run("pktmon", "filter list").IndexOf("2XPing", StringComparison.OrdinalIgnoreCase) >= 0) Detector.Cleanup(); } catch { }
                // a firewall that's off (locks can't work then)
                try { if (!ServerLock.FirewallOn()) Issues.Report("firewall_off", "Windows Firewall is off for the current network profile"); } catch { }
            });
            if (!Tunnel.Installed)
                Log(T("WireGuard is not installed, so relay routing is off until you install it (Tools).",
                      "برنامج WireGuard مو مثبّت، فالوسيط مقفّل لين تثبّته (من «أدوات وسجل»)."));
            BuildWindowEvents();
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
                // keep checking while the app stays open (it can run minimized for days)
                var updates = new System.Windows.Forms.Timer { Interval = 3 * 60 * 60 * 1000 };
                updates.Tick += delegate { CheckForUpdate(false); };
                updates.Start();
            };
            FormClosing += delegate
            {
                Interlocked.Increment(ref monitorGen);
                Interlocked.Increment(ref boostGen);
                bool tunnel = tunnelActive, locked = lockSummary != null, testing = lockTesting;
                // from here on, no queued step adds rules or starts the tunnel (one already queued returns at once)
                FirewallWorker.Closing = true;
                // bounded (the updater waits for this process), but always: the firewall changes still queued finish
                // first, then whatever may be left goes, whether or not the window thinks a lock is on
                var cleanup = new Thread(() =>
                {
                    FirewallWorker.Drain(8000);
                    if (tunnel || Tunnel.ServiceInstalled) Tunnel.Stop();
                    if (locked || ServerLock.MayExist) ServerLock.Remove();
                    if (testing) Detector.Cleanup(); // the lock test's thread won't reach its finally once the app exits
                }) { IsBackground = true };
                cleanup.Start();
                cleanup.Join(14000);
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

        // ---------------------------------------------------------------- minimized window (taskbar)

        // Minimized, 2X Ping stays a normal window in the taskbar (no tray icon): its title shows the live status, so
        // hovering the taskbar button shows it, and important notices flash the button and wait for the window to come back.
        bool wasMinimized;
        FormWindowState restoreState = FormWindowState.Normal; // what a restore goes back to (Normal or Maximized)
        string liveStatus;                                          // e.g. "locked on Europe · 78 ms"; null = nothing yet
        readonly List<HeldNotice> heldNotices = new List<HeldNotice>();
        class HeldNotice { public string Text; public ToastKind Kind; public bool AboutMatch; }
        const int HeldMax = 4;

        void BuildWindowEvents()
        {
            Resize += delegate
            {
                bool min = WindowState == FormWindowState.Minimized;
                if (!min) restoreState = WindowState;
                if (min == wasMinimized) return;
                wasMinimized = min;
                if (min) Telemetry.Send("use", null, -1, "minimized");
                ApplyTitle();
                if (!min) ShowHeldNotices();
            };
            Shown += delegate { ShowHeldNotices(); };
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

        // Someone opened 2X Ping again while it runs: show this window on top (also when minimized).
        public void BringUp()
        {
            if (IsDisposed) return;
            RestoreWindow();
            TopMost = true; // a short TopMost flip gets it past the foreground lock
            TopMost = false;
            Activate();
        }

        void RestoreWindow()
        {
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = restoreState;
            Activate();
        }

        bool Minimized { get { return !Visible || WindowState == FormWindowState.Minimized; } }

        // The live status for the taskbar title while minimized.
        void SetLiveStatus(string text)
        {
            liveStatus = text;
            if (Minimized) ApplyTitle();
        }

        // Minimized: "2X Ping · locked on Europe · 78 ms" (what hovering the taskbar button shows); otherwise the normal title.
        void ApplyTitle()
        {
            // Brand.Caption is wrapped in LRE/PDF marks: keeps "2X" in Latin order inside a right-to-left title bar
            string normal = Brand.Caption + (Brand.IsBeta ? " (Beta)" : "") + "  -  by " + Brand.Developer + (devMode ? "   [DEV]" : "");
            string title = TitleFor(normal, WindowState == FormWindowState.Minimized, liveStatus);
            if (Text != title) Text = title;
        }

        internal static string TitleFor(string normal, bool minimized, string status)
        {
            return minimized && !string.IsNullOrEmpty(status) ? Brand.Caption + " · " + status : normal;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct FlashInfo { public uint Size; public IntPtr Hwnd; public uint Flags, Count, Timeout; }
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool FlashWindowEx(ref FlashInfo info);

        // Flash the taskbar button until the window comes to the front.
        void FlashTaskbar()
        {
            if (!IsHandleCreated) return;
            var fi = new FlashInfo { Hwnd = Handle, Flags = 2 | 12, Count = 0, Timeout = 0 }; // FLASHW_TRAY | FLASHW_TIMERNOFG
            fi.Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf(fi);
            try { FlashWindowEx(ref fi); } catch { }
        }

        // An important notice while minimized: kept (the latest few) for when the window is back.
        void HoldNotice(string text, ToastKind kind, bool aboutMatch)
        {
            heldNotices.RemoveAll(n => n.Text == text);
            heldNotices.Add(new HeldNotice { Text = text, Kind = kind, AboutMatch = aboutMatch });
            while (heldNotices.Count > HeldMax) heldNotices.RemoveAt(0);
            FlashTaskbar();
        }

        // A new match or a lock change: the held warnings about the old match or lock check are stale. Errors stay.
        void DropHeldWarnings()
        {
            heldNotices.RemoveAll(n => n.AboutMatch && n.Kind != ToastKind.Bad);
        }

        void ShowHeldNotices()
        {
            if (Minimized || heldNotices.Count == 0) return;
            var list = heldNotices.ToList();
            heldNotices.Clear();
            foreach (var n in list) Msg(n.Text, n.Kind);
        }

        void ApplyDirection()
        {
            ApplyTitle();
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
                Dock = DockStyle.Right, TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 14, 0),
                Visible = !simple // the simple layout's card says the state
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
                TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 12, 0), Text = T("Ready", "جاهز"),
                Visible = !simple
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
            // the state card places its own tiles and links on the reading side
            if (parent is ListView || parent is ListBox || parent is ComboBox || parent is TextBox || parent is StateCard) return;
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

        // The state card (see CardLogic): the situation in plain words, the region tiles and the one thing to press.
        // It sits in a plain panel, so it is never a direct child of the mirrored form.
        Panel BuildStateCard()
        {
            cardHost = new Panel { Dock = DockStyle.Top, Height = 280 };
            stateCard = new StateCard { Dock = DockStyle.Fill };
            stateCard.ButtonPressed += delegate { CardButton(); };
            stateCard.SelectionChanged += delegate { userPicked = true; UpdateCard(); };
            stateCard.OtherRegions += delegate
            {
                expanded = !expanded;
                if (expanded) Telemetry.Send("use", null, -1, "other regions");
                UpdateCard();
            };
            stateCard.Manual += delegate
            {
                manualOpen = !manualOpen;
                if (manualOpen) Telemetry.Send("use", null, -1, "pick by hand");
                ApplyCardLayout();
            };
            stateCard.TestPing += delegate { Telemetry.Send("use", null, -1, "test link"); RunTest(); };
            cardHost.Controls.Add(stateCard);
            cardHost.Resize += delegate { FitCard(stateCard.PreferredHeight(cardHost.Width)); };
            choicesGame = null;
            return cardHost;
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
                if (item == null || g == null || !g.CanLock || !(item.Tag is Region) || ((Region)item.Tag).NoRelay) return;
                var cell = FirstCell(serverList, item, item.Bounds);
                bool onBox = Lang.Ar ? e.X >= cell.Right - CheckArea && e.X <= cell.Right : e.X >= cell.X && e.X <= cell.X + CheckArea;
                if (!onBox) return;
                clickedCheck = true;
                TogglePick((Region)item.Tag);
            };
            serverList.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Space || CurrentGame == null || !CurrentGame.CanLock) return;
                foreach (ListViewItem item in serverList.SelectedItems) { var r = item.Tag as Region; if (r != null && !r.NoRelay) TogglePick(r); }
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
            // (the simple layout shows it under the state card only after "Pick by hand")
            listCard = new Card { Dock = DockStyle.Fill, Fill = Panel1, Radius = 14, Padding = new Padding(12, 12, 12, 12) };
            listCard.Controls.Add(serverList);
            listCard.Controls.Add(pickBar);
            p.Controls.Add(listCard);
            p.Controls.Add(listGap = new Panel { Dock = DockStyle.Top, Height = 12 });
            p.Controls.Add(infoBar = BuildInfoBar());
            p.Controls.Add(infoGap = new Panel { Dock = DockStyle.Top, Height = 12 });
            infoBar.Visible = infoGap.Visible = !simple;
            p.Controls.Add(BuildStateCard());
            p.Controls.Add(bottom);
            p.Controls.Add(gameNote);
            p.Resize += delegate { ApplyCardLayout(); };
            ApplyCardLayout();
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
            if (g != null && g.CanLock && g.Hosts((Region)e.Item.Tag) && !((Region)e.Item.Tag).NoRelay)
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
            if (!g.Released || g.InDevelopment)
            {
                // a small tag: "trial" (developer mode only, not in the public version yet) or
                // "in development" (public, but its server lock is still being worked on)
                bool trial = !g.Released;
                string tag = trial ? T("TRIAL", "تجريبي") : T("IN DEVELOPMENT", "قيد التطوير");
                using (var f = Fonts.Semi(7.5f))
                {
                    var ts = TextRenderer.MeasureText(tag, f);
                    var tagRc = new Rectangle(Lang.Ar ? textRc.X : textRc.Right - ts.Width - 8, e.Bounds.Y + (e.Bounds.Height - 18) / 2, ts.Width + 8, 18);
                    using (var path = Shapes.Round(tagRc, 6))
                    using (var b = new SolidBrush(trial ? Color.FromArgb(28, 70, 74) : Color.FromArgb(66, 52, 28))) e.Graphics.FillPath(b, path);
                    TextRenderer.DrawText(e.Graphics, tag, f, tagRc, trial ? DevAccent : Warn, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    textRc = Lang.Ar ? new Rectangle(textRc.X + tagRc.Width + 6, textRc.Y, textRc.Width - tagRc.Width - 6, textRc.Height)
                                     : new Rectangle(textRc.X, textRc.Y, textRc.Width - tagRc.Width - 6, textRc.Height);
                }
            }
            TextRenderer.DrawText(e.Graphics, g.Name, Font, textRc,
                sel ? TextC : Color.FromArgb(206, 196, 226), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | Align);
        }

        bool pendingRebuild; // settings changed while a test, a server check or a reset was running: rebuild when it ends

        // Called wherever busy ends: a Settings change made meanwhile is applied now (after the caller is done).
        void SetIdle()
        {
            busy = false;
            if (!pendingRebuild || !IsHandleCreated) return;
            // posted, never inline: the caller goes on using the current controls first
            try
            {
                BeginInvoke((Action)(() =>
                {
                    if (busy || !pendingRebuild) return;
                    pendingRebuild = false;
                    RebuildUi();
                }));
            }
            catch (InvalidOperationException) { }
        }

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
        // The advanced layout has the card, the list and the live graph on one page: it needs a taller window than the
        // simple one. Grows the window to that height when the screen allows (never shrinks it).
        const int AdvancedClientHeight = 900;

        void FitAdvancedHeight(bool center)
        {
            if (simple || WindowState != FormWindowState.Normal) return;
            var area = Screen.FromControl(this).WorkingArea;
            int h = Math.Min(Height - ClientSize.Height + LogicalToDeviceUnits(AdvancedClientHeight), area.Height);
            if (h <= Height) return;
            Height = h;
            if (center) CenterToScreen();
            else if (Bottom > area.Bottom || Top < area.Top) Top = Math.Max(area.Top, area.Bottom - Height);
        }

        void RebuildUi()
        {
            bool wasSimple = simple;
            simple = Prefs.Simple;
            devMode = DevModeSaved;
            GameDb.ShowUnreleased = devMode;
            games = GameDb.Available(allGames);
            var g = CurrentGame;
            // re-selecting the game below starts its card over: the match seen and the player's choices survive a rebuild
            string keepRegion = matchRegion, keepServer = currentServer;
            bool keepOnPicked = matchOnPicked, keepViaRelay = matchViaRelay, keepUserPicked = userPicked, keepExpanded = expanded;
            int keepMs = currentMs;
            SuspendLayout();
            var old = Controls.Cast<Control>().ToArray();
            Controls.Clear();
            ApplyDirection();
            BuildUi();
            foreach (var c in old) c.Dispose();
            ResumeLayout(true);
            if (wasSimple && !simple) FitAdvancedHeight(false);
            ShowUpdateBar();
            RefreshRelays();
            btnBoost.Enabled = !boosting;
            btnStop.Enabled = boosting;
            if (g != null && gameList.Items.Contains(g))
            {
                gameList.SelectedItem = g;
                matchRegion = keepRegion; currentServer = keepServer; currentMs = keepMs;
                matchOnPicked = keepOnPicked; matchViaRelay = keepViaRelay;
                userPicked = keepUserPicked; expanded = keepExpanded;
                UpdateCard();
            }
            else if (gameList.Items.Count > 0) gameList.SelectedIndex = 0; // the open game was hidden in Settings
        }

        // ---------------------------------------------------------------- helpers

        int uiThread;
        readonly List<Action> earlyUi = new List<Action>(); // posted from other threads before the window had a handle
        bool handleReady;

        void Ui(Action a)
        {
            if (IsDisposed || Disposing) return;
            if (Thread.CurrentThread.ManagedThreadId == uiThread) { a(); return; }
            // without a handle, InvokeRequired says false even on another thread: never run it here, keep it for later
            lock (earlyUi)
                if (!handleReady) { earlyUi.Add(a); return; }
            try { BeginInvoke(a); }
            catch (InvalidOperationException) { } // also ObjectDisposedException: the window is closing
        }

        void FlushEarlyUi()
        {
            List<Action> waiting;
            lock (earlyUi)
            {
                handleReady = true;
                waiting = earlyUi.ToList();
                earlyUi.Clear();
            }
            foreach (var a in waiting)
                try { BeginInvoke(a); } catch (InvalidOperationException) { }
        }

        void Log(string msg)
        {
            Ui(() => logBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + "   " + msg + Environment.NewLine));
        }

        void SetStatus(string msg) { Ui(() => statusLabel.Text = msg); }

        ChatToasts chat;
        Panel centerPanel;

        // Notices appear as chat lines at the bottom of the window and fade out (no popups, no OK button).
        // While the window is minimized, warnings and errors flash its taskbar button and show when it's back; the rest
        // (information, good news the card already shows) is dropped.
        void Msg(string text, ToastKind kind = ToastKind.Info) { Msg(text, kind, false); }

        // aboutMatch: a notice about the match or the lock check (from AdvancedMsg), dropped when the match or lock changes
        void Msg(string text, ToastKind kind, bool aboutMatch)
        {
            Ui(() =>
            {
                if (Minimized)
                {
                    if (kind == ToastKind.Warn || kind == ToastKind.Bad) HoldNotice(text, kind, aboutMatch);
                    return;
                }
                if (chat == null) chat = new ChatToasts(this, () => centerPanel.RectangleToScreen(centerPanel.ClientRectangle));
                chat.Show(text, kind);
            });
        }

        // A notice the simple layout leaves out because the state card already says it; the advanced layout keeps it.
        // While the window is minimized the card can't be seen, so a warning still goes through (flashing the taskbar
        // button, shown when the window is back).
        void AdvancedMsg(string text, ToastKind kind = ToastKind.Info)
        {
            if (!simple || Minimized) Msg(text, kind, true);
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
            // the advanced layout's note line; the simple layout leaves the note out, since it talks about the list
            // and buttons only the advanced layout shows
            gameNote.Text = g.Note;
            Telemetry.Game = g.Name;
            Telemetry.Server = null;
            Storage.SetSetting("last_game", g.Icon);
            ResetDetectButton();
            // the card starts over for this game: nothing from the previous game's match, tiles or test stays on it
            ClearMatch();
            choicesGame = null;
            userPicked = expanded = false;
            if (loadFailed == g) loadFailed = null;
            pickBar.Visible = g.CanLock;
            UpdateLockBar();
            serverList.Items.Clear();
            UpdateCard();
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
                        loadFailed = g;
                        UpdateCard();
                        SetStatus(T("Couldn't get the servers. Check your internet, then pick the game again.",
                                    "ما قدرت أجيب السيرفرات. شيّك على النت، وبعدين اختار اللعبة مرة ثانية."));
                        Log(g.Name + ": " + err);
                        Issues.Report("servers_load_failed", g.Name + ": " + err, null, g.Icon);
                        return;
                    }
                    loadedGames.Add(g);
                    if (g.Regions.Count == 0) loadFailed = g;
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
                    else if ((Prefs.AutoTest && !tested.Contains(g)) || retestGame == g)
                    {
                        retestGame = null;
                        RunTest();
                    }
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
            return DeadlockData.Hosting.TryGetValue(r.Id, out mm) && activity != null && activity.ContainsKey(mm) ? mm : "";
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

        void FillServers(Game g)
        {
            serverList.BeginUpdate();
            serverList.Items.Clear();
            // the datacenters that host matches but can't be locked (no relay) are listed too, after the others
            var rows = g.Regions.Concat(g.NoRelayHosts).ToList();
            if (Grouped(g))
            {
                var groups = rows.GroupBy(RegionKey)
                    .OrderBy(gr => gr.Key == "" ? 1 : 0)
                    .ThenByDescending(gr => gr.Key == "" ? 0 : activity[gr.Key]).ToList();
                bool first = true, russia = false;
                foreach (var gr in groups)
                {
                    if (gr.Key == "" && !russia) russia = AddRussiaHeader();
                    AddHeader(new GroupHeader { Key = gr.Key, Matches = gr.Key == "" ? 0 : activity[gr.Key], Top = first && gr.Key != "" });
                    first = false;
                    foreach (var r in gr.OrderBy(x => x.NoRelay ? 2 : LockedOut(g, x) ? 1 : 0).ThenBy(BestLatency)) AddRegionRow(r);
                }
                if (!russia) AddRussiaHeader();
            }
            else
                foreach (var r in rows) AddRegionRow(r);
            serverList.EndUpdate();
            RebuildChoices();
            UpdateCard();
        }

        void AddHeader(GroupHeader h)
        {
            var hi = new ListViewItem("") { Tag = h };
            for (int i = 0; i < 7; i++) hi.SubItems.Add("");
            serverList.Items.Add(hi);
        }

        // Deadlock: Russia has no datacenter of its own (its queue plays on the European ones), so it gets a header line
        // of its own with its players and why it can't be picked. false when there are no numbers for it.
        bool AddRussiaHeader()
        {
            int matches;
            if (activity == null || !activity.TryGetValue(DeadlockData.Russia, out matches)) return false;
            AddHeader(new GroupHeader { Key = DeadlockData.Russia, Matches = matches });
            return true;
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
            if (h.Key == DeadlockData.Russia)
                return name + "   ·   " + Stats.Approx(PlayersOf(h.Matches)) + T(" online", " أونلاين") + "   ·   " +
                       T("separate queue on the European servers, the lock can't choose it", "لها كيو خاص يلعب على سيرفرات أوروبا، والتثبيت ما يقدر يختارها");
            return name + "   ·   " + Stats.Approx(PlayersOf(h.Matches)) + T(" online", " أونلاين") +
                   "   ·   " + T("wait ", "الانتظار ") + Stats.EstimateWait(h.Matches);
        }

        void DrawGroupHeader(DrawListViewSubItemEventArgs e, GroupHeader h)
        {
            var row = e.Item.Bounds;
            using (var b = new SolidBrush(Color.FromArgb(40, 30, 60))) e.Graphics.FillRectangle(b, row);
            var font = Fonts.Semi(10.5f);
            var text = Rectangle.Inflate(row, -14, 0);
            TextRenderer.DrawText(e.Graphics, HeaderText(h), font, text, h.Key == "" || h.Key == DeadlockData.Russia ? Dim : Lavender,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | Align);
            if (h.Top)
            {
                // the busiest region gets a gold sparkle badge on the opposite side
                var flags = TextFormatFlags.VerticalCenter | (Lang.Ar ? TextFormatFlags.Left : TextFormatFlags.Right);
                TextRenderer.DrawText(e.Graphics, T("✦ most players", "✦ الأكثر لاعبين"), font, text, Warn, flags | Lang.Flags);
            }
            font.Dispose();
        }

        // ---------------------------------------------------------------- the state card

        List<RegionChoice> allChoices = new List<RegionChoice>(); // every region of the game on screen
        List<RegionChoice> featured = new List<RegionChoice>();   // the two or three tiles shown first
        List<RegionChoice> everyChoice = new List<RegionChoice>(); // all of them, featured first ("Other regions")
        Game choicesGame;   // the game the choices were built for
        string choicesSig;  // what they show, so a rebuild that changes nothing keeps the same tiles
        bool userPicked;    // the player clicked a tile: keep their selection when the numbers change
        bool expanded, manualOpen;
        Game testGame, testNext, retestGame, loadFailed, lockGame;
        Game lockingGame;   // a lock for this game is being applied right now (the card says so)
        readonly HashSet<Game> loadedGames = new HashSet<Game>();
        string lockNames;   // the lock in the card's words: region names, or the picked server names
        // what the automatic check saw: the region of the running match, or (games that can't be locked) the server
        string matchRegion, currentServer;
        bool matchOnPicked, matchViaRelay;
        int currentMs = -1;

        void ClearMatch()
        {
            matchRegion = null;
            matchOnPicked = matchViaRelay = false;
            currentServer = null;
            currentMs = -1;
        }

        void SetMatch(string region, bool onPicked, bool viaRelay)
        {
            // a different match: the warnings held about the last one are stale (the same match keeps them)
            if (region != matchRegion || onPicked != matchOnPicked || viaRelay != matchViaRelay) DropHeldWarnings();
            matchRegion = region;
            matchOnPicked = onPicked;
            matchViaRelay = viaRelay;
        }

        // The region (as the player chooses it) a datacenter belongs to, or the datacenter's own name.
        static string RegionNameOf(Game g, Region dc)
        {
            var def = g.RegionMap != null ? g.RegionMap.FirstOrDefault(d => d.Ids.Contains(dc.Id)) : null;
            return def != null ? Lang.T(def.En, def.Ar) : ShortName(dc);
        }

        static string Short(string name) { return name.Split(new[] { "  [", "  (" }, StringSplitOptions.None)[0]; }

        // A Valve relay's place in a word or two, for "via Dubai" / «عن طريق دبي».
        static string PlaceName(Region r)
        {
            string n = ShortName(r);
            foreach (var cut in new[] { " (", " - " })
            {
                int i = n.IndexOf(cut, StringComparison.Ordinal);
                if (i > 0) n = n.Substring(0, i);
            }
            return n;
        }

        // The region a Valve datacenter's matches belong to, in the player's words (hosts without a relay included).
        static string RegionOfPop(Game g, string id)
        {
            string mm;
            if (g.HostPops != null && DeadlockData.Hosting.TryGetValue(id, out mm) && Stats.MmRegions.ContainsKey(mm))
                return Lang.T(Stats.MmRegions[mm][0], Stats.MmRegions[mm][1]);
            var r = g.Regions.FirstOrDefault(x => x.Id == id) ?? g.NoRelayHosts.FirstOrDefault(x => x.Id == id);
            return r != null ? RegionNameOf(g, r) : id;
        }

        // What a strict lock plan costs, in the card's terms (null = nothing worth telling): the entry relay it closes
        // was the best way in (R3), the best way in is a datacenter of a region not picked (I3, 10 ms or more), or an
        // entry that reaches a left-out region has to stay open (G5). target: the picked regions' names.
        internal static RouteNote NoteOf(Game g, LockPlan plan, string target)
        {
            if (plan == null) return null;
            var note = new RouteNote
            {
                Target = target,
                BeforeMs = plan.Unlocked.Known ? (int)Math.Round(plan.Unlocked.Ms) : -1,
                AfterMs = plan.Locked.Known ? (int)Math.Round(plan.Locked.Ms) : -1,
            };
            Func<string, Region> pop = id => g.Regions.FirstOrDefault(r => r.Id == id);
            Route why;
            if (plan.MayLeak)
            {
                note.Kind = RouteNoteKind.MayLeak;
                note.Entry = PlaceName(pop(plan.Kept));
                note.Reaches = plan.Reason.TryGetValue(plan.Kept, out why) ? RegionOfPop(g, why.To) : null;
                return note;
            }
            string entry = plan.Unlocked.Entry;
            if (entry == null) return null;
            if (plan.Close.Contains(entry))
            {
                note.Kind = RouteNoteKind.ClosedEntry;
                note.Entry = PlaceName(pop(entry));
                note.Reaches = RegionOfPop(g, plan.Reason[entry].To);
                return note;
            }
            if (plan.Blocked.Contains(entry) && (!plan.Locked.Known || plan.Locked.Ms - plan.Unlocked.Ms >= 10))
            {
                note.Kind = RouteNoteKind.BlockedHost;
                note.Entry = RegionOfPop(g, entry);
                return note;
            }
            return null;
        }

        // The regions to choose from. Games without a region table (developer mode only) get one per datacenter.
        List<RegionChoice> BuildChoices(Game g)
        {
            if (g == null || !g.CanLock || !loadedGames.Contains(g)) return new List<RegionChoice>();
            if (g.RegionMap != null && g.Provider == Provider.Valve && g.Routes != null)
            {
                // a tile's ping is the game's: the best route through what is open now (the lock's open relays, or all
                // of them), "via Dubai" when it enters at a relay (I5)
                var pings = StrictLock.Pings(g);
                var byId = new Dictionary<string, Region>();
                foreach (var r in g.Regions) byId[r.Id] = r;
                Func<string, bool> open = id => { Region x; return !byId.TryGetValue(id, out x) || !LockedOut(g, x); };
                var routes = new Dictionary<Region, Route>();
                Func<Region, Route> route = r =>
                {
                    Route rt;
                    if (!routes.TryGetValue(r, out rt)) routes[r] = rt = StrictLock.Best(g.Routes, new[] { r.Id }, pings, open);
                    return rt;
                };
                Func<Region, double> latency = r =>
                {
                    var relay = BestRelay(r); // the player's own relay, as on the rest of the screen
                    return relay != null ? Math.Min(route(r).Ms, relay.Value.Value) : route(r).Ms;
                };
                Func<Region, string> via = r =>
                {
                    var rt = route(r);
                    Region e;
                    return rt.Known && rt.Entry != r.Id && latency(r) >= rt.Ms && byId.TryGetValue(rt.Entry, out e) && !g.Hosts(e) ? PlaceName(e) : null;
                };
                return RegionGroups.Build(g, g.AppId == "1422450" ? activity : null, PlayersOf, latency, via);
            }
            if (g.RegionMap != null)
                return RegionGroups.Build(g, g.AppId == "1422450" ? activity : null, PlayersOf, BestLatency);
            return g.Regions.Where(r => g.Hosts(r) && !r.Detected).Select(r => new RegionChoice
            {
                Def = RegionDef.Of(r.Id, Short(r.NameEn), Short(r.NameAr ?? r.NameEn), r.Id),
                Members = new List<Region> { r },
                Ping = BestLatency(r)
            }).ToList();
        }

        // New pings, player numbers or a new game: build the tiles again. The player's own selection stays as long
        // as those regions are still there; otherwise the best one starts selected.
        void RebuildChoices()
        {
            var g = CurrentGame;
            if (g == null) return;
            var all = BuildChoices(g);
            string sig = string.Join("|", all.Select(c => c.Def.Key + ":" + (c.Ping < double.MaxValue ? Math.Round(c.Ping).ToString() : "-") +
                                                          ":" + c.Via + ":" + c.Matches + ":" + c.Players).ToArray());
            if (g == choicesGame && sig == choicesSig) return;
            var keep = g == choicesGame && userPicked ? allChoices.Where(c => c.Selected).Select(c => c.Def.Key).ToList() : null;
            var feat = RegionGroups.Featured(all);
            if (keep != null && (keep.Count == 0 || all.Any(c => keep.Contains(c.Def.Key))))
                foreach (var c in all) c.Selected = keep.Contains(c.Def.Key);
            allChoices = all;
            featured = feat;
            everyChoice = RegionGroups.FeaturedFirst(all, feat);
            choicesGame = g;
            choicesSig = sig;
        }

        // Games that can't be locked: the lowest ping among the game's own servers (not the ones found in a match).
        static Region CardBest(Game g)
        {
            return g.Regions.Where(r => !r.Detected && BestLatency(r) < double.MaxValue).OrderBy(BestLatency).FirstOrDefault();
        }

        // Puts what the app knows right now on the card.
        void UpdateCard()
        {
            var g = CurrentGame;
            if (stateCard == null || g == null) return;
            if (g != choicesGame) RebuildChoices();
            var input = new CardInput
            {
                Game = g.Name,
                Loaded = loadedGames.Contains(g),
                LoadFailed = loadFailed == g,
                // this game's test runs, or it is next in line after another game's
                Testing = testGame == g || (testGame != null && !tested.Contains(g) && (Prefs.AutoTest || testNext == g)),
                Tested = tested.Contains(g),
                CanLock = g.CanLock,
                Locking = lockingGame == g,
                // every region, shown or folded away: the button names and locks all the selected ones
                Choices = everyChoice,
                LockedNames = lockSummary != null && lockGame == g ? lockNames : null,
                MatchRegion = matchRegion,
                MatchOnPicked = matchOnPicked,
                MatchViaRelay = matchViaRelay,
                CurrentServer = currentServer,
                CurrentMs = currentMs,
                NoMatchYet = noMatchLong && lockSummary != null && lockGame == g,
                RussiaNote = g.AppId == "1422450"
            };
            int russia;
            if (input.RussiaNote && activity != null && activity.TryGetValue(DeadlockData.Russia, out russia)) input.RussiaPlayers = PlayersOf(russia);
            // Overwatch: the picked regions are all outside the game regions the player's matches were on
            if (g.CanLock && HasZones(g)) input.ZoneWarning = Zones.Warning(RegionGroups.Chosen(everyChoice), SeenZones(g));
            // Valve: what the strict lock on the chosen regions costs, before it is applied; the lock that is on keeps its own
            if (g.CanLock && g.Provider == Provider.Valve && g.Routes != null)
            {
                if (input.LockedNames != null) input.Route = lockRoute;
                else
                {
                    var chosen = RegionGroups.Chosen(everyChoice);
                    if (chosen.Count > 0)
                        input.Route = NoteOf(g, StrictLock.For(g, RegionGroups.PickIds(g, chosen), StrictLock.Pings(g)),
                                             CardLogic.JoinNames(chosen.Select(c => c.Name).ToList()));
                }
            }
            if (!g.CanLock)
            {
                var best = CardBest(g);
                if (best != null)
                {
                    input.BestServer = ShortName(best);
                    input.BestMs = (int)Math.Round(BestLatency(best));
                }
            }
            // "Pick by hand" and "Test ping" only in the simple layout: the advanced one always shows the list and the test button
            stateCard.SetLinks(everyChoice.Count > featured.Count, expanded, simple, simple);
            stateCard.Show(CardLogic.Decide(input), expanded ? everyChoice : featured);
            ApplyCardLayout();
        }

        // Simple layout: the card fills the middle, and "Pick by hand" puts the server list under it.
        // Advanced: the card on top (as tall as it needs), the info bar, the list and the live graph under it. At the
        // advanced window's default height the graph keeps its full height and the list at least ListMin; only a
        // shorter window folds the graph away (its buttons stay). A card taller than its room scrolls.
        void ApplyCardLayout()
        {
            if (cardHost == null || listCard == null) return;
            bool showList = !simple || manualOpen;
            listCard.Visible = listGap.Visible = showList;
            int want = stateCard.PreferredHeight(cardHost.Parent != null ? cardHost.Parent.ClientSize.Width : cardHost.Width);
            if (!showList)
            {
                cardHost.Dock = DockStyle.Fill;
                FitCard(want);
                return;
            }
            cardHost.Dock = DockStyle.Top;
            var page = cardHost.Parent;
            int height = want;
            if (page != null)
            {
                const int ListMin = 160, CardMin = 150, GraphFull = 200, GraphMin = 180, ControlsOnly = 56;
                int others = 0;
                foreach (Control c in page.Controls)
                    if (c != cardHost && c != bottomPanel && c.Visible && (c.Dock == DockStyle.Top || c.Dock == DockStyle.Bottom)) others += c.Height;
                int avail = page.ClientSize.Height - others; // card + list + graph
                if (bottomPanel.Visible)
                {
                    int bottom = avail - ListMin - CardMin >= GraphFull ? GraphFull
                               : avail - ListMin - CardMin >= GraphMin ? avail - ListMin - CardMin : ControlsOnly;
                    if (bottomPanel.Height != bottom) bottomPanel.Height = bottom;
                    avail -= bottom;
                }
                height = Math.Max(CardMin, Math.Min(want, avail - ListMin));
            }
            if (cardHost.Height != height) cardHost.Height = height;
            FitCard(want);
        }

        // The card fills its host; when the host is shorter than the card needs, the host scrolls instead of cutting tiles off.
        void FitCard(int want)
        {
            bool scroll = cardHost.ClientSize.Height > 0 && want > cardHost.Height;
            if (scroll)
            {
                cardHost.AutoScroll = true;
                stateCard.Dock = DockStyle.Top;
                if (stateCard.Height != want) stateCard.Height = want;
            }
            else if (stateCard.Dock != DockStyle.Fill)
            {
                cardHost.AutoScroll = false;
                stateCard.Dock = DockStyle.Fill;
            }
        }

        // The card's one button: what it does depends on what the card says.
        void CardButton()
        {
            var g = CurrentGame;
            var v = stateCard.View;
            if (g == null || v == null) return;
            switch (v.Kind)
            {
                case CardKind.Choose:
                    var chosen = RegionGroups.Chosen(everyChoice);
                    if (chosen.Count == 0 || !btnLock.Enabled) return; // nothing chosen, or a lock is being applied
                    Telemetry.Send("use", string.Join(", ", chosen.Select(c => c.Def.En).ToArray()), -1, "play on regions");
                    LockRegions(chosen);
                    break;
                case CardKind.Locked:
                case CardKind.InMatchOk:
                case CardKind.InMatchRelay:
                case CardKind.Outside:
                    UnlockServers();
                    break;
                case CardKind.Testing:
                    Telemetry.Send("use", null, -1, "test button");
                    RunTest();
                    break;
                case CardKind.Offline:
                    // the same as picking the game again, and test it once it's there
                    tested.Remove(g);
                    loadedGames.Remove(g); // "Loading" until it's back, so no test runs on the old list
                    retestGame = g;
                    OnGameChanged();
                    break;
            }
        }

        // How the card names a lock: the regions when whole regions are picked, otherwise the picked servers.
        string CardLockNames(Game g, List<Region> picked)
        {
            var ids = new HashSet<string>(picked.Where(r => g.Hosts(r)).Select(r => r.Id));
            var whole = BuildChoices(g).Where(c => c.Members.All(m => ids.Contains(m.Id))).ToList();
            var covered = new HashSet<string>(whole.SelectMany(c => c.Members).Select(m => m.Id));
            if (whole.Count > 0 && ids.All(covered.Contains)) return CardLogic.JoinNames(whole.Select(c => c.Name).ToList());
            return string.Join(Lang.Ar ? "، " : ", ", picked.Select(ShortName).ToArray());
        }

        // Lock the datacenters of the chosen regions (only ones that host matches).
        void LockRegions(List<RegionChoice> chosen)
        {
            var g = CurrentGame;
            if (g == null || !g.CanLock) return;
            var ids = RegionGroups.PickIds(g, chosen);
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
                                            (r.NoRelay ? T("   ·  can't be locked (Valve lists no relay here)", "   ·  ما ينثبّت (Valve ما تعطي وسيط له)") : "") +
                                            (devMode ? "   ·  ‎" + r.Cidrs.Count + " ranges‎" : "");
            item.SubItems[ColRegion].ForeColor = r.Here || r.Picked ? Accent : (r.Detected ? Warn : TextC);

            string mm;
            int matches;
            if (g != null && g.AppId == "1422450" && activity != null && DeadlockData.Hosting.TryGetValue(r.Id, out mm) &&
                activity.TryGetValue(mm, out matches))
            {
                SetSub(item, ColPlayers, Stats.Approx(PlayersOf(matches)), TextC);
                SetSub(item, ColWait, Stats.EstimateWait(matches), matches >= Stats.FastQueue ? Accent : matches >= Stats.MediumQueue ? Warn : Bad);
            }
            else
            {
                SetSub(item, ColPlayers, "-", Dim);
                SetSub(item, ColWait, "-", Dim);
            }

            // hosts matches but has no relay: nothing to ping, and the lock never blocks it (it never says "blocked")
            if (r.NoRelay)
            {
                item.SubItems[ColRegion].ForeColor = Dim;
                foreach (int i in new[] { ColPing, ColJitter, ColLoss, ColVia, ColRoute }) SetSub(item, i, "-", Dim);
                return;
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
            if (g == null) return;
            if (busy)
            {
                // another game's test is running: this one goes next
                if (testGame != null && testGame != g) { testNext = g; UpdateCard(); }
                return;
            }
            if (g.Regions.Count == 0) { SetStatus(T("No servers to test.", "ما في سيرفرات أفحصها.")); return; }
            busy = true;
            testGame = g;
            btnTest.Enabled = false;
            // regions blocked by the lock can't answer anyway, so skip them
            var regions = g.Regions.Where(r => !LockedOut(g, r)).ToList();
            var rels = relays.ToList();
            SetStatus(rels.Count > 0 ? T("Testing relays...", "أفحص سيرفرات الوسيط...") : T("Testing...", "أفحص..."));
            UpdateCard();
            var watch = Stopwatch.StartNew();

            Task.Run(() =>
            {
                try
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
                        testGame = null;
                        btnTest.Enabled = true;
                        tested.Add(g);
                        Storage.SaveLast(g);
                        if (pendingRebuild) { pendingRebuild = false; RebuildUi(); return; }
                        // the user switched games while this test ran: test the game they are looking at now
                        var cur = CurrentGame;
                        bool wanted = testNext == cur;
                        testNext = null;
                        if (cur != g && cur != null && (Prefs.AutoTest || wanted) && cur.Regions.Count > 0 && !tested.Contains(cur))
                        {
                            RunTest();
                            return;
                        }
                        // the card follows the game on screen: this game's new pings, or just "no test running" for another one
                        if (cur == g) RebuildChoices();
                        UpdateCard();
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
                }
                catch (Exception ex)
                {
                    ReportCrash("ping test", ex);
                    Ui(() =>
                    {
                        testGame = null;
                        btnTest.Enabled = true;
                        SetStatus(T("The ping test stopped with an error. Press Test ping to try again.",
                                    "فحص البنق وقف بخطأ. اضغط «افحص البنق» وجرّب مرة ثانية."));
                        UpdateCard();
                        SetIdle();
                    });
                }
            });
        }

        // An unexpected error on a background task: reported like a crash, without ending the app.
        static void ReportCrash(string where, Exception ex)
        {
            Issues.Report("crash", where + ": " + ex.GetType().Name + ": " + ex.Message,
                new Dictionary<string, object> { { "stack", Privacy.ShortStack(ex) }, { "fatal", false } }, where + ex.GetType().Name);
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
            if (Minimized) RestoreWindow();
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
            return g.DatacenterOf(ip);
        }

        // The addresses that can be the game's server: voice chat and service hosts (Game.DetectIgnore) are left out.
        internal static List<Detector.Hit> ServerHits(Game g, List<Detector.Hit> hits)
        {
            return hits.Where(h => !g.Ignored(h.Ip)).ToList();
        }

        // Overwatch: the game regions (Zones) the player's matches were on, learned from the server checks; never shrinks.
        static string ZonesKey(Game g) { return "zones_seen_" + g.Icon; }

        static bool HasZones(Game g) { return g.RegionMap != null && g.RegionMap.Any(d => d.Zone != null); }

        // SeenZones is asked at every card update: read settings.txt once per game, again after LearnZones or an hour
        // later (zones age out after Zones.FreshDays days)
        readonly Dictionary<string, KeyValuePair<DateTime, List<string>>> seenZonesCache = new Dictionary<string, KeyValuePair<DateTime, List<string>>>();

        // The zones seen in the last Zones.FreshDays days. An old plain list ("am,eu") is saved once with today's time.
        List<string> SeenZones(Game g)
        {
            var now = DateTime.UtcNow;
            KeyValuePair<DateTime, List<string>> cached;
            if (seenZonesCache.TryGetValue(g.Icon, out cached) && now - cached.Key < TimeSpan.FromHours(1)) return cached.Value;
            string saved = Storage.GetSetting(ZonesKey(g));
            if (!string.IsNullOrEmpty(saved))
            {
                string migrated = Zones.Format(Zones.Parse(saved, now));
                if (migrated != saved) Storage.SetSetting(ZonesKey(g), migrated);
                saved = migrated;
            }
            var recent = Zones.Recent(saved, now);
            seenZonesCache[g.Icon] = new KeyValuePair<DateTime, List<string>>(now, recent);
            return recent;
        }

        void LearnZones(Game g, IEnumerable<Region> datacenters)
        {
            if (!HasZones(g)) return; // Valve and the rest: nothing to learn
            string next = Zones.Learn(g, Storage.GetSetting(ZonesKey(g)), datacenters.Select(r => r.Id), DateTime.UtcNow);
            if (string.IsNullOrEmpty(next)) return;
            Storage.SetSetting(ZonesKey(g), next);
            seenZonesCache.Remove(g.Icon);
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
            if (LockTestRunning()) return;
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
                    // voice chat and service hosts are never the server the player is on
                    hits = ServerHits(g, hits);
                    // the game servers it talked to, so the server lists can grow: only after that filter, and only for
                    // games with dedicated servers (a custom game's capture can hold other players' home addresses)
                    if (g.Provider != Provider.Custom && g.Processes.Length > 0 && hits.Count > 0)
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
                    Ui(() => { btnDetect.Enabled = true; Msg(T("Server check failed: ", "ما زبط الفحص: ") + ex.Message, ToastKind.Bad); SetIdle(); });
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
            SetIdle();
            btnDetect.Enabled = true;
            foreach (var r in g.Regions) r.Here = false;
            var found = hits.Select(h => ValveRegionOf(g, h.Ip)).Where(r => r != null && g.Hosts(r)).Distinct().ToList();
            foreach (var h in hits) Log(T("Game traffic: ", "ترافيك اللعبة: ") + h.Ip + ":" + h.Port + "  (" + h.Packets + ")");
            var relay = RelayRegionOf(g, hits);
            if (found.Count == 0 && relay != null)
            {
                // the game goes through a nearby Valve relay, which hides which datacenter the match is on
                if (CurrentGame == g) { SetMatch(null, false, true); FillServers(g); }
                SetStatus(T("Through Valve's relay in ", "داخل شبكة Valve من وسيط ") + ShortName(relay));
                Telemetry.Send("detect", relay.NameEn, -1, "through relay");
                AdvancedMsg(T("Your game goes through Valve's relay in " + ShortName(relay) + ", the shortest way into their network, so the match server itself is hidden. " +
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
            LearnZones(g, found);
            if (CurrentGame == g)
            {
                dcSinceLock = true;
                noMatchLong = false;
                SetMatch(RegionNameOf(g, found[0]), found.All(r => r.Picked), false);
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
            AdvancedMsg(T("You're playing on: ", "إنت تلعب دحين على: ") + names, onPicked ? ToastKind.Good : ToastKind.Warn);
            if (lockSummary != null)
                AdvancedMsg(onPicked
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
                SetIdle();
                btnDetect.Enabled = true;
                if (found.Count == 0) { NothingFound(); return; }
                foreach (var r in found)
                {
                    g.Regions.RemoveAll(x => x.Id == r.Id);
                    g.Regions.Insert(0, r);
                }
                var top = found[0];
                if (CurrentGame == g)
                {
                    if (!g.CanLock) { currentServer = ShortName(top); currentMs = top.Direct.Ok ? (int)Math.Round(top.Direct.Avg) : -1; }
                    FillServers(g);
                }
                SetStatus(T("You are on: ", "إنت دحين على: ") + top.Name);
                Telemetry.Server = top.NameEn;
                Telemetry.Send("detect", top.NameEn, top.Direct.Ok ? (int)Math.Round(top.Direct.Avg) : -1, null);
                AdvancedMsg(T("You're playing on: ", "إنت تلعب دحين على: ") + top.Name, ToastKind.Good);
                AdvancedMsg((top.Direct.Ok ? T("Ping: ", "البنق: ") + Ms(top.Direct.Avg) + T(". ", "، ")
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
        // since the lock: a match datacenter was seen; and (Overwatch) none in more than Zones.NoMatchAfter of checks
        // that actually watched the running game (observedSinceLock, see Zones.AddObserved)
        bool dcSinceLock, noMatchLong;
        TimeSpan observedSinceLock;
        DateTime lastObservedCheck = DateTime.MinValue;
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
            matchSinceLock = dcSinceLock = noMatchLong = false;
            observedSinceLock = TimeSpan.Zero;
            lastObservedCheck = DateTime.MinValue;
            DropHeldWarnings(); // they were about the lock before this change
            lastAutoServer = null;
            lastAutoDetect = DateTime.MinValue;
            ClearMatch(); // a match from before the change says nothing about the new lock
            if (btnDetect != null)
            {
                if (lockSummary != null) SetDetect(T("Checking at your next match...", "بنتأكد في مباراتك الجاية..."), Lavender);
                else ResetDetectButton();
            }
            UpdateCard();
        }

        void AutoDetectTick()
        {
            var g = CurrentGame;
            CheckLockRules();
            if (g == null || busy || autoDetecting || lockTesting || !appActive || g.Processes.Length == 0) return;
            if (DateTime.Now - lastAutoDetect < TimeSpan.FromSeconds(Verifying ? 30 : 90)) return;
            autoDetecting = true;
            lastAutoDetect = DateTime.Now;
            Task.Run(() =>
            {
                List<Detector.Hit> hits = null;
                string where = null;
                int whereMs = -1;
                bool running = false;
                try { running = Detector.IsRunning(g.Processes); }
                catch (Exception ex) { ReportCrash("automatic check", ex); }
                if (running)
                    try
                    {
                        bool matched;
                        hits = ServerHits(g, Detector.Capture(g.Processes, 8, out matched)); // never voice chat or services
                        // games without a datacenter list: the main server's city
                        if (hits.Count > 0 && !hits.Any(h => ValveRegionOf(g, h.Ip) != null)) where = Stats.Locate(hits[0].Ip);
                        // games that can't be locked: the card shows that server's ping too
                        if (where != null && !g.CanLock)
                        {
                            var p = Pinger.Measure(Storage.MakeDetected(hits[0].Ip, where).Endpoints[0], 3, 1000);
                            if (p.Ok) whereMs = (int)Math.Round(p.Avg);
                        }
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
                    if (!running)
                    {
                        lastObservedCheck = DateTime.MinValue; // time with the game closed is never "no match yet"
                        if (lastAutoServer != null) ResetDetectButton();
                        if (matchRegion != null || matchViaRelay || currentServer != null) { ClearMatch(); UpdateCard(); }
                        return;
                    }
                    if (hits != null) ShowAutoDetect(g, hits, where, whereMs);
                });
            });
        }

        // The lock's firewall rules can vanish (antivirus, another tool, a firewall reset): check every 2 minutes.
        void CheckLockRules()
        {
            if (lockSummary == null || DateTime.Now - lastRulesCheck < TimeSpan.FromMinutes(2)) return;
            lastRulesCheck = DateTime.Now;
            string locked = lockSummary;
            FirewallWorker.Run(() =>
            {
                if (ServerLock.Present() != false) return; // there, or netsh couldn't tell: never a false alarm
                Issues.Report("lock_rules_missing", "lock on " + locked + " but no firewall rules");
                Ui(() =>
                {
                    if (lockSummary != locked) return;
                    Msg(T("Your server lock was removed from Windows Firewall (maybe by a security app). Press Lock my servers again.",
                          "تثبيت السيرفرات انشال من جدار حماية ويندوز (يمكن برنامج حماية). اضغط «ثبّت سيرفراتي» مرة ثانية."), ToastKind.Warn);
                });
            });
        }

        void ShowAutoDetect(Game g, List<Detector.Hit> hits, string where, int whereMs)
        {
            var seenDc = hits.Count > 0 ? HostRegionOf(g, hits) : null;
            if (seenDc != null)
            {
                dcSinceLock = true;
                LearnZones(g, new[] { seenDc });
            }
            // Overwatch: the checks watched the running game for a few minutes since the lock and saw no match
            // datacenter. The picked regions may be outside the game's region, where it never finds a match (see Zones)
            var checkedAt = DateTime.Now;
            observedSinceLock = Zones.AddObserved(observedSinceLock, lastObservedCheck, checkedAt);
            lastObservedCheck = checkedAt;
            bool noMatch = Zones.NoMatchYet(lockSummary != null && lockGame == g, HasZones(g), dcSinceLock, observedSinceLock);
            if (noMatch != noMatchLong)
            {
                noMatchLong = noMatch;
                UpdateCard();
            }
            if (hits.Count == 0)
            {
                // not in a match (any more)
                if (matchRegion != null || matchViaRelay || currentServer != null) { ClearMatch(); UpdateCard(); }
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
            var dc = seenDc;
            var relay = dc == null ? RelayRegionOf(g, hits) : null;
            if (relay != null)
            {
                // only a relay in sight: the match server is hidden behind it, so there's nothing to check or report
                SetDetect(T("Via relay: ", "عن طريق وسيط: ") + ShortName(relay), Lavender);
                lastAutoServer = relay.Id;
                SetMatch(null, false, true);
                UpdateCard();
                return;
            }
            string name = dc != null ? ShortName(dc) : (where ?? top.Ip);
            bool ok = dc == null ? lockSummary == null : (lockSummary == null || dc.Picked);
            SetDetect(T("On: ", "إنت على: ") + name, ok ? Good : Warn);
            // the card: a datacenter we know names the region; a game without them shows the city and its ping
            if (dc != null) SetMatch(RegionNameOf(g, dc), dc.Picked, false);
            else if (!g.CanLock && where != null) { currentServer = where; currentMs = whereMs; }
            UpdateCard();
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
                AdvancedMsg(T("You're on " + name + ", which isn't one of your picked servers. Restart the game and queue again.",
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
            btnUnlock.Enabled = lockSummary != null && lockingGame == null; // never while a new lock is being applied
        }

        void LockServers()
        {
            var g = CurrentGame;
            if (g == null || !g.CanLock) return;
            if (LockTestRunning()) return;
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
            string cardNames = CardLockNames(g, picked);
            // Valve: the strict lock (R3) also closes the entry relays that reach the other regions about as fast
            LockPlan plan = null;
            RouteNote route = null;
            if (g.Provider == Provider.Valve && g.Routes != null)
            {
                plan = StrictLock.For(g, picked.Select(r => r.Id).ToList(), StrictLock.Pings(g));
                route = NoteOf(g, plan, cardNames);
                WarnLongerRoute(g, plan, route);
            }
            var closed = new HashSet<string>(plan != null ? plan.Close : new List<string>());
            var closedCidrs = g.Regions.Where(r => closed.Contains(r.Id)).SelectMany(r => r.Cidrs).ToList();
            btnLock.Enabled = false;
            lockingGame = g; // the status bar is hidden in the simple layout: the card says it too
            UpdateLockBar();
            SetStatus(T("Locking your servers...", "أثبّت سيرفراتك..."));
            UpdateCard();
            Action<Exception> failed = ex => Ui(() =>
            {
                lockingGame = null;
                btnLock.Enabled = true;
                UpdateLockBar();
                UpdateCard();
                Log(T("Lock failed: ", "ما زبط التثبيت: ") + ex.Message);
                Issues.Report("lock_apply_failed", g.Name + ": " + ex.Message, null, g.Icon);
                Msg(T("Lock failed: ", "ما زبط التثبيت: ") + ex.Message, ToastKind.Bad);
            });
            Task.Run(() =>
            {
                try
                {
                    List<string> block;
                    var allow = IpLists.Resolve(g.LockAllow ?? new string[0]);
                    if (g.LockSpace != null)
                        // everything the game's servers can live on, except the picked datacenters (new servers included)
                        block = IpLists.Subtract(IpLists.Resolve(g.LockSpace), picked.SelectMany(r => r.Cidrs).Concat(allow));
                    else
                    {
                        var keep = new HashSet<string>(picked.SelectMany(r => r.Cidrs));
                        // the other regions' datacenters, and the entry relays the strict lock closes; the other relays
                        // stay open: the game enters Valve's network through them
                        block = g.Regions.Where(r => !r.Picked && !r.Detected && g.Hosts(r)).SelectMany(r => r.Cidrs).Concat(closedCidrs)
                                         .Where(c => !keep.Contains(c)).ToList();
                    }
                    var info = new LockTest.LockInfo
                    {
                        Active = true, Picked = picked.Select(r => r.Id).ToList(), Block = block,
                        Allow = allow,
                        Mode = g.LockSpace != null ? "game-udp" : "valve-all",
                        Program = exe, Protocol = exe == null ? null : "UDP",
                    };
                    info.Ports = info.Protocol != null ? g.LockPorts : null;
                    FirewallWorker.Run(() =>
                    {
                        if (FirewallWorker.Closing) return; // the app is closing: no new rules
                        int n;
                        try { n = ServerLock.Apply(block, exe, exe != null ? "UDP" : null, g.LockPorts); }
                        catch (Exception ex) { failed(ex); return; }
                        int skipped = ServerLock.Skipped; // entries of a downloaded list that weren't valid addresses
                        info.Scope = LockScope(info, n);
                        bool firewallOn = ServerLock.FirewallOn();
                        if (!firewallOn) Issues.Report("firewall_off", "while locking " + g.Name);
                        Ui(() =>
                        {
                            lockingGame = null;
                            lockSummary = names;
                            lockGame = g;
                            lockNames = cardNames;
                            lockInfo = info;
                            lockClosed = closed;
                            lockRoute = route;
                            LockChanged(g);
                            Telemetry.Server = string.Join(", ", picked.Select(r => r.NameEn));
                            Telemetry.Send("lock", Telemetry.Server, -1, n + " ranges blocked, mode " + info.Mode +
                                           (skipped > 0 ? ", " + skipped + " bad entries skipped" : "") +
                                           (closed.Count > 0 ? ", relays closed " + string.Join(",", closed.ToArray()) : "") +
                                           (plan != null && plan.MayLeak ? ", kept open " + plan.Kept : ""));
                            if (devMode) DevOut("lock mode " + info.Mode + ": " + info.Scope);
                            btnLock.Enabled = true;
                            UpdateLockBar();
                            FillServers(g);
                            Log(T("Lock ON (", "التثبيت شغّال (") + g.Name + "): " + names + "  (" + n + ")");
                            SetStatus(T("Locked: ", "مثبّت على: ") + names);
                            StartLockMonitor();
                            AdvancedMsg(T("Locked. You'll only be matched on: ", "تم التثبيت. ما بتلعب إلا على: ") + names, ToastKind.Good);
                            if (g.LockSpace != null)
                                AdvancedMsg(T("Try a casual match first (not ranked): if " + g.Name + " still picks a blocked server, the match won't load, and in ranked that counts as leaving.",
                                      "جرّبه في مباراة عادية أول (مو رانكد): لو اللعبة اختارت سيرفر محجوب المباراة ما تفتح، وفي الرانكد تنحسب منسحب."), ToastKind.Warn);
                            AdvancedMsg(T("If the game is open, restart it (or wait a minute) before you queue.",
                                  "لو اللعبة مفتوحة، اقفلها وافتحها من جديد (أو استنى دقيقة) قبل ما تدوّر مباراة."));
                            if (Storage.GetSetting("tip_tray") != "1")
                            {
                                Storage.SetSetting("tip_tray", "1");
                                Msg(T("You can minimize 2X Ping: the lock stays on until you unlock or close it.",
                                      "تقدر تصغّر البرنامج، والتثبيت يبقى شغّال لين تفكّه أو تقفل البرنامج."));
                            }
                            // the lock can't work with the firewall off: this one the player has to know
                            if (!firewallOn)
                                Msg(T("Windows Firewall is off, so the lock can't block anything. Turn it on in Windows Security > Firewall.",
                                      "جدار حماية ويندوز مطفي، فالتثبيت ما يقدر يحجب شي. شغّله من أمان ويندوز ← جدار الحماية."), ToastKind.Bad);
                        });
                    });
                }
                catch (Exception ex) { failed(ex); }
            });
        }

        // Valve games enter Valve's network at any relay they reach and ride it to the match server (shortest paths over
        // Valve's own pings, I3). When the lock makes that way longer (it closes the entry relay, or the best way in is
        // a datacenter of a region not picked) or can't be strict (G5), say so with the numbers before it is applied.
        void WarnLongerRoute(Game g, LockPlan plan, RouteNote note)
        {
            if (note == null) return;
            string id = note.Kind == RouteNoteKind.MayLeak ? plan.Kept : plan.Unlocked.Entry;
            Telemetry.Send("use", id, note.AfterMs >= 0 && note.BeforeMs >= 0 ? note.AfterMs - note.BeforeMs : -1,
                           "longer route warning, " + note.Kind + ", " + string.Join(",", g.Regions.Where(r => r.Picked).Select(r => r.Id).ToArray()));
            Msg(CardLogic.RouteText(note), ToastKind.Warn);
        }

        void UnlockServers()
        {
            if (lockSummary == null || lockingGame != null) return;
            if (LockTestRunning()) return;
            lockSummary = null;
            lockGame = null;
            lockNames = null;
            lockInfo = null;
            lockClosed = null;
            lockRoute = null;
            if (!boosting) StopMonitor();
            Telemetry.Server = null;
            Telemetry.Send("unlock", null, -1, null);
            LockChanged(CurrentGame);
            UpdateLockBar();
            var g = CurrentGame;
            if (g != null) FillServers(g);
            FirewallWorker.Run(() =>
            {
                if (ServerLock.Remove())
                {
                    Log(T("Lock removed, all servers are back.", "فكّينا التثبيت، وكل السيرفرات رجعت."));
                    SetStatus(T("Lock removed.", "فكّينا التثبيت."));
                    return;
                }
                Issues.Report("unlock_failed", "firewall rules still there after unlock");
                Log(T("Some firewall rules could not be removed.", "في قواعد بجدار الحماية ما قدرت أشيلها."));
                Ui(() => Msg(T("The lock is off, but some firewall rules are still there. Press Unlock again, or close 2X Ping and open it again, it cleans them on start.",
                               "فكّينا التثبيت، بس في قواعد بجدار الحماية لسا موجودة. اضغط «فك التثبيت» مرة ثانية، أو اقفل البرنامج وافتحه، وهو ينظفها أول ما يفتح."), ToastKind.Warn));
            });
        }

        // Undo everything 2X Ping changed on this PC, then verify nothing is left.
        public void ResetEverything()
        {
            if (LockTestRunning()) return;
            if (busy || lockingGame != null)
            {
                Msg(T("Wait for the current test to finish first.", "استنى الفحص الحالي يخلص أول."), ToastKind.Warn);
                return;
            }

            lockSummary = null;
            lockGame = null;
            lockNames = null;
            lockInfo = null;
            lockClosed = null;
            lockRoute = null;
            ClearMatch();
            StopAll();
            foreach (var g in games) foreach (var r in g.Regions) { r.Picked = false; r.Here = false; }
            UpdateCard();
            Storage.ClearPicks();
            Telemetry.Server = null;
            Telemetry.Send("reset", null, -1, null);
            busy = true;
            SetStatus(T("Putting everything back...", "أرجّع كل شي زي ما كان..."));
            FirewallWorker.Run(() =>
            {
                bool left = true;
                try
                {
                    ServerLock.Remove();
                    Tunnel.Stop();
                    Detector.Cleanup();
                    foreach (var g in games)
                        foreach (var p in g.Processes.SelectMany(n => Process.GetProcessesByName(n)))
                            using (p) try { p.PriorityClass = ProcessPriorityClass.Normal; } catch { }
                    left = ServerLock.Present() != false;
                }
                catch (Exception ex) { ReportCrash("reset", ex); } // busy is always cleared below
                Ui(() =>
                {
                    SetIdle();
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
            if (region == null || region.NoRelay || LockedOut(g, region))
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
            // set before the tunnel exists: a Stop (or closing the app) from now on always removes it, even half-made
            tunnelActive = true;
            int gen = Interlocked.Increment(ref boostGen);
            Action<Exception> boostFailed = ex =>
            {
                Log(T("Boost failed: ", "ما زبط البوست: ") + ex.Message);
                Telemetry.Send("error", region.NameEn, -1, "boost via " + relay.Name + ": " + ex.GetType().Name + ": " + ex.Message);
                Ui(() =>
                {
                    if (gen != Volatile.Read(ref boostGen)) return;
                    StopAll(); // removes the half-made tunnel too
                    Msg(T("Boost failed: ", "ما زبط البوست: ") + ex.Message, ToastKind.Bad);
                });
            };
            // the address lists and DNS take network time: done first, so the firewall queue only holds the tunnel step
            Task.Run(() =>
            {
                List<string> cidrs;
                try
                {
                    cidrs = new List<string>();
                    if (g.Provider == Provider.Aws) cidrs.AddRange(GameDb.AwsCidrs(region.Id));
                    // the relay tunnel carries IPv4 only (setup-relay.sh): IPv6 relay addresses stay on the normal route
                    else cidrs.AddRange(region.Cidrs.Where(c => !RemoteAddress.IsV6(c)));
                    foreach (var ep in region.Endpoints)
                    {
                        var ip = Pinger.TryResolve(ep.Host);
                        if (ip != null) cidrs.Add(ip + "/32");
                    }
                    cidrs = cidrs.Distinct().ToList();
                }
                catch (Exception ex) { boostFailed(ex); return; }
                FirewallWorker.Run(() =>
                {
                    // Stop was pressed, or the app is closing: the tunnel isn't started at all
                    if (FirewallWorker.Closing || gen != Volatile.Read(ref boostGen)) return;
                    try { Tunnel.Start(Tunnel.BuildConf(relay, cidrs)); }
                    catch (Exception ex) { boostFailed(ex); return; }
                    // Stop was pressed while it was starting: its Tunnel.Stop is queued right after this
                    if (gen != Volatile.Read(ref boostGen)) return;
                    Log(T("Route ready through ", "الطريق جاهز عن طريق ") + relay.Name + " - " + region.Name + " (" + cidrs.Count + ")");
                    Telemetry.Send("boost", region.NameEn, -1, "relay " + relay.Name);
                    Ui(() =>
                    {
                        if (gen != Volatile.Read(ref boostGen)) return;
                        RaisePriority(g);
                        StartMonitor(target, T("BOOST · ", "بوست · ") + ShortName(region) + T(" via ", " عن طريق ") + relay.Name);
                    });
                });
            });
        }

        // Live ping in the status bar (and the graph in the advanced layout), every second.
        void StartMonitor(Endpoint target, string label)
        {
            int gen = Interlocked.Increment(ref monitorGen);
            graph.Clear();
            graph.Label = label;
            SetStatus(label);
            var t = new Thread(() =>
            {
                var last = DateTime.MinValue;
                while (gen == Volatile.Read(ref monitorGen))
                {
                    // while the game is in front: one ping every 15 s, just for the taskbar title
                    if (!appActive && DateTime.Now - last < TimeSpan.FromSeconds(15)) { Thread.Sleep(500); continue; }
                    last = DateTime.Now;
                    double v = Pinger.Once(target, 1500);
                    if (gen != Volatile.Read(ref monitorGen)) break;
                    Ui(() =>
                    {
                        if (gen != Volatile.Read(ref monitorGen)) return;
                        liveLabel.Text = T("Status: ", "الحالة: ") + label + "  ·  " + Ms(v);
                        liveLabel.ForeColor = PingColor(v);
                        SetLiveStatus(label + " · " + Ms(v));
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
            Interlocked.Increment(ref monitorGen);
            Telemetry.Ping = -1;
            liveLabel.Text = StatusOff;
            liveLabel.ForeColor = Dim;
            SetLiveStatus(null);
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
            Interlocked.Increment(ref boostGen); // a route still being built is dropped
            if (tunnelActive)
            {
                tunnelActive = false;
                FirewallWorker.Run(() =>
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
                try
                {
                    foreach (var r in rels)
                    {
                        r.Ping = r.MeasurePing();
                        string agent = !r.HasAgent ? T("no agent", "بدون أداة قياس")
                            : r.ProbeFromRelay(new Endpoint("1.1.1.1", 443)) >= 0 ? T("agent OK", "أداة القياس شغّالة") : T("agent not reachable", "أداة القياس ما ترد");
                        Log(T("Relay ", "الوسيط ") + r.Name + ": " + Ms(r.Ping) + "، " + agent);
                    }
                    Ui(() => { RefreshRelays(); SetStatus(T("Relay test done, details in Tools & Log.", "خلص فحص الوسطاء، التفاصيل في «أدوات وسجل».")); });
                }
                catch (Exception ex)
                {
                    ReportCrash("relay test", ex);
                    SetStatus(T("The relay test stopped with an error.", "فحص الوسطاء وقف بخطأ."));
                }
            });
        }

        // ---------------------------------------------------------------- tools

        void FlushDns()
        {
            Task.Run(() =>
            {
                string output;
                int code = Proc.Run("ipconfig", "/flushdns", 10000, out output);
                Log(code == 0 ? T("DNS cache cleared.", "مسحنا كاش DNS.") : T("Couldn't clear the DNS cache.", "ما قدرت أمسح كاش DNS."));
            });
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
            var flow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 410, FlowDirection = FlowDirection.LeftToRight, RightToLeft = RightToLeft.No };
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
            // the lock test takes a whole row
            Func<string, Action, RoundButton> wide = (text, act) =>
            {
                var b = MakeButton(text, Panel2);
                b.Size = new Size(308, 32);
                b.Margin = new Padding(0, 0, 8, 8);
                b.Click += delegate { try { act(); } catch (Exception ex) { DevOut("! " + ex.Message); } };
                flow.Controls.Add(b);
                return b;
            };
            devLockTestBtn = wide(LockTestIdleText, DevLockTest);
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

        // ---- lock test (developer mode only)

        bool lockTesting;
        RoundButton devLockTestBtn;
        const int LockTestSeconds = 60;

        // A lock test reads the lock as it was when it started: lock, unlock and reset wait for it.
        bool LockTestRunning()
        {
            if (!lockTesting) return false;
            Msg(T("A lock test is recording, wait for it to finish.", "اختبار التثبيت يسجّل، استنى لين يخلص."), ToastKind.Warn);
            return true;
        }

        string LockTestIdleText { get { return T("Record lock test (60 s)", "سجّل اختبار (60 ثانية)"); } }

        static string LockScope(LockTest.LockInfo info, int n)
        {
            return (info.Protocol ?? "any protocol") + ", in+out, " +
                   (info.Program != null ? "program " + info.Program : "whole PC (every program)") + ", " +
                   (info.Ports != null ? "remote ports " + info.Ports : "all ports") + ", " + n + " ranges";
        }

        // «سجّل اختبار»: a minute of every packet while the player starts the game and queues.
        void DevLockTest()
        {
            var g = CurrentGame;
            if (g == null) return;
            ShowPage(3);
            if (lockTesting) { DevOut("lock test already recording"); return; }
            if (busy || autoDetecting) { DevOut("another capture or test is running, try again in a few seconds"); return; }
            lockTesting = true;
            var info = lockInfo != null && lockGame == g ? lockInfo : new LockTest.LockInfo
            {
                Picked = g.Regions.Where(r => r.Picked).Select(r => r.Id).ToList(),
                Mode = g.CanLock ? "none (not locked)" : "none (game can't be locked)",
                Scope = "no lock rules", Allow = new List<string>(g.LockAllow ?? new string[0])
            };
            DevOut("---- lock test, " + g.Name + ", mode " + info.Mode + ": start the game now and queue (" + LockTestSeconds + " s)");
            DevOut(LockTest.Procedure.Replace("\r\n", " "));
            var at = DateTime.Now;
            Task.Run(() =>
            {
                try
                {
                    var owners = new Dictionary<string, string>();
                    var cap = LockTest.Record(LockTestSeconds, left => Ui(() =>
                    {
                        if (devLockTestBtn != null && !devLockTestBtn.IsDisposed)
                            devLockTestBtn.Text = left > 0 ? T("Recording... ", "أسجّل... ") + left + T(" s", " ثانية") : T("Reading the capture...", "أقرأ التسجيل...");
                    }), owners);
                    var lines = LockTest.Classify(g, cap, info, owners);
                    string text = LockTest.Report(g, cap, info, lines, LockTestSeconds, at);
                    string path = Path.Combine(Storage.Dir, "locktest-" + at.ToString("yyyyMMdd-HHmmss") + ".txt");
                    File.WriteAllText(path, text);
                    // the console gets the header and findings; the table of every address stays in the file
                    foreach (var l in LockTest.Short(g, cap, info, lines, LockTestSeconds, at).Split('\n')) DevOut(l.TrimEnd('\r'));
                    DevOut("full table: " + path);
                    Telemetry.Send("locktest", info.Picked.Count > 0 ? string.Join(", ", info.Picked) : null, -1, LockTest.Summary(g, cap, info, lines), LockTest.Data(cap, info, lines, LockTestSeconds));
                }
                catch (Exception ex) { DevOut("! lock test failed: " + ex.Message); }
                finally
                {
                    Ui(() =>
                    {
                        lockTesting = false;
                        if (devLockTestBtn != null && !devLockTestBtn.IsDisposed) devLockTestBtn.Text = LockTestIdleText;
                    });
                }
            });
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
                string text;
                Proc.Run("netsh", "advfirewall firewall show rule name=\"2XPing-RegionLock\" verbose", 15000, out text);
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
            foreach (var k in new[] { "beta_ack", "tip_tray" }) Storage.SetSetting(k, "");
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
