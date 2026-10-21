using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace PingBoost
{
    // Usage statistics sent to the developer's Supabase project (see tools/supabase-setup.sql).
    // Users are told about it on first launch (WelcomeDialog); using the beta requires accepting it.
    // Settings' «شارك إحصائيات البيتا» (Prefs.ShareStats, on by default) decides how much: off, every personal field gets
    // a fixed value and no location lookup is made (Payload), so only anonymous usage is sent (SECURITY.md).
    static class Telemetry
    {
        // the name an install sends while sharing is off; the developer panel marks these «مجهول» / Anonymous
        public const string AnonymousName = "(anonymous)";

        // Project URL and the PUBLIC (anon / publishable) key. The public key can only call checkin(), never read.
        public const string SupabaseUrl = "https://exzriktevuapbbgqwgle.supabase.co";
        public const string SupabaseKey = "sb_publishable_RN9M6tcRWFXbqfJdbSOo1A_h0upD6RQ";

        public static bool Configured { get { return SupabaseUrl != "" && SupabaseKey != ""; } }

        // live state, updated by the main window
        public static volatile string Game, Server;
        public static int Ping = -1;

        static System.Threading.Timer heartbeat;
        // "still open" every 2 minutes (ruling R2, was every minute); the developer panel counts an install as online for
        // OnlineWindow after its last one: two beats and a margin
        public static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(120);
        public static readonly TimeSpan OnlineWindow = TimeSpan.FromMinutes(4.5);
        // .NET closes a connection after 100 s idle by default, so every 2-minute beat would pay a new TCP + TLS handshake
        // (about 5 KB). Kept open a little longer than one beat, one connection carries them all: measured 1 connection
        // for 3 posts 120 s apart instead of 3 (loopback listener, 2026-10-13); Supabase itself kept an idle TLS connection
        // open for 125 s.
        public static readonly TimeSpan KeepConnection = HeartbeatEvery + TimeSpan.FromSeconds(30);
        static System.Net.ServicePoint supabasePoint; // held, so the setting lives as long as the app

        // The Supabase connection is kept open across heartbeats (KeepConnection).
        internal static void KeepConnectionOpen()
        {
            try
            {
                if (supabasePoint == null) supabasePoint = System.Net.ServicePointManager.FindServicePoint(new Uri(SupabaseUrl));
                supabasePoint.MaxIdleTime = (int)KeepConnection.TotalMilliseconds;
            }
            catch { }
        }
        static string country, city, isp, asn;
        static readonly DateTime sessionStart = DateTime.UtcNow;
        // one random id per launch, so the events of a session can be grouped
        static readonly string session = Guid.NewGuid().ToString("N").Substring(0, 12);

        // Made once and kept: the same id for the whole session even if settings.txt can't be written.
        static string installId;
        public static string InstallId
        {
            get
            {
                var id = installId;
                if (id != null) return id;
                id = Storage.GetOrAddSetting("install_id", () => Guid.NewGuid().ToString());
                Guid parsed;
                if (!Guid.TryParse(id, out parsed))
                {
                    // a damaged value would be refused by the database: replaced, and the new one kept for next time
                    id = Guid.NewGuid().ToString();
                    Storage.SetSetting("install_id", id);
                }
                return installId = id;
            }
        }

        public static string Nickname
        {
            get { return Storage.GetSetting("nickname") ?? ""; }
            set { Storage.SetSetting("nickname", (value ?? "").Trim()); }
        }

        public static bool HasConsent { get { return Storage.GetSetting("consent") == "1" && Nickname.Length >= 2; } }

        // startupMs: time from launch until the window was ready
        public static void Start(List<Game> games, long startupMs)
        {
            if (!Configured || !HasConsent) return;
            KeepConnectionOpen();
            ThreadPool.QueueUserWorkItem(delegate
            {
                // offline, or the statistics server paused: never a reason to crash (an exception here would end the app)
                try
                {
                    var info = SystemInfo.Collect(games);
                    info["startup_ms"] = startupMs;
                    info["updated"] = Updater.JustUpdated;
                    launchInfo = info;
                    Post("start", null, -1, Environment.OSVersion.ToString(), null, info);
                }
                catch { }
            });
            if (heartbeat == null) heartbeat = new System.Threading.Timer(delegate { Send("heartbeat", null, -1, null); }, null, HeartbeatEvery, HeartbeatEvery);
        }

        // Sent right away and waited for (a few seconds at most): for reports made just before the app dies.
        public static void SendNow(string kind, string detail, Dictionary<string, object> data)
        {
            if (!Configured || !HasConsent) return;
            try { Post(kind, null, -1, detail, data, null, 4000); } catch { }
        }

        // Sent while the app closes, so it waits (briefly) instead of firing and forgetting.
        public static void Exit()
        {
            if (!Configured || !HasConsent) return;
            try
            {
                // no location lookup while closing: what is already known is enough
                Post("exit", null, -1, null,
                     new Dictionary<string, object> { { "minutes", Math.Round((DateTime.UtcNow - sessionStart).TotalMinutes, 1) } }, null, 3000,
                     null, false);
            }
            catch { }
        }

        // City and country from the user's internet address, looked up once a day (the address itself is not stored).
        // A failed lookup (offline, or the service's rate limit) waits 30 minutes before the next try, so events
        // never each wait for it; one lookup at a time.
        static DateTime geoRetryAt = DateTime.MinValue;
        static readonly object geoGate = new object();

        static void EnsureLocation(bool lookup)
        {
            string today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            if (country == null || Storage.GetSetting("geo_at") == today)
            {
                country = Storage.GetSetting("geo_country");
                city = Storage.GetSetting("geo_city");
                isp = Storage.GetSetting("geo_isp");
                asn = Storage.GetSetting("geo_as");
            }
            if (!lookup || Storage.GetSetting("geo_at") == today || DateTime.UtcNow < geoRetryAt) return;
            if (!Monitor.TryEnter(geoGate)) return; // another event is looking it up right now
            try
            {
                if (Storage.GetSetting("geo_at") == today) return;
                string c, ci, i, a;
                // HTTPS first, so nobody on the way can change the answer; the older plain-HTTP service only as a fallback
                bool ok = false;
                try { ok = ParseGeo(Http.Get("https://ipwho.is/?fields=success,country,city,connection", 6000), out c, out ci, out i, out a); }
                catch { c = ci = i = a = null; }
                if (!ok)
                    try { ok = ParseGeo(Http.Get("http://ip-api.com/json/?fields=status,country,city,isp,as", 6000), out c, out ci, out i, out a); }
                    catch { }
                if (!ok) { geoRetryAt = DateTime.UtcNow.AddMinutes(30); return; }
                country = c; city = ci; isp = i; asn = a;
                Storage.SetSettings(new Dictionary<string, string>
                {
                    { "geo_country", c }, { "geo_city", ci }, { "geo_isp", i }, { "geo_as", a }, { "geo_at", today }
                });
            }
            finally { Monitor.Exit(geoGate); }
        }

        // The answer of ipwho.is ({"success":true,"connection":{"asn":..,"isp":..}}) or ip-api.com ({"status":"success","as":..}).
        public static bool ParseGeo(string json, out string country, out string city, out string isp, out string asn)
        {
            country = city = isp = asn = null;
            Dictionary<string, object> d;
            try { d = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>; }
            catch { return false; }
            if (d == null) return false;
            object v;
            bool ok = (d.TryGetValue("success", out v) && true.Equals(v)) || (d.TryGetValue("status", out v) && "success".Equals(v));
            if (!ok) return false;
            Func<Dictionary<string, object>, string, string> s = (x, k) => { object o; return x != null && x.TryGetValue(k, out o) && o != null ? Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture) : null; };
            country = s(d, "country");
            city = s(d, "city");
            object conn;
            var cd = d.TryGetValue("connection", out conn) ? conn as Dictionary<string, object> : null;
            if (cd != null)
            {
                isp = s(cd, "isp") ?? s(cd, "org");
                string number = s(cd, "asn"), org = s(cd, "org");
                if (number != null) asn = "AS" + number + (org != null ? " " + org : "");
            }
            else
            {
                isp = s(d, "isp");
                asn = s(d, "as");
            }
            return country != null;
        }

        public static Dictionary<string, string> Headers(string key)
        {
            var h = new Dictionary<string, string> { { "apikey", key } };
            if (key.StartsWith("eyJ")) h["Authorization"] = "Bearer " + key; // legacy JWT keys
            return h;
        }

        // Fire and forget: never blocks the UI, never throws.
        public static void Send(string kind, string server, int ping, string detail)
        {
            Send(kind, server, ping, detail, null);
        }

        // data: structured details of the event (stored as JSON), e.g. every result of a ping test.
        public static void Send(string kind, string server, int ping, string detail, Dictionary<string, object> data)
        {
            if (!Configured || !HasConsent) return;
            string game = Game, srv = server ?? Server;
            int pg = ping >= 0 ? ping : Ping;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { Post(kind, srv, pg, detail, data, null, 8000, game); } catch { }
            });
        }

        // The launch details (SystemInfo), kept for this session: sent again with the next event after sharing is switched,
        // so the database's copy follows the switch right away instead of at the next launch.
        static volatile Dictionary<string, object> launchInfo;
        static int infoSentAs = -1; // what the database holds: 1 = the full launch details, 0 = the anonymous ones, -1 = none yet

        static void Post(string kind, string server, int ping, string detail, Dictionary<string, object> data, Dictionary<string, object> info,
                         int timeout = 8000, string game = null, bool lookup = true)
        {
            // read on every send, so switching it in Settings counts from the next one (no restart)
            bool share = Prefs.ShareStats;
            if (share) EnsureLocation(lookup); // off: no location lookup at all
            if (info == null && launchInfo != null && infoSentAs != (share ? 1 : 0)) info = launchInfo;
            var p = Payload(share, kind, game ?? Game, server ?? Server, ping, detail, data, info);
            string body = "{\"p\":" + new JavaScriptSerializer().Serialize(p) + "}";
            Http.Send("POST", SupabaseUrl.TrimEnd('/') + "/rest/v1/rpc/checkin", body, Headers(SupabaseKey), timeout);
            if (info != null) infoSentAs = share ? 1 : 0;
        }

        // The body of one checkin() call. share = false: the name, the PC name, the location and the internet provider get
        // fixed values (an empty text, which replaces what the database kept from before; a null would keep it) and the
        // launch details shrink to AnonymousInfo. The rest (install id, versions, the game, tests, locks, servers, match
        // reports, problems and buttons used) is the same either way.
        internal static Dictionary<string, object> Payload(bool share, string kind, string game, string server, int ping, string detail,
                                                           Dictionary<string, object> data, Dictionary<string, object> info)
        {
            // never the Windows user name or local folders (exception texts and tool output carry paths)
            detail = Privacy.Scrub(detail);
            if (data != null) data = (Dictionary<string, object>)Privacy.ScrubData(data);
            var p = new Dictionary<string, object>
            {
                { "install_id", InstallId }, { "kind", kind }, { "nickname", share ? Nickname : AnonymousName },
                { "device_name", share ? Environment.MachineName : "" }, { "app_version", Brand.DisplayVersion },
                { "os", Environment.OSVersion.Version.ToString() }, { "lang", Lang.Ar ? "ar" : "en" },
                { "mode", Prefs.Simple ? "simple" : "advanced" }, { "country", share ? country : "" }, { "city", share ? city : "" },
                { "isp", share ? isp : "" }, { "asn", share ? asn : "" }, { "session", session },
                { "game", game }, { "server", server }, { "ping", ping >= 0 ? (object)ping : null }, { "detail", detail }
            };
            if (data != null) p["data"] = data;
            if (info != null) p["info"] = share ? info : AnonymousInfo(info);
            return p;
        }

        // What stays of the launch details while sharing is off: the Windows version, how long the app took to open and
        // whether it was just updated, with the "anonymous" mark the developer panel counts.
        internal static readonly string[] AnonymousInfoKeys = { "windows", "startup_ms", "updated" };

        internal static Dictionary<string, object> AnonymousInfo(Dictionary<string, object> info)
        {
            var d = new Dictionary<string, object> { { "anonymous", true } };
            object v;
            if (info != null) foreach (var k in AnonymousInfoKeys) if (info.TryGetValue(k, out v)) d[k] = v;
            return d;
        }

        // An installs row (developer side) last sent while sharing was off.
        public static bool IsAnonymous(string nickname, Dictionary<string, object> info)
        {
            object v;
            return nickname == AnonymousName || (info != null && info.TryGetValue("anonymous", out v) && true.Equals(v));
        }

        // ---------------------------------------------------------------- developer side (reading)

        static string DevKeyFile { get { return Path.Combine(Storage.Dir, "dev.key"); } }

        // The developer's SECRET key lives only in this file on the developer's PC. It is never in the app or the repo.
        public static string DevKey
        {
            get { try { return File.Exists(DevKeyFile) ? File.ReadAllText(DevKeyFile).Trim() : null; } catch { return null; } }
            set { if (string.IsNullOrEmpty(value)) { try { File.Delete(DevKeyFile); } catch { } } else File.WriteAllText(DevKeyFile, value.Trim()); }
        }

        public static object[] Query(string pathAndQuery)
        {
            string json = Http.Send("GET", SupabaseUrl.TrimEnd('/') + "/rest/v1/" + pathAndQuery, null, Headers(DevKey), 15000);
            return (object[])new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json);
        }
    }
}
