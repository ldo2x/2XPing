using System;
using System.Collections.Generic;
using System.Linq;

namespace PingBoost
{
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

        static readonly string[] All = { Americas, Europe, Asia };

        // Only zones seen this recently count: a player who moved their game to another region stops being warned
        // about the old one after this long.
        public const int FreshDays = 14;

        // The saved list "am:<utc ticks>,eu:<utc ticks>" as zone -> when it was last seen (UTC). Entries of the old
        // plain list ("am,eu") count as seen at nowUtc (migration); unknown entries are dropped.
        public static Dictionary<string, DateTime> Parse(string saved, DateTime nowUtc)
        {
            var d = new Dictionary<string, DateTime>();
            foreach (var part in (saved ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var f = part.Trim().Split(':');
                string z = f[0].Trim();
                if (!All.Contains(z)) continue;
                long ticks;
                DateTime at = nowUtc;
                if (f.Length > 1)
                {
                    if (!long.TryParse(f[1].Trim(), out ticks) || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) continue;
                    at = new DateTime(ticks, DateTimeKind.Utc);
                }
                DateTime had;
                if (!d.TryGetValue(z, out had) || at > had) d[z] = at;
            }
            return d;
        }

        // The form saved in settings, always in the same order.
        public static string Format(Dictionary<string, DateTime> seen)
        {
            return string.Join(",", All.Where(seen.ContainsKey).Select(z => z + ":" + seen[z].Ticks).ToArray());
        }

        // The saved list with a zone seen at nowUtc (others are kept as they are; a zone of null adds nothing).
        public static string Add(string saved, string zone, DateTime nowUtc)
        {
            var d = Parse(saved, nowUtc);
            if (zone != null && All.Contains(zone)) d[zone] = nowUtc;
            return Format(d);
        }

        // What to save after a server check saw these datacenters (Region.Id), or null when nothing changes: a game without
        // zones, datacenters in no zone, or zones already saved within the hour (the time only needs to be about right,
        // so settings.txt isn't written at every check).
        public static string Learn(Game g, string saved, IEnumerable<string> regionIds, DateTime nowUtc)
        {
            if (g == null || regionIds == null) return null;
            var had = Parse(saved, nowUtc);
            string next = saved;
            bool changed = false;
            foreach (var id in regionIds)
            {
                string z = Of(g, id);
                DateTime at;
                if (z == null || (had.TryGetValue(z, out at) && nowUtc - at <= TimeSpan.FromHours(1))) continue;
                next = Add(next, z, nowUtc);
                had[z] = nowUtc;
                changed = true;
            }
            return changed && !string.IsNullOrEmpty(next) ? next : null;
        }

        // "No match found?" counts only time the server check actually watched the running game: the gap between two
        // checks is added when it is short (a normal check interval); a longer gap (checks paused while the game is
        // in front, the game closed) adds nothing.
        public static readonly TimeSpan NoMatchAfter = TimeSpan.FromMinutes(3), MaxCheckGap = TimeSpan.FromMinutes(2);

        public static TimeSpan AddObserved(TimeSpan observed, DateTime lastCheck, DateTime now)
        {
            if (lastCheck == DateTime.MinValue) return observed;
            var gap = now - lastCheck;
            return gap > TimeSpan.Zero && gap <= MaxCheckGap ? observed + gap : observed;
        }

        public static bool NoMatchYet(bool lockedHere, bool hasZones, bool dcSinceLock, TimeSpan observed)
        {
            return lockedHere && hasZones && !dcSinceLock && observed > NoMatchAfter;
        }

        // The zones seen in the last FreshDays days, in a fixed order.
        public static List<string> Recent(string saved, DateTime nowUtc)
        {
            var d = Parse(saved, nowUtc);
            return All.Where(z => d.ContainsKey(z) && nowUtc - d[z] <= TimeSpan.FromDays(FreshDays)).ToList();
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
            return Lang.T(where + ", but you play on " + played + ". You'll only find a match if the game's own region is " + need + ".",
                          where + "، وإنت تلعب على " + played + ". ما بتلقى مباراة إلا لو كانت منطقة لعبتك " + need + ".");
        }
    }
}
