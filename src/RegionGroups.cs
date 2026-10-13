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

    // Overwatch only matchmakes inside the game region set in the game (Americas, Europe or Asia): a lock on
    // datacenters outside it finds no match, or a blocked server (lock tests 2026-10-13). The app learns the player's
    // region from the datacenters their matches were on, and warns before a lock that can't find a match.
    static class Zones
    {
        public const string Americas = "am", Europe = "eu", Asia = "as";

        public static string Name(string zone)
        {
            if (zone == Americas) return Lang.T("Americas", "أمريكا");
            if (zone == Europe) return Lang.T("Europe", "أوروبا");
            if (zone == Asia) return Lang.T("Asia", "آسيا");
            return zone;
        }

        // The game region of a datacenter (Region.Id), or null when the game has none or the datacenter is in no region.
        public static string Of(Game g, string regionId)
        {
            if (g == null || g.RegionMap == null || regionId == null) return null;
            var def = g.RegionMap.FirstOrDefault(d => d.Zone != null && d.Ids.Contains(regionId));
            return def != null ? def.Zone : null;
        }

        // The persisted list ("am,eu") as a list; unknown entries are dropped.
        public static List<string> Parse(string saved)
        {
            var list = new List<string>();
            foreach (var z in (saved ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = z.Trim();
                if ((t == Americas || t == Europe || t == Asia) && !list.Contains(t)) list.Add(t);
            }
            return list;
        }

        // The persisted list with a zone added (never removes one).
        public static string Add(string saved, string zone)
        {
            var list = Parse(saved);
            if (zone != null && (zone == Americas || zone == Europe || zone == Asia) && !list.Contains(zone)) list.Add(zone);
            return string.Join(",", list.ToArray());
        }

        // The card's warning when none of the picked regions is in a game region the player's matches were on, or null:
        // "Singapore is in Asia, and you play on Americas. ..." Regions without a zone (other games) never warn.
        public static string Warning(List<RegionChoice> picked, ICollection<string> seen)
        {
            if (picked == null || picked.Count == 0 || seen == null || seen.Count == 0) return null;
            if (picked.Any(c => c.Def.Zone == null || seen.Contains(c.Def.Zone))) return null;
            var zones = picked.Select(c => c.Def.Zone).Distinct().ToList();
            // "Singapore is in Asia" / "سنغافورة في منطقة آسيا", one part per zone
            string where = CardLogic.JoinNames(zones.Select(z =>
            {
                var names = picked.Where(c => c.Def.Zone == z).Select(c => c.Name).ToList();
                return CardLogic.JoinNames(names) + Lang.T(names.Count == 1 ? " is in " : " are in ", " في منطقة ") + Name(z);
            }).ToList());
            string played = CardLogic.JoinNames(seen.Select(Name).ToList());
            string need = Lang.Ar ? string.Join(" أو ", zones.Select(Name).ToArray()) : string.Join(" or ", zones.Select(Name).ToArray());
            return Lang.T(where + ", but you play on " + played + ". You'll only find a match if your game is set to " + need + ".",
                          where + "، وإنت تلعب على " + played + ". ما بتلقى مباراة إلا لو كانت لعبتك على منطقة " + need + ".");
        }
    }

    // One tile on the main screen: a region, its hosting datacenters and the numbers shown on it.
    class RegionChoice
    {
        public RegionDef Def;
        public List<Region> Members = new List<Region>();
        public double Ping = double.MaxValue; // best member's ping in ms; double.MaxValue = unknown
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
                c.Ping = c.Members.Min(m => latency(m));
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
                if (busiest != null) { busiest.Tag = "queue"; featured.Add(busiest); }
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

        // Deadlock's regions are Valve's matchmaking regions; each takes the datacenters that serve it. Relay-only
        // locations (Dubai, Johannesburg) are in no region, so they never show up as a choice.
        public static List<RegionDef> DeadlockMap(Game g)
        {
            var map = new List<RegionDef>();
            foreach (var mm in Stats.MmRegions)
            {
                var ids = g.Regions.Where(r => g.Hosts(r) && Stats.PopToMm.ContainsKey(r.Id) && Stats.PopToMm[r.Id] == mm.Key)
                                   .Select(r => r.Id).ToArray();
                if (ids.Length > 0) map.Add(RegionDef.Of(mm.Key, mm.Value[0], mm.Value[1], ids));
            }
            return map;
        }
    }
}
