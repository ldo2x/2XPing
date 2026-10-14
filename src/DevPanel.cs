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
        TabControl tabs;
        TabPage dashPage;
        public event Action ForgetKey;

        public DevPanel()
        {
            BackColor = Theme.Bg;
            ForeColor = Theme.TextC;
            Font = Fonts.Ui(9.5f);
            RightToLeft = RightToLeft.No; // developer tools stay in English, left to right

            var top = new Panel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(10, 8, 10, 8) };
            summary = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = Fonts.Semi(11f), ForeColor = Theme.Accent };
            var btn = new RoundButton { Text = "Refresh", Dock = DockStyle.Right, Width = 100, FlatStyle = FlatStyle.Flat, BackColor = Theme.Panel2 };
            btn.FlatAppearance.BorderSize = 0;
            btn.Click += delegate { Reload(); };
            var forget = new RoundButton { Text = "Forget key", Dock = DockStyle.Right, Width = 100, FlatStyle = FlatStyle.Flat, BackColor = Theme.Panel2 };
            forget.FlatAppearance.BorderSize = 0;
            forget.Click += delegate { Telemetry.DevKey = null; var h = ForgetKey; if (h != null) h(); };
            top.Controls.Add(summary);
            top.Controls.Add(forget);
            top.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
            top.Controls.Add(btn);

            tabs = new TabControl { Dock = DockStyle.Fill };
            // the dashboard first: counters and charts from one dev_stats() call (it loads and refreshes by itself)
            dashPage = new TabPage(Lang.T("Dashboard", "لوحة الأرقام")) { BackColor = Theme.Bg };
            dashPage.Controls.Add(new DevDashboard { Dock = DockStyle.Fill });
            tabs.TabPages.Add(dashPage);
            online = List("Name", 120, "Device", 120, "City", 100, "Country", 100, "Game", 120, "Server", 160, "Ping", 55, "Version", 90,
                          "Net", 70, "ISP", 150, "Last seen", 90);
            stats = List("What", 200, "Value", 420, "Count", 90);
            versions = List("Version", 160, "Users (7 days)", 120, "Share", 90, "", 300);
            devices = List("Name", 120, "Windows", 200, "CPU", 220, "GPU", 200, "RAM", 55, "Screen", 110, "Net", 80, "ISP", 150,
                           "Games installed", 300, "Last seen", 110);
            log = List("Time", 120, "Name", 110, "Kind", 70, "Game", 110, "Server", 150, "Ping", 50, "Detail", 300, "Data", 400);
            issues = List("Problem", 300, "Code", 150, "Times", 60, "Players", 65, "Versions", 140, "Last seen", 110, "Latest example", 520);
            errorsOnly = new CheckBox { Text = "Errors only", Dock = DockStyle.Top, Height = 28, ForeColor = Theme.TextC };
            errorsOnly.CheckedChanged += delegate { FillLog(); };
            var logPage = new TabPage("Log") { BackColor = Theme.Bg };
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

            // the lists refresh every 30 s while one of them is on screen (not while the dashboard is: it has its own pace)
            refresh = new System.Windows.Forms.Timer { Interval = 30000 };
            refresh.Tick += delegate { if (Visible && ListsShown) Reload(); };
            refresh.Start();
            VisibleChanged += delegate { LoadListsOnce(); };
            // the summary and Refresh above the tabs are the lists': hidden while the dashboard (with its own) is shown
            top.Visible = ListsShown;
            tabs.SelectedIndexChanged += delegate { top.Visible = ListsShown; LoadListsOnce(); };
            Disposed += delegate { refresh.Dispose(); };
        }

        bool ListsShown { get { return tabs.SelectedTab != dashPage; } }

        void LoadListsOnce()
        {
            if (Visible && ListsShown && !loadedOnce) { loadedOnce = true; Reload(); }
        }

        static TabPage Page(string title, Control c)
        {
            var p = new TabPage(title) { BackColor = Theme.Bg };
            p.Controls.Add(c);
            return p;
        }

        static ListView List(params object[] cols)
        {
            var lv = new ListView
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, BorderStyle = BorderStyle.None,
                BackColor = Theme.Panel1, ForeColor = Theme.TextC, GridLines = false
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
                    try
                    {
                        BeginInvoke((Action)(() =>
                        {
                            summary.Text = "Error: " + ex.Message;
                            if (issues.Items.Count == 0) NoData(issues, "No data (couldn't load)");
                        }));
                    }
                    catch { }
                }
            });
        }

        void Fill()
        {
            var now = DateTime.Now;
            var on = installs.Where(r => now - T(r, "last_seen") < Telemetry.OnlineWindow).ToList();
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
                item.ForeColor = k == 0 ? Theme.Good : Theme.Warn;
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
                item.ForeColor = grp.Key == "crash" || grp.Key == "lock_outside" || grp.Key == "lock_rules_missing" ? Theme.Bad : Theme.Warn;
                issues.Items.Add(item);
            }
            if (issues.Items.Count == 0) NoData(issues, "No problems reported");
            issues.EndUpdate();
        }

        // An empty grid says why it is empty, in one dim row.
        static void NoData(ListView lv, string text)
        {
            lv.Items.Add(new ListViewItem(text) { ForeColor = Theme.Dim });
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
                if (S(r, "kind") == "error") item.ForeColor = Theme.Bad;
                log.Items.Add(item);
            }
            log.EndUpdate();
        }
    }
}
