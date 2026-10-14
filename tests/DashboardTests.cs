using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;

namespace PingBoost
{
    // The developer dashboard's pure parts: reading dev_stats() (a saved sample answer), the numbers' text,
    // the percentages and the chart axes.
    static partial class Tests
    {
        static DashStats SampleStats()
        {
            return DashStats.Parse(Data("dev-stats.json"));
        }

        static void DashParsesCountersAndDeltas()
        {
            var s = SampleStats();
            Check(s.Days == 7, "days 7: " + s.Days);
            Check(s.GeneratedAt == new DateTime(2026, 10, 14, 9, 30, 12, DateTimeKind.Utc), "generated_at in UTC: " + s.GeneratedAt.ToString("o"));
            Check(s.Counter("installs_total") == 1234 && s.Counter("online_now") == 17 && s.Counter("issues_24h") == 12, "counters");
            Check(s.Counter("missing") == 0 && !s.HasDelta("online_now"), "unknown counter 0, no delta for online");
            Check(s.HasDelta("active_7d") && s.Delta("active_7d") == -12 && s.Delta("new_today") == 0, "deltas");
        }

        static void DashParsesSeriesInDayOrder()
        {
            var s = SampleStats();
            Check(s.Series.Count == 7, "7 days: " + s.Series.Count);
            Check(s.Series[0].Day == new DateTime(2026, 10, 8) && s.Series[6].Day == new DateTime(2026, 10, 14), "sorted by day");
            Check(s.Series[6].Active == 58 && s.Series[6].Matches == 93 && s.Series[6].Issues == 4, "today's values");
            long last, before;
            DashMath.LastAndBefore(s.Series.Select(d => d.New).ToList(), 3, out last, out before);
            Check(last == 6 + 4 + 6 && before == 9 + 4 + 7, "last 3 days vs 3 before: " + last + " / " + before);
        }

        static void DashParsesBreakdownsWithShares()
        {
            var s = SampleStats();
            var v = s.Breakdown("version");
            Check(v.Total == 240 && !v.Multi && v.Items.Count == 3, "version breakdown");
            Check(Math.Abs(v.Items[0].Pct - 75) < 1e-9, "180/240 = 75%: " + v.Items[0].Pct);
            Check(v.Other == 4, "the rest past the listed items: " + v.Other);
            var g = s.Breakdown("game");
            Check(g.Multi && g.Other == 0, "multi breakdown has no 'other'");
            Check(g.Items.Sum(i => i.Pct) > 100, "multi shares may add past 100%");
            Check(s.Breakdown("nothing").Items.Count == 0, "a missing breakdown is empty, not an error");
        }

        static void DashParsesMatchQuality()
        {
            var s = SampleStats();
            Check(s.MatchTotal == 640 && s.UnknownDc == 3, "match total and unknown dc");
            Check(s.Verdicts.Count == 5 && s.Verdicts[0].Key == "inside" && Math.Abs(s.Verdicts[0].Pct - 78.125) < 1e-9, "verdicts with shares");
            Check(s.Verdicts.Any(x => x.Key == "outside_gateway" && x.Count == 30), "a kept gateway is its own verdict");
            var eu = s.Regions.First(r => r.Region == "Europe");
            Check(eu.Gateway == 25 && Math.Abs(eu.InsidePct - 100.0 * 480 / 495) < 1e-9, "inside % ignores kept-gateway matches: " + eu.InsidePct);
            Check(Math.Abs(eu.QueueMedianS - 42.5) < 1e-9 && Math.Abs(eu.AvgDiffMs - 3.2) < 1e-9, "decimals");
            var none = s.Regions.First(r => r.Region == "(none)");
            Check(double.IsNaN(none.InsidePct) && double.IsNaN(none.AvgDiffMs), "no lock: no inside %, null diff = NaN");
            Check(double.IsNaN(s.Regions.First(r => r.Region == "SEAsia").QueueMedianS), "null queue = NaN");
            Check(s.Pairs.Count == 4 && s.Pairs[3] == new PointF(60, 55.5f), "pairs");
            Check(s.PingN == 4 && Math.Abs(s.AvgDiff - 5.8) < 1e-9, "ping summary");
            Check(s.Dcs.Count == 3 && !s.Dcs[2].Known && s.Dcs[1].Dc == "dxb", "datacenters");
            Check(s.Issues.Count == 2 && s.Issues[0].Code == "lock_outside" && s.Issues[0].Players == 4, "issues");
        }

        static void DashParsesEmptyAndBrokenAnswers()
        {
            var s = DashStats.Parse("{}");
            Check(s.Series.Count == 0 && s.MatchTotal == 0 && s.Counter("online_now") == 0 && double.IsNaN(s.AvgDiff), "empty answer");
            s = DashStats.Parse("{\"counters\":{\"online_now\":\"x\"},\"series\":[{\"day\":\"bad\"}],\"match\":{\"ping\":{\"pairs\":[[1],[\"a\",2]]}}}");
            Check(s.Counter("online_now") == 0 && s.Series.Count == 0 && s.Pairs.Count == 0, "broken values are skipped");
            bool threw = false;
            try { DashStats.Parse("[1,2]"); } catch (FormatException) { threw = true; }
            Check(threw, "not an object -> FormatException");
        }

