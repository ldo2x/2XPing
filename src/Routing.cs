using System;
using System.Collections.Generic;
using System.Linq;

namespace PingBoost
{
    // Which way to a region is fastest: straight there or through one of the player's relays.
    static class Routing
    {
        public const double RelayMarginMs = 3; // a relay must beat direct by this much to be chosen

        public static KeyValuePair<string, double>? BestRelay(Region r)
        {
            if (r.ViaRelay.Count == 0) return null;
            return r.ViaRelay.OrderBy(kv => kv.Value).First();
        }

        // Relay to use for this region, or null when direct is best / unknown.
        public static Relay AutoRelay(Region r, IEnumerable<Relay> relays)
        {
            var best = BestRelay(r);
            if (best == null) return null;
            bool directOk = r.Direct != null && r.Direct.Ok;
            if (directOk && best.Value.Value > r.Direct.Avg - RelayMarginMs) return null;
            return relays.FirstOrDefault(x => x.Name == best.Value.Key);
        }

        // The lowest ping to the region, direct or through a relay; double.MaxValue = unknown
        public static double BestLatency(Region r)
        {
            var direct = r.Direct; // read once: a test may replace it meanwhile
            double d = direct != null && direct.Ok ? direct.Avg : double.MaxValue;
            var b = BestRelay(r);
            return b != null ? Math.Min(d, b.Value.Value) : d;
        }

        // The region with the lowest known ping, or null when none answered
        public static Region Best(IEnumerable<Region> regions)
        {
            return regions.Where(r => BestLatency(r) < double.MaxValue).OrderBy(BestLatency).FirstOrDefault();
        }

        // The server list's order: regions the lock shuts out last, then by ping
        public static List<Region> SortForList(IEnumerable<Region> regions, Func<Region, bool> lockedOut)
        {
            return regions.OrderBy(r => lockedOut(r) ? 1 : 0).ThenBy(BestLatency).ToList();
        }
    }
}
