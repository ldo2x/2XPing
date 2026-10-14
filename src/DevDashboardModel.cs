using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Web.Script.Serialization;

namespace PingBoost
{
    // The developer dashboard's numbers, read from the dev_stats() answer (tools/supabase-dev-stats.sql).
    // Pure: no network, no controls, so the tests can feed it a saved answer (tests/data/dev-stats.json).
    class DashSlice
    {
        public string Key;
        public long Count;
        public double Pct; // share of the breakdown's total, 0..100
    }

    class DashBreakdown
    {
        public string Name;
        public long Total;
        public bool Multi;            // one install can be in several items: the shares can add up past 100 %
        public List<DashSlice> Items = new List<DashSlice>();
        public long Other;            // single-valued only: the installs in the items past the top 15
    }

    class DashDay
    {
        public DateTime Day;
        public long Active, New, Matches, Locks, Errors, Issues;
    }

    class DashRegion
    {
        public string Region;
        public long Matches, Inside, Outside, Gateway, Unknown, NoLock; // Gateway: outside, on a gateway kept open by the player's setting
        public double QueueMedianS = double.NaN, AvgDiffMs = double.NaN;
        // inside out of the matches the lock decided (inside + outside; a kept gateway is the player's choice); NaN when none
        public double InsidePct { get { return Inside + Outside > 0 ? 100.0 * Inside / (Inside + Outside) : double.NaN; } }
    }

    class DashDc
    {
        public string Region, Dc;
        public long Count;
        public bool Known;
    }

    class DashIssue
    {
        public string Code;
        public long Count, Players;
    }

    class DashStats
    {
        public int Days;
        public DateTime GeneratedAt = DateTime.MinValue; // UTC
        public Dictionary<string, long> Counters = new Dictionary<string, long>();
        public Dictionary<string, long> Deltas = new Dictionary<string, long>();
        public List<DashDay> Series = new List<DashDay>();
        public Dictionary<string, DashBreakdown> Breakdowns = new Dictionary<string, DashBreakdown>();
        public long MatchTotal, UnknownDc;
        public List<DashSlice> Verdicts = new List<DashSlice>(), Sources = new List<DashSlice>();
        public List<DashRegion> Regions = new List<DashRegion>();
        public long PingN;
        public double AvgExpected = double.NaN, AvgActual = double.NaN, AvgDiff = double.NaN, MedianDiff = double.NaN;
        public List<PointF> Pairs = new List<PointF>(); // X = expected ms, Y = actual ms
        public List<DashDc> Dcs = new List<DashDc>();
        public List<DashIssue> Issues = new List<DashIssue>();

        public long Counter(string key) { long v; return Counters.TryGetValue(key, out v) ? v : 0; }
        public bool HasDelta(string key) { return Deltas.ContainsKey(key); }
        public long Delta(string key) { long v; return Deltas.TryGetValue(key, out v) ? v : 0; }

        public DashBreakdown Breakdown(string name)
        {
            DashBreakdown b;
            return Breakdowns.TryGetValue(name, out b) ? b : new DashBreakdown { Name = name };
        }

        // ------------------------------------------------------------------ parsing

