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

    enum Provider { Aws, Valve, Tencent, Blizzard, Custom }

    class Region
    {
        public string Id, NameEn, NameAr;
        public bool Picked; // "play on this server" (Valve region lock)
        public bool Detected; // found by live capture
        public bool Here;     // the game is connected to this region right now
        public string Flag;   // ISO country code for the flag image (assets/flags)
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
        public string Name, NoteEn, NoteAr, AppId, SteamAppId, Icon;
        public string[] Processes = new string[0];
        public Provider Provider;
        public string[] AwsRegions;
        public List<Region> Regions = new List<Region>();
        public bool Loaded;
        public string Note { get { return Lang.T(NoteEn, NoteAr); } }
        public bool Released;      // part of the public version; the rest only show in developer mode until fully tested
        public string[] LockSpace; // games locked by "everything except the picked datacenters" (see IpLists)
        public string[] LockAllow; // never blocked (the game's own services that aren't match servers)
        public string[] DetectIgnore; // never the server the player is on (voice chat, services): the server check skips them
        public string LockPorts;   // remote ports the lock covers (null = all)
        public Dictionary<string, string[]> LockGroups; // "Play on it" picks the whole group, so matches are found quickly
        // Valve: the datacenters that host this game's matches (null = all). The others are only relays: the game
        // enters Valve's network through the closest one (e.g. Dubai for Saudi players) and rides it to the match
        // server, so blocking them gains nothing and makes the route longer.
        public HashSet<string> HostPops;
        // The regions the player chooses between on the main screen (null = none, e.g. Arena Breakout). Deadlock fills it at load.
        public List<RegionDef> RegionMap;
        public bool Hosts(Region r) { return HostPops == null || HostPops.Contains(r.Id); }
        // Valve: the usual ping between two of its datacenters over its own network (GetSDRConfig "typical_pings")
        public Dictionary<string, int> Backbone = new Dictionary<string, int>();
        public int Between(string a, string b)
        {
            if (a == b) return 0;
            int ms;
            return Backbone.TryGetValue(string.CompareOrdinal(a, b) < 0 ? a + " " + b : b + " " + a, out ms) ? ms : -1;
        }
        public bool CanLock { get { return Provider == Provider.Valve || LockSpace != null; } }
        public bool Ignored(string ip) { return DetectIgnore != null && DetectIgnore.Any(c => Detector.InCidr(ip, c)); }
        // The datacenter (or Valve relay) an address belongs to, or null; ignored addresses never belong to one.
        public Region DatacenterOf(string ip)
        {
            if (Ignored(ip)) return null;
            return Regions.FirstOrDefault(r => r.Cidrs.Any(c => Detector.InCidr(ip, c)));
        }
        public string LockKey { get { return AppId ?? Icon; } } // where the picks are saved
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
            {"ap-east-1", new[] {"Asia - Hong Kong", "آسيا - هونغ كونغ"}},
            {"ap-southeast-2", new[] {"Oceania - Sydney", "أستراليا - سيدني"}},
            {"af-south-1", new[] {"Africa - Cape Town", "أفريقيا - كيب تاون"}},
            {"us-east-1", new[] {"NA - Virginia", "أمريكا - فيرجينيا"}},
            {"us-east-2", new[] {"NA - Ohio", "أمريكا - أوهايو"}},
            {"us-west-1", new[] {"NA - California", "أمريكا - كاليفورنيا"}},
            {"us-west-2", new[] {"NA - Oregon", "أمريكا - أوريغون"}},
            {"ca-central-1", new[] {"NA - Canada", "كندا"}},
            {"sa-east-1", new[] {"South America - Sao Paulo", "أمريكا الجنوبية - ساو باولو"}},
        };

        // Dead by Daylight's AWS GameLift regions. The game measures its ping to each region's GameLift ping beacon
        // (gamelift-ping.<region>.api.aws) and picks the region from that, so the lock blocks those regions' addresses.
        static readonly string[] DbdRegions =
        {
            "eu-central-1", "eu-west-1", "eu-west-2", "ap-south-1", "ap-southeast-1", "ap-east-1", "ap-northeast-1",
            "ap-northeast-2", "ap-southeast-2", "us-east-1", "us-east-2", "us-west-1", "us-west-2", "ca-central-1", "sa-east-1"
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

        // Overwatch 2 datacenters (codes as Blizzard names them). Their addresses come from the official lists:
        // Google Cloud regions, Amazon Bahrain, and Blizzard's own datacenters: every block of Blizzard's network
        // (RIPEstat, AS57976) placed by its location, plus the community lists of github.com/foryVERX/Overwatch-Server-Selector.
        // Blocks located in Irvine (Blizzard's headquarters: services, Practice Range) are left open. Mapped 2026-10-12.
        // Two service hosts are cut out of the blocks around them (see OwServiceHosts): 66.40.191.0/24 (LAX1) and 137.221.104.0/24 (AMS1).
        // { code, English, Arabic, flag, ping (gcp:<region> | ip | host:port), sources... }
        static readonly string[][] OwDatacenters =
        {
            new[] {"GMEC2", "Dammam (Saudi Arabia)", "الدمام - السعودية", "sa", "gcp:me-central2", "gcp:me-central2"},
            new[] {"GMEC1", "Doha (Qatar)", "الدوحة - قطر", "qa", "gcp:me-central1", "gcp:me-central1"},
            new[] {"MES1", "Bahrain", "البحرين", "bh", "dynamodb.me-south-1.amazonaws.com:443", "aws:me-south-1"},
            new[] {"GEW3", "Frankfurt (Germany)", "فرانكفورت - ألمانيا", "de", "gcp:europe-west3", "gcp:europe-west3"},
            new[] {"AMS1", "Amsterdam (Netherlands)", "أمستردام - هولندا", "nl", "5.42.168.1", "5.42.168.0/21", "64.224.26.0/23", "185.60.112.0/23",
                   "24.105.53.0/24", "24.105.54.0/24", "37.244.0.0/24", "37.244.3.0/24", "37.244.8.0/23", "37.244.14.0/23", "37.244.16.0/24",
                   "37.244.20.0/24", "37.244.24.0/24", "37.244.26.0/24", "37.244.28.0/23", "37.244.30.0/23", "37.244.54.0/24", "66.40.184.0/24",
                   "137.221.64.0/24", "137.221.72.0/24", "137.221.77.0/24", "137.221.78.0/23", "137.221.80.0/24", "137.221.82.0/24",
                   "137.221.86.0/24", "137.221.88.0/24", "137.221.95.0/24", "137.221.105.0/24", "137.221.106.0/23", "137.221.108.0/22", "137.221.112.0/24",
                   "158.115.219.0/24"},
            new[] {"CDG1", "Paris (France)", "باريس - فرنسا", "fr", "5.42.176.10", "5.42.176.0/23", "37.244.32.0/23", "37.244.44.0/22",
                   "37.244.50.0/24", "37.244.60.0/22"},
            new[] {"GEN1", "Finland", "فنلندا", "fi", "gcp:europe-north1", "gcp:europe-north1"},
            new[] {"GUE4", "Virginia (US East)", "فيرجينيا - أمريكا", "us", "gcp:us-east4", "gcp:us-east4", "37.244.4.0/22", "37.244.13.0/24",
                   "37.244.19.0/24", "37.244.27.0/24", "37.244.34.0/24", "137.221.75.0/24", "137.221.76.0/24", "198.74.33.0/24"},
            new[] {"ORD1", "Chicago (US Central)", "شيكاغو - أمريكا", "us", "24.105.40.1", "24.105.40.0/21", "24.105.56.0/21", "gcp:us-central1",
                   "37.244.2.0/24", "37.244.17.0/24", "37.244.23.0/24", "37.244.36.0/23", "137.221.69.0/24", "137.221.74.0/24", "198.74.37.0/24"},
            new[] {"LAX1", "Los Angeles (US West)", "لوس أنجلوس - أمريكا", "us", "gcp:us-west2", "64.224.24.0/23", "24.105.8.0/21", "24.105.24.0/21",
                   "gcp:us-west2", "5.42.178.0/23", "24.105.18.0/24", "37.244.25.0/24", "37.244.38.0/24", "66.40.176.0/21", "66.40.185.0/24",
                   "66.40.186.0/23", "66.40.188.0/23", "66.40.190.0/24", "137.221.68.0/24", "137.221.70.0/24", "137.221.73.0/24", "137.221.89.0/24",
                   "158.115.218.0/24", "158.115.220.0/23"},
            new[] {"GBR1", "Sao Paulo (Brazil)", "ساو باولو - البرازيل", "br", "gcp:southamerica-east1", "gcp:southamerica-east1"},
            new[] {"ICN1", "Seoul (Korea)", "سيول - كوريا", "kr", "121.254.137.1", "121.254.0.0/16", "117.52.0.0/16", "202.9.66.0/23",
                   "gcp:asia-northeast3", "37.244.52.0/23", "37.244.55.0/24", "103.198.32.0/24", "137.221.84.0/24", "182.162.31.0/24", "185.60.114.0/23"},
            new[] {"GTK1", "Tokyo (Japan)", "طوكيو - اليابان", "jp", "gcp:asia-northeast1", "gcp:asia-northeast1", "59.153.40.0/22"},
            new[] {"TPE1", "Taiwan", "تايوان", null, "5.42.160.1", "5.42.160.0/21", "gcp:asia-east1", "150.116.9.0/24", "158.115.194.0/23",
                   "158.115.198.0/23", "158.115.200.0/21"},
            new[] {"GSG1", "Singapore", "سنغافورة", "sg", "gcp:asia-southeast1", "gcp:asia-southeast1", "103.4.114.0/23"},
            new[] {"SYD2", "Sydney (Australia)", "سيدني - أستراليا", "au", "158.115.196.1", "158.115.196.0/23", "37.244.42.0/24",
                   "gcp:australia-southeast1", "37.244.10.0/24", "37.244.35.0/24", "37.244.40.0/22", "103.198.33.0/24", "137.221.81.0/24",
                   "137.221.85.0/24", "158.115.222.0/24"},
        };

        // What the lock blocks: every match datacenter that wasn't picked (whole Google regions, so new servers there
        // are blocked too). Everything else stays open: blocking all of Google's and Blizzard's networks also cut
        // services the game needs before a match (the Practice Range wouldn't even load), tested 2026-10-11.
        static readonly string[] OwSpace = OwDatacenters.SelectMany(dc => dc.Skip(5)).Distinct().ToArray();
        // Never blocked: Blizzard's Irvine range (Practice Range and services live there), and two Blizzard service hosts
        // inside the datacenter blocks (login: TCP 3724 to 66.40.191.x, TCP 443 to 137.221.104.171; blocking them broke
        // login, lock test 2026-10-13). The service hosts are also left out of the datacenters' own lists.
        static readonly string[] OwServiceHosts = { "66.40.191.0/24", "137.221.104.0/24" };
        static readonly string[] OwAllow = new[] { "64.224.0.0/21" }.Concat(OwServiceHosts).ToArray();
        // Never taken for the server the player is on: voice chat (Vivox), and everything in OwAllow (Irvine's menu and
        // Practice Range host, the service hosts): none of them is a match server.
        static readonly string[] OwIgnore = new[] { "85.236.96.0/21" }.Concat(OwAllow).ToArray();

        // Datacenters that make up one region. Allowing a whole region keeps queues short: Overwatch spreads players
        // over all of them (the MINA server selector's queue tests passed 5 of 5 per region this way).
        static readonly Dictionary<string, string[]> OwGroups = Groups(
            new[] { "GMEC2", "GMEC1", "MES1" },          // Middle East
            new[] { "GEW3", "AMS1", "CDG1", "GEN1" },    // Europe
            new[] { "GUE4", "ORD1" },                    // North America east
            new[] { "LAX1", "ORD1" },                    // North America west
            new[] { "GBR1" },                            // Brazil
            new[] { "ICN1", "GTK1", "TPE1" },            // East Asia
            new[] { "GSG1" },                            // Southeast Asia
            new[] { "SYD2" });                           // Australia

        // The regions Overwatch players choose between (datacenters grouped as the spec lists them).
        static readonly List<RegionDef> OwRegionMap = new List<RegionDef>
        {
            RegionDef.Of("me", "Middle East", "الشرق الأوسط", "GMEC2", "GMEC1", "MES1").In(Zones.Europe),
            RegionDef.Of("eu", "Europe", "أوروبا", "GEW3", "AMS1", "CDG1", "GEN1").In(Zones.Europe),
            RegionDef.Of("na", "North America", "أمريكا الشمالية", "GUE4", "ORD1", "LAX1").In(Zones.Americas),
            RegionDef.Of("sa", "South America", "أمريكا الجنوبية", "GBR1").In(Zones.Americas),
            RegionDef.Of("ea", "East Asia", "شرق آسيا", "ICN1", "GTK1", "TPE1").In(Zones.Asia),
            RegionDef.Of("sg", "Singapore", "سنغافورة", "GSG1").In(Zones.Asia),
            RegionDef.Of("au", "Australia", "أستراليا", "SYD2").In(Zones.Asia),
        };

        static readonly List<RegionDef> DbdRegionMap = new List<RegionDef>
        {
            RegionDef.Of("eu", "Europe", "أوروبا", "eu-central-1", "eu-west-1", "eu-west-2"),
            RegionDef.Of("na", "North America", "أمريكا الشمالية", "us-east-1", "us-east-2", "ca-central-1", "us-west-1", "us-west-2"),
            RegionDef.Of("in", "India", "الهند", "ap-south-1"),
            RegionDef.Of("ea", "East Asia", "شرق آسيا", "ap-northeast-1", "ap-northeast-2", "ap-east-1"),
            RegionDef.Of("sg", "Singapore", "سنغافورة", "ap-southeast-1"),
            RegionDef.Of("au", "Australia", "أستراليا", "ap-southeast-2"),
            RegionDef.Of("sa", "South America", "أمريكا الجنوبية", "sa-east-1"),
        };

        static Dictionary<string, string[]> Groups(params string[][] groups)
        {
            var d = new Dictionary<string, string[]>();
            foreach (var g in groups) foreach (var id in g) if (!d.ContainsKey(id)) d[id] = g;
            return d;
        }

        static List<Region> LoadOverwatch(Game g)
        {
            // Google's per-region test endpoints (gcping) give a real ping to each Google region
            var gcping = new Dictionary<string, string>();
            try
            {
                var root = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(
                    Http.Get("https://global.gcping.com/api/endpoints", 10000));
                foreach (var kv in root)
                {
                    var d = kv.Value as Dictionary<string, object>;
                    object url;
                    if (d != null && d.TryGetValue("URL", out url)) gcping[kv.Key] = ((string)url).TrimEnd('/') + "/api/ping";
                }
            }
            catch { }

            var picks = Storage.LoadPicks(g.LockKey);
            var list = new List<Region>();
            foreach (var dc in OwDatacenters)
            {
                var r = new Region { Id = dc[0], NameEn = dc[1] + "  [" + dc[0] + "]", NameAr = dc[2] + "  [" + dc[0] + "]", Flag = dc[3] };
                string ping = dc[4], url;
                if (ping.StartsWith("gcp:")) { if (gcping.TryGetValue(ping.Substring(4), out url)) r.Endpoints.Add(new Endpoint(url, Endpoint.Http)); }
                else r.Endpoints.Add(Endpoint.Parse(ping));
                try { r.Cidrs.AddRange(IpLists.Resolve(dc.Skip(5))); } catch { }
                r.Picked = picks.Contains(r.Id);
                list.Add(r);
            }
            return list;
        }

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

        // Developer mode also shows the games that aren't released yet.
        public static bool ShowUnreleased;

        // The games players see: the released ones (all of them in developer mode).
        public static List<Game> Available(List<Game> all)
        {
            return all.Where(g => g.Released || ShowUnreleased).ToList();
        }

        public static List<Game> All()
        {
            var aws = Aws.Keys.ToArray();
            // games on Amazon are locked region by region, from Amazon's official address list
            var awsLock = aws.Select(r => "aws:" + r).ToArray();
            return new List<Game>
            {
                new Game { Name = "Deadlock", Icon = "deadlock", Released = true, Provider = Provider.Valve, AppId = "1422450", Processes = new[] {"deadlock", "project8"},
                    HostPops = new HashSet<string>(Stats.PopToMm.Keys),
                    NoteEn = "Tick the servers you want to play on, then press Lock my servers.",
                    NoteAr = "علّم (✓) على السيرفرات اللي تبغى تلعب عليها، وبعدين اضغط «ثبّت سيرفراتي»." },
                new Game { Name = "Counter-Strike 2", Icon = "cs2", Provider = Provider.Valve, AppId = "730", SteamAppId = "730", Processes = new[] {"cs2"},
                    NoteEn = "Tick the servers you want to play on, then press Lock my servers.",
                    NoteAr = "علّم (✓) على السيرفرات اللي تبغى تلعب عليها، وبعدين اضغط «ثبّت سيرفراتي»." },
                new Game { Name = "Dota 2", Icon = "dota2", Provider = Provider.Valve, AppId = "570", SteamAppId = "570", Processes = new[] {"dota2"},
                    NoteEn = "Tick the servers you want to play on, then press Lock my servers.",
                    NoteAr = "علّم (✓) على السيرفرات اللي تبغى تلعب عليها، وبعدين اضغط «ثبّت سيرفراتي»." },
                new Game { Name = "Arena Breakout: Infinite", Icon = "abi", Released = true, Provider = Provider.Tencent, SteamAppId = "2073620", Processes = new[] {"UAGame"},
                    NoteEn = "The game picks your region from your account. Join a raid and press Which server am I on? to see your real server.",
                    NoteAr = "اللعبة تحدد منطقتك من حسابك. ادخل رايد واضغط «على أي سيرفر أنا؟» عشان تعرف سيرفرك الحقيقي." },
                new Game { Name = "Overwatch 2", Icon = "overwatch", Released = true, Provider = Provider.Blizzard, SteamAppId = "2357570", Processes = new[] {"Overwatch"}, LockSpace = OwSpace, LockAllow = OwAllow, DetectIgnore = OwIgnore, LockGroups = OwGroups, RegionMap = OwRegionMap,
                    LockPorts = "1-3723,3725-65535", // 3724 is Blizzard's own port: never block it
                    NoteEn = "Tick the datacenters you want and press Lock my servers. Try it in Quick Play first: if the game still picks a blocked one, the match won't load.",
                    NoteAr = "علّم على المراكز اللي تبغاها واضغط «ثبّت سيرفراتي». جرّبه في الكويك أول: لو اللعبة اختارت مركز محجوب، المباراة ما تفتح." },
                new Game { Name = "Valorant", Icon = "valorant", Provider = Provider.Aws, AwsRegions = aws, Processes = new[] {"VALORANT-Win64-Shipping"}, LockSpace = awsLock,
                    NoteEn = "Valorant runs on Amazon: tick the regions you want and press Lock my servers. Try it in Unrated first: if the game still picks a blocked one, the match won't load.",
                    NoteAr = "Valorant على Amazon: علّم على المناطق اللي تبغاها واضغط «ثبّت سيرفراتي». جرّبه في Unrated أول: لو اللعبة اختارت منطقة محجوبة، المباراة ما تفتح." },
                new Game { Name = "Fortnite", Icon = "fortnite", Provider = Provider.Aws, AwsRegions = aws, Processes = new[] {"FortniteClient-Win64-Shipping"}, LockSpace = awsLock,
                    NoteEn = "Pick the region in the game (Settings > Game > Matchmaking Region). To stay on one datacenter inside it, tick it here and press Lock my servers.",
                    NoteAr = "اختار المنطقة من داخل اللعبة (الإعدادات ← Game ← Matchmaking Region). ولو تبغى مركز معيّن داخلها، علّم عليه هنا واضغط «ثبّت سيرفراتي»." },
                new Game { Name = "PUBG: Battlegrounds", Icon = "pubg", SteamAppId = "578080", Provider = Provider.Aws, AwsRegions = aws, Processes = new[] {"TslGame"}, LockSpace = awsLock,
                    NoteEn = "Pick the server in the lobby (Server, next to Start). To stay on one Amazon datacenter, tick it here and press Lock my servers.",
                    NoteAr = "اختار السيرفر من اللوبي (Server جنب زر Start). ولو تبغى مركز Amazon معيّن، علّم عليه هنا واضغط «ثبّت سيرفراتي»." },
                new Game { Name = "Apex Legends", Icon = "apex", SteamAppId = "1172470", Provider = Provider.Aws, AwsRegions = aws, Processes = new[] {"r5apex"},
                    NoteEn = "Apex lets you pick the data center on its start screen (Data Center). Pick the one with the lowest ping here.",
                    NoteAr = "Apex تخليك تختار المركز من شاشتها الأولى (Data Center). اختار اللي بنقه أقل هنا." },
                new Game { Name = "Rocket League", Icon = "rocketleague", SteamAppId = "252950", Provider = Provider.Aws, AwsRegions = aws, Processes = new[] {"RocketLeague"},
                    NoteEn = "Rocket League lets you tick the regions yourself in the Play menu. Tick the ones with the lowest ping here.",
                    NoteAr = "Rocket League تخليك تختار المناطق بنفسك من قائمة Play. اختار اللي بنقها أقل هنا." },
                new Game { Name = "Dead by Daylight", Icon = "dbd", Released = true, SteamAppId = "381210", Provider = Provider.Aws, AwsRegions = DbdRegions, RegionMap = DbdRegionMap,
                    LockSpace = DbdRegions.Select(r => "aws:" + r).ToArray(), Processes = new[] {"DeadByDaylight-Win64-Shipping"},
                    NoteEn = "Dead by Daylight runs on Amazon GameLift and has no Middle East region. Tick the regions you want and press Lock my servers.",
                    NoteAr = "Dead by Daylight على Amazon GameLift، وما فيها منطقة للشرق الأوسط. علّم على المناطق اللي تبغاها واضغط «ثبّت سيرفراتي»." },
            };
        }

        // Country of each region id (AWS, Valve, Tencent, Google), for the flag next to the server name.
        static readonly Dictionary<string, string> RegionCountry = new Dictionary<string, string>
        {
            // AWS
            {"me-south-1","bh"}, {"me-central-1","ae"}, {"eu-central-1","de"}, {"eu-west-3","fr"}, {"eu-west-2","gb"},
            {"eu-west-1","ie"}, {"eu-north-1","se"}, {"eu-south-1","it"}, {"ap-south-1","in"}, {"ap-southeast-1","sg"},
            {"ap-northeast-1","jp"}, {"ap-northeast-2","kr"}, {"ap-southeast-2","au"}, {"af-south-1","za"},
            {"us-east-1","us"}, {"us-east-2","us"}, {"us-west-1","us"}, {"us-west-2","us"}, {"ap-east-1","hk"}, {"ca-central-1","ca"}, {"sa-east-1","br"},
            // Valve
            {"ams","nl"}, {"fra","de"}, {"dfra","de"}, {"lhr","gb"}, {"par","fr"}, {"mad","es"}, {"vie","at"}, {"dvie","at"},
            {"waw","pl"}, {"sto","se"}, {"sto2","se"}, {"hel","fi"}, {"bom","in"}, {"bom2","in"}, {"maa","in"}, {"maa2","in"},
            {"sgp","sg"}, {"hkg","hk"}, {"tyo","jp"}, {"seo","kr"}, {"syd","au"}, {"gum","gu"}, {"jnb","za"}, {"dxb","ae"},
            {"iad","us"}, {"atl","us"}, {"ord","us"}, {"dfw","us"}, {"lax","us"}, {"sea","us"}, {"okc","us"},
            {"gru","br"}, {"eze","ar"}, {"lim","pe"}, {"scl","cl"},
            // Tencent
            {"me-saudi-arabia","sa"}, {"eu-frankfurt","de"}, {"na-ashburn","us"}, {"na-siliconvalley","us"}, {"ap-singapore","sg"},
            {"ap-hongkong","hk"}, {"ap-tokyo","jp"}, {"ap-seoul","kr"}, {"ap-bangkok","th"}, {"ap-jakarta","id"}, {"sa-saopaulo","br"},
            // Google
            {"me-central2","sa"}, {"me-central1","qa"}, {"europe-west4","nl"}, {"europe-west3","de"}, {"europe-west9","fr"},
            {"europe-west2","gb"}, {"europe-west1","be"}, {"asia-south1","in"}, {"asia-southeast1","sg"}, {"asia-northeast1","jp"},
            {"australia-southeast1","au"}, {"us-east4","us"}, {"us-west1","us"}, {"southamerica-east1","br"},
        };

        // Fallback for Valve datacenters not in the table: the country written in their description, e.g. "(China)".
        static readonly Dictionary<string, string> CountryNames = new Dictionary<string, string>
        {
            {"China","cn"}, {"Netherlands","nl"}, {"Germany","de"}, {"England","gb"}, {"France","fr"}, {"Spain","es"},
            {"Austria","at"}, {"Poland","pl"}, {"Sweden","se"}, {"Finland","fi"}, {"India","in"}, {"Japan","jp"},
            {"South Korea","kr"}, {"Australia","au"}, {"South Africa","za"}, {"United Arab Emirates","ae"}, {"Brazil","br"},
            {"Argentina","ar"}, {"Peru","pe"}, {"Chile","cl"}, {"Singapore","sg"}, {"Hong Kong","hk"},
            {"Georgia","us"}, {"Texas","us"}, {"California","us"}, {"Washington","us"}, {"Virginia","us"}, {"Illinois","us"},
        };

        static string FlagFor(string id, string desc)
        {
            string cc;
            if (id != null && RegionCountry.TryGetValue(id, out cc)) return cc;
            if (desc != null)
                foreach (var kv in CountryNames)
                    if (desc.Contains("(" + kv.Key + ")")) return kv.Value;
            return null;
        }

        public static void Load(Game g)
        {
            if (g.Loaded) return;
            var list = new List<Region>();
            if (g.Provider == Provider.Aws)
            {
                var picks = Storage.LoadPicks(g.LockKey);
                foreach (var id in g.AwsRegions)
                {
                    var r = new Region { Id = id, NameEn = Aws[id][0] + "  (" + id + ")", NameAr = Aws[id][1] }; // the code (eu-central-1) is technical and breaks right-to-left text
                    if (g.LockSpace != null)
                    {
                        // the same beacon the game itself measures
                        r.Endpoints.Add(new Endpoint("gamelift-ping." + id + ".api.aws", 7770, true));
                        try { r.Cidrs.AddRange(IpLists.Resolve("aws:" + id)); } catch { }
                        r.Picked = picks.Contains(id);
                    }
                    r.Endpoints.Add(new Endpoint("dynamodb." + id + ".amazonaws.com", 443));
                    r.Endpoints.Add(new Endpoint("ec2." + id + ".amazonaws.com", 443));
                    list.Add(r);
                }
            }
            else if (g.Provider == Provider.Valve)
            {
                list = LoadValve(g.AppId, g.Backbone);
                var picks = Storage.LoadPicks(g.AppId);
                foreach (var r in list) r.Picked = picks.Contains(r.Id);
            }
            else if (g.Provider == Provider.Blizzard)
            {
                list = LoadOverwatch(g);
            }
            else if (g.Provider == Provider.Tencent)
            {
                foreach (var kv in Tencent)
                {
                    var r = new Region { Id = kv.Key, NameEn = kv.Value[0] + "  (" + kv.Key + ")", NameAr = kv.Value[1] };
                    r.Endpoints.Add(new Endpoint("cos." + kv.Key + ".myqcloud.com", 443));
                    list.Add(r);
                }
            }
            else
            {
                list = Storage.LoadCustom();
            }
            // Valve and Overwatch name the datacenter instead (see "Which server am I on?")
            if (g.Provider != Provider.Valve && g.LockSpace == null) list.InsertRange(0, Storage.LoadDetected(g.Name));
            foreach (var r in list) if (r.Flag == null) r.Flag = FlagFor(r.Id, r.NameEn);
            g.Regions = list;
            if (g.Icon == "deadlock") g.RegionMap = RegionGroups.DeadlockMap(g);
            g.Loaded = g.Provider != Provider.Custom;
        }

        static IEnumerable<object> Arr(object o)
        {
            var e = o as IEnumerable;
            return e == null || o is string ? Enumerable.Empty<object>() : e.Cast<object>();
        }

        static List<Region> LoadValve(string appId, Dictionary<string, int> backbone)
        {
            string json = Http.Get("https://api.steampowered.com/ISteamApps/GetSDRConfig/v1/?appid=" + appId, 10000);
            var root = (Dictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json);
            var pops = (Dictionary<string, object>)root["pops"];
            object typical;
            backbone.Clear();
            if (root.TryGetValue("typical_pings", out typical))
                foreach (var e in Arr(typical))
                {
                    var t = Arr(e).ToList();
                    if (t.Count < 3) continue;
                    string a = t[0].ToString(), b = t[1].ToString();
                    backbone[string.CompareOrdinal(a, b) < 0 ? a + " " + b : b + " " + a] = Convert.ToInt32(t[2], System.Globalization.CultureInfo.InvariantCulture);
                }
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
            return IpLists.Resolve("aws:" + region).Distinct().ToList();
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

        static string LastFile { get { return Path.Combine(Dir, "last.txt"); } }

        // Last ping test of each game, shown right away on the next launch: game \t region \t ping \t loss
        public static void SaveLast(Game g)
        {
            try
            {
                var lines = File.Exists(LastFile) ? File.ReadAllLines(LastFile).Where(l => !l.StartsWith(g.Icon + "\t")).ToList() : new List<string>();
                foreach (var r in g.Regions.Where(r => r.Direct != null && !r.Detected))
                    lines.Add(string.Join("\t", g.Icon, r.Id, Math.Round(r.Direct.Avg).ToString(System.Globalization.CultureInfo.InvariantCulture),
                                          Math.Round(r.Direct.Loss).ToString(System.Globalization.CultureInfo.InvariantCulture)));
                File.WriteAllLines(LastFile, lines);
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
