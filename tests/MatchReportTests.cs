using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;

namespace PingBoost
{
    // The automatic match report (kind "match"): once per match, a minute after it was seen, and what it says.
    static partial class Tests
    {
        static readonly DateTime T0 = new DateTime(2026, 10, 14, 12, 45, 0);

        static MatchLock EuropeLock()
        {
            return new MatchLock
            {
                Locked = true, Picked = new List<string> { "fra", "ams" }, PickedRegions = new List<string> { "Europe" },
                Closed = new List<string> { "dfjr" }, Gateways = new List<string> { "dxb" }, DubaiWayIn = true,
                ExpectedMs = 58, ExpectedVia = "dxb", LockedAt = T0.AddMinutes(-7),
            };
        }

        static void MatchReporterOncePerMatchAfterAMinute()
        {
            var r = new MatchReporter(null);
            int locks = 0;
            Func<MatchLock> lk = () => { locks++; return EuropeLock(); };
            Check(!r.Due(T0), "nothing seen: nothing due");
            Check(r.See("#1", true, T0.AddSeconds(-5), T0, lk) && locks == 1, "a new connection is a new match (the lock taken once)");
            Check(r.StartedAt == T0.AddSeconds(-5), "it began at its first log line");
            Check(!r.See("#1", true, T0, T0.AddSeconds(20), lk) && locks == 1, "the same connection is the same match");
            Check(!r.Due(T0.AddSeconds(59)), "59 s: not yet");
            Check(r.Due(T0.AddSeconds(60)), "60 s: due");
            r.MarkSent();
            Check(!r.Due(T0.AddSeconds(80)), "never twice");
            r.NoMatch();
            Check(!r.See("#1", true, T0, T0.AddSeconds(100), lk), "a log connection stays one match after a no-match check");
            Check(r.See("#2", true, T0.AddMinutes(30), T0.AddMinutes(30), lk) && !r.Sent, "a new connection id: a new match");
            // captures: the same server is a new match only after a check that saw no match
            var c = new MatchReporter(null);
            Check(c.See("dc:fra", false, DateTime.MinValue, T0, lk) && c.StartedAt == T0, "first capture: new, begun now");
            Check(!c.See("dc:fra", false, DateTime.MinValue, T0.AddSeconds(30), lk), "seen again: same match");
            c.NoMatch();
            Check(c.See("dc:fra", false, DateTime.MinValue, T0.AddMinutes(40), lk), "after a no-match check: a new match");
            Check(c.See("dc:ams", false, DateTime.MinValue, T0.AddMinutes(41), lk), "another server: a new match");
            // the last reported connection survives a restart
            var again = new MatchReporter("#1");
            Check(!again.See("#1", true, T0, T0, lk) && again.Sent, "reported before the restart: not again");
            Check(!new MatchReporter(null).See(null, true, T0, T0, lk), "no key: nothing");
            // what was learned belongs to the current, unsent match
            var s = new MatchReporter(null);
            s.SetLog(new MatchSeen { Dc = "fra" });
            Check(s.Log == null, "no match: ignored");
            s.See("#5", true, T0, T0, lk);
            s.SetLog(new MatchSeen { Dc = "fra" });
            Check(s.Log != null && s.Log.Dc == "fra", "kept for the current match");
            s.See("#6", true, T0, T0.AddSeconds(30), lk);
            Check(s.Log == null && s.Capture == null, "a new match starts empty");
        }

        static SessionSet Sessions(string text, out ConsoleLine primary)
        {
            var gc = new GameConsole("unused");
            gc.Feed(text + "\n", T0.AddMinutes(1), false);
            primary = gc.LastRelay;
            return gc.LastSessions;
        }