        public static DashStats Parse(string json)
        {
            var root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as Dictionary<string, object>;
            if (root == null) throw new FormatException("dev_stats: not a JSON object");
            var s = new DashStats();
            s.Days = (int)Long(Get(root, "days"));
            DateTime at;
            if (DateTime.TryParse(Str(Get(root, "generated_at")), CultureInfo.InvariantCulture,
                                  DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out at))
                s.GeneratedAt = at;

            var counters = Get(root, "counters") as Dictionary<string, object>;
            if (counters != null) foreach (var kv in counters) s.Counters[kv.Key] = Long(kv.Value);
            var deltas = Get(root, "deltas") as Dictionary<string, object>;
            if (deltas != null) foreach (var kv in deltas) if (kv.Value != null) s.Deltas[kv.Key] = Long(kv.Value);

            foreach (var o in Arr(Get(root, "series")))
            {
                var d = o as Dictionary<string, object>;
                DateTime day;
                if (d == null || !DateTime.TryParseExact(Str(Get(d, "day")), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day))
                    continue;
                s.Series.Add(new DashDay
                {
                    Day = day, Active = Long(Get(d, "active")), New = Long(Get(d, "new")), Matches = Long(Get(d, "matches")),
                    Locks = Long(Get(d, "locks")), Errors = Long(Get(d, "errors")), Issues = Long(Get(d, "issues"))
                });
            }
            s.Series.Sort((a, b) => a.Day.CompareTo(b.Day));

            var brk = Get(root, "breakdowns") as Dictionary<string, object>;
            if (brk != null)
                foreach (var kv in brk)
                {
                    var d = kv.Value as Dictionary<string, object>;
                    if (d == null) continue;
                    var b = new DashBreakdown { Name = kv.Key, Total = Long(Get(d, "total")), Multi = Get(d, "multi") is bool && (bool)Get(d, "multi") };
                    b.Items = Slices(Get(d, "items"), b.Total);
                    if (!b.Multi) b.Other = Math.Max(0, b.Total - b.Items.Sum(i => i.Count));
                    s.Breakdowns[kv.Key] = b;
                }

            var m = Get(root, "match") as Dictionary<string, object>;
            if (m != null)
            {
                s.MatchTotal = Long(Get(m, "total"));
                s.UnknownDc = Long(Get(m, "unknown_dc"));
                s.Verdicts = Slices(Get(m, "verdict"), s.MatchTotal);
                s.Sources = Slices(Get(m, "source"), s.MatchTotal);
                foreach (var o in Arr(Get(m, "by_region")))
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    s.Regions.Add(new DashRegion
                    {
                        Region = Str(Get(d, "region")), Matches = Long(Get(d, "n")), Inside = Long(Get(d, "inside")),
                        Outside = Long(Get(d, "outside")), Gateway = Long(Get(d, "gateway")), Unknown = Long(Get(d, "unknown")), NoLock = Long(Get(d, "nolock")),
                        QueueMedianS = Num(Get(d, "queue_median_s")), AvgDiffMs = Num(Get(d, "avg_diff_ms"))
                    });
                }
                var p = Get(m, "ping") as Dictionary<string, object>;
                if (p != null)
                {
                    s.PingN = Long(Get(p, "n"));
                    s.AvgExpected = Num(Get(p, "avg_expected"));
                    s.AvgActual = Num(Get(p, "avg_actual"));
                    s.AvgDiff = Num(Get(p, "avg_diff"));
                    s.MedianDiff = Num(Get(p, "median_diff"));
                    foreach (var o in Arr(Get(p, "pairs")))
                    {
                        var pair = o as object[];
                        if (pair == null || pair.Length < 2) continue;
                        double e = Num(pair[0]), a = Num(pair[1]);
                        if (!double.IsNaN(e) && !double.IsNaN(a)) s.Pairs.Add(new PointF((float)e, (float)a));
                    }
                }
                foreach (var o in Arr(Get(m, "dcs")))
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    s.Dcs.Add(new DashDc { Region = Str(Get(d, "region")), Dc = Str(Get(d, "dc")), Count = Long(Get(d, "n")),
                                           Known = !(Get(d, "known") is bool) || (bool)Get(d, "known") });
                }
            }

