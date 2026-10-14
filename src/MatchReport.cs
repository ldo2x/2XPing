using System;
using System.Collections.Generic;
using System.Linq;

namespace PingBoost
{
    // The lock as it was when a match was first seen (a lock changed during the match doesn't apply to it).
    class MatchLock
    {
        public bool Locked;
        public List<string> Picked = new List<string>();        // datacenter ids the lock was applied with
        public List<string> PickedRegions = new List<string>(); // their matchmaking regions ("Europe")
        public List<string> Closed = new List<string>();        // entry relays the lock closed (LockPlan.Close)
        public List<string> Gateways = new List<string>();      // gateways kept open (LockPlan.Gateways)
        public bool DubaiWayIn;                                  // the setting when the lock was applied
        public int ExpectedMs = -1;                              // the card's expected ping, -1 = unknown
        public string ExpectedVia;                               // the POP id the expected route enters at, null = direct/unknown
        public DateTime LockedAt = DateTime.MinValue;            // when the lock was applied

        public HashSet<string> Picks { get { return Locked ? new HashSet<string>(Picked) : null; } }
    }

    // What the app learned about one match: from the game's console log or from a traffic capture.
    class MatchSeen
    {
        public string Dc;       // the datacenter id, null = unknown
        public string Region;   // its matchmaking region key, null = unknown
        public bool OnPicked;   // the lock (MatchLock.Picks) allows it
        public string Source = MatchReport.SourceNone;
        public string Entry;    // the relay the game entered by, null = unknown
        public int ActualMs = -1; // the game's own ping (front+back of the primary session), -1 = unknown
        public readonly List<KeyValuePair<string, int>> Backs = new List<KeyValuePair<string, int>>();
    }

    // When to send a match report: once per match, a minute after it was first seen (the log's session lines are in
    // by then). A match is a new console-log connection id, or a server the capture saw after a check that saw no match.
    // Pure: the clock is passed in.
    class MatchReporter
    {
        public static readonly TimeSpan Delay = TimeSpan.FromSeconds(60);
        // a log connection whose newest line is older than this when first read is a match long over: not reported
        public static readonly TimeSpan Stale = TimeSpan.FromMinutes(15);

        string key;            // the current match; null = none
        bool sent, gap;        // reported already; a check saw no match since the capture key was last seen
        DateTime seenAt;       // when the app first saw it
        public DateTime StartedAt { get; private set; } // when it began (its first log line, or seenAt)
        public MatchLock Lock { get; private set; }     // the lock when it was first seen
        public MatchSeen Log { get; private set; }      // the newest the console log says about it
        public MatchSeen Capture { get; private set; }  // the newest a capture said about it

        // lastSent: the connection id reported last (kept across launches, so a restart doesn't report it again)
        public MatchReporter(string lastSent)
        {
            if (!string.IsNullOrEmpty(lastSent)) { key = lastSent; sent = true; }
        }

        public string Key { get { return key; } }
        public bool Sent { get { return sent; } }

        // A match seen. fromLog: key is a console-log connection id (unique: the same id is always the same match);
        // otherwise a capture's server, which is a new match only after a check that saw no match. startedAt: when it
        // began (MinValue = now). lockNow: the lock right now (kept only for a new match). True = a new match.
        public bool See(string newKey, bool fromLog, DateTime startedAt, DateTime now, Func<MatchLock> lockNow)
        {
            if (string.IsNullOrEmpty(newKey)) return false;
            bool same = newKey == key && (fromLog || !gap);
            gap = false;
            if (same) return false;
            key = newKey;
            sent = false;
            seenAt = now;
            StartedAt = startedAt == DateTime.MinValue || startedAt > now ? now : startedAt;
            Lock = lockNow != null ? lockNow() : null;
            Log = Capture = null;
            return true;
        }

        // A check saw the game in no match: the next capture of the same server is a new match.
        public void NoMatch() { gap = true; }

        // What the log or a capture says about the current match (ignored when there is none, or it was reported).
        public void SetLog(MatchSeen m) { if (key != null && !sent) Log = m; }
        public void SetCapture(MatchSeen m) { if (key != null && !sent) Capture = m; }

        public bool Due(DateTime now) { return key != null && !sent && Lock != null && now - seenAt >= Delay; }

        public void MarkSent() { sent = true; }
    }

    // Pure: the "match" event's data (see SECURITY.md): what the lock promised and where the match really was.
    static class MatchReport
    {
        public const string SourceColocated = "log_colocated", SourceLocated = "log_located", SourceCapture = "capture",
                            SourceRelay = "relay_only", SourceNone = "none";

        // The matchmaking region a datacenter's matches belong to: Deadlock's region_mode ("SEAsia"), another game's
        // region key; null when unknown.
        public static string RegionKey(Game g, string id)
        {
            if (g == null || id == null) return null;
            string mm;
            if (g.HostPops != null && DeadlockData.Hosting.TryGetValue(id, out mm)) return mm;
            var def = g.RegionMap != null ? g.RegionMap.FirstOrDefault(d => d.Ids.Contains(id)) : null;
            return def != null ? def.Key : null;
        }

        public static List<string> RegionKeys(Game g, IEnumerable<string> ids)
        {
            return (ids ?? new string[0]).Select(id => RegionKey(g, id)).Where(k => k != null).Distinct().ToList();
        }

