using System;
using System.Collections.Generic;
using System.Linq;

namespace PingBoost
{
    // Valve's network as SDR rides it (Deadlock audit 2026-10-13, §1.1 and I1/I3/I5): the game enters at any relay it
    // can reach and Valve carries it on to the match server, so the ping to a datacenter is the best of
    // "ping to an open relay + Valve's own ping from there". Valve's direct numbers (typical_pings) cover only some
    // pairs, so the shortest path over them (Floyd–Warshall, once per config load) fills in the rest.
    class SdrRoutes
    {
        const int Inf = int.MaxValue / 4;
        readonly Dictionary<string, int> index = new Dictionary<string, int>();
        readonly int[,] dist;

        SdrRoutes(int n) { dist = new int[n, n]; }

        // backbone: "a b" (ordinal order) -> ms, as GameDb.ParseValve reads typical_pings
        public static SdrRoutes Build(Dictionary<string, int> backbone)
        {
            var ids = new List<string>();
            foreach (var key in backbone.Keys)
                foreach (var id in key.Split(' '))
                    if (!ids.Contains(id)) ids.Add(id);
            var net = new SdrRoutes(ids.Count);
            for (int i = 0; i < ids.Count; i++) net.index[ids[i]] = i;
            int n = ids.Count;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    net.dist[i, j] = i == j ? 0 : Inf;
            foreach (var kv in backbone)
            {
                var ab = kv.Key.Split(' ');
                if (ab.Length != 2 || kv.Value < 0) continue;
                int a = net.index[ab[0]], b = net.index[ab[1]];
                if (kv.Value < net.dist[a, b]) net.dist[a, b] = net.dist[b, a] = kv.Value;
            }
            for (int k = 0; k < n; k++)
                for (int i = 0; i < n; i++)
                {
                    int ik = net.dist[i, k];
                    if (ik >= Inf) continue;
                    for (int j = 0; j < n; j++)
                    {
                        int via = ik + net.dist[k, j];
                        if (via < net.dist[i, j]) net.dist[i, j] = via;
                    }
                }
            return net;
        }

        // Every POP the table knows.
        public IEnumerable<string> Ids { get { return index.Keys; } }

        // Valve's shortest ping from a to b over its own network in ms; 0 for the same place, -1 = no path known.
        public int Dist(string a, string b)
        {
            if (a == null || b == null) return -1;
            if (a == b) return 0;
            int i, j;
            if (!index.TryGetValue(a, out i) || !index.TryGetValue(b, out j)) return -1;
            int d = dist[i, j];
            return d >= Inf ? -1 : d;
        }
    }

    // A way to one or more datacenters: the ping and where the game enters Valve's network.
    class Route
    {
        public double Ms = double.MaxValue; // double.MaxValue = no route known
        public string Entry;                // the POP the game enters at (the datacenter itself for a direct route)
        public string To;                   // the datacenter it reaches
        public bool Known { get { return Ms < double.MaxValue; } }
    }

    // What the strict lock blocks beyond the other regions' datacenters, and what it costs (ruling R3, guard G5).
    class LockPlan
    {
        public List<string> Close = new List<string>();      // entry relays the rule closes, in the order it closed them
        public Dictionary<string, Route> Reason = new Dictionary<string, Route>(); // entry -> its route to a left-out datacenter
        public HashSet<string> Blocked = new HashSet<string>(); // the other regions' datacenters the lock blocks anyway
        public List<string> Gateways = new List<string>();    // gateways (Dubai) kept open as entries, judged by R3 like a relay
        public List<string> Picked = new List<string>();      // the datacenters the plan is for
        public string Kept;                                   // G5: the entry kept open so the picked ones stay reachable
        public bool MayLeak { get { return Kept != null; } }  // ...which also reaches a left-out datacenter
        public Route Unlocked = new Route();                  // best way to the picked datacenters with nothing blocked
        public Route Locked = new Route();                    // ...and through what the lock leaves open
    }

    static class StrictLock
    {
        // An entry relay is closed when it reaches a left-out datacenter within this much of the best picked route (R3).
        public const double MarginMs = 20;

