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
        static string country, city;

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

        public static void Start()
        {
            Send("start", null, -1, Environment.OSVersion.ToString());
            if (heartbeat == null) heartbeat = new System.Threading.Timer(delegate { Send("heartbeat", null, -1, null); }, null, 60000, 60000);
        }

        // City and country from the user's internet address, looked up once a day (the address itself is not stored).
        static void EnsureLocation()
        {
            string today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            if (Storage.GetSetting("geo_at") == today)
            {
                country = Storage.GetSetting("geo_country");
                city = Storage.GetSetting("geo_city");
                return;
            }
            try
            {
                var d = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(
                    Http.Get("http://ip-api.com/json/?fields=status,country,city", 6000));
                if ((string)d["status"] != "success") return;
                country = (string)d["country"];
                city = (string)d["city"];
                Storage.SetSetting("geo_country", country);
                Storage.SetSetting("geo_city", city);
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
            if (!Configured || !HasConsent) return;
            string game = Game, srv = server ?? Server;
            int pg = ping >= 0 ? ping : Ping;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    EnsureLocation();
                    var p = new Dictionary<string, object>
                    {
                        { "install_id", InstallId }, { "kind", kind }, { "nickname", Nickname },
                        { "device_name", Environment.MachineName }, { "app_version", Brand.DisplayVersion },
                        { "os", Environment.OSVersion.Version.ToString() }, { "lang", Lang.Ar ? "ar" : "en" },
                        { "mode", Prefs.Simple ? "simple" : "advanced" }, { "country", country }, { "city", city },
                        { "game", game }, { "server", srv }, { "ping", pg >= 0 ? (object)pg : null }, { "detail", detail }
                    };
                    string body = "{\"p\":" + new JavaScriptSerializer().Serialize(p) + "}";
                    Http.Send("POST", SupabaseUrl.TrimEnd('/') + "/rest/v1/rpc/checkin", body, Headers(SupabaseKey), 8000);
                }
                catch { }
            });
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
                f.Font = new Font("Segoe UI", 10f);
                f.RightToLeft = Lang.Ar ? RightToLeft.Yes : RightToLeft.No;
                const int W = 480;

                f.Controls.Add(new PictureBox { Image = Brand.Logo, SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(228, 14, 64, 64) });
                f.Controls.Add(new Label
                {
                    Text = Lang.T("Welcome to 2X Ping ✦", "يا هلا فيك في 2X Ping ✦"), Font = new Font("Segoe UI Semibold", 15f),
                    ForeColor = MainForm.Accent, TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(20, 82, W, 36)
                });
                f.Controls.Add(new Label
                {
                    Text = Lang.T("What should we call you?", "إيش اسمك؟ (عشان نناديك)"),
                    Bounds = new Rectangle(20, 126, W, 24), ForeColor = MainForm.TextC
                });
                var name = new TextBox
                {
                    Bounds = new Rectangle(20, 152, W, 28), BackColor = MainForm.Panel2, ForeColor = MainForm.TextC,
                    BorderStyle = BorderStyle.FixedSingle, MaxLength = 30, Text = Telemetry.Nickname
                };
                f.Controls.Add(name);

                // the games this player cares about, two per row, in reading order
                f.Controls.Add(new Label
                {
                    Text = Lang.T("Which games do you play?", "إيش الألعاب اللي تلعبها؟"),
                    Bounds = new Rectangle(20, 196, W, 24), ForeColor = MainForm.TextC
                });
                f.Controls.Add(new Label
                {
                    Text = Lang.T("Only these show up in your list. You can change them any time in Settings.",
                                  "بس هادي اللي تطلع لك في القائمة، وتقدر تغيّرها أي وقت من الإعدادات."),
                    Bounds = new Rectangle(20, 220, W, 20), ForeColor = MainForm.Dim, Font = new Font("Segoe UI", 8.5f)
                });
                var picks = new List<KeyValuePair<string, CheckBox>>();
                var all = GameDb.All();
                const int colW = (W - 20) / 2;
                for (int i = 0; i < all.Count; i++)
                {
                    bool first = i % 2 == 0;
                    int gx = first ^ Lang.Ar ? 20 : 20 + colW + 20;
                    var cb = new CheckBox
                    {
                        Text = "  " + all[i].Name, ForeColor = MainForm.TextC, Cursor = Cursors.Hand,
                        Bounds = new Rectangle(gx, 246 + i / 2 * 30, colW, 30),
                        TextImageRelation = TextImageRelation.ImageBeforeText, ImageAlign = ContentAlignment.MiddleLeft
                    };
                    var ic = Brand.GameIcon(all[i].Icon);
                    if (ic != null) cb.Image = new Bitmap(ic, 20, 20);
                    f.Controls.Add(cb);
                    picks.Add(new KeyValuePair<string, CheckBox>(all[i].Icon, cb));
                }
                int y = 246 + (all.Count + 1) / 2 * 30 + 12;

                var notice = new Label
                {
                    Bounds = new Rectangle(20, y, W, 70), BackColor = MainForm.Panel1, ForeColor = MainForm.Dim,
                    Padding = new Padding(12), Font = new Font("Segoe UI", 9.5f),
                    Text = Lang.T(
                        "This is a beta. To improve it, 2X Ping sends usage statistics while it is open.\nAccepting is required to use the beta.",
                        "هادي نسخة تجريبية، وعشان نطوّرها البرنامج يرسل إحصائيات استخدام وهو مفتوح.\nالموافقة شرط لاستخدام النسخة التجريبية.")
                };
                f.Controls.Add(notice);
                var details = new LinkLabel
                {
                    Text = Lang.T("Statistics details", "تفاصيل الإحصائيات"), Bounds = new Rectangle(20, y + 74, W, 24),
                    LinkColor = MainForm.Lavender, ActiveLinkColor = Color.White, LinkBehavior = LinkBehavior.HoverUnderline
                };
                details.LinkClicked += delegate { Brand.Open(Brand.RepoUrl + "/blob/main/SECURITY.md#usage-statistics-beta"); };
                f.Controls.Add(details);

                var ok = new Button
                {
                    Text = Lang.T("Agree and start", "موافق وأبدأ"), DialogResult = DialogResult.OK,
                    Bounds = new Rectangle(Lang.Ar ? 280 : 20, y + 112, 220, 40), FlatStyle = FlatStyle.Flat,
                    BackColor = MainForm.Accent, ForeColor = Color.Black, Font = new Font("Segoe UI Semibold", 10.5f), Enabled = false
                };
                var exit = new Button
                {
                    Text = Lang.T("Exit", "خروج"), DialogResult = DialogResult.Cancel,
                    Bounds = new Rectangle(Lang.Ar ? 20 : 280, y + 112, 220, 40), FlatStyle = FlatStyle.Flat,
                    BackColor = MainForm.Panel2, ForeColor = MainForm.TextC
                };
                ok.FlatAppearance.BorderSize = exit.FlatAppearance.BorderSize = 0;
                // ready once there is a name and at least one game
                EventHandler refresh = delegate
                {
                    ok.Enabled = name.Text.Trim().Length >= 2 && picks.Any(p => p.Value.Checked);
                    ok.BackColor = ok.Enabled ? MainForm.Accent : MainForm.Panel2;
                };
                name.TextChanged += refresh;
                foreach (var p in picks) p.Value.CheckedChanged += refresh;
                refresh(null, EventArgs.Empty);
                f.Controls.Add(ok);
                f.Controls.Add(exit);
                f.AcceptButton = ok;
                f.CancelButton = exit;
                f.ClientSize = new Size(W + 40, y + 168);

                if (f.ShowDialog(owner) != DialogResult.OK) return false;
                Telemetry.Nickname = name.Text;
                Prefs.HiddenGames = new HashSet<string>(picks.Where(p => !p.Value.Checked).Select(p => p.Key));
                Storage.SetSetting("consent", "1");
                return true;
            }
        }
    }

    // Developer-only window: who is online now, usage stats and the event log.
    // Opened by clicking the logo 5 times; asks for the secret key once and keeps it in dev.key.
    class DevDashboard : Form
    {
        ListView online, stats, log;
        Label summary;
        CheckBox errorsOnly;
        System.Windows.Forms.Timer refresh;
        object[] installs = new object[0], events = new object[0];

        public static void Open(IWin32Window owner)
        {
            if (!Telemetry.Configured)
            {
                MessageBox.Show(owner, "Statistics backend is not configured yet (Telemetry.SupabaseUrl / SupabaseKey).", Brand.Name);
                return;
            }
            if (string.IsNullOrEmpty(Telemetry.DevKey))
            {
                var v = InputDialog.Show(owner, "Developer key", new[] { "Supabase secret key (stays only on this PC, in dev.key)" }, new[] { "" });
                if (v == null || v[0] == "") return;
                Telemetry.DevKey = v[0];
            }
            new DevDashboard().Show(owner);
        }

        DevDashboard()
        {
            Text = Brand.Caption + " - Developer";
            ClientSize = new Size(1180, 720);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = MainForm.Bg;
            ForeColor = MainForm.TextC;
            Font = new Font("Segoe UI", 9.5f);

            var top = new Panel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(10, 8, 10, 8) };
            summary = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI Semibold", 11f), ForeColor = MainForm.Accent };
            var btn = new Button { Text = "Refresh", Dock = DockStyle.Right, Width = 100, FlatStyle = FlatStyle.Flat, BackColor = MainForm.Panel2 };
            btn.FlatAppearance.BorderSize = 0;
            btn.Click += delegate { Reload(); };
            var forget = new Button { Text = "Forget key", Dock = DockStyle.Right, Width = 100, FlatStyle = FlatStyle.Flat, BackColor = MainForm.Panel2 };
            forget.FlatAppearance.BorderSize = 0;
            forget.Click += delegate { Telemetry.DevKey = null; Close(); };
            top.Controls.Add(summary);
            top.Controls.Add(forget);
            top.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
            top.Controls.Add(btn);

            var tabs = new TabControl { Dock = DockStyle.Fill };
            online = List("Name", 130, "Device", 130, "City", 110, "Country", 110, "Game", 130, "Server", 170, "Ping", 60, "Version", 90, "Last seen", 120);
            stats = List("What", 200, "Value", 360, "Count", 90);
            log = List("Time", 140, "Name", 120, "Kind", 80, "Game", 120, "Server", 160, "Ping", 60, "Detail", 460);
            errorsOnly = new CheckBox { Text = "Errors only", Dock = DockStyle.Top, Height = 28, ForeColor = MainForm.TextC };
            errorsOnly.CheckedChanged += delegate { FillLog(); };
            var logPage = new TabPage("Log") { BackColor = MainForm.Bg };
            logPage.Controls.Add(log);
            logPage.Controls.Add(errorsOnly);
            tabs.TabPages.Add(Page("Online now", online));
            tabs.TabPages.Add(Page("Statistics", stats));
            tabs.TabPages.Add(logPage);

            Controls.Add(tabs);
            Controls.Add(top);

            refresh = new System.Windows.Forms.Timer { Interval = 30000 };
            refresh.Tick += delegate { Reload(); };
            refresh.Start();
            FormClosed += delegate { refresh.Stop(); };
            Shown += delegate { Reload(); };
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
                    BeginInvoke((Action)(() => { installs = i; events = e; Fill(); }));
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
                    S(r, "current_game"), S(r, "current_server"), S(r, "current_ping"), S(r, "app_version"), T(r, "last_seen").ToString("HH:mm:ss") }));
            online.EndUpdate();

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
            stats.EndUpdate();

            FillLog();
        }

        void FillLog()
        {
            var names = installs.ToDictionary(r => S(r, "install_id"), r => S(r, "nickname"));
            log.BeginUpdate();
            log.Items.Clear();
            foreach (var r in events.Where(r => !errorsOnly.Checked || S(r, "kind") == "error"))
            {
                string n;
                names.TryGetValue(S(r, "install_id"), out n);
                var item = new ListViewItem(new[] { T(r, "at").ToString("MM-dd HH:mm:ss"), n ?? "?", S(r, "kind"), S(r, "game"),
                    S(r, "server"), S(r, "ping"), S(r, "detail").Replace("\r", " ").Replace("\n", " ") });
                if (S(r, "kind") == "error") item.ForeColor = MainForm.Bad;
                log.Items.Add(item);
            }
            log.EndUpdate();
        }
    }
}