        static void MatchReportFromTheConsoleLog()
        {
            var g = SavedDeadlock();
            ConsoleLine primary;
            // SE Asia match hosted in Dubai, entered at Mumbai: located from the delays, the game's own ping through the primary
            var set = Sessions("10/14 12:45:22 [SteamNetSockets] [#2801803541 SDR server steamid:1 vport 0] Requesting session from bom2#91 (155.133.224.21:27059).  Ping = 210 = 183+27 (front+back).  Rank=1 Score=260\n" +
                               "10/14 12:45:22 [SteamNetSockets] [#2801803541 SDR server steamid:1 vport 0] Requesting session from sgp#103 (103.10.124.118:27025).  Ping = 293 = 217+76 (front+back).  Rank=2 Score=355\n" +
                               "10/14 12:45:23 [SteamNetSockets] [#2801803541 SDR server steamid:1 vport 0] Selecting bom2#91 (155.133.224.21:27059) as primary.", out primary);
            Check(set.Since == new DateTime(2026, 10, 14, 12, 45, 22) && set.Pings.Count == 2 && set.Pings[0].Value == 210, "first line time and front+back kept");
            var se = new HashSet<string> { "sgp", "hkg", "dxb" };
            var m = MatchReport.FromLog(g, set, primary, se);
            Check(m.Dc == "dxb" && m.Region == "SEAsia" && m.Source == MatchReport.SourceLocated, "Dubai, SE Asia, located: " + m.Dc + " " + m.Source);
            Check(m.Entry == "bom2" && m.ActualMs == 210, "entered at Mumbai, the game's ping 210: " + m.Entry + " " + m.ActualMs);
            Check(m.OnPicked && MatchReport.Verdict(new MatchLock { Locked = true }, m) == "inside", "inside an SE Asia lock");
            Check(m.Backs.Count == 2 && m.Backs[0].Key == "bom2" && m.Backs[0].Value == 27, "the backs");
            var eu = MatchReport.FromLog(g, set, primary, new HashSet<string> { "fra" });
            Check(!eu.OnPicked && MatchReport.Verdict(new MatchLock { Locked = true }, eu) == "outside", "outside a Frankfurt-only lock");
            // a relay next to the server names it; no primary line: the lowest session ping
            set = Sessions("10/14 13:00:01 [SteamNetSockets] [#1671292347 SDR server steamid:1 vport 0] Requesting session from dfra#51 (89.222.108.162:27019).  Ping = 78 = 78+0 (front+back).\n" +
                           "10/14 13:00:01 [SteamNetSockets] [#1671292347 SDR server steamid:1 vport 0] Requesting session from dxb#7 (1.2.3.4:27019).  Ping = 71 = 31+40 (front+back).", out primary);
            m = MatchReport.FromLog(g, set, primary, new HashSet<string> { "dfra", "fra" });
            Check(m.Dc == "dfra" && m.Source == MatchReport.SourceColocated && m.Region == "Europe" && m.OnPicked, "colocated: " + m.Dc + " " + m.Source);
            Check(m.Entry == null && m.ActualMs == 71, "no primary: the lowest ping, " + m.ActualMs);
            // a primary line from an earlier connection is not this one's
            var old = new ConsoleLine { Code = "fra", At = new DateTime(2026, 10, 14, 12, 0, 0) };
            Check(MatchReport.FromLog(g, set, old, null).Entry == null, "an older primary line is ignored");
            // one far relay: no place, only the way in
            set = Sessions("10/14 13:10:00 [SteamNetSockets] [#9 SDR server steamid:1 vport 0] Requesting session from dxb#7 (1.2.3.4:27019).  Ping = 71 = 31+40 (front+back).\n" +
                           "10/14 13:10:01 [SteamNetSockets] [#9 SDR server steamid:1 vport 0] Selecting dxb#7 (1.2.3.4:27019) as primary.", out primary);
            m = MatchReport.FromLog(g, set, primary, null);
            Check(m.Dc == null && m.Source == MatchReport.SourceRelay && m.Entry == "dxb" && m.ActualMs == 71, "relay only: " + m.Source);
            Check(MatchReport.Verdict(new MatchLock { Locked = true }, m) == "unknown" && MatchReport.Verdict(new MatchLock(), m) == "nolock", "unknown / nolock");
            Check(MatchReport.FromLog(g, null, null, null).Source == MatchReport.SourceNone, "no sessions: none");
            Check(MatchReport.RegionKey(g, "hel") == "Europe" && MatchReport.RegionKey(g, "bom2") == null, "region keys");
            Check(MatchReport.RegionKeys(g, new[] { "fra", "ams", "sgp" }).SequenceEqual(new[] { "Europe", "SEAsia" }), "picked regions, each once");
        }

        static void MatchReportMergesTheCapture()
        {
            var log = new MatchSeen { Source = MatchReport.SourceRelay, Entry = "dxb", ActualMs = 71 };
            log.Backs.Add(new KeyValuePair<string, int>("dxb", 40));
            var cap = new MatchSeen { Dc = "fra", Region = "Europe", OnPicked = true, Source = MatchReport.SourceCapture };
            var m = MatchReport.Merge(log, cap);
            Check(m.Dc == "fra" && m.Source == MatchReport.SourceCapture && m.Entry == "dxb" && m.ActualMs == 71 && m.Backs.Count == 1,
                  "the capture's datacenter with the log's way in, ping and backs");
            var located = new MatchSeen { Dc = "dxb", Source = MatchReport.SourceLocated };
            Check(MatchReport.Merge(located, cap) == located, "the log's datacenter wins");
            Check(MatchReport.Merge(null, cap) == cap && MatchReport.Merge(log, null) == log && MatchReport.Merge(null, null) == null, "one side only");
            var relayCap = new MatchSeen { Source = MatchReport.SourceRelay, Entry = "dfjr" };
            var bare = new MatchSeen();
            Check(MatchReport.Merge(bare, relayCap).Entry == "dfjr", "the capture's entry relay when the log has none");
        }

