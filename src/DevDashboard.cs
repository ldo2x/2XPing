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
            Font = ChartFonts.Small;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Shapes.Behind(this));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Shapes.Round(new RectangleF(0, 0, Width - 1, Height - 1), 12))
            using (var b = new SolidBrush(Theme.Panel1)) g.FillPath(b, path);
            bool rtl = RightToLeft == RightToLeft.Yes;
            var flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter |
                        (rtl ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left);
            int w = Width - 24;
            ChartBase.DrawFit(g, Value, ChartFonts.Big, new Rectangle(12, 8, w, 34), ValueColor, flags);
            ChartBase.DrawFit(g, Caption, Font, new Rectangle(12, 44, w, 18), Theme.Dim, flags);
            if (DeltaText != "")
                ChartBase.DrawFit(g, DeltaText, Font, new Rectangle(12, 63, w, 18), DeltaColor, flags);
        }
    }

    // Where each piece of the dashboard goes (pure, tested): tiles in as many columns as fit, charts two side by side
    // when there is room and one under the other when narrow, mirrored in Arabic.
    static class DashLayout
    {
        public const int Margin = 16, Gap = 12;

        public class Item
        {
            public string How;   // "tile", "section", "full", "half", "half2" (the right-hand partner of a half)
            public int Height;
        }

        // width: the panel's whole width (a vertical scrollbar's room is always kept, so the content never gets wider
        // than the panel and no horizontal bar appears). Returns each item's place; total = the content's height.
        public static Rectangle[] Compute(IList<Item> items, int width, int scrollBarW, bool rtl, out int total)
        {
            var r = new Rectangle[items.Count];
            int W = Math.Max(280, width - scrollBarW - 2 * Margin);
            int tileCols = Math.Max(2, Math.Min(5, (W + Gap) / (150 + Gap)));
            int tileW = (W - (tileCols - 1) * Gap) / tileCols;
            bool two = W >= 720;
            int halfW = two ? (W - Gap) / 2 : W;
            int y = 8, tileIdx = 0, rowH = 0;
            Func<int, int, int, int, Rectangle> put = (x, yy, w, h) => new Rectangle(rtl ? Margin + W - x - w : Margin + x, yy, w, h);
            for (int i = 0; i < items.Count; i++)
            {
                string how = items[i].How;
                int h = items[i].Height;
                if (how != "tile" && tileIdx > 0) { y += rowH + Gap; tileIdx = 0; rowH = 0; }
                if (how == "tile")
                {
                    if (tileIdx > 0 && tileIdx % tileCols == 0) y += rowH + Gap;
                    r[i] = put((tileIdx % tileCols) * (tileW + Gap), y, tileW, h);
                    tileIdx++;
                    rowH = h;
                }
                else if (how == "half" && two)
                {
                    // a half without a partner after it still takes only half the row
                    bool partner = i + 1 < items.Count && items[i + 1].How == "half2";
                    r[i] = put(0, y, halfW, h);
                    if (!partner) y += h + Gap;
                }
                else if (how == "half2" && two)
                {
                    r[i] = put(halfW + Gap, y, W - halfW - Gap, h);
                    y += h + Gap;
                }
                else
                {
                    if (how == "section") y += 4;
                    r[i] = put(0, y, W, h);
                    y += h + (how == "section" ? 6 : Gap);
                }
            }
            if (tileIdx > 0) y += rowH + Gap;
            total = y + Margin;
            return r;
        }
    }

    // «لوحة الأرقام» / Dashboard: the developer's counters and charts, from one dev_stats() call
    // (tools/supabase-dev-stats.sql) with the secret key. The answer is kept for 5 minutes, and it only refreshes
    // by itself while the page is on screen and the window isn't minimised, at most once every 5 minutes.
    class DevDashboard : Panel
    {
        static string T(string en, string ar) { return Lang.T(en, ar); }

        // false = never read dev.key nor call Supabase (offscreen renders and tests); the environment variable
        // 2XPING_DASHBOARD_OFFLINE=1 turns it off too
        public static bool LoadEnabled = Environment.GetEnvironmentVariable("2XPING_DASHBOARD_OFFLINE") != "1";

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
        readonly List<DashLayout.Item> items = new List<DashLayout.Item>();
        bool loading, built, building;
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

            var top = new Panel { Dock = DockStyle.Top, Height = 52, Padding = new Padding(16, 8, 16, 0) };
            range = new Segmented
            {
                Items = new[] { T("7 days", "7 أيام"), T("30 days", "30 يوم"), T("90 days", "90 يوم") },
                Dock = Lang.Ar ? DockStyle.Left : DockStyle.Right, Width = 240, Height = 36, Font = Fonts.Ui(9f)
            };
            range.SelectedIndex = 1;
            range.SelectedIndexChanged += delegate { Load(false); };
            refreshBtn = new RoundButton { Text = T("Refresh", "حدّث"), Dock = Lang.Ar ? DockStyle.Left : DockStyle.Right, Width = 90, BackColor = Theme.Panel2 };
            refreshBtn.Click += delegate { Load(true); };
            var title = new Label { Text = T("Dashboard", "لوحة الأرقام"), Dock = DockStyle.Fill, AutoSize = false,
                                    Font = Fonts.Semi(13f), ForeColor = Theme.Accent, TextAlign = ContentAlignment.MiddleLeft };
            top.Controls.Add(title);
            top.Controls.Add(range);
            top.Controls.Add(new Panel { Dock = Lang.Ar ? DockStyle.Left : DockStyle.Right, Width = 8 });
            top.Controls.Add(refreshBtn);
            // the status (updated at..., errors) on its own line, so a narrow window never hides it
            status = new Label { Dock = DockStyle.Top, Height = 22, Padding = new Padding(16, 0, 16, 0), TextAlign = ContentAlignment.MiddleLeft,
                                 ForeColor = Theme.Dim, Font = Fonts.Ui(9f), AutoEllipsis = true };

            body = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg };
            // the scroll area measured the pieces at their old (wider) places before this ran: place them, then let it
            // measure again, or narrowing the window brings back a horizontal scrollbar (a rebuild does this itself)
            body.Resize += delegate { Place(); if (!building) body.PerformLayout(); };
            Controls.Add(body);
            Controls.Add(status);
            Controls.Add(top);

            // checks every minute; Load only fetches when the shown answer is 5 minutes old
            timer = new System.Windows.Forms.Timer { Interval = 60000 };
            timer.Tick += delegate { if (OnScreen) Load(false); };
            timer.Start();
            VisibleChanged += delegate { if (OnScreen) Load(false); };
            Disposed += delegate { timer.Dispose(); };
            Show(null);
        }

        bool OnScreen
        {
            get
            {
                var f = FindForm();
                return Visible && (f == null || f.WindowState != FormWindowState.Minimized);
            }
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
            if (!LoadEnabled) { if (s == null) status.Text = T("Loading is off (offline render).", "التحميل مطفي (عرض بدون نت)."); return; }
            string key = Telemetry.DevKey;
            if (string.IsNullOrEmpty(key)) { status.Text = T("No secret key on this PC.", "ما فيه مفتاح سري على هالجهاز."); return; }
            loading = true;
            refreshBtn.Enabled = false;
            status.Text = T("Loading the numbers...", "لحظة، نجيب الأرقام...");
            int tz = (int)Math.Round(TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes);
            ThreadPool.QueueUserWorkItem(delegate
            {
                DashStats got = null;
                string error = null;
                try
                {
                    string req = "{\"p_days\":" + days + ",\"p_tz_minutes\":" + tz + "}";
                    string json = Http.Send("POST", Telemetry.SupabaseUrl.TrimEnd('/') + "/rest/v1/rpc/dev_stats", req, Telemetry.Headers(key), 20000);
                    got = DashStats.Parse(json);
                }
                catch (WebException ex)
                {
                    var r = ex.Response as HttpWebResponse;
                    var f = ex as HttpFailure;
                    error = (f != null ? f.Code == HttpStatusCode.NotFound : r != null && r.StatusCode == HttpStatusCode.NotFound)
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
                        // the period was switched while this one loaded: load the one now picked
                        if (days != Days) Load(false);
                    }));
                }
                catch { } // the page was closed meanwhile
            });
        }

        // ------------------------------------------------------------------ building the page

        void Show(DashStats s)
        {
            if (s != null)
            {
                DateTime at;
                fetched.TryGetValue(s.Days, out at);
                status.Text = T("Updated ", "آخر تحديث ") + (at == DateTime.MinValue ? s.GeneratedAt : at).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) +
                              "  ·  " + T("last " + s.Days + " days", "آخر " + s.Days + " " + DashFmt.ArNoun(s.Days, "يوم", "أيام"));
            }
            // the same answer again (a tab visit, a timer tick with nothing new): nothing to rebuild
            if (built && s == shown) return;
            shown = s;
            built = true;
            Build(s ?? new DashStats { Days = Days });
        }

        void Build(DashStats s)
        {
            int keepY = -body.AutoScrollPosition.Y; // stay where the reader was
            building = true;
            body.SuspendLayout();
            foreach (var c in laidOut) c.Dispose();
            laidOut.Clear();
            items.Clear();
            bool empty = shown == null;

            // counters
            Add(Section(T("Users", "المستخدمين")), "section", 34);
            Tile(s, "online_now", T("Online now", "متصلين دحين"), null, empty);
            Tile(s, "installs_total", T("All installs", "كل الأجهزة"), null, empty);
            // installs that switched off «شارك إحصائيات البيتا» (no name, PC or location); "-" until dev_stats() counts them
            Tile(s, "installs_anonymous", T("Anonymous", "مجهول"), null, empty);
            Tile(s, "active_today", T("Active today", "نشطين اليوم"), T("vs yesterday", "عن أمس"), empty);
            Tile(s, "active_7d", T("Active 7 days", "نشطين 7 أيام"), T("vs the 7 days before", "عن الأسبوع اللي قبله"), empty);
            Tile(s, "active_30d", T("Active 30 days", "نشطين 30 يوم"), null, empty);
            Tile(s, "new_today", T("New today", "جدد اليوم"), T("vs yesterday", "عن أمس"), empty);
            Tile(s, "new_7d", T("New 7 days", "جدد 7 أيام"), T("vs the 7 days before", "عن الأسبوع اللي قبله"), empty);
            Tile(s, "matches_today", T("Matches today", "مباريات اليوم"), T("vs yesterday", "عن أمس"), empty);
            Tile(s, "locks_today", T("Locks today", "تثبيتات اليوم"), T("vs yesterday", "عن أمس"), empty);
            Tile(s, "issues_24h", T("Problems 24 h", "مشاكل آخر 24 ساعة"), T("vs the 24 h before", "عن اليوم اللي قبله"), empty, true);
            // messages from «تواصل معنا» (DevPanel's Messages tab lists them); "-" until dev_stats() counts them
            Tile(s, "feedback_7d", T("New messages (7 days)", "رسائل جديدة (7 أيام)"), null, empty);

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
                    Breakdown(s, "lock_region", T("Deadlock regions picked", "مناطق ديدلوك المختارة"), RegionWords()));
            AddPair(Breakdown(s, "mode", T("Layout", "الواجهة"), Words("simple", T("Simple", "بسيطة"), "advanced", T("Advanced", "متقدمة"))),
                    Breakdown(s, "lang", T("Language", "اللغة"), Words("ar", "عربي", "en", "English")));
            AddPair(Breakdown(s, "os", T("Windows", "الويندوز"), null), null);

            // matches
            Add(Section(T("Matches", "المباريات")), "section", 34);
            var act = new LineChart { Title = T("Matches and locks per day", "المباريات والتثبيتات كل يوم"), XLabels = labels };
            act.Lines.Add(new LineChart.Series { Name = T("Matches", "مباريات"), Color = Theme.Lavender, Values = col(d => d.Matches) });
            act.Lines.Add(new LineChart.Series { Name = T("Locks", "تثبيتات"), Color = Theme.Warn, Values = col(d => d.Locks) });
            Add(act, "full", 220);

            var donut = new DonutChart { Title = T("Did the lock hold? (match reports)", "التثبيت مسك؟ (تقارير المباريات)"),
                                         CenterText = DashFmt.Count(s.MatchTotal), CenterSub = T("matches", DashFmt.ArNoun(s.MatchTotal, "مباراة", "مباريات")) };
            if (s.DupSuspect > 0) donut.Note = T("+" + s.DupSuspect + " maybe repeats", "+" + s.DupSuspect + " يمكن مكررة");
            foreach (var v in s.Verdicts)
                donut.Slices.Add(new DonutChart.Slice { Label = VerdictName(v.Key), Value = v.Count, Color = VerdictColor(v.Key) });
            var sources = Bars(T("How the match server was found", "كيف عرفنا سيرفر المباراة"),
                               s.Sources.Select(x => Bar(SourceName(x.Key), x.Pct, DashFmt.Pct(x.Pct) + "  " + DashFmt.Count(x.Count), Theme.Lavender)), 100);
            AddPair(donut, sources, 200);

            var inside = Bars(T("Inside the lock, per picked region", "داخل التثبيت حسب المنطقة"),
                              s.Regions.Where(r => !double.IsNaN(r.InsidePct))
                                       .Select(r => Bar(RegionName(r.Region), r.InsidePct, DashFmt.Pct(r.InsidePct) + T(" of ", " من ") + DashFmt.Count(r.Inside + r.Outside),
                                                        r.InsidePct >= 95 ? Theme.Good : r.InsidePct >= 80 ? Theme.Warn : Theme.Bad)), 100);
            inside.ValueW = 96;
            var queues = s.Regions.Where(r => !double.IsNaN(r.QueueMedianS)).ToList();
            var queue = Bars(T("Median queue per picked region (min:s)", "الانتظار الوسيط حسب المنطقة (دقيقة:ثانية)"),
                             queues.Select(r => Bar(RegionName(r.Region), r.QueueMedianS, DashFmt.Duration(r.QueueMedianS), Theme.Accent)), double.NaN);
            AddPair(inside, queue);

            var scatter = new ScatterChart
            {
                Title = T("Expected vs actual ping (average gap)", "البنق المتوقع والحقيقي (متوسط الفرق)"),
                Note = s.PingN > 0 ? DashFmt.Ms(s.AvgDiff, true).Trim('‎') : "",
                Points = s.Pairs, XName = T("expected (ms)", "المتوقع (ms)"), YName = T("actual (ms)", "الحقيقي (ms)")
            };
            var buckets = DashMath.DiffBuckets(s.Pairs);
            var diff = new ColumnChart
            {
                Title = T("How far off the expected ping was (ms, latest matches)", "كم بعد البنق الحقيقي عن المتوقع (ms، آخر المباريات)"),
                Labels = DashMath.DiffBucketLabels, Values = buckets.Select(b => (double)b).ToArray(),
                Colors = new[] { Theme.Lavender, Theme.Lavender, Theme.Good, Theme.Good, Theme.Good, Theme.Warn, Theme.Bad, Theme.Bad }
            };
            AddPair(scatter, diff, 250);

            string arrow = Lang.Ar ? " ← " : " → ";
            var dcs = Bars(T("Datacenters seen per picked region (red: unknown to the app)", "الداتا سنترات اللي طلعت حسب المنطقة (الأحمر: مو معروف للبرنامج)"),
                           s.Dcs.Select(d => Bar(RegionName(d.Region) + arrow + d.Dc, d.Count, DashFmt.Count(d.Count),
                                                 d.Known ? Theme.Lavender : Theme.Bad)), double.NaN);
            dcs.LabelW = 190;
            if (s.UnknownDc > 0) dcs.Note = "? " + s.UnknownDc;
            // every row up to 24 (6 per region x 4 regions); past that the card says "+N more"
            Add(dcs, "full", BarList.PreferredHeightFor(Math.Min(24, s.Dcs.Count)));

            // problems
            Add(Section(T("Problems", "المشاكل")), "section", 34);
            var prob = new LineChart { Title = T("Errors and problem reports per day", "الأخطاء وتقارير المشاكل كل يوم"), XLabels = labels };
            prob.Lines.Add(new LineChart.Series { Name = T("Problems", "مشاكل"), Color = Theme.Warn, Values = col(d => d.Issues) });
            prob.Lines.Add(new LineChart.Series { Name = T("Errors", "أخطاء"), Color = Theme.Bad, Values = col(d => d.Errors) });
            var issues = Bars(T("Top problems, last 7 days", "أكثر المشاكل، آخر 7 أيام"),
                              s.Issues.Select(i => Bar(i.Code, i.Count, IssueText(i), i.Code == "crash" || i.Code == "lock_outside" ? Theme.Bad : Theme.Warn)),
                              double.NaN);
            issues.LabelW = 170;
            issues.ValueW = 130;
            AddPair(prob, issues, Math.Max(220, BarList.PreferredHeightFor(Math.Min(15, s.Issues.Count))));

            Place();
            body.ResumeLayout(true);
            building = false;
            body.AutoScrollPosition = new Point(0, keepY);
        }

        // ------------------------------------------------------------------ pieces

        void Add(Control c, string how, int height)
        {
            c.RightToLeft = RightToLeft;
            items.Add(new DashLayout.Item { How = how, Height = height });
            laidOut.Add(c);
            body.Controls.Add(c);
        }

        // two charts side by side (one under the other when narrow), as tall as the taller one needs
        void AddPair(Control a, Control b, int height)
        {
            int h = height > 0 ? height : PairHeight(a as BarList, b as BarList);
            Add(a, "half", h);
            if (b != null) Add(b, "half2", h);
        }

        void AddPair(Control a, Control b) { AddPair(a, b, 0); }

        // tall enough for every bar of both (a breakdown has at most 11: the top 10 and «غيرها»)
        internal static int PairHeight(BarList a, BarList b)
        {
            int rows = Math.Max(a != null ? a.Bars.Count : 0, b != null ? b.Bars.Count : 0);
            return Math.Max(120, BarList.PreferredHeightFor(Math.Min(11, rows)));
        }

        static Label Section(string text)
        {
            return new Label { Text = text, Font = ChartFonts.Section, ForeColor = Theme.Lavender, AutoSize = false,
                               TextAlign = ContentAlignment.BottomLeft }; // right-to-left flips it to the right
        }

        void Tile(DashStats s, string key, string caption, string deltaWhat, bool empty, bool lowerIsBetter = false)
        {
            // a counter the database doesn't send yet (an older dev_stats()) shows "-", not a made-up 0
            empty = empty || !s.Counters.ContainsKey(key);
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

        static string IssueText(DashIssue i)
        {
            return T(DashFmt.Count(i.Count) + "x, " + i.Players + (i.Players == 1 ? " player" : " players"),
                     DashFmt.ArCounted(i.Count, "مرة", "مرات", "مرتين") + "، " + DashFmt.ArCounted(i.Players, "لاعب", "لاعبين", "لاعبَين"));
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

        // a breakdown as shares: "42%  12", the top 10 and, for one-per-install breakdowns, the rest as «غيرها»
        internal static BarList Breakdown(DashStats s, string name, string title, Dictionary<string, string> words)
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
            string users = T(b.Total == 1 ? " user" : " users", " " + DashFmt.ArNoun(b.Total, "مستخدم", "مستخدمين"));
            string total = b.Total > 0 ? "  ·  " + DashFmt.Count(b.Total) + users + (b.Multi ? T(", can add past 100%", "، ممكن يزيد عن 100%") : "") : "";
            return new BarList { Title = title + total, Bars = bars, Max = 100 };
        }

        static string VerdictName(string k)
        {
            switch (k)
            {
                case "inside": return T("Inside the lock", "داخل التثبيت");
                case "outside": return T("Outside the lock", "برا التثبيت");
                case "outside_gateway": return T("Outside via kept gateway", "برا عبر بوابة مفتوحة");
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

        // Deadlock's region keys (Europe, Row, SEAsia...) shown with their names
        static Dictionary<string, string> RegionWords()
        {
            var d = new Dictionary<string, string>();
            foreach (var kv in Stats.MmRegions) d[kv.Key] = Lang.T(kv.Value[0], kv.Value[1]);
            return d;
        }

        static string RegionName(string r)
        {
            if (r == "(none)") return T("(no lock)", "(بدون تثبيت)");
            string[] names;
            return r != null && Stats.MmRegions.TryGetValue(r, out names) ? Lang.T(names[0], names[1]) : r;
        }

        // ------------------------------------------------------------------ layout

        // Puts every piece in its place for the current width, and tells the scroll area how tall the content is
        // (AutoScroll alone measured the pieces before they were placed and lost the scrollbar).
        void Place()
        {
            if (laidOut.Count == 0) return;
            int total;
            var rects = DashLayout.Compute(items, body.Width, SystemInformation.VerticalScrollBarWidth, Lang.Ar, out total);
            body.AutoScrollMinSize = new Size(0, total);
            var scroll = body.AutoScrollPosition;
            for (int i = 0; i < laidOut.Count; i++)
            {
                var r = rects[i];
                laidOut[i].Bounds = new Rectangle(r.X + scroll.X, r.Y + scroll.Y, r.Width, r.Height);
            }
        }
    }
}
