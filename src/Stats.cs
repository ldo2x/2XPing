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

        // Valve datacenter -> Deadlock matchmaking region it serves (only the clear ones).
        public static readonly Dictionary<string, string> PopToMm = new Dictionary<string, string>
        {
            {"ams","Europe"}, {"fra","Europe"}, {"dfra","Europe"}, {"lhr","Europe"}, {"par","Europe"}, {"mad","Europe"},
            {"vie","Europe"}, {"dvie","Europe"}, {"waw","Europe"}, {"sto","Europe"}, {"sto2","Europe"}, {"hel","Europe"},
            {"iad","Row"}, {"atl","Row"}, {"ord","Row"}, {"dfw","Row"}, {"lax","Row"}, {"sea","Row"},
            {"sgp","SEAsia"}, {"hkg","SEAsia"}, {"tyo","SEAsia"}, {"seo","SEAsia"}, {"bom","SEAsia"}, {"bom2","SEAsia"},
            {"maa","SEAsia"}, {"maa2","SEAsia"}, {"gum","SEAsia"},
            {"gru","SAmerica"}, {"eze","SAmerica"}, {"lim","SAmerica"}, {"scl","SAmerica"},
            {"syd","Oceania"},
        };

        public const int PlayersPerMatch = 12;
        public const double AvgMatchMinutes = 30;

        static Dictionary<string, int> deadlockCache;
        static DateTime deadlockAt;

        // Deadlock matches started in the last hour per matchmaking region (sample tracked by deadlock-api.com).
        public static Dictionary<string, int> DeadlockActivity()
        {
            lock (MmRegions)
            {
                if (deadlockCache != null && DateTime.Now - deadlockAt < TimeSpan.FromMinutes(3)) return deadlockCache;
                string sql = "SELECT toString(region_mode) AS region, uniq(match_id) AS matches FROM active_matches " +
                             "WHERE start_time > now() - INTERVAL 1 HOUR GROUP BY region";
                string json = Http.Get("https://api.deadlock-api.com/v1/sql?query=" + Uri.EscapeDataString(sql), 30000);
                var result = new Dictionary<string, int>();
                foreach (var row in (object[])new JavaScriptSerializer().DeserializeObject(json))
                {
                    var d = (Dictionary<string, object>)row;
                    result[(string)d["region"]] = Convert.ToInt32(d["matches"], CultureInfo.InvariantCulture);
                }
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

        // Rough queue-time bucket from how many matches a region starts per hour.
        public static string EstimateWait(int matchesPerHour)
        {
            if (matchesPerHour >= 100) return Lang.T("~1-3 min", "‎1-3‎ دقائق");
            if (matchesPerHour >= 40) return Lang.T("~3-6 min", "‎3-6‎ دقائق");
            if (matchesPerHour >= 10) return Lang.T("~6-12 min", "‎6-12‎ دقيقة");
            if (matchesPerHour > 0) return Lang.T("12+ min", "أكثر من 12 دقيقة");
            return Lang.T("very slow", "بطيء جداً");
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

        // "Country - City" of an IP (ip-api.com, free).
        public static string Locate(string ip)
        {
            try
            {
                string json = Http.Get("http://ip-api.com/json/" + ip + "?fields=status,country,city" + (Lang.Ar ? "&lang=ar" : ""), 6000);
                var d = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
                if ((string)d["status"] != "success") return null;
                return d["country"] + " - " + d["city"];
            }
            catch { return null; }
        }
    }

    // Finds the game server the game is talking to right now, using Windows' built-in packet monitor (pktmon).
    static class Detector
    {
        static readonly Regex PacketLine = new Regex(
            @"(\d+\.\d+\.\d+\.\d+)\.(\d+) > (\d+\.\d+\.\d+\.\d+)\.(\d+): UDP", RegexOptions.Compiled);

        public class Hit
        {
            public string Ip;
            public int Port, Packets;
        }

        // Local UDP ports opened by the game's processes.
        static HashSet<int> GamePorts(string[] processNames)
        {
            var pids = new HashSet<int>(processNames.SelectMany(n => Process.GetProcessesByName(n)).Select(p => p.Id));
            var ports = new HashSet<int>();
            if (pids.Count == 0) return ports;
            foreach (var line in Run("netstat", "-ano -p UDP").Split('\n'))
            {
                var f = line.Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                int pid, port;
                if (f.Length >= 4 && f[0] == "UDP" && int.TryParse(f[f.Length - 1], out pid) && pids.Contains(pid))
                {
                    var local = f[1];
                    if (int.TryParse(local.Substring(local.LastIndexOf(':') + 1), out port)) ports.Add(port);
                }
            }
            return ports;
        }

        static bool IsPublic(string ip)
        {
            var b = ip.Split('.').Select(int.Parse).ToArray();
            if (b[0] == 10 || b[0] == 127 || b[0] == 0 || b[0] >= 224) return false;
            if (b[0] == 192 && b[1] == 168) return false;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;
            if (b[0] == 169 && b[1] == 254) return false;
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false;
            return true;
        }

        public static List<Hit> Capture(string[] processNames, int seconds, out bool matchedGame)
        {
            string dir = Path.Combine(Storage.Dir, "capture");
            Directory.CreateDirectory(dir);
            string etl = Path.Combine(dir, "cap.etl"), txt = Path.Combine(dir, "cap.txt");
            if (File.Exists(txt)) File.Delete(txt);

            var gamePorts = GamePorts(processNames);
            Run("pktmon", "filter remove");
            Run("pktmon", "filter add 2XPingUDP -t UDP");
            try
            {
                Run("pktmon", "start --capture --comp nics --pkt-size 96 --file-name \"" + etl + "\"");
                Thread.Sleep(seconds * 1000);
            }
            finally
            {
                Run("pktmon", "stop");
                Run("pktmon", "filter remove");
            }
            Run("pktmon", "etl2txt \"" + etl + "\" --out \"" + txt + "\"");
            if (!File.Exists(txt)) throw new Exception("pktmon produced no output.");

            // also refresh after capture: the game may have opened its socket during it
            gamePorts.UnionWith(GamePorts(processNames));
            matchedGame = gamePorts.Count > 0;

            var hits = new Dictionary<string, Hit>();
            foreach (var line in File.ReadLines(txt))
            {
                var m = PacketLine.Match(line);
                if (!m.Success) continue;
                string a = m.Groups[1].Value, b = m.Groups[3].Value;
                int pa = int.Parse(m.Groups[2].Value), pb = int.Parse(m.Groups[4].Value);
                string remote; int rport, lport;
                if (IsPublic(b) && !IsPublic(a)) { remote = b; rport = pb; lport = pa; }
                else if (IsPublic(a) && !IsPublic(b)) { remote = a; rport = pa; lport = pb; }
                else continue;
                if (rport == 53 || rport == 123 || rport == 443) continue; // DNS, time sync, QUIC web traffic
                if (matchedGame && !gamePorts.Contains(lport)) continue;
                Hit h;
                if (!hits.TryGetValue(remote, out h)) hits[remote] = h = new Hit { Ip = remote, Port = rport };
                h.Packets++;
            }
            try { File.Delete(etl); File.Delete(txt); } catch { }
            return hits.Values.Where(h => h.Packets >= 20).OrderByDescending(h => h.Packets).Take(3).ToList();
        }

        static string Run(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(60000);
                return o;
            }
        }
    }
}
