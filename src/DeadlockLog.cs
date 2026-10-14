using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace PingBoost
{
    // Deadlock's own console log (GameConsole, the player starts the game with -condebug): found from the game's exe
    // while it runs, looked for again at most every 10 minutes while not found. Read by the 20 s follower and by each
    // server check (on background threads; one lock keeps them apart).
    sealed class DeadlockLog
    {
        readonly Func<Game, string> logPath;          // where the game writes its log (from its exe), null = unknown
        readonly Func<Game, DateTime> gameStart;      // when the running game started (MinValue = unknown)
        readonly Action<Exception> crashed;
        readonly Func<DateTime> now;
        readonly object gate = new object();
        GameConsole log;
        DateTime lookup = DateTime.MinValue;
        volatile bool on;
        // the last time a server check saw the game in no match: a console line from before it is about an earlier match
        DateTime noMatchAt = DateTime.MinValue;

        public DeadlockLog(Action<Exception> crashed)
            : this(g => GameConsole.PathFromExe(SystemInfo.FindExe(g)), StartOf, crashed, () => DateTime.Now) { }

        // tests: a fake log path, game start and clock
        public DeadlockLog(Func<Game, string> logPath, Func<Game, DateTime> gameStart, Action<Exception> crashed, Func<DateTime> now)
        {
            this.logPath = logPath;
            this.gameStart = gameStart;
            this.crashed = crashed;
            this.now = now;
        }

        // the log file exists: no "-condebug" hint needed
        public bool On { get { return on; } }

        public bool Peeked;   // the saved exe path was looked at once (PeekSaved)
        public volatile bool Following; // a 20 s follower read is running

        // Before the first check reads it: is the log there, from the exe path saved last time? Background thread.
        // True = it is (On is set now).
        public bool PeekSaved(string exe)
        {
            string path = GameConsole.PathFromExe(exe);
            if (path != null && File.Exists(path) && !on) { on = true; return true; }
            return false;
        }

        static DateTime StartOf(Game g)
        {
            var start = DateTime.MinValue;
            foreach (var name in g.Processes)
                foreach (var p in Process.GetProcessesByName(name))
                    try { if (start == DateTime.MinValue || p.StartTime < start) start = p.StartTime; }
                    catch { }
                    finally { p.Dispose(); }
            return start;
        }

        // Background: reads what Deadlock added to its console log since the last time. The newest match datacenter it
        // named (null = none), when the running game started (MinValue = unknown) and when the log was read (MinValue =
        // not read: no log).
        public ConsoleLine Read(Game g, out DateTime started, out DateTime readAt)
        {
            started = readAt = DateTime.MinValue;
            if (!DeadlockData.Is(g)) return null;
            try
            {
                started = gameStart(g);
                lock (gate)
                {
                    if (log == null && now() - lookup > TimeSpan.FromMinutes(10))
                    {
                        lookup = now();
                        string path = logPath(g);
                        if (path != null) log = new GameConsole(path);
                    }
                    if (log == null) return null;
                    readAt = now();
                    log.Read(readAt);
                    // a read that failed for a moment (the game writing) doesn't turn the log off: only a missing file does
                    on = log.Exists;
                    return log.LastDc;
                }
            }
            catch (Exception ex)
            {
                crashed(ex);
                return null;
            }
        }

        // UI: a check saw no match, so a console line read up to now (in this check or before) is about an earlier match.
        public void NoMatchSeen(DateTime capturedAt, DateTime readAt)
        {
            noMatchAt = readAt > capturedAt ? readAt : capturedAt;
        }

        // UI: the match datacenter from Deadlock's log when it is about this match (GameConsole.Current). Null otherwise.
        public ConsoleLine Fresh(ConsoleLine line, DateTime started, DateTime lockChangedAt)
        {
            return GameConsole.Current(line, started, lockChangedAt, noMatchAt);
        }

        // The datacenter a console line names, when it is one that hosts Deadlock matches: its id, its row (null for a
        // place the app has no row for), the region it plays and whether the lock allows it.
        public static bool Resolve(Game g, ConsoleLine line, out string id, out Region dc, out string region, out bool onPicked)
        {
            return Resolve(g, line, null, out id, out dc, out region, out onPicked);
        }

        // picks: the datacenters the lock that is on picked (LockScope.PicksOf), or null for the current ticks.
        public static bool Resolve(Game g, ConsoleLine line, HashSet<string> picks, out string id, out Region dc, out string region, out bool onPicked)
        {
            dc = null; region = null; onPicked = false;
            id = GameConsole.Known(line.Code, c => DeadlockData.IsKnown(c) || g.Regions.Any(r => r.Id == c));
            if (id == null || !DeadlockData.Hosting.ContainsKey(id)) return false; // a relay or an unknown place: no answer
            string want = id;
            dc = g.Regions.FirstOrDefault(r => r.Id == want);
            bool? itself = dc != null ? LockScope.Picked(picks, dc) : (bool?)null;
            if (dc == null) dc = g.NoRelayHosts.FirstOrDefault(r => r.Id == want);
            region = Places.RegionOfPop(g, id);
            onPicked = DeadlockData.OnPicked(id, itself, g.Regions.Where(r => LockScope.Picked(picks, r)).Select(r => r.Id),
                                             g.Regions.Where(r => g.Hosts(r)).Select(r => r.Id));
            return true;
        }
    }
}