        static void DashNumberText()
        {
            Check(DashFmt.Count(950) == "950" && DashFmt.Count(1234) == "1,234" && DashFmt.Count(9999) == "9,999", "small counts");
            Check(DashFmt.Count(12345) == "12.3k" && DashFmt.Count(10000) == "10k" && DashFmt.Count(250000) == "250k", "thousands: " + DashFmt.Count(12345));
            Check(DashFmt.Count(1500000) == "1.5M" && DashFmt.Count(-1234) == "-1,234", "millions and negatives");
            Check(DashFmt.Pct(42.4) == "42%" && DashFmt.Pct(0) == "0%" && DashFmt.Pct(0.4) == "<1%" && DashFmt.Pct(100) == "100%", "percent");
            Check(DashFmt.Pct(99.7) == ">99%" && DashFmt.Pct(double.NaN) == "-", "never rounds up to 100%");
            Check(DashFmt.Delta(3) == "‎+3" && DashFmt.Delta(-2) == "‎-2" && DashFmt.Delta(0) == "‎±0", "deltas");
            Check(DashFmt.Ms(5.8, true) == "‎+6 ms‎" && DashFmt.Ms(-4, true) == "‎-4 ms‎" && DashFmt.Ms(double.NaN, false) == "-", "ms");
            Check(DashFmt.Duration(95) == "1:35" && DashFmt.Duration(30) == "0:30" && DashFmt.Duration(double.NaN) == "-", "durations");
        }

        static void DashPercentAndBuckets()
        {
            Check(DashMath.Percent(1, 4) == 25 && DashMath.Percent(5, 0) == 0, "percent, no total = 0");
            var b = DashMath.DiffBuckets(new[] { new PointF(58, 61), new PointF(58, 58), new PointF(70, 95), new PointF(60, 55.5f),
                                                 new PointF(50, 20), new PointF(50, 100), new PointF(50, 53) });
            Check(b.Length == DashMath.DiffBucketLabels.Length, "one count per label");
            Check(b[3] == 3 && b[6] == 1 && b[2] == 1 && b[0] == 1 && b[7] == 1, "buckets: " + string.Join(",", b.Select(x => x.ToString()).ToArray()));
            Check(b.Sum() == 7, "every pair counted once");
        }

        static void ChartTicksAreRound()
        {
            var t = ChartScale.NiceTicks(0, 93, 5, true);
            Check(t[0] == 0 && t[t.Length - 1] >= 93 && t.Length <= 7, "0..93: " + string.Join(",", t.Select(x => x.ToString()).ToArray()));
            Check(t.Zip(t.Skip(1), (a, c) => c - a).Distinct().Count() == 1, "even steps");
            Check(string.Join(",", t.Select(x => x.ToString()).ToArray()) == "0,20,40,60,80,100", "steps of 20: " + string.Join(",", t.Select(x => x.ToString()).ToArray()));
            var z = ChartScale.NiceTicks(0, 0, 5, true);
            Check(z.Length >= 2 && z[0] == 0 && z[z.Length - 1] == 1, "all zero -> 0..1");
            var small = ChartScale.NiceTicks(0, 3, 5, true);
            Check(small.All(x => x == Math.Round(x)), "whole numbers never step below 1");
            var flat = ChartScale.NiceTicks(5, 5, 5, false);
            Check(flat[0] <= 0 && flat[flat.Length - 1] >= 5, "flat range keeps zero in view");
            Check(ChartScale.NiceStep(0.13) == 0.1 && ChartScale.NiceStep(7) == 10 && ChartScale.NiceStep(6) == 5 && ChartScale.NiceStep(30) == 50 && ChartScale.NiceStep(23.25) == 20 && ChartScale.NiceStep(0) == 1, "nice steps");
            var nan = ChartScale.NiceTicks(double.NaN, double.NaN, 5, true);
            Check(nan.Length >= 2, "NaN range still gives an axis");
        }

        static void ChartMapAndTickText()
        {
            Check(ChartScale.Map(50, 0, 100, 0, 200) == 100, "middle");
            Check(ChartScale.Map(0, 0, 100, 300, 100) == 300 && ChartScale.Map(100, 0, 100, 300, 100) == 100, "y grows upwards");
            Check(ChartScale.Map(7, 5, 5, 40, 90) == 40, "flat range maps to lo");
            Check(ChartScale.TickText(20) == "20" && ChartScale.TickText(0.5) == "0.5" && ChartScale.TickText(25000) == "25k", "tick text");
        }

        static void RunDashboardTests()
        {
            Run("DashParsesCountersAndDeltas", DashParsesCountersAndDeltas);
            Run("DashParsesSeriesInDayOrder", DashParsesSeriesInDayOrder);
            Run("DashParsesBreakdownsWithShares", DashParsesBreakdownsWithShares);
            Run("DashParsesMatchQuality", DashParsesMatchQuality);
            Run("DashParsesEmptyAndBrokenAnswers", DashParsesEmptyAndBrokenAnswers);
            Run("DashNumberText", DashNumberText);
            Run("DashPercentAndBuckets", DashPercentAndBuckets);
            Run("ChartTicksAreRound", ChartTicksAreRound);
            Run("ChartMapAndTickText", ChartMapAndTickText);
        }
    }
}
