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

        static SessionSet Conn(string id, DateTime since, string server)
        {
            return new SessionSet { Conn = id, Since = since, At = since, Server = server };
        }

        static void MatchReporterOncePerMatchAfterAMinute()
        {
            var r = new MatchReporter(null, DateTime.MinValue);
            int locks = 0;
            Func<MatchLock> lk = () => { locks++; return EuropeLock(); };
            var game = T0.AddMinutes(-30);
            Check(!r.Due(T0), "nothing seen: nothing due");
            Check(r.SeeLog(Conn("#1", T0.AddSeconds(-5), "11"), game, T0, T0, lk) && locks == 1 && r.Key == "#1", "a new connection is a new match (the lock taken once)");
            Check(r.StartedAt == T0.AddSeconds(-5), "it began at its first log line");
            Check(r.SeeLog(Conn("#1", T0.AddSeconds(-5), "11"), game, T0.AddSeconds(20), T0.AddSeconds(20), lk) && locks == 1, "the same connection is the same match");
            Check(!r.Due(T0.AddSeconds(59)), "59 s: not yet");
            Check(r.Due(T0.AddSeconds(60)), "60 s: due");
            r.MarkSent();
            Check(!r.Due(T0.AddSeconds(80)), "never twice");
            r.NoMatch(T0.AddSeconds(90));
            r.SeeLog(Conn("#1", T0.AddSeconds(-5), "11"), game, T0.AddSeconds(100), T0.AddSeconds(100), lk);
            Check(r.Sent && locks == 1, "a log connection stays one match after a no-match check");
            Check(r.SeeLog(Conn("#2", T0.AddMinutes(40), "22"), game, T0.AddMinutes(40), T0.AddMinutes(40), lk) && !r.Sent && locks == 2, "a new connection id: a new match");
            // filters: an earlier run of the game, a match long over, no id
            var f = new MatchReporter(null, DateTime.MinValue);
            Check(!f.SeeLog(Conn("#3", game.AddMinutes(-1), "1"), game, T0, T0, lk) && f.Key == null, "from before the game started: ignored");
            Check(!f.SeeLog(Conn("#3", T0.AddMinutes(-20), "1"), game, T0, T0, lk) && f.Key == null, "20 minutes old: ignored");
            Check(!f.SeeLog(Conn("", T0, "1"), game, T0, T0, lk), "no connection id: ignored");
            // the last reported match survives a restart
            var again = new MatchReporter("#1", T0);
            Check(again.SeeLog(Conn("#1", T0, "11"), game, T0, T0, lk) && again.Sent, "reported before the restart: not again");
            var capAgain = new MatchReporter("dc:fra", T0);
            Check(!capAgain.SeeCapture("dc:fra", DateTime.MinValue, T0.AddMinutes(2), lk) && capAgain.Sent, "M8: a capture match reported before the restart: not again");
            // what was learned belongs to the current, unsent match
            var s = new MatchReporter(null, DateTime.MinValue);
            s.SetLog(new MatchSeen { Dc = "fra" });
            Check(s.Log == null, "no match: ignored");
            s.SeeLog(Conn("#5", T0, "5"), game, T0, T0, lk);
            s.SetLog(new MatchSeen { Dc = "fra" });
            Check(s.Log != null && s.Log.Dc == "fra", "kept for the current match");
            s.SeeLog(Conn("#6", T0.AddMinutes(1), "6"), game, T0.AddMinutes(1), T0.AddMinutes(1), lk);
            Check(s.Log == null && s.Capture == null, "a new match starts empty");
        }

        // I1: a console.log left from an earlier -condebug run doesn't stop the checks from starting matches
        static void MatchReporterLogCountsOnlyWhenItGaveThisRunAConnection()
        {
            var game = T0.AddMinutes(-10);
            var r = new MatchReporter(null, DateTime.MinValue);
            Check(!r.LogOn(game), "nothing from the log yet: off");
            Check(!r.SeeLog(Conn("#old", game.AddHours(-3), "1"), game, T0, T0, null) && !r.LogOn(game), "an old run's connection: still off");
            Check(r.SeeCapture("dc:fra", game, T0, null), "so the check starts the match");
            Check(r.SeeLog(Conn("#7", T0.AddMinutes(5), "7"), game, T0.AddMinutes(5), T0.AddMinutes(5), null) && r.LogOn(game), "a connection of this run: on");
            Check(!r.LogOn(T0.AddMinutes(30)), "the game started again later: off until it gives one");
            // the log read after the check started the same match: the same match (adopted), not a second one
            var a = new MatchReporter(null, DateTime.MinValue);
            a.SeeCapture("relay:dxb", game, T0, null);
            Check(a.SeeLog(Conn("#8", T0.AddSeconds(-40), "8"), game, T0.AddSeconds(20), T0.AddSeconds(20), null) && a.Key == "#8" && a.FromLog &&
                  a.StartedAt == T0.AddSeconds(-40), "adopted by the log, begun at its first line");
            Check(a.Due(T0.AddSeconds(60)), "still due a minute after it was first seen");
        }

        // I2: a check's server changing within a match is the same match; a gap or a long silence starts a new one
        static void MatchReporterCaptureMatchesEndOnAGap()
        {
            var r = new MatchReporter(null, DateTime.MinValue);
            Check(r.SeeCapture("relay:dxb", DateTime.MinValue, T0, null) && r.StartedAt == T0, "first capture: new, begun now");
            Check(!r.SeeCapture("dc:fra", DateTime.MinValue, T0.AddMinutes(1), null) && r.Key == "dc:fra", "relay then datacenter: the same match");
            Check(!r.SeeCapture("relay:bom2", DateTime.MinValue, T0.AddMinutes(2), null), "another relay: the same match");
            r.MarkSent();
            Check(!r.SeeCapture("dc:fra", DateTime.MinValue, T0.AddMinutes(4), null) && r.Sent, "one report");
            r.NoMatch(T0.AddMinutes(30));
            Check(r.SeeCapture("dc:fra", DateTime.MinValue, T0.AddMinutes(31), null) && !r.Sent, "after a no-match check: a new match");
            // re-queued from inside the game (no check in between): a long silence on a match old enough
            var q = new MatchReporter(null, DateTime.MinValue);
            q.SeeCapture("dc:fra", DateTime.MinValue, T0, null);
            Check(!q.SeeCapture("dc:fra", DateTime.MinValue, T0.AddMinutes(10), null), "10 minutes later (match 10 min old): the same match");
            Check(!q.SeeCapture("dc:fra", DateTime.MinValue, T0.AddMinutes(14), null), "4 minutes of silence: the same match");
            Check(q.SeeCapture("dc:fra", DateTime.MinValue, T0.AddMinutes(40), null), "26 minutes of silence on a 40-minute-old match: a new one");
        }

        // I3: the queue starts at the lock, or at the newest check that saw no match after the match before
        static void MatchReporterQueueTime()
        {
            var game = T0.AddHours(-1);
            Func<MatchLock> lk = EuropeLock; // locked at T0 - 7 min
            var r = new MatchReporter(null, DateTime.MinValue);
            r.SeeLog(Conn("#1", T0, "1"), game, T0, T0, lk);
            Check(r.QueueFrom == T0.AddMinutes(-7), "first match after the lock: from the lock");
            // second match under the same lock, nothing saw the end of the first: unknown, not 40 minutes
            r.SeeLog(Conn("#2", T0.AddMinutes(40), "2"), game, T0.AddMinutes(40), T0.AddMinutes(40), lk);
            Check(r.QueueFrom == DateTime.MinValue, "a later match with no end seen: unknown");
            var d = MatchReport.Data("Deadlock", r.Lock, null, r.StartedAt, r.QueueFrom, T0.AddMinutes(41), -1);
            Check(d["queue_s"] == null, "queue_s null");
            // a check saw the menu between: from that check
            r.NoMatch(T0.AddMinutes(75));
            r.SeeLog(Conn("#3", T0.AddMinutes(80), "3"), game, T0.AddMinutes(80), T0.AddMinutes(80), lk);
            Check(r.QueueFrom == T0.AddMinutes(75), "after a no-match check: from it");
            d = MatchReport.Data("Deadlock", r.Lock, null, r.StartedAt, r.QueueFrom, T0.AddMinutes(81), -1);
            Check((int)d["queue_s"] == 300, "5 minutes: " + d["queue_s"]);
            // unlocked: from the game's start for the first match of the run
            var u = new MatchReporter(null, DateTime.MinValue);
            u.SeeLog(Conn("#4", T0, "4"), game, T0, T0, () => new MatchLock());
            Check(u.QueueFrom == game, "unlocked, first match: from the game's start");
            // matches only the checks saw: their start is late, so no queue time
            var c = new MatchReporter(null, DateTime.MinValue);
            c.SeeCapture("dc:fra", game, T0, lk);
            Check(c.QueueFrom == DateTime.MinValue, "capture: unknown");
        }

        // M7: a reconnect to the same match server is the same match; another server is a new one
        static void MatchReporterReconnectIsTheSameMatch()
        {
            var game = T0.AddHours(-1);
            var r = new MatchReporter(null, DateTime.MinValue);
            r.SeeLog(Conn("#1", T0, "900"), game, T0, T0, EuropeLock);
            r.MarkSent();
            Check(r.SeeLog(Conn("#2", T0.AddMinutes(20), "900"), game, T0.AddMinutes(20), T0.AddMinutes(20), EuropeLock) && r.Sent && r.Key == "#2",
                  "the same server 20 minutes in: a reconnect, not reported again");
            Check(r.SeeLog(Conn("#3", T0.AddMinutes(45), "901"), game, T0.AddMinutes(45), T0.AddMinutes(45), EuropeLock) && !r.Sent, "another server: a new match");
            var n = new MatchReporter(null, DateTime.MinValue);
            n.SeeLog(Conn("#1", T0, "900"), game, T0, T0, EuropeLock);
            n.MarkSent();
            n.NoMatch(T0.AddMinutes(30));
            Check(n.SeeLog(Conn("#2", T0.AddMinutes(35), "900"), game, T0.AddMinutes(35), T0.AddMinutes(35), EuropeLock) && !n.Sent,
                  "the same server after a check saw no match: a new match");
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
            Check(m.OnPicked && MatchReport.Verdict(new MatchLock { Locked = true }, m, T0) == "inside", "inside an SE Asia lock");
            Check(m.Backs.Count == 2 && m.Backs[0].Key == "bom2" && m.Backs[0].Value == 27, "the backs");
            var eu = MatchReport.FromLog(g, set, primary, new HashSet<string> { "fra" });
            Check(!eu.OnPicked && MatchReport.Verdict(new MatchLock { Locked = true }, eu, T0) == "outside", "outside a Frankfurt-only lock");
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
            Check(MatchReport.Verdict(new MatchLock { Locked = true }, m, T0) == "unknown" && MatchReport.Verdict(new MatchLock(), m, T0) == "nolock", "unknown / nolock");
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
            var d = MatchReport.Data("Deadlock", lk, m, start, lk.LockedAt, lk.LockedAt.AddSeconds(900), 66);
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
            Check(MatchReport.Detail(lk, m, start) == "outside, dxb, log_located", "detail: " + MatchReport.Detail(lk, m, start));
            // no lock, nothing known: empty lists, nulls, the live ping only when given
            var gameStart = T0.AddMinutes(-3);
            d = MatchReport.Data("Deadlock", new MatchLock { DubaiWayIn = true }, null, T0, gameStart, T0.AddMinutes(1), -1);
            Check(!(bool)d["locked"] && ((string[])d["picked"]).Length == 0 && ((string[])d["gateways_open"]).Length == 0, "no lock: empty lists");
            Check(d["expected_ms"] == null && d["expected_via"] == null && d["dc"] == null && d["actual_ms"] == null && d["lock_age_s"] == null,
                  "unknowns are null");
            Check((string)d["verdict"] == "nolock" && (string)d["source"] == "none", "nolock, none");
            Check((int)d["queue_s"] == 180, "unlocked: queue from the game's start");
            // M2: the live ping (to the locked server) only for a match on the lock's servers
            var inside = new MatchSeen { Dc = "fra", OnPicked = true, Source = MatchReport.SourceCapture };
            d = MatchReport.Data("Deadlock", lk, inside, T0, DateTime.MinValue, T0, 66);
            Check((int)d["actual_ms"] == 66 && (string)d["verdict"] == "inside", "the live ping for a match inside the lock");
            d = MatchReport.Data("Deadlock", lk, new MatchSeen(), T0, DateTime.MinValue, T0, 66);
            Check(d["actual_ms"] == null && (string)d["verdict"] == "unknown", "no live ping for a match somewhere unknown");
            Check(MatchReport.ActualMs(lk, new MatchSeen { Dc = "sgp" }, T0, 66) == -1, "...or outside the lock");
            // M4: a lock applied after the match began couldn't steer it
            Check(MatchReport.Verdict(lk, inside, lk.LockedAt.AddSeconds(-1)) == "unknown", "lock after the match began: unknown");
            // M1/M5: the row's server never falls back to the lock's names
            Check(MatchReport.RowServer(inside) == "fra" && MatchReport.RowServer(new MatchSeen { Entry = "dxb" }) == "via dxb" &&
                  MatchReport.RowServer(new MatchSeen()) == "?" && MatchReport.RowServer(null) == "?", "row server");
            d = MatchReport.Data("Deadlock", new MatchLock(), null, T0, DateTime.MinValue, T0, -1);
            Check(d["queue_s"] == null, "no lock, game start unknown: no queue time");
        }

        // M9: a Europe lock that kept Dubai open as the way in, and a match in Dubai: outside (the dashboard reads it
        // with gateways_open); the expected route's entry as an id; a capture judged by the lock's snapshot
        static void MatchReportGatewayAndExpectedVia()
        {
            var g = SavedDeadlock();
            var picks = RegionIds(g, "Europe");
            var plan = StrictLock.For(g, picks, RiyadhPings(), true);
            Check(plan.Gateways.Contains("dxb"), "the Europe lock keeps Dubai open: " + string.Join(",", plan.Gateways.ToArray()));
            string via, viaId;
            int ms = LockScope.Expected(g, new HashSet<string>(picks), plan.Close, plan.Gateways, RiyadhPings(), null, out via, out viaId);
            Check(ms == (int)Math.Round(plan.Locked.Ms) && viaId == "dxb" && via != null && via != "dxb", "expected " + ms + " via id " + viaId + " (" + via + ")");
            string via2;
            Check(LockScope.Expected(g, new HashSet<string>(picks), plan.Close, plan.Gateways, RiyadhPings(), null, out via2) == ms && via2 == via, "the old overload agrees");
            ConsoleLine primary;
            var set = Sessions("10/14 12:45:22 [SteamNetSockets] [#2801803541 SDR server steamid:1 vport 0] Requesting session from bom2#91 (155.133.224.21:27059).  Ping = 210 = 183+27 (front+back).\n" +
                               "10/14 12:45:22 [SteamNetSockets] [#2801803541 SDR server steamid:1 vport 0] Requesting session from sgp#103 (103.10.124.118:27025).  Ping = 293 = 217+76 (front+back).", out primary);
            Check(set.Server == "1", "the match server's id is kept (only compared)");
            var m = MatchReport.FromLog(g, set, primary, new HashSet<string>(picks));
            var lk = new MatchLock { Locked = true, Picked = picks, Gateways = plan.Gateways.ToList(), LockedAt = T0.AddMinutes(-30) };
            Check(m.Dc == "dxb" && MatchReport.Verdict(lk, m, T0) == "outside", "a match on the kept gateway is outside the lock");
            // M3: the capture is judged by the lock the match had
            var r = new MatchReporter(null, DateTime.MinValue);
            r.SeeCapture("dc:sgp", DateTime.MinValue, T0, () => lk);
            var cap = new MatchSeen { Dc = "sgp", OnPicked = true, Source = MatchReport.SourceCapture };
            r.SetCapture(cap);
            Check(!r.Capture.OnPicked, "Singapore is outside the snapshot's Europe picks, whatever the lock on now says");
        }

        static void RunMatchReportTests()
        {
            Run("MatchReporterOncePerMatchAfterAMinute", MatchReporterOncePerMatchAfterAMinute);
            Run("MatchReporterLogCountsOnlyWhenItGaveThisRunAConnection", MatchReporterLogCountsOnlyWhenItGaveThisRunAConnection);
            Run("MatchReporterCaptureMatchesEndOnAGap", MatchReporterCaptureMatchesEndOnAGap);
            Run("MatchReporterQueueTime", MatchReporterQueueTime);
            Run("MatchReporterReconnectIsTheSameMatch", MatchReporterReconnectIsTheSameMatch);
            Run("MatchReportGatewayAndExpectedVia", MatchReportGatewayAndExpectedVia);
            Run("MatchReportFromTheConsoleLog", MatchReportFromTheConsoleLog);
            Run("MatchReportMergesTheCapture", MatchReportMergesTheCapture);
            Run("MatchReportDataContract", MatchReportDataContract);
        }
    }
}
