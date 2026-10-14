using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace PingBoost
{
    // Online player numbers and matchmaking-speed estimates.
    static class Stats
    {
        // Deadlock matchmaking regions (deadlock-api.com region_mode) -> display names
        public static readonly Dictionary<string, string[]> MmRegions = new Dictionary<string, string[]>
        {
            {"Europe", new[] {"Europe", "أوروبا"}},
            {"Row", new[] {"North America", "أمريكا الشمالية"}},
            {"SEAsia", new[] {"SE Asia", "جنوب شرق آسيا"}},
            {"Russia", new[] {"Russia", "روسيا"}},
            {"Oceania", new[] {"Oceania", "أوقيانوسيا"}},
            {"SAmerica", new[] {"South America", "أمريكا الجنوبية"}},
        };

        public const int PlayersPerMatch = 12;
        // average of all modes (deadlock-api, one day: normal 36 min, Street Brawl 14 min)
        public const double AvgMatchMinutes = 32;
        // share of the players with the game open who are inside a match; the rest are in the menus, the queue or away
        public const double InMatchShare = 0.75;

        static int deadlockPerDay = -1;
        static DateTime deadlockPerDayAt;

        static Dictionary<string, object> shared;
        static DateTime sharedAt;

        // The Deadlock numbers Supabase fetches once a minute for every copy of the app (tools/supabase-setup.sql),
        // or null when they're missing or more than 10 minutes old: then each PC asks deadlock-api itself.
        static Dictionary<string, object> SharedDeadlock()
        {
            if (shared != null && DateTime.Now - sharedAt < TimeSpan.FromSeconds(60)) return shared;
            try
            {
                // every column: "regions_source" only exists once the newer setup SQL ran, and naming it would fail before that
                string json = Http.Send("GET", Telemetry.SupabaseUrl + "/rest/v1/game_stats?game=eq.deadlock&select=*",
                                        null, Telemetry.Headers(Telemetry.SupabaseKey), 8000);
                var rows = (object[])new JavaScriptSerializer().DeserializeObject(json);
                if (rows.Length == 0) return null;
                var row = (Dictionary<string, object>)rows[0];
                DateTime at;
                if (!(row["updated_at"] is string) ||
                    !DateTime.TryParse((string)row["updated_at"], CultureInfo.InvariantCulture,
                                       DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out at) ||
                    DateTime.UtcNow - at > TimeSpan.FromMinutes(10))
                    return null;
                shared = row;
                sharedAt = DateTime.Now;
                return row;
            }
            catch { return null; }
        }

        // Deadlock players online right now (in matches, the queue, the menus or away), or -1.
        // The total comes from every match deadlock-api fetches in a day; the active-match list below only holds
        // the top 200 matches of the game's Watch tab, so it is used for each region's share, never for the count.
        public static int DeadlockOnline()
        {
            lock (MmRegions)
            {
                var s = SharedDeadlock();
                if (s != null && s["matches_per_day"] != null)
                {
                    deadlockPerDay = Convert.ToInt32(s["matches_per_day"], CultureInfo.InvariantCulture);
                    deadlockPerDayAt = DateTime.Now;
                }
                else if (deadlockPerDay < 0 || DateTime.Now - deadlockPerDayAt > TimeSpan.FromHours(1))
                {
                    var d = (Dictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                        .DeserializeObject(Http.Get("https://api.deadlock-api.com/v1/info", 15000));
                    deadlockPerDay = Convert.ToInt32(d["fetched_matches_per_day"], CultureInfo.InvariantCulture);
                    deadlockPerDayAt = DateTime.Now;
                }
                double inMatches = deadlockPerDay / 24.0 * PlayersPerMatch * AvgMatchMinutes / 60.0;
                // the day's average, scaled to how busy this hour is (about 0.5 at night, 1.5 at the evening peak)
                double busy = 1;
                if (s != null && s.ContainsKey("now_factor") && s["now_factor"] != null)
                    busy = Math.Max(0.3, Math.Min(2.5, Convert.ToDouble(s["now_factor"], CultureInfo.InvariantCulture)));
                return (int)Math.Round(inMatches * busy / InMatchShare);
            }
        }

        static Dictionary<string, int> deadlockCache;
        static DateTime deadlockAt;

        // Deadlock matches started in the last hour per matchmaking region, among the top 200 live matches
        // (the game's Watch tab, tracked by deadlock-api.com): a sample, good for comparing regions.
        // Supabase's shared copy first; without it this PC counts /v1/matches/active itself (allowed 100 requests a
        // second and cached by deadlock-api for 2 minutes; the old /v1/sql query allowed 20 an hour and is going away).
        public static Dictionary<string, int> DeadlockActivity()
        {
            lock (MmRegions)
            {
                var s = SharedDeadlock();
                // only regions counted from the live list (the old /v1/sql count was on another scale)
                var regions = DeadlockData.SharedRegions(s);
                if (regions != null) return regions;
                if (deadlockCache != null && DateTime.Now - deadlockAt < TimeSpan.FromMinutes(3)) return deadlockCache;
                string json = Http.Get("https://api.deadlock-api.com/v1/matches/active", 30000);
                var result = DeadlockData.CountRegions(json, DeadlockData.UnixNow());
                deadlockCache = result;
                deadlockAt = DateTime.Now;
                return result;
            }
        }

        // Players in matches right now ~= matches started per hour * players per match * hours per match.
        public static int EstimatePlayers(int matchesPerHour)
        {
            return (int)Math.Round(matchesPerHour * PlayersPerMatch * AvgMatchMinutes / 60.0);
        }

        // The sample's thresholds for the wait buckets. The live list holds about 2.6 times fewer matches per region than
        // the old /v1/sql count of the last hour (Europe 69 vs 189, North America 51 vs 121, measured together 2026-10-13),
        // so the old 100 / 40 / 10 became 40 / 15 / 4.
        public const int FastQueue = 40, MediumQueue = 15, SlowQueue = 4;

        // The wait bucket EstimateWait names: 0 the shortest ... 4 "very slow".
        public static int WaitBucket(int matches)
        {
            return matches >= FastQueue ? 0 : matches >= MediumQueue ? 1 : matches >= SlowQueue ? 2 : matches > 0 ? 3 : 4;
        }

        // Rough queue-time bucket from a region's matches in the live sample (DeadlockActivity). Arabic says the range in
        // words ("1 إلى 3"): a "1-3" range is turned around ("3-1") when drawn right to left.
        public static string EstimateWait(int matchesPerHour)
        {
            if (matchesPerHour >= FastQueue) return Lang.T("~1-3 min", "1 إلى 3 دقايق");
            if (matchesPerHour >= MediumQueue) return Lang.T("~3-6 min", "3 إلى 6 دقايق");
            if (matchesPerHour >= SlowQueue) return Lang.T("~6-12 min", "6 إلى 12 دقيقة");
            if (matchesPerHour > 0) return Lang.T("12+ min", "فوق 12 دقيقة");
            return Lang.T("very slow", "بطيء مرة");
        }

        // "≈1,234+" kept left-to-right inside Arabic text
        public static string Approx(int players) { return Lang.Ar ? players.ToString("N0") : "~" + players.ToString("N0"); }

        public static int SteamPlayers(string appId)
        {
            if (string.IsNullOrEmpty(appId)) return -1;
            try
            {
                string json = Http.Get("https://api.steampowered.com/ISteamUserStats/GetNumberOfCurrentPlayers/v1/?appid=" + appId, 10000);
                var resp = (Dictionary<string, object>)((Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json))["response"];
                object n;
                return resp.TryGetValue("player_count", out n) ? Convert.ToInt32(n, CultureInfo.InvariantCulture) : -1;
            }
            catch { return -1; }
        }

        // Each address's place, remembered for an hour (a failed lookup for 10 minutes): the automatic check asks every
        // 30-90 s for the same server, and ip-api.com allows 45 requests a minute (performance audit item 7).
        static readonly Dictionary<string, KeyValuePair<DateTime, string>> located = new Dictionary<string, KeyValuePair<DateTime, string>>();
        public static readonly TimeSpan LocateKeep = TimeSpan.FromHours(1), LocateRetry = TimeSpan.FromMinutes(10);

        // "Country - City" of an IP (ip-api.com, free), or null.
        public static string Locate(string ip) { return Locate(ip, DateTime.UtcNow, LocateOnline); }

        internal static string Locate(string ip, DateTime now, Func<string, string> lookup)
        {
            if (ip == null) return null;
            lock (located)
            {
                KeyValuePair<DateTime, string> e;
                if (located.TryGetValue(ip, out e) && now < e.Key) return e.Value;
            }
            string place = lookup(ip);
            lock (located)
            {
                if (located.Count >= 256) located.Clear(); // a long session never grows it without end
                located[ip] = new KeyValuePair<DateTime, string>(now + (place != null ? LocateKeep : LocateRetry), place);
            }
            return place;
        }

        internal static void ClearLocated() { lock (located) located.Clear(); } // tests

        static string LocateOnline(string ip)
        {
            try
            {
                string json = Http.Get("http://ip-api.com/json/" + ip + "?fields=status,country,city", 6000);
                var d = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
                if ((string)d["status"] != "success") return null;
                return d["country"] + " - " + d["city"];
            }
            catch { return null; }
        }
    }
}