        static void MatchReportDataContract()
        {
            var lk = EuropeLock();
            var m = new MatchSeen { Dc = "dxb", Region = "SEAsia", OnPicked = false, Source = MatchReport.SourceLocated, Entry = "bom2", ActualMs = 120 };
            m.Backs.Add(new KeyValuePair<string, int>("bom2", 27));
            m.Backs.Add(new KeyValuePair<string, int>("sgp", 76));
            var start = lk.LockedAt.AddSeconds(420);
            var d = MatchReport.Data("Deadlock", lk, m, start, DateTime.MinValue, lk.LockedAt.AddSeconds(900), 66);
            var keys = new[] { "game", "locked", "picked", "picked_regions", "closed", "gateways_open", "dubai_way_in", "expected_ms", "expected_via",
                               "dc", "region", "source", "entry", "actual_ms", "backs", "verdict", "queue_s", "lock_age_s" };
            Check(d.Keys.OrderBy(k => k).SequenceEqual(keys.OrderBy(k => k)), "exactly the contract's fields: " + string.Join(",", d.Keys.ToArray()));
            Check((bool)d["locked"] && ((string[])d["picked"]).SequenceEqual(new[] { "fra", "ams" }) && ((string[])d["closed"])[0] == "dfjr" &&
                  ((string[])d["gateways_open"])[0] == "dxb" && (bool)d["dubai_way_in"], "the lock");
            Check((int)d["expected_ms"] == 58 && (string)d["expected_via"] == "dxb", "expected");
            Check((string)d["dc"] == "dxb" && (string)d["region"] == "SEAsia" && (string)d["source"] == "log_located" && (string)d["entry"] == "bom2", "the match");
            Check((int)d["actual_ms"] == 120 && (string)d["verdict"] == "outside", "the game's own ping wins over the live one; outside");
            Check((int)d["queue_s"] == 420 && (int)d["lock_age_s"] == 900, "queue 420 s, lock 900 s old");
            var backs = (Dictionary<string, object>)d["backs"];
            Check(backs.Count == 2 && (int)backs["sgp"] == 76, "backs as an object");
            string json = new JavaScriptSerializer().Serialize(Privacy.ScrubData(d));
            Check(json.Length < 1024, "small: " + json.Length + " bytes");
            Check(json.Contains("\"backs\":{\"bom2\":27,\"sgp\":76}") && json.Contains("\"picked\":[\"fra\",\"ams\"]"), "JSON shape: " + json);
            Check(MatchReport.Detail(lk, m) == "outside, dxb, log_located", "detail: " + MatchReport.Detail(lk, m));
            // no lock, nothing known: empty lists, nulls, the live ping only when given
            var gameStart = T0.AddMinutes(-3);
            d = MatchReport.Data("Deadlock", new MatchLock { DubaiWayIn = true }, null, T0, gameStart, T0.AddMinutes(1), -1);
            Check(!(bool)d["locked"] && ((string[])d["picked"]).Length == 0 && ((string[])d["gateways_open"]).Length == 0, "no lock: empty lists");
            Check(d["expected_ms"] == null && d["expected_via"] == null && d["dc"] == null && d["actual_ms"] == null && d["lock_age_s"] == null,
                  "unknowns are null");
            Check((string)d["verdict"] == "nolock" && (string)d["source"] == "none", "nolock, none");
            Check((int)d["queue_s"] == 180, "unlocked: queue from the game's start");
            d = MatchReport.Data("Deadlock", lk, new MatchSeen(), T0, DateTime.MinValue, T0, 66);
            Check((int)d["actual_ms"] == 66 && (string)d["verdict"] == "unknown", "the live ping when the match gave none");
            d = MatchReport.Data("Deadlock", new MatchLock(), null, T0, DateTime.MinValue, T0, -1);
            Check(d["queue_s"] == null, "no lock, game start unknown: no queue time");
        }

        static void RunMatchReportTests()
        {
            Run("MatchReporterOncePerMatchAfterAMinute", MatchReporterOncePerMatchAfterAMinute);
            Run("MatchReportFromTheConsoleLog", MatchReportFromTheConsoleLog);
            Run("MatchReportMergesTheCapture", MatchReportMergesTheCapture);
            Run("MatchReportDataContract", MatchReportDataContract);
        }
    }
}
