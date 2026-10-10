using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace PingBoost
{
    static class Lang
    {
        public static bool Ar = true;
        public static string T(string en, string ar) { return Ar ? ar : en; }
        public static TextFormatFlags Flags { get { return Ar ? TextFormatFlags.RightToLeft : TextFormatFlags.Default; } }
    }

    enum Provider { Aws, Valve, Tencent, Custom }

    class Region
    {
        public string Id, NameEn, NameAr;
        public bool Picked; // "play on this server" (Valve region lock)
        public bool Detected; // found by live capture
        public bool Here;     // the game is connected to this region right now
        public List<Endpoint> Endpoints = new List<Endpoint>();
        public List<string> Cidrs = new List<string>();
        public PingResult Direct;
        public Endpoint BestEndpoint;
        public Dictionary<string, double> ViaRelay = new Dictionary<string, double>();

        public string Name { get { return Lang.Ar && NameAr != null ? NameAr : NameEn; } }
        public Endpoint Target { get { return BestEndpoint ?? Endpoints.FirstOrDefault(); } }
    }

    class Game
    {
        public string Name, NoteEn, NoteAr, AppId, SteamAppId;
        public string[] Processes = new string[0];
        public Provider Provider;
        public string[] AwsRegions;
        public List<Region> Regions = new List<Region>();
        public bool Loaded;
        public string Note { get { return Lang.T(NoteEn, NoteAr); } }
        public bool CanLock { get { return Provider == Provider.Valve; } }
        public override string ToString() { return Name; }
    }

    static class GameDb
    {
        static readonly Dictionary<string, string[]> Aws = new Dictionary<string, string[]>
        {
            {"me-south-1", new[] {"Middle East - Bahrain", "الشرق الأوسط - البحرين"}},
            {"me-central-1", new[] {"Middle East - UAE", "الشرق الأوسط - الإمارات"}},
            {"eu-central-1", new[] {"Europe - Frankfurt", "أوروبا - فرانكفورت"}},
            {"eu-west-3", new[] {"Europe - Paris", "أوروبا - باريس"}},
            {"eu-west-2", new[] {"Europe - London", "أوروبا - لندن"}},
            {"eu-west-1", new[] {"Europe - Ireland", "أوروبا - أيرلندا"}},
            {"eu-north-1", new[] {"Europe - Stockholm", "أوروبا - ستوكهولم"}},
            {"eu-south-1", new[] {"Europe - Milan", "أوروبا - ميلانو"}},
            {"ap-south-1", new[] {"Asia - Mumbai", "آسيا - مومباي"}},
            {"ap-southeast-1", new[] {"Asia - Singapore", "آسيا - سنغافورة"}},
            {"ap-northeast-1", new[] {"Asia - Tokyo", "آسيا - طوكيو"}},
            {"ap-northeast-2", new[] {"Asia - Seoul", "آسيا - سيول"}},
            {"ap-southeast-2", new[] {"Oceania - Sydney", "أستراليا - سيدني"}},
            {"af-south-1", new[] {"Africa - Cape Town", "أفريقيا - كيب تاون"}},
            {"us-east-1", new[] {"NA - Virginia", "أمريكا - فيرجينيا"}},
            {"us-east-2", new[] {"NA - Ohio", "أمريكا - أوهايو"}},
            {"us-west-2", new[] {"NA - Oregon", "أمريكا - أوريغون"}},
            {"ca-central-1", new[] {"NA - Canada", "كندا"}},
            {"sa-east-1", new[] {"South America - Sao Paulo", "أمريكا الجنوبية - ساو باولو"}},
        };

        // Tencent Cloud regions (reference datacenters, pinged via their regional storage endpoint)
        static readonly Dictionary<string, string[]> Tencent = new Dictionary<string, string[]>
        {
            {"me-saudi-arabia", new[] {"Middle East - Saudi Arabia", "الشرق الأوسط - السعودية"}},
            {"eu-frankfurt", new[] {"Europe - Frankfurt", "أوروبا - فرانكفورت"}},
            {"na-ashburn", new[] {"NA - Virginia", "أمريكا - فيرجينيا"}},
            {"na-siliconvalley", new[] {"NA - Silicon Valley", "أمريكا - سيليكون فالي"}},
            {"ap-singapore", new[] {"Asia - Singapore", "آسيا - سنغافورة"}},
            {"ap-hongkong", new[] {"Asia - Hong Kong", "آسيا - هونغ كونغ"}},
            {"ap-tokyo", new[] {"Asia - Tokyo", "آسيا - طوكيو"}},
            {"ap-seoul", new[] {"Asia - Seoul", "آسيا - سيول"}},
            {"ap-bangkok", new[] {"Asia - Bangkok", "آسيا - بانكوك"}},
            {"ap-jakarta", new[] {"Asia - Jakarta", "آسيا - جاكرتا"}},
            {"sa-saopaulo", new[] {"South America - Sao Paulo", "أمريكا الجنوبية - ساو باولو"}},
        };

        static readonly Dictionary<string, string> ValveAr = new Dictionary<string, string>
        {
            {"dxb", "دبي - الإمارات"}, {"fra", "فرانكفورت - ألمانيا"}, {"dfra", "فرانكفورت (Datapacket) - ألمانيا"},
            {"ams", "أمستردام - هولندا"}, {"lhr", "لندن - بريطانيا"}, {"par", "باريس - فرنسا"},
            {"mad", "مدريد - إسبانيا"}, {"vie", "فيينا - النمسا"}, {"dvie", "فيينا (Datapacket) - النمسا"},
            {"waw", "وارسو - بولندا"}, {"sto", "ستوكهولم (Kista) - السويد"}, {"sto2", "ستوكهولم (Bromma) - السويد"},
            {"hel", "هلسنكي - فنلندا"}, {"bom", "مومباي - الهند"}, {"bom2", "مومباي - الهند"},
            {"maa", "تشيناي - الهند"}, {"maa2", "تشيناي - الهند"}, {"sgp", "سنغافورة"},
            {"hkg", "هونغ كونغ"}, {"tyo", "طوكيو - اليابان"}, {"seo", "سيول - كوريا"},
            {"syd", "سيدني - أستراليا"}, {"gum", "غوام"}, {"jnb", "جوهانسبرغ - جنوب أفريقيا"},
            {"iad", "فيرجينيا - أمريكا"}, {"atl", "أتلانتا - أمريكا"}, {"ord", "شيكاغو - أمريكا"},
            {"dfw", "دالاس - أمريكا"}, {"lax", "لوس أنجلوس - أمريكا"}, {"sea", "سياتل - أمريكا"},
            {"gru", "ساو باولو - البرازيل"}, {"eze", "بوينس آيرس - الأرجنتين"}, {"lim", "ليما - بيرو"},
            {"scl", "سانتياغو - تشيلي"},
        };

        public static List<Game> All()
        {
            var aws = Aws.Keys.ToArray();
            return new List<Game>
            {
                new Game { Name = "Deadlock", Provider = Provider.Valve, AppId = "1422450", Processes = new[] {"deadlock", "project8"},
                    NoteEn = "Tick the servers you want to play on, then press Lock my servers.",
                    NoteAr = "علّم (✓) على السيرفرات اللي تبغى تلعب عليها، وبعدين اضغط «ثبّت سيرفراتي»." },
                new Game { Name = "Counter-Strike 2", Provider = Provider.Valve, AppId = "730", SteamAppId = "730", Processes = new[] {"cs2"},
                    NoteEn = "Tick the servers you want to play on, then press Lock my servers.",
                    NoteAr = "علّم (✓) على السيرفرات اللي تبغى تلعب عليها، وبعدين اضغط «ثبّت سيرفراتي»." },
                new Game { Name = "Dota 2", Provider = Provider.Valve, AppId = "570", SteamAppId = "570", Processes = new[] {"dota2"},
                    NoteEn = "Tick the servers you want to play on, then press Lock my servers.",
                    NoteAr = "علّم (✓) على السيرفرات اللي تبغى تلعب عليها، وبعدين اضغط «ثبّت سيرفراتي»." },
                new Game { Name = "Arena Breakout: Infinite", Provider = Provider.Tencent, SteamAppId = "2073620", Processes = new[] {"UAGame"},
                    NoteEn = "The game picks your region from your account. Join a raid and press Which server am I on? to see your real server.",
                    NoteAr = "اللعبة تحدد منطقتك من حسابك. ادخل رايد واضغط «على أي سيرفر أنا؟» عشان تعرف سيرفرك الحقيقي." },
                new Game { Name = "Valorant", Provider = Provider.Aws, AwsRegions = aws, Processes = new[] {"VALORANT-Win64-Shipping"},
                    NoteEn = "The ping shown is to each region's AWS datacenter, where the game servers are.",
                    NoteAr = "البنق اللي تشوفه هو لمركز البيانات (AWS) في كل منطقة، وهناك سيرفرات اللعبة." },
                new Game { Name = "Fortnite", Provider = Provider.Aws, AwsRegions = aws, Processes = new[] {"FortniteClient-Win64-Shipping"},
                    NoteEn = "The ping shown is to each region's AWS datacenter, where the game servers are.",
                    NoteAr = "البنق اللي تشوفه هو لمركز البيانات (AWS) في كل منطقة، وهناك سيرفرات اللعبة." },
                new Game { Name = "PUBG: Battlegrounds", SteamAppId = "578080", Provider = Provider.Aws, AwsRegions = aws, Processes = new[] {"TslGame"},
                    NoteEn = "The ping shown is to each region's AWS datacenter, where the game servers are.",
                    NoteAr = "البنق اللي تشوفه هو لمركز البيانات (AWS) في كل منطقة، وهناك سيرفرات اللعبة." },
                new Game { Name = "Apex Legends", SteamAppId = "1172470", Provider = Provider.Aws, AwsRegions = aws, Processes = new[] {"r5apex"},
                    NoteEn = "Apex uses several hosting providers, so these numbers are a rough guide.",
                    NoteAr = "Apex على أكثر من مزوّد، فالأرقام هنا تقريبية." },
                new Game { Name = "Rocket League", SteamAppId = "252950", Provider = Provider.Aws, AwsRegions = aws, Processes = new[] {"RocketLeague"},
                    NoteEn = "These numbers are a rough guide based on AWS datacenters.",
                    NoteAr = "الأرقام هنا تقريبية حسب مراكز AWS." },
                new Game { Name = "Dead by Daylight", SteamAppId = "381210", Provider = Provider.Aws, AwsRegions = aws, Processes = new[] {"DeadByDaylight-Win64-Shipping"},
                    NoteEn = "The ping shown is to each region's AWS datacenter, where the game servers are.",
                    NoteAr = "البنق اللي تشوفه هو لمركز البيانات (AWS) في كل منطقة، وهناك سيرفرات اللعبة." },
                new Game { Name = "Custom servers", Provider = Provider.Custom,
                    NoteEn = "Your own servers. Add them from Tools > Add a server (Advanced layout).",
                    NoteAr = "سيرفرات تضيفها بنفسك من «أدوات وسجل» ← «ضيف سيرفر» (في الوضع المتقدم)." },
            };
        }

        public static void Load(Game g)
        {
            if (g.Loaded) return;
            var list = new List<Region>();
            if (g.Provider == Provider.Aws)
            {
                foreach (var id in g.AwsRegions)
                {
                    var r = new Region { Id = id, NameEn = Aws[id][0] + "  (" + id + ")", NameAr = Aws[id][1] + "  (" + id + ")" };
                    r.Endpoints.Add(new Endpoint("dynamodb." + id + ".amazonaws.com", 443));
                    r.Endpoints.Add(new Endpoint("ec2." + id + ".amazonaws.com", 443));
                    list.Add(r);
                }
            }
            else if (g.Provider == Provider.Valve)
            {
                list = LoadValve(g.AppId);
                var picks = Storage.LoadPicks(g.AppId);
                foreach (var r in list) r.Picked = picks.Contains(r.Id);
            }
            else if (g.Provider == Provider.Tencent)
            {
                foreach (var kv in Tencent)
                {
                    var r = new Region { Id = kv.Key, NameEn = kv.Value[0] + "  (" + kv.Key + ")", NameAr = kv.Value[1] + "  (" + kv.Key + ")" };
                    r.Endpoints.Add(new Endpoint("cos." + kv.Key + ".myqcloud.com", 443));
                    list.Add(r);
                }
            }
            else
            {
                list = Storage.LoadCustom();
            }
            // Valve servers are matched to their region instead (see "Which server am I on?")
            if (g.Provider != Provider.Valve) list.InsertRange(0, Storage.LoadDetected(g.Name));
            g.Regions = list;
            g.Loaded = g.Provider != Provider.Custom;
        }

        static IEnumerable<object> Arr(object o)
        {
            var e = o as IEnumerable;
            return e == null || o is string ? Enumerable.Empty<object>() : e.Cast<object>();
        }

        static List<Region> LoadValve(string appId)
        {
            string json = Http.Get("https://api.steampowered.com/ISteamApps/GetSDRConfig/v1/?appid=" + appId, 10000);
            var root = (Dictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json);
            var pops = (Dictionary<string, object>)root["pops"];
            var list = new List<Region>();
            foreach (var kv in pops)
            {
                var pop = kv.Value as Dictionary<string, object>;
                object relays;
                if (pop == null || !pop.TryGetValue("relays", out relays)) continue;
                object desc;
                pop.TryGetValue("desc", out desc);
                string ar;
                var r = new Region
                {
                    Id = kv.Key,
                    NameEn = (desc as string ?? kv.Key) + "  [" + kv.Key + "]",
                    NameAr = ValveAr.TryGetValue(kv.Key, out ar) ? ar + "  [" + kv.Key + "]" : null
                };
                foreach (var rel in Arr(relays))
                {
                    var d = rel as Dictionary<string, object>;
                    object ip;
                    if (d == null || !d.TryGetValue("ipv4", out ip)) continue;
                    if (r.Endpoints.Count < 2) r.Endpoints.Add(new Endpoint((string)ip, 0));
                    r.Cidrs.Add(ip + "/32");
                }
                object ranges;
                if (pop.TryGetValue("service_address_ranges", out ranges))
                    foreach (var c in Arr(ranges)) r.Cidrs.Add(c.ToString());
                if (r.Endpoints.Count > 0) list.Add(r);
            }
            return list.OrderBy(x => x.NameEn).ToList();
        }

        // EC2 address ranges of an AWS region (where the game servers live). Cached for 3 days.
        public static List<string> AwsCidrs(string region)
        {
            string cache = Path.Combine(Storage.Dir, "aws-ip-ranges.json");
            if (!File.Exists(cache) || File.GetLastWriteTime(cache) < DateTime.Now.AddDays(-3))
                File.WriteAllText(cache, Http.Get("https://ip-ranges.amazonaws.com/ip-ranges.json", 30000));
            var root = (Dictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                .DeserializeObject(File.ReadAllText(cache));
            var result = new List<string>();
            foreach (var p in Arr(root["prefixes"]))
            {
                var d = p as Dictionary<string, object>;
                if (d == null) continue;
                if ((string)d["region"] == region && (string)d["service"] == "EC2")
                    result.Add((string)d["ip_prefix"]);
            }
            return result.Distinct().ToList();
        }
    }

    static class Storage
    {
        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "2XPing");

        static string RelaysFile { get { return Path.Combine(Dir, "relays.txt"); } }
        static string CustomFile { get { return Path.Combine(Dir, "custom.txt"); } }
        static string PicksFile { get { return Path.Combine(Dir, "picks.txt"); } }
        static string SettingsFile { get { return Path.Combine(Dir, "settings.txt"); } }

        static Storage()
        {
            // keep settings from the PingBoost days
            string old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PingBoost");
            try { if (Directory.Exists(old) && !Directory.Exists(Dir)) Directory.Move(old, Dir); } catch { }
            Directory.CreateDirectory(Dir);
        }

        static string Clean(string s) { return (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' '); }

        // settings.txt holds key=value lines
        static Dictionary<string, string> ReadSettings()
        {
            var d = new Dictionary<string, string>();
            try
            {
                if (File.Exists(SettingsFile))
                    foreach (var line in File.ReadAllLines(SettingsFile))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0) d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }
            }
            catch { }
            return d;
        }

        public static string GetSetting(string key)
        {
            string v;
            return ReadSettings().TryGetValue(key, out v) ? v : null;
        }

        public static void SetSetting(string key, string value)
        {
            var d = ReadSettings();
            d[key] = Clean(value);
            try { File.WriteAllLines(SettingsFile, d.Select(kv => kv.Key + "=" + kv.Value)); } catch { }
        }

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
            var lines = File.Exists(PicksFile)
                ? File.ReadAllLines(PicksFile).Where(l => !l.StartsWith(appId + "\t")).ToList()
                : new List<string>();
            lines.Add(appId + "\t" + string.Join(",", codes));
            File.WriteAllLines(PicksFile, lines);
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
            File.WriteAllLines(RelaysFile, relays.Select(r =>
                string.Join("\t", Clean(r.Name), Clean(r.ConfPath), Clean(r.AgentUrl), Clean(r.Token))));
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

        static string DetectedFile { get { return Path.Combine(Dir, "detected.txt"); } }

        // Servers found by live capture: game \t ip \t location
        public static List<Region> LoadDetected(string game)
        {
            var list = new List<Region>();
            if (!File.Exists(DetectedFile)) return list;
            foreach (var line in File.ReadAllLines(DetectedFile))
            {
                var f = line.Split('\t');
                if (f.Length < 3 || f[0] != game) continue;
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
            var lines = File.Exists(DetectedFile)
                ? File.ReadAllLines(DetectedFile).Where(l => !l.StartsWith(game + "\t" + ip + "\t")).ToList()
                : new List<string>();
            lines.Add(string.Join("\t", Clean(game), ip, Clean(location)));
            File.WriteAllLines(DetectedFile, lines);
        }

        public static void AddCustom(string name, string endpoints, string cidrs)
        {
            File.AppendAllLines(CustomFile, new[] { string.Join("\t", Clean(name), Clean(endpoints), Clean(cidrs)) });
        }
    }
}