        // The best route to any of targets entering at a POP that answered ping (ping: POP -> ms) and is open.
        public static Route Best(SdrRoutes net, IEnumerable<string> targets, IDictionary<string, double> ping, Func<string, bool> open)
        {
            var best = new Route();
            if (net == null || ping == null) return best;
            foreach (var t in targets)
                foreach (var kv in ping)
                {
                    if (kv.Value >= double.MaxValue || (open != null && !open(kv.Key))) continue;
                    int d = net.Dist(kv.Key, t);
                    if (d < 0) continue;
                    double ms = kv.Value + d;
                    // equal pings: the direct way wins, then the first found
                    if (ms < best.Ms || (ms == best.Ms && kv.Key == t && best.Entry != best.To))
                    {
                        best.Ms = ms;
                        best.Entry = kv.Key;
                        best.To = t;
                    }
                }
            return best;
        }

        // picked: the datacenters the player wants; blockedHosts: the hosting datacenters the lock blocks (the other
        // regions'); leftOut: every datacenter whose matches the lock is meant to keep away (blockedHosts plus hosts
        // without a relay of other regions); entries: relay-only POPs (no matches of their own).
        // R3: an entry relay is closed exactly when it gives a route to a left-out datacenter within MarginMs of the best
        // picked route through what stays open. Closing one can make the picked route longer, which can bring another
        // entry within the margin, so the rule runs until nothing changes (the closed set only grows).
        // An entry without a ping can't be measured: when Valve's network has a path from it to a left-out datacenter it
        // is closed anyway (a lost ping must never open the lock).
        // G5: if that leaves no answering POP with a route to a picked datacenter, the cheapest closed entry to them is
        // pinned open (a measured route first, else the shortest path), the plan says the lock may not hold, and the rule
        // runs again from the start with it open: with a real picked route, entries closed while there was none (or a
        // longer one) may stay open now, so the result is again an R3 fixpoint.
        public static LockPlan Plan(SdrRoutes net, IDictionary<string, double> ping, ICollection<string> picked,
                                    ICollection<string> blockedHosts, ICollection<string> leftOut, ICollection<string> entries, double margin)
        {
            var plan = new LockPlan();
            if (net == null || ping == null || picked == null || picked.Count == 0) return plan;
            plan.Picked = picked.ToList();
            plan.Blocked = new HashSet<string>(blockedHosts);
            plan.Unlocked = Best(net, picked, ping, null);
            var closed = Rule(net, ping, picked, blockedHosts, leftOut, entries, margin, null, plan);
            plan.Locked = Best(net, picked, ping, id => !blockedHosts.Contains(id) && !closed.Contains(id));
            if (!plan.Locked.Known && closed.Count > 0)
            {
                // G5: never leave the game without a way to the picked datacenters
                var shut = closed;
                string kept = Best(net, picked, ping, id => shut.Contains(id)).Entry;
                if (kept == null)
                {
                    // no closed entry answered ping: the one closest to the picked datacenters over Valve's network
                    int bestD = int.MaxValue;
                    foreach (var e in plan.Close)
                        foreach (var t in picked)
                        {
                            int d = net.Dist(e, t);
                            if (d >= 0 && d < bestD) { bestD = d; kept = e; }
                        }
                }
                if (kept != null)
                {
                    var why = plan.Reason[kept];
                    plan.Kept = kept;
                    plan.Close.Clear();
                    plan.Reason.Clear();
                    closed = Rule(net, ping, picked, blockedHosts, leftOut, entries, margin, kept, plan);
                    plan.Reason[kept] = why;
                    plan.Locked = Best(net, picked, ping, id => !blockedHosts.Contains(id) && !closed.Contains(id));
                }
            }
            return plan;
        }

        // The best way from one entry to a left-out datacenter: its ping plus the path, or (no ping) the path alone with
        // the ping unknown (To set, Ms unknown).
        static Route Reach(SdrRoutes net, IDictionary<string, double> ping, ICollection<string> leftOut, string e)
        {
            double pe;
            if (ping.TryGetValue(e, out pe) && pe < double.MaxValue)
                return Best(net, leftOut, new Dictionary<string, double> { { e, pe } }, null);
            var r = Best(net, leftOut, new Dictionary<string, double> { { e, 0 } }, null);
            r.Ms = double.MaxValue;
            return r;
        }

