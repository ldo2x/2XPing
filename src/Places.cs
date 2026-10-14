using System;
using System.Collections.Generic;
using System.Linq;

namespace PingBoost
{
    // How a datacenter or a Valve relay is named to the player.
    static class Places
    {
        // The region a datacenter belongs to, in the player's words (its own short name when no region table has it).
        public static string RegionNameOf(Game g, Region dc)
        {
            var def = g.RegionMap != null ? g.RegionMap.FirstOrDefault(d => d.Ids.Contains(dc.Id)) : null;
            return def != null ? Lang.T(def.En, def.Ar) : Fmt.ShortName(dc);
        }

        // A Valve relay's place in a word or two, for "via Dubai" / «عن طريق دبي».
        public static string PlaceName(Region r)
        {
            string n = Fmt.ShortName(r);
            foreach (var cut in new[] { " (", " - " })
            {
                int i = n.IndexOf(cut, StringComparison.Ordinal);
                if (i > 0) n = n.Substring(0, i);
            }
            return n;
        }

        // The region a Valve datacenter's matches belong to, in the player's words (hosts without a relay included).
        public static string RegionOfPop(Game g, string id)
        {
            string mm;
            if (g.HostPops != null && DeadlockData.Hosting.TryGetValue(id, out mm) && Stats.MmRegions.ContainsKey(mm))
                return Lang.T(Stats.MmRegions[mm][0], Stats.MmRegions[mm][1]);
            var r = g.Regions.FirstOrDefault(x => x.Id == id) ?? g.NoRelayHosts.FirstOrDefault(x => x.Id == id);
            return r != null ? RegionNameOf(g, r) : id;
        }
    }
}