            foreach (var o in Arr(Get(root, "issues")))
            {
                var d = o as Dictionary<string, object>;
                if (d == null) continue;
                s.Issues.Add(new DashIssue { Code = Str(Get(d, "code")), Count = Long(Get(d, "n")), Players = Long(Get(d, "players")) });
            }
            return s;
        }

        static List<DashSlice> Slices(object items, long total)
        {
            var l = new List<DashSlice>();
            foreach (var o in Arr(items))
            {
                var d = o as Dictionary<string, object>;
                if (d == null) continue;
                long n = Long(Get(d, "n"));
                l.Add(new DashSlice { Key = Str(Get(d, "k")), Count = n, Pct = DashMath.Percent(n, total) });
            }
            return l;
        }

        static object Get(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v : null;
        }

        static object[] Arr(object o) { return o as object[] ?? new object[0]; }

        static string Str(object o) { return o == null ? "" : Convert.ToString(o, CultureInfo.InvariantCulture); }

        // JSON numbers arrive as int, long or decimal (and a broken value as a string): NaN when it isn't a number
        public static double Num(object o)
        {
            if (o == null || o is bool) return double.NaN;
            if (o is string)
            {
                double v;
                return double.TryParse((string)o, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : double.NaN;
            }
            try { return Convert.ToDouble(o, CultureInfo.InvariantCulture); }
            catch { return double.NaN; }
        }

        static long Long(object o)
        {
            double v = Num(o);
            return double.IsNaN(v) || double.IsInfinity(v) ? 0 : (long)Math.Round(v);
        }
    }

    // The arithmetic behind the dashboard's numbers and charts.
    static class DashMath
    {
        // part of total in percent; 0 when there is no total
        public static double Percent(long part, long total)
        {
            return total <= 0 ? 0 : 100.0 * part / total;
        }

        // The ping guess's error (actual - expected) counted into fixed buckets, for the bar chart.
        public static readonly string[] DiffBucketLabels = { "< -20", "-20..-10", "-10..-3", "±3", "3..10", "10..20", "20..40", "> 40" };

        public static long[] DiffBuckets(IEnumerable<PointF> pairs)
        {
            var b = new long[DiffBucketLabels.Length];
            foreach (var p in pairs)
            {
                double d = p.Y - p.X;
                int i = d < -20 ? 0 : d < -10 ? 1 : d < -3 ? 2 : d <= 3 ? 3 : d <= 10 ? 4 : d <= 20 ? 5 : d <= 40 ? 6 : 7;
                b[i]++;
            }
            return b;
        }

        // The sum of a series' last `days` days and of the `days` before them (the change shown under a chart).
        public static void LastAndBefore(IList<long> values, int days, out long last, out long before)
        {
            last = 0; before = 0;
            for (int i = 0; i < values.Count; i++)
            {
                int back = values.Count - 1 - i;
                if (back < days) last += values[i];
                else if (back < 2 * days) before += values[i];
            }
        }
    }

    // How the dashboard writes its numbers (Western digits in both languages, like the rest of the app).
    static class DashFmt
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // 950 -> "950", 1234 -> "1,234", 12345 -> "12.3k", 1500000 -> "1.5M"
        public static string Count(long v)
        {
            long a = Math.Abs(v);
            string sign = v < 0 ? "-" : "";
            if (a < 10000) return sign + a.ToString("#,0", Inv);
            if (a < 1000000) return sign + Trim((a / 1000.0).ToString(a < 100000 ? "0.0" : "0", Inv)) + "k";
            return sign + Trim((a / 1000000.0).ToString("0.0", Inv)) + "M";
        }

        static string Trim(string s) { return s.EndsWith(".0") ? s.Substring(0, s.Length - 2) : s; }

        // 42.4 -> "42%", 0.4 -> "<1%", 0 -> "0%", 99.6 -> ">99%" (a part is never rounded up to a whole 100% or down to 0%)
        public static string Pct(double v)
        {
            if (double.IsNaN(v)) return "-";
            if (v <= 0) return "0%";
            if (v < 1) return "<1%";
            if (v >= 99.5 && v < 100) return ">99%";
            return Math.Round(v).ToString("0", Inv) + "%";
        }

        // +3, -2, ±0 (an LRM keeps the sign in front inside Arabic text)
        public static string Delta(long v)
        {
            return "‎" + (v > 0 ? "+" + Count(v) : v < 0 ? "-" + Count(-v) : "±0");
        }

        // 12.4 -> "12 ms", NaN -> "-"; signed adds a + to positive values
        public static string Ms(double v, bool signed)
        {
            if (double.IsNaN(v)) return "-";
            long r = (long)Math.Round(v);
            return "‎" + (signed && r > 0 ? "+" : "") + r.ToString(Inv) + " ms‎";
        }

        // 95 -> "1:35", 30 -> "0:30", NaN -> "-"
        public static string Duration(double seconds)
        {
            if (double.IsNaN(seconds) || seconds < 0) return "-";
            long s = (long)Math.Round(seconds);
            return (s / 60).ToString(Inv) + ":" + (s % 60).ToString("00", Inv);
        }
    }
}