        // R3 until nothing changes, with pinned (if any) kept open; fills plan.Close and plan.Reason, returns the closed set.
        static HashSet<string> Rule(SdrRoutes net, IDictionary<string, double> ping, ICollection<string> picked, ICollection<string> blockedHosts,
                                    ICollection<string> leftOut, ICollection<string> entries, double margin, string pinned, LockPlan plan)
        {
            var closed = new HashSet<string>();
            Func<string, bool> open = id => !blockedHosts.Contains(id) && !closed.Contains(id);
            while (true)
            {
                var p = Best(net, picked, ping, open);
                bool changed = false;
                foreach (var e in entries)
                {
                    if (e == pinned || closed.Contains(e) || picked.Contains(e)) continue;
                    var reach = Reach(net, ping, leftOut, e);
                    if (reach.To == null) continue; // no path to anything left out
                    // measured: closed within the margin; not measured: closed whenever such a path exists
                    if (reach.Known && reach.Ms > p.Ms + margin) continue;
                    closed.Add(e);
                    plan.Close.Add(e);
                    plan.Reason[e] = reach;
                    changed = true;
                }
                if (!changed) break;
            }
            return closed;
        }

        // The plan for a game's lock on the picked datacenters (Region.Id), from its measured pings (POP -> ms).
        // Games without a hosting table have no entry relays: nothing extra is closed, the routes are still told.
        // keepGateways (Prefs.DubaiWayIn): a gateway whose region isn't picked (Dubai under a Europe lock) is an entry
        // relay, judged by R3 like the others and not left out; off, it is blocked like any host that isn't picked.
        public static LockPlan For(Game g, ICollection<string> picked, IDictionary<string, double> ping, bool keepGateways)
        {
            var gateways = g.HostPops == null || g.GatewayPops == null ? new List<string>()
                : DeadlockData.EntryGateways(picked, keepGateways).Where(id => g.GatewayPops.Contains(id) && g.Regions.Any(r => r.Id == id)).ToList();
            var blocked = new HashSet<string>(g.Regions.Where(r => g.Hosts(r) && !r.Detected && !picked.Contains(r.Id) && !gateways.Contains(r.Id))
                                                       .Select(r => r.Id));
            var entries = g.HostPops == null ? new List<string>()
                : g.Regions.Where(r => !g.Hosts(r) || gateways.Contains(r.Id)).Select(r => r.Id).ToList();
            var leftOut = new HashSet<string>(blocked);
            // hosts without a relay can't be blocked, but an open relay still reaches them: one of a region the player
            // didn't pick is left out too (one of a picked region is where the player wants to play)
            var pickedRegions = new HashSet<string>();
            foreach (var id in picked)
            {
                string mm;
                if (DeadlockData.Hosting.TryGetValue(id, out mm)) pickedRegions.Add(mm);
            }
            foreach (var r in g.NoRelayHosts)
            {
                string mm;
                if (!(DeadlockData.Hosting.TryGetValue(r.Id, out mm) && pickedRegions.Contains(mm))) leftOut.Add(r.Id);
            }
            var plan = Plan(g.Routes, ping, picked, blocked, leftOut, entries, MarginMs);
            plan.Gateways = gateways;
            return plan;
        }

        // How long a good ping stands in for a test that failed.
        public static readonly TimeSpan KeepGood = TimeSpan.FromMinutes(30);

        // The measured pings of a game's datacenters: the latest test, or when it failed the last good one of the past
        // 30 minutes (one lost round must not open the lock); datacenters with neither are left out.
        public static Dictionary<string, double> Pings(Game g)
        {
            return Pings(g, DateTime.UtcNow);
        }

        public static Dictionary<string, double> Pings(Game g, DateTime nowUtc)
        {
            var d = new Dictionary<string, double>();
            foreach (var r in g.Regions)
            {
                var now = r.Direct; // read once: a test may replace it meanwhile
                if (now != null && now.Ok) d[r.Id] = now.Avg;
                else
                {
                    var last = r.LastGood;
                    if (last != null && last.Ok && nowUtc - r.LastGoodAt <= KeepGood) d[r.Id] = last.Avg;
                }
            }
            return d;
        }
    }
}
