using System;
using System.Collections.Generic;
using System.Linq;

namespace PingBoost
{
    // A region the player chooses: a name and the datacenters (Region.Id) that make it up.
    class RegionDef
    {
        public string Key, En, Ar;
        public string[] Ids;
        // Overwatch: the game region it belongs to ("am", "eu" or "as", see Zones); null for other games
        public string Zone;

        public static RegionDef Of(string key, string en, string ar, params string[] ids)
        {
            return new RegionDef { Key = key, En = en, Ar = ar, Ids = ids };
        }

        public RegionDef In(string zone) { Zone = zone; return this; }
    }

    // One tile on the main screen: a region, its hosting datacenters and the numbers shown on it.
    class RegionChoice
    {
        public RegionDef Def;
        public List<Region> Members = new List<Region>();
        public double Ping = double.MaxValue; // best member's ping in ms; double.MaxValue = unknown
        public string Via;                    // Valve: the relay that route enters at ("Dubai"), null = straight there
        public int Matches = -1;              // matches running there now (queue speed); -1 = no data
        public int Players = -1;              // players there now; -1 = no data
        public string Tag;                    // "best", "ping", "queue" or null
        public bool Selected;

        public string Name { get { return Lang.T(Def.En, Def.Ar); } }
    }

    static class RegionGroups
    {
        const int FeaturedMax = 3;

        // activity: matches running per region key (null or missing = no queue data); playersOf turns matches into players;
        // latency: a datacenter's best ping in ms (double.MaxValue = unknown), so the tile agrees with the rest of the app.
        public static List<RegionChoice> Build(Game g, Dictionary<string, int> activity, Func<int, int> playersOf, Func<Region, double> latency)
        {
            return Build(g, activity, playersOf, latency, null);
        }

        // via: the relay a datacenter's best route enters at, named for the tile (null = none); the tile takes the
        // one of its best member.
        public static List<RegionChoice> Build(Game g, Dictionary<string, int> activity, Func<int, int> playersOf, Func<Region, double> latency,
                                               Func<Region, string> via)
        {
            var result = new List<RegionChoice>();
            if (g.RegionMap == null) return result;
            foreach (var def in g.RegionMap)
            {
                var c = new RegionChoice { Def = def };
                foreach (var id in def.Ids)
                {
                    var r = g.Regions.FirstOrDefault(x => x.Id == id);
                    if (r != null && g.Hosts(r)) c.Members.Add(r);
                }
                if (c.Members.Count == 0) continue;
                Region bestMember = null;
                // a gateway (Dubai) doesn't set its region's ping: from the Gulf it would make SE Asia look like 31 ms
                // although most of its matches are far away; it counts only as the region's sole member
                bool others = c.Members.Any(m => !g.Gateway(m));
                foreach (var m in c.Members)
                {
                    if (others && g.Gateway(m)) continue;
                    double ms = latency(m);
                    if (bestMember == null || ms < c.Ping) { c.Ping = ms; bestMember = m; }
                }
                if (via != null && c.Ping < double.MaxValue) c.Via = via(bestMember);
                int matches;
                if (activity != null && activity.TryGetValue(def.Key, out matches))
                {
                    c.Matches = matches;
                    c.Players = playersOf != null ? playersOf(matches) : -1;
                }
                result.Add(c);
            }
            return result;
        }

        // The tiles shown first: the best ping, the fastest queue, then the next best pings, at most three.
        public static List<RegionChoice> Featured(List<RegionChoice> all)
        {
            var byPing = all.OrderBy(c => c.Ping).ToList(); // stable: equal pings keep their order
            var featured = new List<RegionChoice>();
            if (byPing.Count == 0) return featured;

            var best = byPing[0];
            RegionChoice busiest = null; // fastest queue = most matches, the better ping on a tie
            foreach (var c in byPing)
                if (c.Matches > 0 && (busiest == null || c.Matches > busiest.Matches)) busiest = c;

            foreach (var c in byPing) c.Tag = null;
            featured.Add(best);
            if (busiest == best) best.Tag = "best";
            else
            {
                best.Tag = "ping";
                // "Shortest wait" only when its wait really is shorter than the best ping's; otherwise "Most players"
                if (busiest != null)
                {
                    busiest.Tag = Stats.WaitBucket(busiest.Matches) < Stats.WaitBucket(Math.Max(0, best.Matches)) ? "queue" : "players";
                    featured.Add(busiest);
                }
            }
            foreach (var c in byPing)
            {
                if (featured.Count >= FeaturedMax) break;
                if (!featured.Contains(c)) featured.Add(c);
            }
            foreach (var c in all) c.Selected = false;
            featured[0].Selected = true;
            return featured;
        }

        // Every region in the order the card shows them after "Other regions": the featured ones first, then the rest by
        // ping. The card decides its words and the lock from this whole list, shown or not, so a region picked from the
        // other regions is named on the button and locked even after the list is folded again.
        public static List<RegionChoice> FeaturedFirst(List<RegionChoice> all, List<RegionChoice> featured)
        {
            return featured.Concat(all.Where(c => !featured.Contains(c)).OrderBy(c => c.Ping)).ToList();
        }

        public static List<RegionChoice> Chosen(List<RegionChoice> every)
        {
            return every.Where(c => c.Selected).ToList();
        }

        // The datacenters to lock for the chosen regions: hosting members only, each once, in the order chosen.
        public static List<string> PickIds(Game g, IEnumerable<RegionChoice> chosen)
        {
            var ids = new List<string>();
            foreach (var c in chosen)
                foreach (var r in c.Members)
                    if (g.Hosts(r) && !ids.Contains(r.Id)) ids.Add(r.Id);
            return ids;
        }

        public static string Busy(int players)
        {
            if (players >= 10000) return Lang.T("lots of players", "لاعبين كثير");
            if (players >= 2000) return Lang.T("some players", "لاعبين متوسط");
            return Lang.T("few players", "لاعبين قليل");
        }

        // Deadlock's regions are Valve's matchmaking regions; each takes the datacenters that serve it (DeadlockData.Hosting,
        // Johannesburg in Europe, Dubai in SE Asia). Relay-only locations (Fujairah, India, Guam) are in no region, hosts without a
        // relay (Helsinki, Falkenstein, US West) can't be locked so they are never members, and Russia has no datacenter
        // of its own: none of them shows up as a choice.
        public static List<RegionDef> DeadlockMap(Game g)
        {
            var map = new List<RegionDef>();
            foreach (var mm in Stats.MmRegions)
            {
                var ids = g.Regions.Where(r => g.Hosts(r) && DeadlockData.Hosting.ContainsKey(r.Id) && DeadlockData.Hosting[r.Id] == mm.Key)
                                   .Select(r => r.Id).ToArray();
                if (ids.Length > 0) map.Add(RegionDef.Of(mm.Key, mm.Value[0], mm.Value[1], ids));
            }
            return map;
        }
    }
}
