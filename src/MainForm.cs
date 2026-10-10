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
        public static readonly Color Bg = Color.FromArgb(18, 20, 26);
        public static readonly Color Panel1 = Color.FromArgb(26, 29, 37);
        public static readonly Color Panel2 = Color.FromArgb(38, 42, 54);
        public static readonly Color Accent = Color.FromArgb(0, 210, 145);
        public static readonly Color TextC = Color.FromArgb(232, 234, 240);
        public static readonly Color Dim = Color.FromArgb(140, 146, 160);
        public static readonly Color Bad = Color.FromArgb(245, 85, 85);
        public static readonly Color Warn = Color.FromArgb(245, 185, 60);

        const double RelayMarginMs = 3; // a relay must beat direct by this much to be chosen
        const int CheckArea = 34;       // width of the "play here" tick box in the region column

        static string T(string en, string ar) { return Lang.T(en, ar); }

        readonly List<Game> games;
        readonly List<Relay> relays;

        ListBox gameList;
        ListView serverList, relayList;
        ComboBox routeBox;
        Button btnTest, btnBoost, btnStop, btnLock, btnUnlock, btnDetect;
        Label liveLabel, statusLabel, gameNote, lockLabel, infoLabel;
        Panel pickBar;
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

        public MainForm()
        {
            Lang.Ar = Storage.LoadArabic();
            games = GameDb.All();
            relays = Storage.LoadRelays();

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1240, 760);
            MinimumSize = new Size(960, 640);
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
                Log(T("WireGuard is not installed - relay routing is off until you install it (Tools tab).",
                      "WireGuard غير مثبت - التوجيه عبر الـ Relay متوقف لين تثبته (من تبويب الأدوات)."));
            Shown += delegate
            {
                gameList.SelectedIndex = 0;
                BetaNotice.ShowOnce(this);
                CheckForUpdate();
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
            Text = Brand.Name + (Brand.IsBeta ? " (Beta)" : "") + "  -  by " + Brand.Developer;
            RightToLeft = Lang.Ar ? RightToLeft.Yes : RightToLeft.No;
            RightToLeftLayout = Lang.Ar;
        }

        // ---------------------------------------------------------------- UI

        void BuildUi()
        {
            var header = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Panel1 };
            header.Controls.Add(liveLabel = new Label
            {
                Text = T("OFF", "متوقف"), Font = new Font("Segoe UI Semibold", 17f), ForeColor = Dim, Width = 330,
                Dock = DockStyle.Right, TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 14, 0)
            });
            var langWrap = new Panel { Dock = DockStyle.Right, Width = 120, Padding = new Padding(8, 15, 8, 15) };
            var langBtn = MakeButton(Lang.Ar ? "English" : "العربية", Panel2);
            langBtn.Dock = DockStyle.Fill;
            langBtn.Click += delegate { SwitchLanguage(); };
            langWrap.Controls.Add(langBtn);
            header.Controls.Add(langWrap);
            var aboutWrap = new Panel { Dock = DockStyle.Right, Width = 92, Padding = new Padding(8, 15, 0, 15) };
            var aboutBtn = MakeButton(T("About", "حول"), Panel2);
            aboutBtn.Dock = DockStyle.Fill;
            aboutBtn.Click += delegate { AboutDialog.Show(this); };
            aboutWrap.Controls.Add(aboutBtn);
            header.Controls.Add(aboutWrap);
            var contactWrap = new Panel { Dock = DockStyle.Right, Width = 110, Padding = new Padding(8, 15, 0, 15) };
            var contactBtn = MakeButton(T("Feedback", "تواصل"), Panel2);
            contactBtn.ForeColor = Warn;
            contactBtn.Dock = DockStyle.Fill;
            contactBtn.Click += delegate { ContactDialog.Show(this, logBox.Text); };
            contactWrap.Controls.Add(contactBtn);
            header.Controls.Add(contactWrap);

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
                Text = "by " + Brand.Developer + "  ·  " + T("game connection optimizer", "محسّن اتصال الألعاب"),
                ForeColor = Dim, AutoSize = true, Location = new Point(67, 38), Font = new Font("Segoe UI", 9f)
            });

            statusLabel = new Label
            {
                Dock = DockStyle.Bottom, Height = 28, BackColor = Panel1, ForeColor = Dim,
                TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 12, 0), Text = T("Ready", "جاهز")
            };
            statusLabel.Controls.Add(new Label
            {
                Dock = DockStyle.Right, AutoSize = false, Width = 260, ForeColor = Color.FromArgb(100, 106, 120),
                TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 12, 0),
                Text = Brand.Name + " v" + Brand.DisplayVersion + "  ·  " + Brand.Copyright
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
            var tabBar = new Panel { Dock = DockStyle.Top, Height = 46 };
            center.Controls.Add(content);
            center.Controls.Add(tabBar);

            string[] names = { T("Servers", "السيرفرات"), T("Relays", "سيرفرات الوسيط"), T("Tools && Log", "الأدوات والسجل") };
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
            ShowPage(currentPage);
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

        Panel BuildServersPage()
        {
            var p = new Panel();
            gameNote = new Label { Dock = DockStyle.Top, Height = 32, ForeColor = Dim, TextAlign = ContentAlignment.MiddleLeft };

            // region lock bar (Valve games)
            pickBar = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = Panel1, Padding = new Padding(8) };
            btnLock = MakeButton(T("Lock servers", "قفل السيرفرات"), Accent);
            btnLock.ForeColor = Color.Black;
            btnLock.Font = new Font("Segoe UI Semibold", 10f);
            btnLock.Dock = DockStyle.Right;
            btnLock.Width = 150;
            btnLock.Click += delegate { LockServers(); };
            btnUnlock = MakeButton(T("Unlock", "فك القفل"), Panel2);
            btnUnlock.Dock = DockStyle.Right;
            btnUnlock.Width = 110;
            btnUnlock.Click += delegate { UnlockServers(); };
            lockLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 4, 0) };
            pickBar.Controls.Add(lockLabel);
            pickBar.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
            pickBar.Controls.Add(btnUnlock);
            pickBar.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
            pickBar.Controls.Add(btnLock);

            serverList = MakeListView(new[] { T("Region", "المنطقة"), T("Ping", "البنق"), T("Jitter", "التذبذب"),
                                              T("Loss", "الفقد"), T("Players", "لاعبين (تقدير)"), T("Match in", "وقت المباراة"),
                                              T("Via relay", "عبر الوسيط"), T("Best route", "أفضل مسار") },
                                      new[] { 266, 72, 76, 56, 100, 100, 82, 140 });
            serverList.DrawSubItem += DrawRegionCheck;
            serverList.MouseDown += (s, e) =>
            {
                clickedCheck = false;
                var item = serverList.GetItemAt(e.X, e.Y);
                var g = CurrentGame;
                if (item == null || g == null || !g.CanLock) return;
                var cell = FirstCell(serverList, item, item.Bounds);
                bool onBox = Lang.Ar ? e.X >= cell.Right - CheckArea && e.X <= cell.Right : e.X >= cell.X && e.X <= cell.X + CheckArea;
                if (!onBox) return;
                clickedCheck = true;
                TogglePick((Region)item.Tag);
            };
            serverList.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Space || CurrentGame == null || !CurrentGame.CanLock) return;
                foreach (ListViewItem item in serverList.SelectedItems) TogglePick((Region)item.Tag);
                e.Handled = true;
            };
            serverList.DoubleClick += delegate { if (!clickedCheck) Boost(); };

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 200 };
            var controls = new Panel { Dock = DockStyle.Bottom, Height = 56 };
            btnTest = MakeButton(T("Test ping", "فحص البنق"), Panel2);
            btnTest.Bounds = new Rectangle(0, 12, 130, 40);
            btnTest.Click += delegate { RunTest(); };
            var routeLabel = new Label { Text = T("Route:", "المسار:"), ForeColor = Dim, AutoSize = true, Location = new Point(144, 22) };
            routeBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Panel2,
                ForeColor = TextC, Bounds = new Rectangle(204, 18, 210, 28),
                DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 22
            };
            routeBox.DrawItem += (s, e) =>
            {
                if (e.Index < 0) return;
                bool sel = (e.State & DrawItemState.Selected) != 0;
                using (var b = new SolidBrush(sel ? Color.FromArgb(32, 78, 64) : Panel2)) e.Graphics.FillRectangle(b, e.Bounds);
                TextRenderer.DrawText(e.Graphics, routeBox.Items[e.Index].ToString(), Font,
                    Rectangle.Inflate(e.Bounds, -4, 0), TextC, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | Lang.Flags);
            };
            btnBoost = MakeButton("BOOST", Accent);
            btnBoost.ForeColor = Color.Black;
            btnBoost.Font = new Font("Segoe UI Semibold", 11f);
            btnBoost.Bounds = new Rectangle(428, 12, 150, 40);
            btnBoost.Click += delegate { Boost(); };
            btnStop = MakeButton(T("Stop", "إيقاف"), Panel2);
            btnStop.Bounds = new Rectangle(588, 12, 100, 40);
            btnStop.Enabled = false;
            btnStop.Click += delegate { StopAll(); };
            controls.Controls.AddRange(new Control[] { btnTest, routeLabel, routeBox, btnBoost, btnStop });

            graph = new PingGraph { Dock = DockStyle.Fill };
            bottom.Controls.Add(graph);
            bottom.Controls.Add(controls);

            p.Controls.Add(serverList);
            p.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 8 });
            p.Controls.Add(pickBar);
            p.Controls.Add(BuildInfoBar());
            p.Controls.Add(bottom);
            p.Controls.Add(gameNote);
            return p;
        }

        Panel BuildRelaysPage()
        {
            var p = new Panel();
            var info = new Label
            {
                Dock = DockStyle.Top, Height = 60, ForeColor = Dim,
                Text = T("A relay is a VPS you own running WireGuard (set it up with setup-relay.sh). When a relay route wins, " +
                         "ONLY the game's IP ranges go through it - browsing, downloads and Discord stay on your normal connection.",
                         "الوسيط (Relay) هو سيرفر VPS خاص فيك عليه WireGuard (تجهّزه بسكربت setup-relay.sh). إذا كان المسار عبره أسرع، " +
                         "يمر ترافيك اللعبة فقط من خلاله، وباقي الإنترنت (المتصفح، التحميل، ديسكورد) يبقى على اتصالك العادي.")
            };
            relayList = MakeListView(new[] { T("Name", "الاسم"), T("Server", "السيرفر"), T("Ping", "البنق"), T("Agent", "أداة القياس") },
                                     new[] { 180, 220, 90, 300 });

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(0, 10, 0, 0) };
            bar.Controls.Add(ToolButton(T("Add relay (.conf)", "إضافة وسيط (.conf)"), AddRelay));
            bar.Controls.Add(ToolButton(T("Remove", "حذف"), RemoveRelay));
            bar.Controls.Add(ToolButton(T("Test relays", "فحص الوسطاء"), TestRelays));

            p.Controls.Add(relayList);
            p.Controls.Add(bar);
            p.Controls.Add(info);
            return p;
        }

        Panel BuildToolsPage()
        {
            var p = new Panel();
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 56 };
            bar.Controls.Add(ToolButton(T("Flush DNS", "مسح كاش DNS"), FlushDns));
            bar.Controls.Add(ToolButton(T("Raise game priority", "رفع أولوية اللعبة"), delegate { RaisePriority(CurrentGame); }));
            bar.Controls.Add(ToolButton(T("Add custom server", "إضافة سيرفر مخصص"), AddCustomServer));
            bar.Controls.Add(ToolButton(T("Get WireGuard", "تحميل WireGuard"), delegate { Process.Start("https://www.wireguard.com/install/"); }));
            bar.Controls.Add(ToolButton(T("Open data folder", "فتح مجلد البيانات"), delegate { Process.Start(Storage.Dir); }));

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
            return item.Selected ? Color.FromArgb(32, 78, 64) : (index % 2 == 0 ? Panel1 : Color.FromArgb(30, 33, 42));
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

            // the visually last column fills the remaining width, so no unpainted header area is left
            int fill = Lang.Ar ? 0 : cols.Length - 1;
            lv.Resize += delegate
            {
                int used = 0;
                for (int i = 0; i < lv.Columns.Count; i++) if (i != fill) used += lv.Columns[i].Width;
                lv.Columns[fill].Width = Math.Max(widths[fill], lv.ClientSize.Width - used);
            };

            lv.DrawColumnHeader += (s, e) =>
            {
                using (var b = new SolidBrush(Panel2)) e.Graphics.FillRectangle(b, e.Bounds);
                TextRenderer.DrawText(e.Graphics, e.Header.Text, Font, Rectangle.Inflate(e.Bounds, -8, 0), Dim,
                    TextFormatFlags.VerticalCenter | Align);
            };
            lv.DrawItem += (s, e) => { };
            lv.DrawSubItem += (s, e) =>
            {
                if (e.ColumnIndex == 0 && lv == serverList) return; // drawn by DrawRegionCheck
                var rc = e.ColumnIndex == 0 ? FirstCell(lv, e.Item, e.Bounds) : e.Bounds;
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

        // First column of the server list: optional tick box + region name.
        void DrawRegionCheck(object sender, DrawListViewSubItemEventArgs e)
        {
            if (e.ColumnIndex != 0) return;
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
                tabButtons[i].BackColor = i == idx ? Color.FromArgb(52, 58, 74) : Panel2;
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
            string name = g.Provider == Provider.Custom ? T("Custom servers", "سيرفرات مخصصة") : g.Name;
            TextRenderer.DrawText(e.Graphics, name, Font,
                new Rectangle(e.Bounds.X + 14, e.Bounds.Y, e.Bounds.Width - 18, e.Bounds.Height),
                sel ? TextC : Color.FromArgb(190, 194, 204), TextFormatFlags.VerticalCenter | Align);
        }

        void SwitchLanguage()
        {
            if (busy || boosting)
            {
                Msg(T("Stop Boost and wait for the ping test to finish first.", "أوقف البوست وانتظر انتهاء الفحص أولاً."));
                return;
            }
            Lang.Ar = !Lang.Ar;
            Storage.SaveArabic(Lang.Ar);
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
            MessageBox.Show(this, text, Brand.Name, MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1,
                Lang.Ar ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : 0);
        }

        bool Ask(string text)
        {
            return MessageBox.Show(this, text, Brand.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1,
                Lang.Ar ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : 0) == DialogResult.Yes;
        }

        // LRM marks keep "74 ms" in order inside right-to-left text.
        static string Ms(double v) { return v < 0 ? T("timeout", "لا يستجيب") : "‎" + Math.Round(v) + " ms‎"; }

        static Color PingColor(double v)
        {
            if (v < 0) return Bad;
            if (v < 60) return Accent;
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
            SetStatus(T("Loading servers for ", "جاري تحميل سيرفرات ") + g.Name + "...");
            Task.Run(() =>
            {
                string err = null;
                try { GameDb.Load(g); } catch (Exception ex) { err = ex.Message; }
                Ui(() =>
                {
                    if (CurrentGame != g) return;
                    if (err != null)
                    {
                        SetStatus(T("Could not load servers: ", "تعذّر تحميل السيرفرات: ") + err);
                        Log(g.Name + ": " + err);
                        return;
                    }
                    FillServers(g);
                    UpdateLockBar();
                    LoadStats(g);
                    SetStatus(g.Regions.Count == 0
                        ? T("No servers. Add one from Tools > Add custom server.", "ما فيه سيرفرات. أضف سيرفر من الأدوات > إضافة سيرفر مخصص.")
                        : g.Regions.Count + T(" regions loaded - press Test ping.", " منطقة جاهزة - اضغط فحص البنق."));
                });
            });
        }

        void FillServers(Game g)
        {
            serverList.BeginUpdate();
            serverList.Items.Clear();
            foreach (var r in g.Regions)
            {
                var item = new ListViewItem(r.Name) { UseItemStyleForSubItems = false, Tag = r };
                item.ForeColor = TextC;
                for (int i = 0; i < 7; i++) item.SubItems.Add("");
                serverList.Items.Add(item);
                UpdateRow(item);
            }
            serverList.EndUpdate();
        }

        void UpdateRow(ListViewItem item)
        {
            var r = (Region)item.Tag;
            item.SubItems[0].ForeColor = r.Picked ? Accent : (r.Detected ? Warn : TextC);

            string mm;
            int matches;
            var g = CurrentGame;
            if (g != null && g.AppId == "1422450" && activity != null && Stats.PopToMm.TryGetValue(r.Id, out mm) &&
                activity.TryGetValue(mm, out matches))
            {
                SetSub(item, 4, Stats.Approx(Stats.EstimatePlayers(matches)), TextC);
                SetSub(item, 5, Stats.EstimateWait(matches), matches >= 100 ? Accent : matches >= 40 ? Warn : Bad);
            }
            else
            {
                SetSub(item, 4, "-", Dim);
                SetSub(item, 5, "-", Dim);
            }

            if (r.Direct == null)
            {
                foreach (int i in new[] { 1, 2, 3, 6, 7 }) SetSub(item, i, "-", Dim);
                return;
            }
            SetSub(item, 1, Ms(r.Direct.Avg), PingColor(r.Direct.Avg));
            SetSub(item, 2, r.Direct.Ok ? "‎" + Math.Round(r.Direct.Jitter, 1) + " ms‎" : "-", r.Direct.Jitter > 10 ? Warn : Dim);
            SetSub(item, 3, Math.Round(r.Direct.Loss) + "%", r.Direct.Loss > 0 ? Bad : Dim);

            var best = BestRelay(r);
            SetSub(item, 6, best == null ? "-" : Ms(best.Value.Value), best == null ? Dim : PingColor(best.Value.Value));

            var relay = AutoRelay(r);
            if (relay != null)
            {
                string gain = r.Direct.Ok ? "  (-" + Math.Round(r.Direct.Avg - best.Value.Value) + " ms)" : "";
                SetSub(item, 7, T("Relay: ", "وسيط: ") + relay.Name + gain, Accent);
            }
            else SetSub(item, 7, r.Direct.Ok ? T("Direct", "مباشر") : "-", r.Direct.Ok ? TextC : Dim);
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
            if (g.Regions.Count == 0) { SetStatus(T("No servers to test.", "ما فيه سيرفرات للفحص.")); return; }
            if (lockSummary != null && g.CanLock)
                Log(T("Note: locked-out regions will show as timeout while the lock is on.",
                      "ملاحظة: المناطق المحجوبة بتظهر \"لا يستجيب\" طول ما القفل شغّال."));
            busy = true;
            btnTest.Enabled = false;
            var regions = g.Regions.ToList();
            var rels = relays.ToList();
            SetStatus(T("Testing relays...", "جاري فحص الوسطاء..."));

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
                    Ui(() => { UpdateRegion(r); SetStatus(T("Testing... ", "جاري الفحص... ") + n + "/" + regions.Count); });
                });

                Ui(() =>
                {
                    busy = false;
                    btnTest.Enabled = true;
                    var top = regions.Where(r => BestLatency(r) < double.MaxValue).OrderBy(BestLatency).FirstOrDefault();
                    if (top == null)
                    {
                        SetStatus(T("No server answered. Check your connection or firewall.", "ولا سيرفر استجاب. تأكد من الاتصال أو الجدار الناري."));
                        return;
                    }
                    if (CurrentGame == g)
                    {
                        g.Regions = g.Regions.OrderBy(BestLatency).ToList(); // closest servers first
                        FillServers(g);
                    }
                    SetStatus(T("Done. Best region: ", "تم. أفضل منطقة: ") + top.Name + " - " + Ms(BestLatency(top)));
                    Log(g.Name + ": " + T("best region ", "أفضل منطقة ") + top.Name + " " + Ms(BestLatency(top)));
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
                    Log(T("New version available: ", "فيه إصدار جديد: ") + tag);
                    if (Ask(T("A new version of 2X Ping is available (" + tag + "). Open the download page?",
                              "فيه إصدار جديد من البرنامج (" + tag + "). تبي تفتح صفحة التحميل؟")))
                        Brand.Open(Brand.ReleasesUrl);
                });
            });
        }

        // ---------------------------------------------------------------- players & server detection

        Panel BuildInfoBar()
        {
            var bar = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(0, 4, 0, 8) };
            btnDetect = MakeButton(T("Detect my server", "اكتشاف السيرفر"), Panel2);
            btnDetect.Dock = DockStyle.Right;
            btnDetect.Width = 160;
            btnDetect.Click += delegate { DetectServer(); };
            infoLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = TextC, AutoEllipsis = true };
            bar.Controls.Add(infoLabel);
            bar.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
            bar.Controls.Add(btnDetect);
            return bar;
        }

        void LoadStats(Game g)
        {
            infoLabel.Text = T("Loading player numbers...", "جاري جلب عدد اللاعبين...");
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
                        text = T("In matches now (estimate): ", "باللعب الحين حوالي ") + Stats.Approx(total) + T("", " لاعب") + "   |   " +
                               string.Join("   ", act.OrderByDescending(kv => kv.Value).Take(4).Select(kv =>
                                   (Stats.MmRegions.ContainsKey(kv.Key) ? Lang.T(Stats.MmRegions[kv.Key][0], Stats.MmRegions[kv.Key][1]) : kv.Key) +
                                   " " + Stats.Approx(Stats.EstimatePlayers(kv.Value))));
                    }
                    else if (steam >= 0)
                        text = T("Online now on Steam: ", "أونلاين الحين على Steam: ") + steam.ToString("N0") +
                               T(" players (all regions - per-region numbers are not published)", " لاعب (كل المناطق - عدد كل منطقة غير منشور)");
                    else if (g.Provider == Provider.Custom)
                        text = "";
                    else
                        text = T("Player numbers are not available for this game.", "عدد اللاعبين غير متاح لهذي اللعبة.");
                    infoLabel.Text = text;
                    infoLabel.ForeColor = TextC;
                    foreach (ListViewItem item in serverList.Items) UpdateRow(item);
                    serverList.Invalidate();
                });
            });
        }

        void DetectServer()
        {
            var g = CurrentGame;
            if (g == null || busy) return;
            if (!Ask(T("Join a match / raid first.\n\n2X Ping will watch the game's network traffic for 15 seconds (Windows pktmon) to find the server you are playing on. Start now?",
                       "ادخل مباراة / رايد أولاً.\n\nالبرنامج بيراقب ترافيك اللعبة 15 ثانية (بأداة pktmon من ويندوز) عشان يعرف السيرفر اللي تلعب عليه. نبدأ؟")))
                return;
            busy = true;
            btnDetect.Enabled = false;
            SetStatus(T("Detecting the game server... (15 s)", "جاري اكتشاف سيرفر اللعبة... (15 ثانية)"));
            Task.Run(() =>
            {
                try
                {
                    bool matched;
                    var hits = Detector.Capture(g.Processes, 15, out matched);
                    var found = new List<Region>();
                    foreach (var h in hits)
                    {
                        string loc = Stats.Locate(h.Ip);
                        Storage.AddDetected(g.Name, h.Ip, loc);
                        var r = Storage.MakeDetected(h.Ip, loc);
                        r.Direct = Pinger.Measure(r.Endpoints[0], 4, 1200);
                        r.BestEndpoint = r.Direct.Ok ? r.Endpoints[0] : null;
                        found.Add(r);
                        Log(T("Detected server: ", "سيرفر مكتشف: ") + h.Ip + ":" + h.Port + "  " + (loc ?? "") + "  (" + h.Packets + T(" packets)", " حزمة)"));
                    }
                    if (!matched)
                        Log(T("Game process not found - results may include other apps (Discord etc.).",
                              "ما لقيت عملية اللعبة شغّالة - النتيجة ممكن تشمل برامج ثانية (ديسكورد وغيره)."));
                    Ui(() =>
                    {
                        busy = false;
                        btnDetect.Enabled = true;
                        if (found.Count == 0)
                        {
                            SetStatus(T("No game traffic found.", "ما لقيت ترافيك لعبة."));
                            Msg(T("No game server traffic was found. Make sure you are inside a match/raid and try again.",
                                  "ما لقيت ترافيك سيرفر لعبة. تأكد إنك داخل مباراة/رايد وجرب مرة ثانية."));
                            return;
                        }
                        foreach (var r in found)
                        {
                            g.Regions.RemoveAll(x => x.Id == r.Id);
                            g.Regions.Insert(0, r);
                        }
                        if (CurrentGame == g) FillServers(g);
                        var top = found[0];
                        SetStatus(T("Your server: ", "سيرفرك: ") + top.Name + " - " + Ms(top.Direct.Avg));
                        Msg(T("You are playing on:\n", "أنت تلعب على:\n") + top.Name + "\n" + T("Ping: ", "البنق: ") +
                            (top.Direct.Ok ? Ms(top.Direct.Avg) : T("the server does not answer ping (normal for some games)", "السيرفر ما يرد على ping (طبيعي لبعض الألعاب)")) +
                            T("\n\nIt is saved in the list - select it and press BOOST to route it through a relay.",
                              "\n\nانحفظ في القائمة - اختاره واضغط BOOST عشان توجّهه عبر الوسيط."));
                    });
                }
                catch (Exception ex)
                {
                    Log(T("Detection failed: ", "فشل الاكتشاف: ") + ex.Message);
                    Ui(() => { busy = false; btnDetect.Enabled = true; Msg(T("Detection failed: ", "فشل الاكتشاف: ") + ex.Message); });
                }
            });
        }

        // ---------------------------------------------------------------- region lock (Valve)

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
                lockLabel.Text = T("LOCKED - you can only play on: ", "مقفول - تلعب فقط على: ") + lockSummary;
                lockLabel.ForeColor = Accent;
            }
            else
            {
                lockLabel.Text = picked == 0
                    ? T("Tick the servers you want to play on, then press Lock.", "حدد (✓) السيرفرات اللي تبي تلعب عليها، ثم اضغط قفل السيرفرات.")
                    : T("Selected: ", "المحدد: ") + picked + T(" region(s) - press Lock to play only there.", " منطقة - اضغط قفل السيرفرات عشان تلعب عليها فقط.");
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
                Msg(T("Tick at least one server first (click the box next to the region).",
                      "حدد سيرفر واحد على الأقل أولاً (اضغط المربع جنب اسم المنطقة)."));
                return;
            }
            var keep = new HashSet<string>(picked.SelectMany(r => r.Cidrs));
            var block = g.Regions.Where(r => !r.Picked && !r.Detected).SelectMany(r => r.Cidrs).Where(c => !keep.Contains(c)).ToList();
            string names = string.Join("، ", picked.Select(r => r.Name.Split(new[] { "  [" }, StringSplitOptions.None)[0]));
            btnLock.Enabled = false;
            SetStatus(T("Applying region lock...", "جاري قفل السيرفرات..."));
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
                        Log(T("Region lock ON (", "تم قفل السيرفرات (") + g.Name + "): " + names + T(" - blocked ", " - تم حجب ") + n + T(" address ranges.", " نطاق."));
                        SetStatus(T("Region lock ON: ", "القفل شغّال: ") + names);
                        Msg(T("Done! You will only be matched on: " + names + "\n\nIf the game is open, restart it (or wait about a minute) before searching for a match.\n" +
                              "The lock is removed automatically when you press Unlock or close 2X Ping.",
                              "تم! بتلعب فقط على: " + names + "\n\nإذا اللعبة مفتوحة أعد تشغيلها (أو انتظر دقيقة تقريباً) قبل ما تبحث عن مباراة.\n" +
                              "القفل ينشال تلقائياً لما تضغط فك القفل أو تقفل البرنامج."));
                    });
                }
                catch (Exception ex)
                {
                    Ui(() =>
                    {
                        btnLock.Enabled = true;
                        Log(T("Region lock failed: ", "فشل القفل: ") + ex.Message);
                        Msg(T("Region lock failed: ", "فشل القفل: ") + ex.Message);
                    });
                }
            });
        }

        void UnlockServers()
        {
            if (lockSummary == null) return;
            lockSummary = null;
            UpdateLockBar();
            Task.Run(() =>
            {
                ServerLock.Remove();
                Log(T("Region lock removed - all servers available.", "تم فك القفل - كل السيرفرات متاحة."));
                SetStatus(T("Region lock removed.", "تم فك القفل."));
            });
        }

        // ---------------------------------------------------------------- boost

        void Boost()
        {
            var g = CurrentGame;
            if (g == null || !btnBoost.Enabled) return;
            Region region = serverList.SelectedItems.Count > 0 ? (Region)serverList.SelectedItems[0].Tag : null;
            if (region == null)
            {
                region = g.Regions.Where(r => BestLatency(r) < double.MaxValue).OrderBy(BestLatency).FirstOrDefault();
                if (region == null) { Msg(T("Run 'Test ping' first, or select a region.", "اضغط فحص البنق أولاً أو اختر منطقة.")); return; }
            }
            var target = region.Target;
            if (target == null) return;

            Relay relay = null;
            if (routeBox.SelectedIndex >= 2) relay = relays[routeBox.SelectedIndex - 2];
            else if (routeBox.SelectedIndex == 0) relay = AutoRelay(region);

            boosting = true;
            btnBoost.Enabled = false;
            btnStop.Enabled = true;

            if (relay == null)
            {
                Log(region.Name + ": " + T("DIRECT route", "مسار مباشر") +
                    (routeBox.SelectedIndex == 0 ? T(" (already the fastest path)", " (هو الأسرع أصلاً)") : ""));
                RaisePriority(g);
                StartMonitor(target, T("Direct", "مباشر"));
                return;
            }

            if (!Tunnel.Installed)
            {
                if (Ask(T("Relay routing needs WireGuard for Windows (free). Open the download page?",
                          "التوجيه عبر الوسيط يحتاج برنامج WireGuard (مجاني). تبي أفتح صفحة التحميل؟")))
                    Process.Start("https://www.wireguard.com/install/");
                StopAll();
                return;
            }

            SetStatus(T("Building route through ", "جاري بناء المسار عبر ") + relay.Name + "...");
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
                    Log(T("Tunnel up through ", "المسار شغّال عبر ") + relay.Name + " - " + region.Name + " (" + cidrs.Count + ")");
                    Ui(() => { RaisePriority(g); StartMonitor(target, T("via ", "عبر ") + relay.Name); });
                }
                catch (Exception ex)
                {
                    Log(T("Boost failed: ", "فشل البوست: ") + ex.Message);
                    Ui(() => { StopAll(); Msg(ex.Message); });
                }
            });
        }

        void StartMonitor(Endpoint target, string label)
        {
            int gen = ++monitorGen;
            graph.Clear();
            graph.Label = label;
            SetStatus(T("BOOST ACTIVE - ", "البوست شغّال - ") + label + " - " + target);
            var t = new Thread(() =>
            {
                while (gen == monitorGen)
                {
                    double v = Pinger.Once(target, 1500);
                    if (gen != monitorGen) break;
                    Ui(() =>
                    {
                        if (gen != monitorGen) return;
                        liveLabel.Text = Ms(v) + "  " + label;
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
            liveLabel.Text = T("OFF", "متوقف");
            liveLabel.ForeColor = Dim;
            btnBoost.Enabled = true;
            btnStop.Enabled = false;
            SetStatus(T("Stopped.", "تم الإيقاف."));
            if (tunnelActive)
            {
                tunnelActive = false;
                Task.Run(() =>
                {
                    Tunnel.Stop();
                    Log(T("Tunnel removed - all traffic is direct again.", "تم إيقاف المسار - كل الترافيك رجع مباشر."));
                });
            }
        }

        void RaisePriority(Game g)
        {
            if (g == null || g.Processes.Length == 0) return;
            var ps = g.Processes.SelectMany(n => Process.GetProcessesByName(n)).ToList();
            if (ps.Count == 0) { Log(g.Name + T(" is not running - priority not changed.", " مو شغّالة - ما تغيّرت الأولوية.")); return; }
            foreach (var p in ps)
            {
                try
                {
                    p.PriorityClass = ProcessPriorityClass.High;
                    Log(g.Name + T(" priority set to High (pid ", ": تم رفع الأولوية إلى عالية (pid ") + p.Id + ").");
                }
                catch (Exception ex) { Log(T("Could not change priority: ", "تعذّر تغيير الأولوية: ") + ex.Message); }
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
                item.SubItems.Add(r.EndpointHost ?? T("(config missing)", "(ملف الإعداد مفقود)")).ForeColor = TextC;
                item.SubItems.Add(r.Ping < 0 ? "-" : Ms(r.Ping)).ForeColor = r.Ping < 0 ? Dim : PingColor(r.Ping);
                item.SubItems.Add(r.HasAgent ? r.AgentUrl : T("none (no relay ping estimate)", "لا يوجد (بدون تقدير البنق)")).ForeColor = Dim;
                relayList.Items.Add(item);
            }
            relayList.EndUpdate();

            int sel = routeBox.SelectedIndex;
            routeBox.Items.Clear();
            routeBox.Items.Add(T("Auto (fastest)", "تلقائي (الأسرع)"));
            routeBox.Items.Add(T("Direct (no relay)", "مباشر (بدون وسيط)"));
            foreach (var r in relays) routeBox.Items.Add(T("Relay: ", "وسيط: ") + r.Name);
            routeBox.SelectedIndex = sel >= 0 && sel < routeBox.Items.Count ? sel : 0;
        }

        void AddRelay()
        {
            string path;
            using (var dlg = new OpenFileDialog { Filter = "WireGuard config (*.conf)|*.conf", Title = T("Select the relay's client.conf", "اختر ملف client.conf الخاص بالوسيط") })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                path = dlg.FileName;
            }
            var probe = new Relay { ConfPath = path };
            string host = probe.EndpointHost;
            if (host == null) { Msg(T("No 'Endpoint =' line found in this config.", "ما لقيت سطر 'Endpoint =' في هذا الملف.")); return; }

            var vals = InputDialog.Show(this, T("Add relay", "إضافة وسيط"),
                new[] { T("Name", "الاسم"), T("Agent URL (from setup-relay.sh, optional)", "رابط أداة القياس (من setup-relay.sh، اختياري)"), T("Agent token", "رمز أداة القياس (Token)") },
                new[] { Path.GetFileNameWithoutExtension(path), "http://" + host + ":8787", "" });
            if (vals == null || vals[0] == "") return;
            if (relays.Any(r => r.Name.Equals(vals[0], StringComparison.OrdinalIgnoreCase)))
            {
                Msg(T("A relay with that name already exists.", "فيه وسيط بنفس الاسم."));
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
            Log(T("Relay added: ", "تمت إضافة وسيط: ") + vals[0] + " (" + host + ")");
        }

        void RemoveRelay()
        {
            if (relayList.SelectedItems.Count == 0) return;
            var r = (Relay)relayList.SelectedItems[0].Tag;
            relays.Remove(r);
            Storage.SaveRelays(relays);
            RefreshRelays();
            Log(T("Relay removed: ", "تم حذف الوسيط: ") + r.Name);
        }

        void TestRelays()
        {
            SetStatus(T("Testing relays...", "جاري فحص الوسطاء..."));
            var rels = relays.ToList();
            Task.Run(() =>
            {
                foreach (var r in rels)
                {
                    r.Ping = r.MeasurePing();
                    string agent = !r.HasAgent ? T("no agent", "بدون أداة قياس")
                        : r.ProbeFromRelay(new Endpoint("1.1.1.1", 443)) >= 0 ? T("agent OK", "أداة القياس تعمل") : T("agent NOT reachable", "أداة القياس لا تستجيب");
                    Log(T("Relay ", "الوسيط ") + r.Name + ": " + Ms(r.Ping) + "، " + agent);
                }
                Ui(() => { RefreshRelays(); SetStatus(T("Relay test done - details in Tools & Log.", "انتهى فحص الوسطاء - التفاصيل في الأدوات والسجل.")); });
            });
        }

        // ---------------------------------------------------------------- tools

        void FlushDns()
        {
            var psi = new ProcessStartInfo("ipconfig", "/flushdns") { UseShellExecute = false, CreateNoWindow = true };
            using (var p = Process.Start(psi))
            {
                p.WaitForExit(10000);
                Log(p.ExitCode == 0 ? T("DNS cache flushed.", "تم مسح كاش DNS.") : T("DNS flush failed.", "فشل مسح كاش DNS."));
            }
        }

        void AddCustomServer()
        {
            var vals = InputDialog.Show(this, T("Add custom server", "إضافة سيرفر مخصص"),
                new[] { T("Name (e.g. My Rust server)", "الاسم (مثال: سيرفر راست)"),
                        T("Servers: host or host:port, comma separated (no port = ICMP ping)", "السيرفرات: IP أو IP:port مفصولة بفاصلة (بدون منفذ = ping عادي)"),
                        T("IP ranges to route through relay (optional, e.g. 1.2.3.0/24)", "نطاقات IP للتوجيه عبر الوسيط (اختياري، مثال 1.2.3.0/24)") },
                new[] { "", "", "" });
            if (vals == null || vals[0] == "" || vals[1] == "") return;
            Storage.AddCustom(vals[0], vals[1], vals[2]);
            Log(T("Custom server added: ", "تمت إضافة سيرفر مخصص: ") + vals[0]);
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
                f.RightToLeftLayout = Lang.Ar;
                f.ClientSize = new Size(520, 24 + labels.Length * 62 + 56);

                var boxes = new TextBox[labels.Length];
                for (int i = 0; i < labels.Length; i++)
                {
                    f.Controls.Add(new Label { Text = labels[i], AutoSize = true, ForeColor = MainForm.Dim, Location = new Point(16, 14 + i * 62) });
                    boxes[i] = new TextBox
                    {
                        Text = defaults[i], Bounds = new Rectangle(16, 38 + i * 62, 488, 26), RightToLeft = RightToLeft.No,
                        BackColor = MainForm.Panel2, ForeColor = MainForm.TextC, BorderStyle = BorderStyle.FixedSingle
                    };
                    f.Controls.Add(boxes[i]);
                }
                int y = 24 + labels.Length * 62 + 8;
                var ok = new Button { Text = Lang.T("OK", "موافق"), DialogResult = DialogResult.OK, Bounds = new Rectangle(320, y, 88, 34), FlatStyle = FlatStyle.Flat, BackColor = MainForm.Accent, ForeColor = Color.Black };
                var cancel = new Button { Text = Lang.T("Cancel", "إلغاء"), DialogResult = DialogResult.Cancel, Bounds = new Rectangle(416, y, 88, 34), FlatStyle = FlatStyle.Flat, BackColor = MainForm.Panel2 };
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

            using (var pen = new Pen(Color.FromArgb(44, 48, 60)))
                for (int i = 0; i <= 4; i++)
                {
                    int y = rc.Bottom - i * rc.Height / 4;
                    g.DrawLine(pen, rc.Left, y, rc.Right, y);
                    TextRenderer.DrawText(g, Math.Round(max * i / 4).ToString(), Font, new Point(6, y - 8), MainForm.Dim);
                }

            if (samples.Count == 0)
            {
                TextRenderer.DrawText(g, Lang.T("Live ping graph appears here while BOOST is active", "رسم البنق المباشر يظهر هنا أثناء تشغيل البوست"),
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
                         Math.Round(100.0 * (samples.Count - ok.Count) / samples.Count) + "%";
            TextRenderer.DrawText(g, stats, Font, new Point(rc.Left, 4), MainForm.TextC, Lang.Flags);
        }
    }
}
