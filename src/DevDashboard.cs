using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace PingBoost
{
    // A counter tile: the big number, what it counts, and the change against the period before.
    class DashTile : Control
    {
        public string Value = "-", Caption = "", DeltaText = "";
        public Color DeltaColor = Theme.Dim, ValueColor = Theme.TextC;

        public DashTile()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            Font = Fonts.Ui(8.5f);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Shapes.Behind(this));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Shapes.Round(new RectangleF(0, 0, Width - 1, Height - 1), 12))
            using (var b = new SolidBrush(Theme.Panel1)) g.FillPath(b, path);
            bool rtl = RightToLeft == RightToLeft.Yes;
            var flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter |
                        (rtl ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left);
            int w = Width - 24;
            using (var f = Fonts.Semi(18f))
                TextRenderer.DrawText(g, Value, f, new Rectangle(12, 8, w, 34), ValueColor, flags);
            TextRenderer.DrawText(g, Caption, Font, new Rectangle(12, 44, w, 18), Theme.Dim, flags);
            if (DeltaText != "")
                TextRenderer.DrawText(g, DeltaText, Font, new Rectangle(12, 63, w, 18), DeltaColor, flags);
        }
    }

    // «لوحة الأرقام» / Dashboard: the developer's counters and charts, from one dev_stats() call
    // (tools/supabase-dev-stats.sql) with the secret key. The answer is kept for 5 minutes, and it only refreshes
    // by itself while the page is on screen, at most once every 5 minutes, so the app stays light.
    class DevDashboard : Panel
    {
        static string T(string en, string ar) { return Lang.T(en, ar); }

        static readonly int[] Ranges = { 7, 30, 90 };
        static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);
        // kept across developer-mode toggles: one answer per period
        static readonly Dictionary<int, DashStats> cache = new Dictionary<int, DashStats>();
        static readonly Dictionary<int, DateTime> fetched = new Dictionary<int, DateTime>();

        readonly Segmented range;
        readonly RoundButton refreshBtn;
        readonly Label status;
        readonly Panel body;
        readonly System.Windows.Forms.Timer timer;
        readonly List<Control> laidOut = new List<Control>();
        bool loading;
        DashStats shown;

        static readonly Color[] Palette =
        {
            Theme.Accent, Theme.Lavender, Theme.Good, Theme.Warn, Color.FromArgb(120, 190, 255), Color.FromArgb(255, 150, 120),
            Color.FromArgb(200, 140, 255), Color.FromArgb(150, 220, 120)
        };

        int Days { get { return Ranges[Math.Max(0, Math.Min(Ranges.Length - 1, range.SelectedIndex))]; } }

        public DevDashboard()
        {
            BackColor = Theme.Bg;
            ForeColor = Theme.TextC;
            Font = Fonts.Ui(9.5f);
            RightToLeft = Lang.Ar ? RightToLeft.Yes : RightToLeft.No;

            var top = new Panel { Dock = DockStyle.Top, Height = 52, Padding = new Padding(16, 8, 16, 8) };
            range = new Segmented
            {
                Items = new[] { T("7 days", "7 أيام"), T("30 days", "30 يوم"), T("90 days", "90 يوم") },
                Dock = Lang.Ar ? DockStyle.Left : DockStyle.Right, Width = 260, Height = 36, Font = Fonts.Ui(9f)
            };
            range.SelectedIndex = 1;
            range.SelectedIndexChanged += delegate { Load(false); };
            refreshBtn = new RoundButton { Text = T("Refresh", "حدّث"), Dock = Lang.Ar ? DockStyle.Left : DockStyle.Right, Width = 96, BackColor = Theme.Panel2 };
            refreshBtn.Click += delegate { Load(true); };
            status = new Label { Dock = DockStyle.Fill, TextAlign = Lang.Ar ? ContentAlignment.MiddleRight : ContentAlignment.MiddleLeft, ForeColor = Theme.Dim, Font = Fonts.Ui(9f) };
            var title = new Label { Text = T("Dashboard", "لوحة الأرقام"), Dock = Lang.Ar ? DockStyle.Right : DockStyle.Left, AutoSize = false, Width = 170,
                                    Font = Fonts.Semi(13f), ForeColor = Theme.Accent,
                                    TextAlign = Lang.Ar ? ContentAlignment.MiddleRight : ContentAlignment.MiddleLeft };
            top.Controls.Add(status);
            top.Controls.Add(title);
            top.Controls.Add(range);
            top.Controls.Add(new Panel { Dock = Lang.Ar ? DockStyle.Left : DockStyle.Right, Width = 8 });
            top.Controls.Add(refreshBtn);

            body = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg };
            body.Resize += delegate { Layout2(); };
            Controls.Add(body);
            Controls.Add(top);

            timer = new System.Windows.Forms.Timer { Interval = (int)MaxAge.TotalMilliseconds };
            timer.Tick += delegate { if (Visible) Load(false); };
            timer.Start();
            VisibleChanged += delegate { if (Visible) Load(false); };
            Disposed += delegate { timer.Dispose(); };
            Show(null);
        }

        // ------------------------------------------------------------------ loading

        void Load(bool force)
        {
            int days = Days;
            DashStats s;
            DateTime at;
            bool fresh = cache.TryGetValue(days, out s) && fetched.TryGetValue(days, out at) && DateTime.UtcNow - at < MaxAge;
            if (s != null) Show(s);
            if ((fresh && !force) || loading) return;
            string key = Telemetry.DevKey;
            if (string.IsNullOrEmpty(key)) { status.Text = T("No secret key on this PC.", "ما فيه مفتاح سري على هالجهاز."); return; }
            loading = true;
            refreshBtn.Enabled = false;
            status.Text = T("Loading...", "جاري التحميل...");
            int tz = (int)Math.Round(TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes);
            ThreadPool.QueueUserWorkItem(delegate
            {
                DashStats got = null;
                string error = null;
                try
                {
                    string body = "{\"p_days\":" + days + ",\"p_tz_minutes\":" + tz + "}";
                    string json = Http.Send("POST", Telemetry.SupabaseUrl.TrimEnd('/') + "/rest/v1/rpc/dev_stats", body, Telemetry.Headers(key), 20000);
                    got = DashStats.Parse(json);
                }
                catch (WebException ex)
                {
                    var r = ex.Response as HttpWebResponse;
                    error = r != null && r.StatusCode == HttpStatusCode.NotFound
                        ? T("dev_stats() isn't in the database yet: run tools/supabase-dev-stats.sql in Supabase.",
                            "dev_stats() مو موجودة في القاعدة لسا: شغّل tools/supabase-dev-stats.sql في Supabase.")
                        : T("Couldn't load: ", "ما قدرنا نحمّل: ") + ex.Message;
                }
                catch (Exception ex) { error = T("Couldn't read the answer: ", "ما قدرنا نقرا الرد: ") + ex.Message; }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        loading = false;
                        refreshBtn.Enabled = true;
                        if (got != null)
                        {
                            cache[days] = got;
                            fetched[days] = DateTime.UtcNow;
                            if (days == Days) Show(got);
                        }
                        else status.Text = error;
                    }));
                }
                catch { } // the page was closed meanwhile
            });
        }

        // ------------------------------------------------------------------ building the page

        void Show(DashStats s)
        {
            shown = s;
            if (s != null)
            {
                DateTime at;
                fetched.TryGetValue(s.Days, out at);
                status.Text = T("Updated ", "آخر تحديث ") + (at == DateTime.MinValue ? s.GeneratedAt : at).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) +
                              "  ·  " + T("last " + s.Days + " days", "آخر " + s.Days + " يوم");
            }
            Build(s ?? new DashStats { Days = Days });
        }

        void Build(DashStats s)
        {
            body.SuspendLayout();
            foreach (var c in laidOut) c.Dispose();
            laidOut.Clear();
            bool empty = shown == null;

            // counters
            Add(Section(T("Users", "المستخدمين")));
            Tile(s, "online_now", T("Online now", "متصلين دحين"), null, empty);
            Tile(s, "installs_total", T("All installs", "كل التثبيتات"), null, empty);
            Tile(s, "active_today", T("Active today", "نشطين اليوم"), T("vs yesterday", "عن أمس"), empty);
            Tile(s, "active_7d", T("Active 7 days", "نشطين 7 أيام"), T("vs the 7 days before", "عن الأسبوع اللي قبله"), empty);
            Tile(s, "active_30d", T("Active 30 days", "نشطين 30 يوم"), null, empty);
            Tile(s, "new_today", T("New today", "جدد اليوم"), T("vs yesterday", "عن أمس"), empty);
            Tile(s, "new_7d", T("New 7 days", "جدد 7 أيام"), T("vs the 7 days before", "عن الأسبوع اللي قبله"), empty);
            Tile(s, "matches_today", T("Matches today", "مباريات اليوم"), T("vs yesterday", "عن أمس"), empty);
            Tile(s, "locks_today", T("Locks today", "تثبيتات اليوم"), T("vs yesterday", "عن أمس"), empty);
            Tile(s, "issues_24h", T("Problems 24 h", "مشاكل آخر 24 ساعة"), T("vs the 24 h before", "عن الـ24 ساعة اللي قبلها"), empty, true);

            var days = s.Series;
            Func<Func<DashDay, long>, double[]> col = f => days.Select(d => (double)f(d)).ToArray();
            var labels = days.Select(d => d.Day.ToString(Lang.Ar ? "d/M" : "MMM d", CultureInfo.InvariantCulture)).ToArray();

            var users = new LineChart { Title = T("Active and new installs per day", "النشطين والجدد كل يوم"), XLabels = labels };
            users.Lines.Add(new LineChart.Series { Name = T("Active", "نشطين"), Color = Theme.Accent, Values = col(d => d.Active) });
            users.Lines.Add(new LineChart.Series { Name = T("New", "جدد"), Color = Theme.Good, Values = col(d => d.New) });
            Add(users, "full", 240);

            AddPair(Breakdown(s, "version", T("App version", "نسخة البرنامج"), null),
                    Breakdown(s, "country", T("Country", "الدولة"), null));
            AddPair(Breakdown(s, "game", T("Games used", "الألعاب المستخدمة"), null),
                    Breakdown(s, "lock_region", T("Regions picked to lock", "المناطق اللي ثبّتوا عليها"), null));
            AddPair(Breakdown(s, "mode", T("Layout", "الواجهة"), Words("simple", T("Simple", "بسيطة"), "advanced", T("Advanced", "متقدمة"))),
                    Breakdown(s, "lang", T("Language", "اللغة"), Words("ar", "عربي", "en", "English")));
            AddPair(Breakdown(s, "os", T("Windows", "الويندوز"), null), null);

            // matches
            Add(Section(T("Matches", "المباريات")));
            var act = new LineChart { Title = T("Matches and locks per day", "المباريات والتثبيتات كل يوم"), XLabels = labels };
            act.Lines.Add(new LineChart.Series { Name = T("Matches", "مباريات"), Color = Theme.Lavender, Values = col(d => d.Matches) });
            act.Lines.Add(new LineChart.Series { Name = T("Locks", "تثبيتات"), Color = Theme.Warn, Values = col(d => d.Locks) });
            Add(act, "full", 220);

            var donut = new DonutChart { Title = T("Did the lock hold? (match reports)", "التثبيت مسك؟ (تقارير المباريات)"),
                                         CenterText = DashFmt.Count(s.MatchTotal), CenterSub = T("matches", "مباراة") };
            foreach (var v in s.Verdicts)
                donut.Slices.Add(new DonutChart.Slice { Label = VerdictName(v.Key), Value = v.Count, Color = VerdictColor(v.Key) });
            var sources = Bars(T("How the match server was found", "كيف عرفنا سيرفر المباراة"),
                               s.Sources.Select(x => Bar(SourceName(x.Key), x.Pct, DashFmt.Pct(x.Pct) + "  " + DashFmt.Count(x.Count), Theme.Lavender)), 100);
            AddPair(donut, sources, 200);

            var inside = Bars(T("Inside the lock, per picked region", "داخل التثبيت حسب المنطقة"),
                              s.Regions.Where(r => !double.IsNaN(r.InsidePct))
                                       .Select(r => Bar(RegionName(r.Region), r.InsidePct, DashFmt.Pct(r.InsidePct) + "  /" + DashFmt.Count(r.Matches),
                                                        r.InsidePct >= 95 ? Theme.Good : r.InsidePct >= 80 ? Theme.Warn : Theme.Bad)), 100);
            var queues = s.Regions.Where(r => !double.IsNaN(r.QueueMedianS)).ToList();
            var queue = Bars(T("Median queue per picked region (min:s)", "متوسط الانتظار حسب المنطقة (دقيقة:ثانية)"),
                             queues.Select(r => Bar(RegionName(r.Region), r.QueueMedianS, DashFmt.Duration(r.QueueMedianS), Theme.Accent)), double.NaN);
            AddPair(inside, queue);

            var scatter = new ScatterChart
            {
                Title = T("Expected vs actual ping", "البنق المتوقع والحقيقي") + (s.PingN > 0
                    ? "  ·  " + T("off by ", "الفرق ") + DashFmt.Ms(s.AvgDiff, true) + T(" on average", " بالمتوسط") : ""),
                Points = s.Pairs, XName = T("expected (ms)", "المتوقع (ms)"), YName = T("actual (ms)", "الحقيقي (ms)")
            };
            var buckets = DashMath.DiffBuckets(s.Pairs);
            var diff = new ColumnChart
            {
                Title = T("How far off the expected ping was (ms)", "كم بعد البنق الحقيقي عن المتوقع (ms)"),
                Labels = DashMath.DiffBucketLabels, Values = buckets.Select(b => (double)b).ToArray(),
                Colors = new[] { Theme.Lavender, Theme.Lavender, Theme.Good, Theme.Good, Theme.Good, Theme.Warn, Theme.Bad, Theme.Bad }
            };
            AddPair(scatter, diff, 250);

            var dcs = Bars(T("Datacenters seen per picked region", "الداتا سنترات اللي طلعت حسب المنطقة") +
                           (s.UnknownDc > 0 ? "  ·  " + T("unknown to the app: ", "مو معروفة للبرنامج: ") + s.UnknownDc : ""),
                           s.Dcs.Select(d => Bar(RegionName(d.Region) + " → " + d.Dc, d.Count, DashFmt.Count(d.Count),
                                                 d.Known ? Theme.Lavender : Theme.Bad)), double.NaN);
            dcs.LabelW = 190;
            Add(dcs, "full", dcs.PreferredHeightFor(Math.Min(18, s.Dcs.Count)));

            // problems
            Add(Section(T("Problems", "المشاكل")));
            var prob = new LineChart { Title = T("Errors and problem reports per day", "الأخطاء وتقارير المشاكل كل يوم"), XLabels = labels };
            prob.Lines.Add(new LineChart.Series { Name = T("Problems", "مشاكل"), Color = Theme.Warn, Values = col(d => d.Issues) });
            prob.Lines.Add(new LineChart.Series { Name = T("Errors", "أخطاء"), Color = Theme.Bad, Values = col(d => d.Errors) });
            var issues = Bars(T("Top problems, last 7 days", "أكثر المشاكل، آخر 7 أيام"),
                              s.Issues.Select(i => Bar(i.Code, i.Count, DashFmt.Count(i.Count) + " · " + i.Players + T(" pl.", " لاعب"),
                                                       i.Code == "crash" || i.Code == "lock_outside" ? Theme.Bad : Theme.Warn)), double.NaN);
            issues.LabelW = 170;
            AddPair(prob, issues, Math.Max(220, issues.PreferredHeightFor(Math.Min(15, s.Issues.Count))));

            body.ResumeLayout();
            Layout2();
        }

        // ------------------------------------------------------------------ pieces

        // controls in order, each with how it is placed: "tile", "section", "full", "half" (two side by side)
        readonly Dictionary<Control, string> place = new Dictionary<Control, string>();
        readonly Dictionary<Control, int> heights = new Dictionary<Control, int>();

        void Add(Control c) { Add(c, "section", 34); }

        void Add(Control c, string how, int height)
        {
            c.RightToLeft = RightToLeft;
            place[c] = how;
            heights[c] = height;
            laidOut.Add(c);
            body.Controls.Add(c);
        }

        void AddPair(Control a, Control b) { AddPair(a, b, 0); }

        // two charts side by side (one under the other when narrow), as tall as the taller one needs
        void AddPair(Control a, Control b, int height)
        {
            int h = height;
            if (h == 0)
            {
                var ba = a as BarList; var bb = b as BarList;
                h = Math.Max(ba != null ? ba.PreferredHeightFor(Math.Min(10, ba.Bars.Count)) : 160,
                             bb != null ? bb.PreferredHeightFor(Math.Min(10, bb.Bars.Count)) : 0);
                h = Math.Max(h, 120);
            }
            Add(a, "half", h);
            if (b != null) Add(b, "half2", h);
        }

        Label Section(string text)
        {
            return new Label { Text = text, Font = Fonts.Semi(12f), ForeColor = Theme.Lavender, AutoSize = false,
                               TextAlign = Lang.Ar ? ContentAlignment.BottomRight : ContentAlignment.BottomLeft };
        }

        void Tile(DashStats s, string key, string caption, string deltaWhat, bool empty, bool lowerIsBetter = false)
        {
            var t = new DashTile { Caption = caption, Value = empty ? "-" : DashFmt.Count(s.Counter(key)) };
            if (!empty && deltaWhat != null && s.HasDelta(key))
            {
                long d = s.Delta(key);
                t.DeltaText = DashFmt.Delta(d) + " " + deltaWhat;
                t.DeltaColor = d == 0 ? Theme.Dim : (d > 0) != lowerIsBetter ? Theme.Good : Theme.Bad;
            }
            if (key == "online_now" && !empty) t.ValueColor = Theme.Good;
            Add(t, "tile", 88);
        }

        static BarList.Bar Bar(string label, double value, string text, Color color)
        {
            return new BarList.Bar { Label = label, Value = value, ValueText = text, Color = color };
        }

        static BarList Bars(string title, IEnumerable<BarList.Bar> bars, double max)
        {
            return new BarList { Title = title, Bars = bars.ToList(), Max = max };
        }

        static Dictionary<string, string> Words(params string[] kv)
        {
            var d = new Dictionary<string, string>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
            return d;
        }

        // a breakdown as shares: "42%  12"; for one-per-install breakdowns the rest past the top 15 is "Other"
        static BarList Breakdown(DashStats s, string name, string title, Dictionary<string, string> words)
        {
            var b = s.Breakdown(name);
            var bars = new List<BarList.Bar>();
            int i = 0;
            foreach (var x in b.Items.Take(10))
            {
                string label;
                if (words == null || !words.TryGetValue(x.Key, out label)) label = x.Key == "?" ? T("unknown", "ما ندري") : x.Key;
                bars.Add(Bar(label, x.Pct, DashFmt.Pct(x.Pct) + "  " + DashFmt.Count(x.Count), Palette[i++ % Palette.Length]));
            }
            long rest = b.Other + (b.Multi ? 0 : b.Items.Skip(10).Sum(x => x.Count));
            if (rest > 0)
            {
                double p = DashMath.Percent(rest, b.Total);
                bars.Add(Bar(T("Other", "غيرها"), p, DashFmt.Pct(p) + "  " + DashFmt.Count(rest), Theme.Dim));
            }
            string total = b.Total > 0 ? "  ·  " + DashFmt.Count(b.Total) + (b.Multi ? T(" users, can add past 100%", " مستخدم، ممكن يزيد عن 100%")
                                                                                       : T(" users", " مستخدم")) : "";
            return new BarList { Title = title + total, Bars = bars, Max = 100 };
        }

        static string VerdictName(string k)
        {
            switch (k)
            {
                case "inside": return T("Inside the lock", "داخل التثبيت");
                case "outside": return T("Outside the lock", "برا التثبيت");
                case "outside_gateway": return T("Outside via kept gateway", "برا التثبيت عن طريق بوابة مفتوحة");
                case "nolock": return T("No lock", "بدون تثبيت");
                default: return T("Can't tell", "ما ندري");
            }
        }

        static Color VerdictColor(string k)
        {
            return k == "inside" ? Theme.Good : k == "outside" ? Theme.Bad : k == "outside_gateway" ? Theme.Warn : k == "nolock" ? Theme.Lavender : Theme.Dim;
        }

        static string SourceName(string k)
        {
            switch (k)
            {
                case "log_colocated": return T("Game log (same place)", "سجل اللعبة (نفس المكان)");
                case "log_located": return T("Game log", "سجل اللعبة");
                case "capture": return T("Traffic", "من الترافك");
                case "relay_only": return T("Relay only", "ريلاي بس");
                case "none": return T("Not found", "ما انعرف");
                default: return k;
            }
        }

        // the match reports use Deadlock's region keys (Europe, Row, SEAsia...): shown with their names
        static string RegionName(string r)
        {
            if (r == "(none)") return T("(no lock)", "(بدون تثبيت)");
            string[] names;
            return r != null && Stats.MmRegions.TryGetValue(r, out names) ? Lang.T(names[0], names[1]) : r;
        }

        // ------------------------------------------------------------------ layout

        // Places everything for the current width: tiles in as many columns as fit, charts two side by side when there
        // is room (one under the other when narrow), mirrored in Arabic.
        void Layout2()
        {
            if (laidOut.Count == 0) return;
            const int margin = 16, gap = 12;
            int W = Math.Max(280, body.ClientSize.Width - 2 * margin);
            int tileCols = Math.Max(2, Math.Min(5, (W + gap) / (150 + gap)));
            int tileW = (W - (tileCols - 1) * gap) / tileCols;
            bool two = W >= 720;
            int halfW = two ? (W - gap) / 2 : W;
            int y = 8, tileIdx = 0, rowH = 0;
            var scroll = body.AutoScrollPosition;
            body.SuspendLayout();
            for (int i = 0; i < laidOut.Count; i++)
            {
                var c = laidOut[i];
                string how = place[c];
                int h = heights[c];
                if (how != "tile" && tileIdx > 0) { y += rowH + gap; tileIdx = 0; rowH = 0; }
                if (how == "tile")
                {
                    if (tileIdx > 0 && tileIdx % tileCols == 0) y += h + gap;
                    Put(c, (tileIdx % tileCols) * (tileW + gap), y, tileW, h, margin, W, scroll);
                    tileIdx++;
                    rowH = h;
                }
                else if (how == "half" && two)
                {
                    // a half without a partner after it still takes only half the row
                    bool partner = i + 1 < laidOut.Count && place[laidOut[i + 1]] == "half2";
                    Put(c, 0, y, halfW, h, margin, W, scroll);
                    if (!partner) y += h + gap;
                }
                else if (how == "half2" && two)
                {
                    Put(c, halfW + gap, y, W - halfW - gap, h, margin, W, scroll);
                    y += h + gap;
                }
                else
                {
                    if (how == "section") y += 4;
                    Put(c, 0, y, W, h, margin, W, scroll);
                    y += h + (how == "section" ? 6 : gap);
                }
            }
            body.ResumeLayout();
        }

        // x is measured from the reading side: in Arabic the first tile sits on the right
        void Put(Control c, int x, int y, int w, int h, int margin, int W, Point scroll)
        {
            int left = Lang.Ar ? margin + W - x - w : margin + x;
            c.Bounds = new Rectangle(left + scroll.X, y + scroll.Y, w, h);
        }
    }
}
