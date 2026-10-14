using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PingBoost
{
    static class Storage
    {
        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "2XPing");

        static string RelaysFile { get { return Path.Combine(Dir, "relays.txt"); } }
        static string CustomFile { get { return Path.Combine(Dir, "custom.txt"); } }
        static string PicksFile { get { return Path.Combine(Dir, "picks.txt"); } }
        static string SettingsPath { get { return Path.Combine(Dir, "settings.txt"); } }

        static Storage()
        {
            // keep settings from the PingBoost days
            string old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PingBoost");
            try { if (Directory.Exists(old) && !Directory.Exists(Dir)) Directory.Move(old, Dir); } catch { }
            Directory.CreateDirectory(Dir);
        }

        static string Clean(string s) { return (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' '); }

        // settings.txt holds key=value lines; one shared, thread-safe copy kept in memory (see SettingsFile)
        static SettingsFile settings;
        static SettingsFile Settings { get { lock (fileGate) return settings ?? (settings = new SettingsFile(SettingsPath)); } }

        // the other small files (picks, relays, last results, detected and custom servers): one writer at a time
        static readonly object fileGate = new object();

        public static string GetSetting(string key) { return Settings.Get(key); }

        // false when the change couldn't be saved to disk (it still holds for this session)
        public static bool SetSetting(string key, string value) { return Settings.Set(key, value); }

        public static bool SetSettings(IDictionary<string, string> values) { return Settings.Set(values); }

        public static string GetOrAddSetting(string key, Func<string> make) { return Settings.GetOrAdd(key, make); }

        public static bool LoadArabic() { return GetSetting("lang") != "en"; }

        public static void SaveArabic(bool ar) { SetSetting("lang", ar ? "ar" : "en"); }

        public static HashSet<string> LoadPicks(string appId)
        {
            var set = new HashSet<string>();
            if (!File.Exists(PicksFile)) return set;
            foreach (var line in File.ReadAllLines(PicksFile))
            {
                var f = line.Split('\t');
                if (f.Length == 2 && f[0] == appId)
                    foreach (var c in f[1].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) set.Add(c);
            }
            return set;
        }

        public static void SavePicks(string appId, IEnumerable<string> codes)
        {
            lock (fileGate)
                try
                {
                    var lines = File.Exists(PicksFile)
                        ? File.ReadAllLines(PicksFile).Where(l => !l.StartsWith(appId + "\t")).ToList()
                        : new List<string>();
                    lines.Add(appId + "\t" + string.Join(",", codes));
                    AtomicFile.WriteAllLines(PicksFile, lines);
                }
                catch { }
        }

        public static void ClearPicks()
        {
            try { if (File.Exists(PicksFile)) File.Delete(PicksFile); } catch { }
        }

        public static List<Relay> LoadRelays()
        {
            var list = new List<Relay>();
            if (!File.Exists(RelaysFile)) return list;
            foreach (var line in File.ReadAllLines(RelaysFile))
            {
                var f = line.Split('\t');
                if (f.Length < 4) continue;
                list.Add(new Relay { Name = f[0], ConfPath = f[1], AgentUrl = f[2], Token = f[3] });
            }
            return list;
        }

        public static void SaveRelays(List<Relay> relays)
        {
            lock (fileGate)
                try
                {
                    AtomicFile.WriteAllLines(RelaysFile, relays.Select(r =>
                        string.Join("\t", Clean(r.Name), Clean(r.ConfPath), Clean(r.AgentUrl), Clean(r.Token))));
                }
                catch { }
        }

        public static List<Region> LoadCustom()
        {
            var list = new List<Region>();
            if (!File.Exists(CustomFile)) return list;
            foreach (var line in File.ReadAllLines(CustomFile))
            {
                var f = line.Split('\t');
                if (f.Length < 3) continue;
                var r = new Region { Id = f[0], NameEn = f[0] };
                foreach (var e in f[1].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    r.Endpoints.Add(Endpoint.Parse(e));
                foreach (var c in f[2].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    r.Cidrs.Add(c.Trim());
                if (r.Endpoints.Count > 0) list.Add(r);
            }
            return list;
        }

        static string LastFile { get { return Path.Combine(Dir, "last.txt"); } }

        // Last ping test of each game, shown right away on the next launch: game \t region \t ping \t loss
        public static void SaveLast(Game g)
        {
            lock (fileGate)
            try
            {
                var lines = File.Exists(LastFile) ? File.ReadAllLines(LastFile).Where(l => !l.StartsWith(g.Icon + "\t")).ToList() : new List<string>();
                foreach (var r in g.Regions.Where(r => r.Direct != null && !r.Detected))
                    lines.Add(string.Join("\t", g.Icon, r.Id, Math.Round(r.Direct.Avg).ToString(System.Globalization.CultureInfo.InvariantCulture),
                                          Math.Round(r.Direct.Loss).ToString(System.Globalization.CultureInfo.InvariantCulture)));
                AtomicFile.WriteAllLines(LastFile, lines);
            }
            catch { }
        }

        public static void LoadLast(Game g)
        {
            try
            {
                if (!File.Exists(LastFile)) return;
                var byId = g.Regions.GroupBy(r => r.Id).ToDictionary(x => x.Key, x => x.First());
                foreach (var line in File.ReadAllLines(LastFile))
                {
                    var f = line.Split('\t');
                    Region r;
                    double avg, loss;
                    if (f.Length < 4 || f[0] != g.Icon || !byId.TryGetValue(f[1], out r) || r.Direct != null) continue;
                    if (!double.TryParse(f[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out avg)) continue;
                    double.TryParse(f[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out loss);
                    r.Direct = new PingResult { Avg = avg, Loss = loss };
                }
            }
            catch { }
        }

        static string DetectedFile { get { return Path.Combine(Dir, "detected.txt"); } }

        // Servers found by live capture: game \t ip \t location
        public static List<Region> LoadDetected(string game)
        {
            var list = new List<Region>();
            if (!File.Exists(DetectedFile)) return list;
            foreach (var line in File.ReadAllLines(DetectedFile))
            {
                var f = line.Split('\t');
                // a damaged line (not a plain address) is skipped instead of breaking the game's whole server list
                if (f.Length < 3 || f[0] != game || !RemoteAddress.Valid(f[1]) || f[1].IndexOfAny(new[] { '/', '-', ':' }) >= 0) continue; // IPv4 only (the /24 below)
                list.Add(MakeDetected(f[1], f[2]));
            }
            return list;
        }

        public static Region MakeDetected(string ip, string location)
        {
            string loc = string.IsNullOrEmpty(location) ? "" : "  " + location;
            var r = new Region
            {
                Id = "detected-" + ip, Detected = true,
                NameEn = "Your server" + loc + "  (" + ip + ")",
                NameAr = "سيرفرك" + loc + "  (" + ip + ")"
            };
            r.Endpoints.Add(new Endpoint(ip, 0));
            int dot = ip.LastIndexOf('.');
            r.Cidrs.Add(ip.Substring(0, dot) + ".0/24"); // the game's servers usually share the /24
            return r;
        }

        public static void AddDetected(string game, string ip, string location)
        {
            lock (fileGate)
                try
                {
                    var lines = File.Exists(DetectedFile)
                        ? File.ReadAllLines(DetectedFile).Where(l => !l.StartsWith(game + "\t" + ip + "\t")).ToList()
                        : new List<string>();
                    lines.Add(string.Join("\t", Clean(game), ip, Clean(location)));
                    AtomicFile.WriteAllLines(DetectedFile, lines);
                }
                catch { }
        }
    }
}
