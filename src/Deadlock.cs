using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Web.Script.Serialization;

namespace PingBoost
{
    // What Deadlock's matchmaking is made of, kept apart from the app so it can be checked against Valve's config.
    // Sources (Deadlock audit 2026-10-13): deadlock-api's ServerRegion enum (the game coordinator's private-lobby
    // regions) agrees with the game's own Citadel_ServerRegion_* strings; ECitadelRegionMode from the game's protobufs.
    static class DeadlockData
    {
        public static bool Is(Game g) { return g != null && g.Icon == "deadlock"; }

        // Valve datacenter (SDR POP id) -> the matchmaking region (region_mode) whose matches it hosts.
        // South Africa (jnb) is in the Europe pool. India (bom/bom2/maa/maa2), Guam (gum), Dubai (dxb) and
        // Fujairah (dfjr) host no Deadlock region: they are only relays, see RelayOnly.
        // Helsinki (hel), Falkenstein (fsn) and US West (eat, alias mwh) host matches but Valve lists no relay for
        // them, so the lock can never block them: they show as "can't be locked" rows (Game.NoRelayHosts), as do the
        // relay-less Datapacket sites dsto and dlhr below.
        public static readonly Dictionary<string, string> Hosting = new Dictionary<string, string>
        {
            {"ams","Europe"}, {"fra","Europe"}, {"dfra","Europe"}, {"lhr","Europe"}, {"par","Europe"}, {"mad","Europe"},
            {"vie","Europe"}, {"dvie","Europe"}, {"waw","Europe"}, {"sto","Europe"}, {"sto2","Europe"}, {"jnb","Europe"},
            {"hel","Europe"}, {"helm","Europe"}, {"fsn","Europe"},
            // Datapacket Stockholm and London: no relays, and no Deadlock server region names them, so hosting is
            // unverified. Listed as hosts so they show as "can't be locked" rows like Helsinki (they can't be blocked anyway).
            {"dsto","Europe"}, {"dlhr","Europe"},
            {"iad","Row"}, {"atl","Row"}, {"ord","Row"}, {"dfw","Row"}, {"lax","Row"}, {"sea","Row"}, {"eat","Row"}, {"mwh","Row"},
            {"sgp","SEAsia"}, {"hkg","SEAsia"}, {"tyo","SEAsia"}, {"seo","SEAsia"},
            {"gru","SAmerica"}, {"eze","SAmerica"}, {"lim","SAmerica"}, {"scl","SAmerica"},
            {"syd","Oceania"},
        };

        // Datacenters Valve lists that host no Deadlock region: entry relays (the game enters Valve's network there and
        // rides it to the match server).
        public static readonly HashSet<string> RelayOnly = new HashSet<string>
        {
            "dxb", "dfjr", "bom", "bom2", "maa", "maa2", "gum",
        };

        // Russia is a player pool, not a place: its matches are played on the European datacenters, and no lock can
        // choose it (it never becomes a tile; the region choices explain it in a footnote).
        public const string Russia = "Russia";

        // ECitadelRegionMode (deadlock-api "region_mode") -> the region key used everywhere in the app.
        static readonly string[] ModeKeys = { "Row", "Europe", "SEAsia", "SAmerica", Russia, "Oceania" };
        // deadlock-api "region_mode_parsed", for an answer without the number
        static readonly Dictionary<string, string> ParsedKeys = new Dictionary<string, string>
        {
            {"row","Row"}, {"europe","Europe"}, {"se_asia","SEAsia"}, {"s_america","SAmerica"}, {"russia",Russia}, {"oceania","Oceania"},
        };

        public static bool IsKnown(string pop) { return pop != null && (Hosting.ContainsKey(pop) || RelayOnly.Contains(pop)); }

