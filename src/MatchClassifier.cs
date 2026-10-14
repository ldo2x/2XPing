using System;
using System.Collections.Generic;
using System.Linq;

namespace PingBoost
{
    enum MatchKind
    {
        NoMatch,          // nothing captured: not in a match
        ViaRelay,         // only the entry relay is in sight: the match server is hidden behind it
        OnDatacenter,     // a hosting datacenter carries the match
        OnUnknownServer   // traffic, but to no datacenter the app knows (games without lists, or a new server)
    }

    // What one server check saw, before anything is shown.
    sealed class MatchOutcome
    {
        public MatchKind Kind;
        public List<Region> Found = new List<Region>(); // every hosting datacenter that carries the match, first = the busiest
        public Region Datacenter;                       // Found[0], or null
        public Region Relay;                            // ViaRelay: where the game enters Valve's network
        public bool AllPicked { get { return Found.Count > 0 && Found.All(r => r.Picked); } }
    }

    // Pure: the server check's captured addresses -> where the player is. Shared by the manual and the automatic check.
    static class MatchClassifier
    {
        // The addresses that can be the game's server: voice chat and service hosts (Game.DetectIgnore) are left out.
        public static List<Detector.Hit> ServerHits(Game g, List<Detector.Hit> hits)
        {
            return hits.Where(h => !g.Ignored(h.Ip)).ToList();
        }

        // The hosting datacenters that carry the match (Valve: with at least half of the packets), busiest first.
        public static List<Region> HostsOf(Game g, List<Detector.Hit> hits)
        {
            return hits.Where(h => g.Provider != Provider.Valve || Detector.CarriesMatch(h))
                       .Select(h => g.DatacenterOf(h.Ip)).Where(r => r != null && g.Hosts(r)).Distinct().ToList();
        }

        // The datacenter the game is on, from the addresses it talks to: one that hosts matches, or null when the game
        // only talks to a relay (e.g. Dubai), which hides the match server behind it. Valve: an address carries the
        // match only with at least half of the game's packets (Detector.MatchShare); a hosting datacenter with less is
        // a relay the game keeps open next to its primary one, never the match (Deadlock audit B3).
        public static Region HostRegionOf(Game g, List<Detector.Hit> hits)
        {
            return hits.Where(h => g.Provider != Provider.Valve || Detector.CarriesMatch(h))
                       .Select(h => g.DatacenterOf(h.Ip)).FirstOrDefault(r => r != null && g.Hosts(r));
        }

        // Where the game enters Valve's network when no address carries the match: Valve, the busiest of its addresses
        // (a relay like Dubai, or a hosting datacenter used as one); other games, a relay-only place. Null = none.
        // The limit: traffic only shows the relays the game talks to. Even an address with most of the packets at a
        // hosting datacenter can be the front relay of a route whose server is elsewhere (front+back routing), and a
        // server at a place without relays (Helsinki, Falkenstein) is never seen at all. Only the game's console log
        // names the server itself; HostRegionOf's answer is the best the capture can do.
        public static Region RelayRegionOf(Game g, List<Detector.Hit> hits)
        {
            if (g.Provider == Provider.Valve) return hits.Select(h => g.DatacenterOf(h.Ip)).FirstOrDefault(r => r != null);
            return hits.Select(h => g.DatacenterOf(h.Ip)).FirstOrDefault(r => r != null && !g.Hosts(r));
        }

        // hits: already through ServerHits.
        public static MatchOutcome Classify(Game g, List<Detector.Hit> hits)
        {
            var m = new MatchOutcome();
            if (hits.Count == 0) { m.Kind = MatchKind.NoMatch; return m; }
            m.Found = HostsOf(g, hits);
            m.Datacenter = m.Found.FirstOrDefault();
            if (m.Datacenter != null) { m.Kind = MatchKind.OnDatacenter; return m; }
            m.Relay = RelayRegionOf(g, hits);
            m.Kind = m.Relay != null ? MatchKind.ViaRelay : MatchKind.OnUnknownServer;
            return m;
        }
    }
}
