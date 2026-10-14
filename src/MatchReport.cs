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
    // by then). Pure: the clock is passed in.
    // - Deadlock's console log (when it gave a connection in this run of the game, LogOn): a new connection id is a new
    //   match, except a reconnect (the same match server, see Reconnect).
    // - Otherwise the server checks (only while 2X Ping is in front): a new match only after a check that saw no match,
    //   or after a long silence (no sighting for CaptureSilence once the current match is MinMatch old). A different
    //   server seen without either is the same match (the game's primary relay or busiest address can change).
    class MatchReporter
    {
        public static readonly TimeSpan Delay = TimeSpan.FromSeconds(60);
        // a log connection whose newest line is older than this when first read is a match long over: not reported
        public static readonly TimeSpan Stale = TimeSpan.FromMinutes(15);
        public static readonly TimeSpan CaptureSilence = TimeSpan.FromMinutes(5);
        public static readonly TimeSpan MinMatch = TimeSpan.FromMinutes(15);
        // a new connection to the same match server this soon after the match began is a reconnect, not a new match
        public static readonly TimeSpan Reconnect = TimeSpan.FromMinutes(50);
        // a log connection that begins this close to a match the checks started is that match
        public static readonly TimeSpan Adopt = TimeSpan.FromMinutes(3);

        string key;            // the current match: a log connection id ("#…") or a capture key; null = none
        string server;         // its match server in the log (SessionSet.Server), null = unknown
        bool sent, gap, fromLog;
        DateTime seenAt;       // when the app first saw it
        DateTime lastCaptureAt = DateTime.MinValue;  // the newest check that saw the current (capture) match
        DateTime lastNoMatchAt = DateTime.MinValue;  // the newest check that saw no match
        DateTime prevStart = DateTime.MinValue;      // when the match before the current one began
        DateTime logConnAt = DateTime.MinValue;      // the first line of the newest connection the log gave
        public DateTime StartedAt { get; private set; } // when it began (its first log line, or the first check that saw it)
        public DateTime QueueFrom { get; private set; } // when its queue began (MinValue = unknown, see Start)
        public MatchLock Lock { get; private set; }     // the lock when it was first seen
        public MatchSeen Log { get; private set; }      // the newest the console log says about it
        public MatchSeen Capture { get; private set; }  // the newest a capture said about it

        // lastSent / lastAt: the match reported last and when it began (kept across launches, so a restart doesn't
        // report it again)
        public MatchReporter(string lastSent, DateTime lastAt)
        {
            QueueFrom = DateTime.MinValue;
            if (string.IsNullOrEmpty(lastSent)) return;
            key = lastSent;
            sent = true;
            fromLog = lastSent.StartsWith("#", StringComparison.Ordinal);
            StartedAt = lastAt;
            if (!fromLog) lastCaptureAt = lastAt;
        }

        public string Key { get { return key; } }
        public bool Sent { get { return sent; } }
        public bool FromLog { get { return fromLog; } }

        // The log gave a connection in this run of the game (gameStart: when it started, MinValue = unknown): it tells
        // the matches, the checks only fill in. A console.log left from an earlier -condebug run doesn't count.
        public bool LogOn(DateTime gameStart)
        {
            return logConnAt != DateTime.MinValue && (gameStart == DateTime.MinValue || logConnAt >= gameStart);
        }

        // The log's newest connection (read at readAt). True when it is the current match (new or not): the caller then
        // tells what it says (SetLog). Ignored: no id or time, from before this run of the game, or (new) long over.
        public bool SeeLog(SessionSet set, DateTime gameStart, DateTime readAt, DateTime now, Func<MatchLock> lockNow)
        {
            if (set == null || string.IsNullOrEmpty(set.Conn) || set.At == DateTime.MinValue || set.Since == DateTime.MinValue) return false;
            if (gameStart != DateTime.MinValue && set.Since < gameStart) return false;
            if (set.Conn == key) { logConnAt = set.Since; return true; }
            if (readAt - set.At > Stale) return false;
            logConnAt = set.Since;
            // a reconnect to the same match server, with no check that saw no match in between
            if (key != null && fromLog && set.Server != null && set.Server == server && set.Since - StartedAt <= Reconnect &&
                lastNoMatchAt < StartedAt)
            {
                key = set.Conn;
                return true;
            }
            // the match the checks already started (the log was read later): the same match
            if (key != null && !fromLog && !gap && set.Since - StartedAt <= Adopt && StartedAt - set.Since <= Adopt)
            {
                key = set.Conn;
                fromLog = true;
                server = set.Server;
                if (set.Since < StartedAt) StartedAt = set.Since;
                return true;
            }
            Start(set.Conn, true, set.Since, now, gameStart, lockNow);
            server = set.Server;
            return true;
        }

        // A check saw a match on capture key k (a datacenter, an entry relay or a server). True = a new match.
        public bool SeeCapture(string k, DateTime gameStart, DateTime now, Func<MatchLock> lockNow)
        {
            if (string.IsNullOrEmpty(k)) return false;
            bool isNew = key == null || fromLog || gap || lastCaptureAt == DateTime.MinValue ||
                         (now - lastCaptureAt > CaptureSilence && now - StartedAt > MinMatch);
            lastCaptureAt = now;
            if (!isNew) { key = k; return false; }
            Start(k, false, now, now, gameStart, lockNow);
            return true;
        }

        // The queue began at the lock (or the game's start without one) or, when a check saw no match since the match
        // before, at that check, whichever is later. Unknown when neither tells (a later match whose start of queue no
        // check saw) and for matches only the checks saw (they see a match late, so its start is unknown too).
        void Start(string k, bool log, DateTime startedAt, DateTime now, DateTime gameStart, Func<MatchLock> lockNow)
        {
            prevStart = key != null ? StartedAt : DateTime.MinValue;
            key = k;
            fromLog = log;
            sent = gap = false;
            seenAt = now;
            StartedAt = startedAt > now ? now : startedAt;
            Lock = lockNow != null ? lockNow() : new MatchLock();
            Log = Capture = null;
            server = null;
            QueueFrom = DateTime.MinValue;
            if (!log) return;
            DateTime start = Lock.Locked ? Lock.LockedAt : gameStart;
            if (start == DateTime.MinValue) return;
            if (prevStart == DateTime.MinValue || prevStart < start) QueueFrom = lastNoMatchAt > start ? lastNoMatchAt : start;
            else if (lastNoMatchAt > prevStart) QueueFrom = lastNoMatchAt;
        }

        // A check saw the game in no match (or closed): the next match the checks see is a new one.
        public void NoMatch(DateTime now)
        {
            gap = true;
            lastNoMatchAt = now;
        }

        // What the log or a capture says about the current match (ignored when there is none, or it was reported).
        public void SetLog(MatchSeen m) { if (key != null && !sent && Lock != null) Log = m; }

        // A capture's datacenter is judged by the lock the match had (the snapshot), not the one on now.
        public void SetCapture(MatchSeen m)
        {
            if (key == null || sent || Lock == null || m == null) return;
            if (Lock.Locked && m.Dc != null) m.OnPicked = Lock.Picked.Contains(m.Dc);
            Capture = m;
        }

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

        // inside / outside the lock the match was first seen with; unknown when its datacenter is unknown or the lock
        // was applied after the match began (it couldn't steer matchmaking); nolock without one.
        public static string Verdict(MatchLock lk, MatchSeen m, DateTime startedAt)
        {
            if (lk == null || !lk.Locked) return "nolock";
            if (m == null || m.Dc == null) return "unknown";
            if (lk.LockedAt != DateTime.MinValue && startedAt != DateTime.MinValue && lk.LockedAt > startedAt) return "unknown";
            return m.OnPicked ? "inside" : "outside";
        }

        // The ping the report gives (-1 = unknown): the match's own, else the live ping (it follows the locked server)
        // only when the match was on the lock's servers.
        public static int ActualMs(MatchLock lk, MatchSeen m, DateTime startedAt, int liveMs)
        {
            if (m != null && m.ActualMs >= 0) return m.ActualMs;
            return Verdict(lk, m, startedAt) == "inside" ? liveMs : -1;
        }

        // The event row's server column: the datacenter, else "via <entry relay>", else "?" (never the lock's names).
        public static string RowServer(MatchSeen m)
        {
            if (m != null && m.Dc != null) return m.Dc;
            return m != null && m.Entry != null ? "via " + m.Entry : "?";
        }

        // The event's data. queueFrom: when the wait for this match began (MatchReporter.QueueFrom, MinValue = unknown).
        // liveMs: the live ping (-1 = unknown), see ActualMs.
        public static Dictionary<string, object> Data(string game, MatchLock lk, MatchSeen m, DateTime startedAt, DateTime queueFrom,
                                                      DateTime now, int liveMs)
        {
            lk = lk ?? new MatchLock();
            m = m ?? new MatchSeen();
            var backs = new Dictionary<string, object>();
            foreach (var kv in m.Backs) backs[kv.Key] = kv.Value;
            int actual = ActualMs(lk, m, startedAt, liveMs);
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
                { "verdict", Verdict(lk, m, startedAt) },
                { "queue_s", queueFrom != DateTime.MinValue && startedAt >= queueFrom ? (object)(int)Math.Round((startedAt - queueFrom).TotalSeconds) : null },
                { "lock_age_s", lk.Locked && lk.LockedAt != DateTime.MinValue && now >= lk.LockedAt ? (object)(int)Math.Round((now - lk.LockedAt).TotalSeconds) : null },
            };
        }

        // The event's one-line detail: "inside, dxb, log_located".
        public static string Detail(MatchLock lk, MatchSeen m, DateTime startedAt)
        {
            m = m ?? new MatchSeen();
            return Verdict(lk, m, startedAt) + ", " + (m.Dc ?? "?") + ", " + (m.Source ?? SourceNone);
        }
    }
}