        // Is a match on datacenter id (named by the game's console log) inside the lock? pickedItself: the datacenter's own
        // pick when the lock can pick it (it has relays), null when it can't (Helsinki, Falkenstein, US West, dsto, dlhr).
        // Such a place can never be blocked, so it counts as picked only when the lock holds its whole region: every
        // hosting datacenter with relays (lockable) of that region is picked. Otherwise the match is outside the lock.
        public static bool OnPicked(string id, bool? pickedItself, IEnumerable<string> pickedIds, IEnumerable<string> lockable)
        {
            if (pickedItself.HasValue) return pickedItself.Value;
            string key, other;
            if (id == null || !Hosting.TryGetValue(id, out key)) return false;
            var picked = new HashSet<string>(pickedIds ?? Enumerable.Empty<string>());
            var region = (lockable ?? Enumerable.Empty<string>()).Where(p => p != null && Hosting.TryGetValue(p, out other) && other == key).ToList();
            return region.Count > 0 && region.All(picked.Contains);
        }

        // POP ids of Valve's config that are in neither list: a new datacenter (the way dfjr appeared) that the app
        // would otherwise silently treat as a relay and never block. Sorted, each once.
        public static List<string> UnknownPops(IEnumerable<string> popIds)
        {
            return (popIds ?? Enumerable.Empty<string>()).Where(p => !string.IsNullOrEmpty(p) && !IsKnown(p))
                                                        .Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();
        }

        // The region key of one active match (region_mode number, else its parsed name), or null.
        static string KeyOf(Dictionary<string, object> m)
        {
            object v;
            if (m.TryGetValue("region_mode", out v) && v != null)
            {
                int n;
                if (int.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                    return n >= 0 && n < ModeKeys.Length ? ModeKeys[n] : null;
            }
            string key;
            if (m.TryGetValue("region_mode_parsed", out v) && v is string && ParsedKeys.TryGetValue((string)v, out key)) return key;
            return null;
        }

        // deadlock-api /v1/matches/active (the game's Watch tab: the top 200 live matches): how many matches each
        // region started in the hour before nowUnix (UTC seconds), each match once. Regions without a match are left
        // out. A sample, good for comparing regions, never a count of everyone.
        public static Dictionary<string, int> CountRegions(string json, long nowUnix)
        {
            var result = new Dictionary<string, int>();
            var rows = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as object[];
            if (rows == null) throw new FormatException("active matches: not a list");
            var seen = new HashSet<string>();
            foreach (var row in rows)
            {
                var m = row as Dictionary<string, object>;
                if (m == null) continue;
                object start, id;
                long at;
                if (!m.TryGetValue("start_time", out start) || start == null ||
                    !long.TryParse(Convert.ToString(start, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out at) ||
                    at <= nowUnix - 3600)
                    continue;
                string key = KeyOf(m);
                if (key == null) continue;
                // a match listed twice counts once (no id: counted as it is)
                if (m.TryGetValue("match_id", out id) && id != null && !seen.Add(Convert.ToString(id, CultureInfo.InvariantCulture))) continue;
                int n;
                result.TryGetValue(key, out n);
                result[key] = n + 1;
            }
            return result;
        }

        // Supabase's shared row (game_stats): its regions, only when they were counted from /v1/matches/active
        // (regions_source = "active"); null otherwise, so the app counts the live list itself. The old /v1/sql numbers
        // were about 2.6 times higher and would make every wait look shorter (Stats.FastQueue etc.).
        public static Dictionary<string, int> SharedRegions(Dictionary<string, object> row)
        {
            object src, regions;
            if (row == null || !row.TryGetValue("regions_source", out src) || !(src is string) || (string)src != "active") return null;
            if (!row.TryGetValue("regions", out regions)) return null;
            var d = regions as Dictionary<string, object>;
            if (d == null) return null;
            var result = new Dictionary<string, int>();
            foreach (var kv in d)
            {
                int n;
                if (kv.Value == null || !int.TryParse(Convert.ToString(kv.Value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) continue;
                result[kv.Key] = n;
            }
            return result;
        }

        public static long UnixNow() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds; }
    }
}