        // What Deadlock's console log says about its newest connection: the datacenter of a relay next to the server
        // (back <= ColocatedMs), or else the place its session delays point to (GameConsole.Locate); the primary relay
        // (the newest "as primary" line, when written for this connection) and the game's own ping through it
        // (front+back; without a primary line the lowest session ping). picks: the lock's (MatchLock.Picks).
        public static MatchSeen FromLog(Game g, SessionSet set, ConsoleLine primary, HashSet<string> picks)
        {
            var m = new MatchSeen();
            if (g == null || set == null) return m;
            m.Backs.AddRange(set.Backs);
            string dc = null;
            foreach (var kv in set.Backs.Where(kv => kv.Value <= GameConsole.ColocatedMs))
            {
                string id, region;
                Region row;
                bool on;
                if (DeadlockLog.Resolve(g, new ConsoleLine { Code = kv.Key }, picks, out id, out row, out region, out on))
                {
                    dc = id;
                    m.OnPicked = on;
                    m.Source = SourceColocated;
                    break;
                }
            }
            if (dc == null && g.Routes != null)
            {
                string at = GameConsole.Locate(set, g.Routes.Ids, g.Routes.Dist);
                string id, region;
                Region row;
                bool on;
                if (at != null && DeadlockLog.Resolve(g, new ConsoleLine { Code = at }, picks, out id, out row, out region, out on))
                {
                    dc = id;
                    m.OnPicked = on;
                    m.Source = SourceLocated;
                }
            }
            m.Dc = dc;
            m.Region = RegionKey(g, dc);
            if (primary != null && primary.At != DateTime.MinValue && primary.At >= set.Since) m.Entry = primary.Code;
            int ms = -1;
            if (m.Entry != null) foreach (var kv in set.Pings) if (kv.Key == m.Entry) ms = kv.Value;
            if (ms < 0 && set.Pings.Count > 0) ms = set.Pings.Min(kv => kv.Value);
            m.ActualMs = ms;
            if (dc == null && m.Entry != null) m.Source = SourceRelay;
            return m;
        }

        // The log's answer, or the capture's where the log names no datacenter (the capture's entry relay or the log's
        // backs fill what the other lacks); null when neither said anything.
        public static MatchSeen Merge(MatchSeen log, MatchSeen capture)
        {
            if (log == null) return capture;
            if (capture == null || log.Dc != null) return log;
            if (capture.Dc != null)
            {
                var m = new MatchSeen
                {
                    Dc = capture.Dc, Region = capture.Region, OnPicked = capture.OnPicked, Source = capture.Source,
                    Entry = log.Entry ?? capture.Entry, ActualMs = log.ActualMs >= 0 ? log.ActualMs : capture.ActualMs,
                };
                m.Backs.AddRange(log.Backs);
                return m;
            }
            if (log.Entry == null && capture.Entry != null)
            {
                var m = new MatchSeen { Source = SourceRelay, Entry = capture.Entry, ActualMs = log.ActualMs >= 0 ? log.ActualMs : capture.ActualMs };
                m.Backs.AddRange(log.Backs);
                return m;
            }
            return log;
        }

        public static string Verdict(MatchLock lk, MatchSeen m)
        {
            if (lk == null || !lk.Locked) return "nolock";
            if (m == null || m.Dc == null) return "unknown";
            return m.OnPicked ? "inside" : "outside";
        }

        // The event's data. queueFrom: when the wait for this match began (the lock, or the game's start without one;
        // MinValue = unknown). liveMs: the live ping, used when the match gave none (-1 = unknown).
        public static Dictionary<string, object> Data(string game, MatchLock lk, MatchSeen m, DateTime startedAt, DateTime gameStart,
                                                      DateTime now, int liveMs)
        {
            lk = lk ?? new MatchLock();
            m = m ?? new MatchSeen();
            var backs = new Dictionary<string, object>();
            foreach (var kv in m.Backs) backs[kv.Key] = kv.Value;
            DateTime queueFrom = lk.Locked ? lk.LockedAt : gameStart;
            int actual = m.ActualMs >= 0 ? m.ActualMs : liveMs;
            return new Dictionary<string, object>
            {
                { "game", game },
                { "locked", lk.Locked },
                { "picked", lk.Locked ? lk.Picked.ToArray() : new string[0] },
                { "picked_regions", lk.Locked ? lk.PickedRegions.ToArray() : new string[0] },
                { "closed", lk.Locked ? lk.Closed.ToArray() : new string[0] },
                { "gateways_open", lk.Locked ? lk.Gateways.ToArray() : new string[0] },
                { "dubai_way_in", lk.DubaiWayIn },
                { "expected_ms", lk.Locked && lk.ExpectedMs >= 0 ? (object)lk.ExpectedMs : null },
                { "expected_via", lk.Locked ? lk.ExpectedVia : null },
                { "dc", m.Dc },
                { "region", m.Region },
                { "source", m.Source ?? SourceNone },
                { "entry", m.Entry },
                { "actual_ms", actual >= 0 ? (object)actual : null },
                { "backs", backs },
                { "verdict", Verdict(lk, m) },
                { "queue_s", queueFrom != DateTime.MinValue && startedAt >= queueFrom ? (object)(int)Math.Round((startedAt - queueFrom).TotalSeconds) : null },
                { "lock_age_s", lk.Locked && lk.LockedAt != DateTime.MinValue && now >= lk.LockedAt ? (object)(int)Math.Round((now - lk.LockedAt).TotalSeconds) : null },
            };
        }

        // The event's one-line detail: "inside, dxb, log_located".
        public static string Detail(MatchLock lk, MatchSeen m)
        {
            m = m ?? new MatchSeen();
            return Verdict(lk, m) + ", " + (m.Dc ?? "?") + ", " + (m.Source ?? SourceNone);
        }
    }
}
