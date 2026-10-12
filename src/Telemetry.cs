using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace PingBoost
{
    // Usage statistics sent to the developer's Supabase project (see tools/supabase-setup.sql).
    // Users are told about it on first launch (WelcomeDialog); using the beta requires accepting it.
    static class Telemetry
    {
        // Project URL and the PUBLIC (anon / publishable) key. The public key can only call checkin(), never read.
        public const string SupabaseUrl = "https://exzriktevuapbbgqwgle.supabase.co";
        public const string SupabaseKey = "sb_publishable_RN9M6tcRWFXbqfJdbSOo1A_h0upD6RQ";

        public static bool Configured { get { return SupabaseUrl != "" && SupabaseKey != ""; } }

        // live state, updated by the main window
        public static volatile string Game, Server;
        public static int Ping = -1;

        static System.Threading.Timer heartbeat;
        static string country, city, isp, asn;
        static readonly DateTime sessionStart = DateTime.UtcNow;
        // one random id per launch, so the events of a session can be grouped
        static readonly string session = Guid.NewGuid().ToString("N").Substring(0, 12);

        public static string InstallId
        {
            get
            {
                var id = Storage.GetSetting("install_id");
                if (string.IsNullOrEmpty(id)) { id = Guid.NewGuid().ToString(); Storage.SetSetting("install_id", id); }
                return id;
            }
        }

        public static string Nickname
        {
            get { return Storage.GetSetting("nickname") ?? ""; }
            set { Storage.SetSetting("nickname", (value ?? "").Trim()); }
        }

        public static bool HasConsent { get { return Storage.GetSetting("consent") == "1" && Nickname.Length >= 2; } }

        // startupMs: time from launch until the window was ready
        public static void Start(List<Game> games, long startupMs)
        {
            if (!Configured || !HasConsent) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                var info = SystemInfo.Collect(games);
                info["startup_ms"] = startupMs;
                info["updated"] = Updater.JustUpdated;
                Post("start", null, -1, Environment.OSVersion.ToString(), null, info);
            });
            if (heartbeat == null) heartbeat = new System.Threading.Timer(delegate { Send("heartbeat", null, -1, null); }, null, 60000, 60000);
        }

        // Sent right away and waited for (a few seconds at most): for reports made just before the app dies.
        public static void SendNow(string kind, string detail, Dictionary<string, object> data)
        {
            if (!Configured || !HasConsent) return;
            try { Post(kind, null, -1, detail, data, null, 4000); } catch { }
        }

        // Sent while the app closes, so it waits (briefly) instead of firing and forgetting.
        public static void Exit()
        {
            if (!Configured || !HasConsent) return;
            try
            {
                Post("exit", null, -1, null,
                     new Dictionary<string, object> { { "minutes", Math.Round((DateTime.UtcNow - sessionStart).TotalMinutes, 1) } }, null, 3000);
            }
            catch { }
        }

        // City and country from the user's internet address, looked up once a day (the address itself is not stored).
        static void EnsureLocation()
        {
            string today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            if (Storage.GetSetting("geo_at") == today)
            {
                country = Storage.GetSetting("geo_country");
                city = Storage.GetSetting("geo_city");
                isp = Storage.GetSetting("geo_isp");
                asn = Storage.GetSetting("geo_as");
                return;
            }
            try
            {
                var d = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(
                    Http.Get("http://ip-api.com/json/?fields=status,country,city,isp,as", 6000));
                if ((string)d["status"] != "success") return;
                country = (string)d["country"];
                city = (string)d["city"];
                isp = d.ContainsKey("isp") ? (string)d["isp"] : null;
                asn = d.ContainsKey("as") ? (string)d["as"] : null;
                Storage.SetSetting("geo_country", country);
                Storage.SetSetting("geo_city", city);
                Storage.SetSetting("geo_isp", isp);
                Storage.SetSetting("geo_as", asn);
                Storage.SetSetting("geo_at", today);
            }
            catch { }
        }

        static Dictionary<string, string> Headers(string key)
        {
            var h = new Dictionary<string, string> { { "apikey", key } };
            if (key.StartsWith("eyJ")) h["Authorization"] = "Bearer " + key; // legacy JWT keys
            return h;
        }

        // Fire and forget: never blocks the UI, never throws.
        public static void Send(string kind, string server, int ping, string detail)
        {
            Send(kind, server, ping, detail, null);
        }

        // data: structured details of the event (stored as JSON), e.g. every result of a ping test.
        public static void Send(string kind, string server, int ping, string detail, Dictionary<string, object> data)
        {
            if (!Configured || !HasConsent) return;
            string game = Game, srv = server ?? Server;
            int pg = ping >= 0 ? ping : Ping;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { Post(kind, srv, pg, detail, data, null, 8000, game); } catch { }
            });
        }

        static void Post(string kind, string server, int ping, string detail, Dictionary<string, object> data, Dictionary<string, object> info,
                         int timeout = 8000, string game = null)
        {
            EnsureLocation();
            var p = new Dictionary<string, object>
            {
                { "install_id", InstallId }, { "kind", kind }, { "nickname", Nickname },
                { "device_name", Environment.MachineName }, { "app_version", Brand.DisplayVersion },
                { "os", Environment.OSVersion.Version.ToString() }, { "lang", Lang.Ar ? "ar" : "en" },
                { "mode", Prefs.Simple ? "simple" : "advanced" }, { "country", country }, { "city", city },
                { "isp", isp }, { "asn", asn }, { "session", session },
                { "game", game ?? Game }, { "server", server ?? Server }, { "ping", ping >= 0 ? (object)ping : null }, { "detail", detail }
            };
            if (data != null) p["data"] = data;
            if (info != null) p["info"] = info;
            string body = "{\"p\":" + new JavaScriptSerializer().Serialize(p) + "}";
            Http.Send("POST", SupabaseUrl.TrimEnd('/') + "/rest/v1/rpc/checkin", body, Headers(SupabaseKey), timeout);
        }

        // ---------------------------------------------------------------- developer side (reading)

        static string DevKeyFile { get { return Path.Combine(Storage.Dir, "dev.key"); } }

        // The developer's SECRET key lives only in this file on the developer's PC. It is never in the app or the repo.
        public static string DevKey
        {
            get { try { return File.Exists(DevKeyFile) ? File.ReadAllText(DevKeyFile).Trim() : null; } catch { return null; } }
            set { if (string.IsNullOrEmpty(value)) { try { File.Delete(DevKeyFile); } catch { } } else File.WriteAllText(DevKeyFile, value.Trim()); }
        }

        public static object[] Query(string pathAndQuery)
        {
            string json = Http.Send("GET", SupabaseUrl.TrimEnd('/') + "/rest/v1/" + pathAndQuery, null, Headers(DevKey), 15000);
            return (object[])new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json);
        }
    }

    // First launch: name + clear notice of what is collected. Accepting is required to use the beta.
    static class WelcomeDialog
    {
        public static bool Show(IWin32Window owner)
        {
            using (var f = new Form())
            {
                f.Text = Brand.Caption;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterScreen;
                f.MinimizeBox = f.MaximizeBox = false;
                f.BackColor = MainForm.Bg;
                f.ForeColor = MainForm.TextC;
                f.Font = Fonts.Ui(10f);
                f.RightToLeft = Lang.Ar ? RightToLeft.Yes : RightToLeft.No;
                const int W = 480;

                f.Controls.Add(new PictureBox { Image = Brand.Logo, SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(228, 14, 64, 64) });
                f.Controls.Add(new Label
                {
                    Text = Lang.T("Welcome to 2X Ping", "هلا فيك في 2X Ping"), Font = Fonts.Semi(15f),
                    ForeColor = MainForm.Accent, TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(20, 82, W, 36)
                });
                // a card with its title; content starts at y = 46 inside it
                Func<int, int, string, Card> section = (cy, h, title) =>
                {
                    var c = new Card { Bounds = new Rectangle(20, cy, W, h), Fill = MainForm.Panel1, Radius = 14 };
                    c.Controls.Add(new Label
                    {
                        Text = title, Font = Fonts.Semi(10.5f), ForeColor = MainForm.Accent, Bounds = new Rectangle(16, 12, W - 32, 26)
                    });
                    f.Controls.Add(c);
                    return c;
                };

                var nameCard = section(128, 104, Lang.T("What's your name?", "إيش اسمك؟"));
                var name = new InputBox { Bounds = new Rectangle(16, 46, W - 32, 42) };
                name.Text = Telemetry.Nickname;
                name.Box.MaxLength = 30;
                nameCard.Controls.Add(name);

                // the games this player cares about, two per row, in reading order
                var all = GameDb.Available(GameDb.All());
                int gamesH = 70 + (all.Count + 1) / 2 * 54 + 8;
                var gamesCard = section(244, gamesH, Lang.T("Which games do you play?", "إيش الألعاب اللي تلعبها؟"));
                gamesCard.Controls.Add(new Label
                {
                    Text = Lang.T("Only these show up in your list. You can change them later in Settings.",
                                  "اللي تختاره بس يطلع لك، وتقدر تغيّره بعدين من الإعدادات."),
                    Bounds = new Rectangle(16, 40, W - 32, 22), ForeColor = MainForm.Dim, Font = Fonts.Ui(8.5f)
                });
                var picks = new List<GameTile>();
                const int tw = (W - 32 - 10) / 2;
                for (int i = 0; i < all.Count; i++)
                {
                    bool first = i % 2 == 0;
                    var tile = new GameTile
                    {
                        Text = all[i].Name, Icon = Brand.GameIcon(all[i].Icon), Tag = all[i].Icon,
                        Bounds = new Rectangle(first ^ Lang.Ar ? 16 : 16 + tw + 10, 70 + i / 2 * 54, tw, 46)
                    };
                    gamesCard.Controls.Add(tile);
                    picks.Add(tile);
                }
                int y = 244 + gamesH + 12;

                var noticeCard = new Card { Bounds = new Rectangle(20, y, W, 116), Fill = MainForm.Panel1, Radius = 14 };
                f.Controls.Add(noticeCard);
                var notice = new Label
                {
                    Bounds = new Rectangle(14, 10, W - 28, 98), ForeColor = MainForm.Dim, Font = Fonts.Ui(9.5f),
                    Text = Lang.T(
                        "This is a beta: while it's open, 2X Ping sends usage statistics so we can improve it.\n" +
                        "New versions are required: you get a few days to update.\nYou need to agree to use the beta.",
                        "هادي نسخة تجريبية، والبرنامج يرسل إحصائيات استخدام وهو مفتوح عشان نطوّره.\n" +
                        "ولما تنزل نسخة جديدة لازم تحدّث، وعندك كم يوم.\nلازم توافق عشان تستخدمه.")
                };
                noticeCard.Controls.Add(notice);
                var details = new LinkLabel
                {
                    Text = Lang.T("Statistics details", "تفاصيل الإحصائيات"), Bounds = new Rectangle(20, y + 120, W, 24),
                    LinkColor = MainForm.Lavender, ActiveLinkColor = Color.White, LinkBehavior = LinkBehavior.HoverUnderline
                };
                details.LinkClicked += delegate { Brand.Open(Brand.RepoUrl + "/blob/main/SECURITY.md#usage-statistics-beta"); };
                f.Controls.Add(details);

                var ok = new RoundButton
                {
                    Text = Lang.T("Agree and start", "موافق وأبدأ"), DialogResult = DialogResult.OK,
                    Bounds = new Rectangle(Lang.Ar ? 280 : 20, y + 158, 220, 40), FlatStyle = FlatStyle.Flat,
                    BackColor = MainForm.Accent, ForeColor = Color.Black, Font = Fonts.Semi(10.5f), Enabled = false
                };
                var exit = new RoundButton
                {
                    Text = Lang.T("Exit", "خروج"), DialogResult = DialogResult.Cancel,
                    Bounds = new Rectangle(Lang.Ar ? 20 : 280, y + 158, 220, 40), FlatStyle = FlatStyle.Flat,
                    BackColor = MainForm.Panel2, ForeColor = MainForm.TextC
                };
                ok.FlatAppearance.BorderSize = exit.FlatAppearance.BorderSize = 0;
                // ready once there is a name and at least one game
                EventHandler refresh = delegate
                {
                    ok.Enabled = name.Text.Trim().Length >= 2 && picks.Any(p => p.Checked);
                    ok.BackColor = ok.Enabled ? MainForm.Accent : MainForm.Panel2;
                };
                name.Box.TextChanged += refresh;
                foreach (var p in picks) p.CheckedChanged += refresh;
                refresh(null, EventArgs.Empty);
                f.Controls.Add(ok);
                f.Controls.Add(exit);
                f.AcceptButton = ok;
                f.CancelButton = exit;
                f.ClientSize = new Size(W + 40, y + 214);

                if (f.ShowDialog(owner) != DialogResult.OK) return false;
                Telemetry.Nickname = name.Text;
                Prefs.HiddenGames = new HashSet<string>(picks.Where(p => !p.Checked).Select(p => (string)p.Tag));
                Storage.SetSetting("consent", "1");
                return true;
            }
        }
    }

    // Developer-only window: who is online now, usage stats and the event log.
    // Opened by clicking the logo 5 times; asks for the secret key once and keeps it in dev.key.
    // The developer's statistics (who is online, versions, devices, problems, the event log), shown as a page
    // of the main window in developer mode. Reads with the secret key kept in dev.key on the developer's PC.
    class DevPanel : Panel
    {
        ListView online, stats, log, versions, devices, issues;
        Label summary;
        CheckBox errorsOnly;
        System.Windows.Forms.Timer refresh;
        object[] installs = new object[0], events = new object[0], issueEvents = new object[0];
        bool loadedOnce;
        public event Action ForgetKey;

        public DevPanel()
        {
            BackColor = MainForm.Bg;
            ForeColor = MainForm.TextC;
            Font = Fonts.Ui(9.5f);
            RightToLeft = RightToLeft.No; // developer tools stay in English, left to right

            var top = new Panel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(10, 8, 10, 8) };
            summary = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = Fonts.Semi(11f), ForeColor = MainForm.Accent };
            var btn = new RoundButton { Text = "Refresh", Dock = DockStyle.Right, Width = 100, FlatStyle = FlatStyle.Flat, BackColor = MainForm.Panel2 };
            btn.FlatAppearance.BorderSize = 0;
            btn.Click += delegate { Reload(); };
            var forget = new RoundButton { Text = "Forget key", Dock = DockStyle.Right, Width = 100, FlatStyle = FlatStyle.Flat, BackColor = MainForm.Panel2 };
            forget.FlatAppearance.BorderSize = 0;
            forget.Click += delegate { Telemetry.DevKey = null; var h = ForgetKey; if (h != null) h(); };
            top.Controls.Add(summary);
            top.Controls.Add(forget);
            top.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
            top.Controls.Add(btn);

            var tabs = new TabControl { Dock = DockStyle.Fill };
            online = List("Name", 120, "Device", 120, "City", 100, "Country", 100, "Game", 120, "Server", 160, "Ping", 55, "Version", 90,
                          "Net", 70, "ISP", 150, "Last seen", 90);
            stats = List("What", 200, "Value", 420, "Count", 90);
            versions = List("Version", 160, "Users (7 days)", 120, "Share", 90, "", 300);
            devices = List("Name", 120, "Windows", 200, "CPU", 220, "GPU", 200, "RAM", 55, "Screen", 110, "Net", 80, "ISP", 150,
                           "Games installed", 300, "Last seen", 110);
            log = List("Time", 120, "Name", 110, "Kind", 70, "Game", 110, "Server", 150, "Ping", 50, "Detail", 300, "Data", 400);
            issues = List("Problem", 300, "Code", 150, "Times", 60, "Players", 65, "Versions", 140, "Last seen", 110, "Latest example", 520);
            errorsOnly = new CheckBox { Text = "Errors only", Dock = DockStyle.Top, Height = 28, ForeColor = MainForm.TextC };
            errorsOnly.CheckedChanged += delegate { FillLog(); };
            var logPage = new TabPage("Log") { BackColor = MainForm.Bg };
            logPage.Controls.Add(log);
            logPage.Controls.Add(errorsOnly);
            tabs.TabPages.Add(Page("Problems", issues));
            tabs.TabPages.Add(Page("Online now", online));
            tabs.TabPages.Add(Page("Statistics", stats));
            tabs.TabPages.Add(Page("Versions", versions));
            tabs.TabPages.Add(Page("Devices", devices));
            tabs.TabPages.Add(logPage);

            Controls.Add(tabs);
            Controls.Add(top);

            // refreshes every 30 s while the page is on screen
            refresh = new System.Windows.Forms.Timer { Interval = 30000 };
            refresh.Tick += delegate { if (Visible) Reload(); };
            refresh.Start();
            VisibleChanged += delegate { if (Visible && !loadedOnce) { loadedOnce = true; Reload(); } };
            Disposed += delegate { refresh.Dispose(); };
        }

        static TabPage Page(string title, Control c)
        {
            var p = new TabPage(title) { BackColor = MainForm.Bg };
            p.Controls.Add(c);
            return p;
        }

        static ListView List(params object[] cols)
        {
            var lv = new ListView
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, BorderStyle = BorderStyle.None,
                BackColor = MainForm.Panel1, ForeColor = MainForm.TextC, GridLines = false
            };
            for (int i = 0; i < cols.Length; i += 2) lv.Columns.Add((string)cols[i], (int)cols[i + 1]);
            return lv;
        }

        static string S(object row, string key)
        {
            var d = row as Dictionary<string, object>;
            object v;
            return d != null && d.TryGetValue(key, out v) && v != null ? Convert.ToString(v) : "";
        }

        // A field of the "info" JSON column (hardware/system details sent at launch).
        static string I(object row, string key) { return Field(row, "info", key); }

        static string Field(object row, string column, string key)
        {
            var d = row as Dictionary<string, object>;
            object info, v;
            if (d == null || !d.TryGetValue(column, out info)) return "";
            var id = info as Dictionary<string, object>;
            if (id == null || !id.TryGetValue(key, out v) || v == null) return "";
            var arr = v as object[];
            return arr != null ? string.Join(", ", arr) : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
        }

        static string Json(object row, string key)
        {
            var d = row as Dictionary<string, object>;
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return "";
            string s = new JavaScriptSerializer().Serialize(v);
            return s.Length > 600 ? s.Substring(0, 600) + "..." : s;
        }

        static Version Ver(string s)
        {
            var m = System.Text.RegularExpressions.Regex.Match(s ?? "", @"(\d+)\.(\d+)\.(\d+)");
            return m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) : new Version(0, 0);
        }

        static DateTime T(object row, string key)
        {
            DateTime t;
            return DateTime.TryParse(S(row, key), null, System.Globalization.DateTimeStyles.RoundtripKind, out t) ? t.ToLocalTime() : DateTime.MinValue;
        }

        void Reload()
        {
            summary.Text = "Loading...";
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    var i = Telemetry.Query("installs?select=*&order=last_seen.desc&limit=5000");
                    var e = Telemetry.Query("events?select=*&order=at.desc&limit=500");
                    var p = Telemetry.Query("events?select=*&kind=eq.issue&order=at.desc&limit=3000");
                    BeginInvoke((Action)(() => { installs = i; events = e; issueEvents = p; Fill(); }));
                }
                catch (Exception ex)
                {
                    try { BeginInvoke((Action)(() => summary.Text = "Error: " + ex.Message)); } catch { }
                }
            });
        }

        void Fill()
        {
            var now = DateTime.Now;
            var on = installs.Where(r => now - T(r, "last_seen") < TimeSpan.FromMinutes(2.5)).ToList();
            int today = installs.Count(r => T(r, "last_seen").Date == now.Date);
            int week = installs.Count(r => now - T(r, "last_seen") < TimeSpan.FromDays(7));
            summary.Text = "Online now: " + on.Count + "     Today: " + today + "     7 days: " + week + "     All users: " + installs.Length +
                           "     (updated " + now.ToString("HH:mm:ss") + ")";

            online.BeginUpdate();
            online.Items.Clear();
            foreach (var r in on)
                online.Items.Add(new ListViewItem(new[] { S(r, "nickname"), S(r, "device_name"), S(r, "city"), S(r, "country"),
                    S(r, "current_game"), S(r, "current_server"), S(r, "current_ping"), S(r, "app_version"), I(r, "net"), S(r, "isp"),
                    T(r, "last_seen").ToString("HH:mm:ss") }));
            online.EndUpdate();

            // who has updated: users active in the last 7 days, by version (newest first)
            var active = installs.Where(r => now - T(r, "last_seen") < TimeSpan.FromDays(7)).ToList();
            versions.BeginUpdate();
            versions.Items.Clear();
            var byVer = active.GroupBy(r => S(r, "app_version")).OrderByDescending(g => Ver(g.Key)).ToList();
            for (int k = 0; k < byVer.Count; k++)
            {
                var g = byVer[k];
                var item = new ListViewItem(new[] { g.Key, g.Count().ToString(), (100.0 * g.Count() / Math.Max(1, active.Count)).ToString("0") + "%",
                                                    k == 0 ? "latest" : "needs to update" });
                item.ForeColor = k == 0 ? MainForm.Good : MainForm.Warn;
                versions.Items.Add(item);
            }
            versions.EndUpdate();

            devices.BeginUpdate();
            devices.Items.Clear();
            foreach (var r in installs.Where(r => I(r, "cpu") != "" || I(r, "gpu") != ""))
                devices.Items.Add(new ListViewItem(new[] { S(r, "nickname"), I(r, "windows"), I(r, "cpu"), I(r, "gpu"), I(r, "ram_gb"),
                    I(r, "screen") + (I(r, "scale") != "" ? " @" + I(r, "scale") + "%" : ""), I(r, "net"), S(r, "isp"),
                    I(r, "games_installed"), T(r, "last_seen").ToString("MM-dd HH:mm") }));
            devices.EndUpdate();

            stats.BeginUpdate();
            stats.Items.Clear();
            Action<string, IEnumerable<string>> top = (what, values) =>
            {
                foreach (var g in values.Where(v => v != "").GroupBy(v => v).OrderByDescending(g => g.Count()).Take(10))
                    stats.Items.Add(new ListViewItem(new[] { what, g.Key, g.Count().ToString() }));
            };
            top("Country", installs.Select(r => S(r, "country")));
            top("City", installs.Select(r => S(r, "city")));
            top("Game (users)", installs.Select(r => S(r, "current_game")));
            top("Server locked", events.Where(r => S(r, "kind") == "lock").Select(r => S(r, "server")));
            top("Server detected", events.Where(r => S(r, "kind") == "detect").Select(r => S(r, "server")));
            top("Version", installs.Select(r => S(r, "app_version")));
            top("Language", installs.Select(r => S(r, "lang")));
            top("Layout", installs.Select(r => S(r, "mode")));
            top("Event", events.Select(r => S(r, "kind")));
            top("ISP", installs.Select(r => S(r, "isp")));
            top("Connection", installs.Select(r => I(r, "net")));
            top("GPU", installs.Select(r => I(r, "gpu")));
            top("CPU", installs.Select(r => I(r, "cpu")));
            top("RAM (GB)", installs.Select(r => I(r, "ram_gb")));
            top("Screen", installs.Select(r => I(r, "screen")));
            top("Windows", installs.Select(r => I(r, "windows")));
            top("Game installed", installs.SelectMany(r => I(r, "games_installed").Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries)));
            double m;
            var mins = events.Where(r => S(r, "kind") == "exit")
                .Select(r => double.TryParse(Field(r, "data", "minutes"), System.Globalization.NumberStyles.Float,
                                             System.Globalization.CultureInfo.InvariantCulture, out m) ? m : -1)
                .Where(x => x >= 0).ToList();
            if (mins.Count > 0)
                stats.Items.Add(new ListViewItem(new[] { "Average session", mins.Average().ToString("0.0") + " min", mins.Count + " sessions" }));
            stats.EndUpdate();

            FillLog();
            FillIssues();
        }

        // Problems the apps found by themselves (Issues), grouped by kind: how often, how many players, latest example.
        void FillIssues()
        {
            issues.BeginUpdate();
            issues.Items.Clear();
            Func<object, string> code = r =>
            {
                string c = Field(r, "data", "code");
                if (c != "") return c;
                string d = S(r, "detail");
                int i = d.IndexOf(':');
                return i > 0 ? d.Substring(0, i) : d;
            };
            foreach (var grp in issueEvents.GroupBy(code).OrderByDescending(g => g.Count()))
            {
                var latest = grp.OrderByDescending(r => T(r, "at")).First();
                string what;
                if (!Issues.Codes.TryGetValue(grp.Key, out what)) what = grp.Key;
                var item = new ListViewItem(new[] { what, grp.Key, grp.Count().ToString(),
                    grp.Select(r => S(r, "install_id")).Distinct().Count().ToString(),
                    string.Join(", ", grp.Select(r => S(r, "app_version")).Distinct().Take(3)),
                    T(latest, "at").ToString("MM-dd HH:mm"), S(latest, "detail").Replace("\r", " ").Replace("\n", " ") });
                item.ForeColor = grp.Key == "crash" || grp.Key == "lock_outside" || grp.Key == "lock_rules_missing" ? MainForm.Bad : MainForm.Warn;
                issues.Items.Add(item);
            }
            issues.EndUpdate();
        }

        void FillLog()
        {
            var names = installs.ToDictionary(r => S(r, "install_id"), r => S(r, "nickname"));
            log.BeginUpdate();
            log.Items.Clear();
            foreach (var r in events.Where(r => !errorsOnly.Checked || S(r, "kind") == "error" || S(r, "kind") == "issue"))
            {
                string n;
                names.TryGetValue(S(r, "install_id"), out n);
                var item = new ListViewItem(new[] { T(r, "at").ToString("MM-dd HH:mm:ss"), n ?? "?", S(r, "kind"), S(r, "game"),
                    S(r, "server"), S(r, "ping"), S(r, "detail").Replace("\r", " ").Replace("\n", " "), Json(r, "data") });
                if (S(r, "kind") == "error") item.ForeColor = MainForm.Bad;
                log.Items.Add(item);
            }
            log.EndUpdate();
        }
    }
}
