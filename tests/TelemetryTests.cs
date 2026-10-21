using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;

namespace PingBoost
{
    // The beta statistics switch (Settings: «شارك إحصائيات البيتا»): what Telemetry.Payload puts in a checkin() call with
    // it on and off. Only the payload builder runs: nothing is sent, and settings.txt lives in a temporary folder.
    static partial class Tests
    {
        const BindingFlags TelemetryStatics = BindingFlags.NonPublic | BindingFlags.Static;

        // A temporary data folder with a name, consent and a remembered location, as on a tester's PC.
        static void WithTelemetryData(Action test)
        {
            WithDataDir(dir =>
            {
                var t = typeof(Telemetry);
                var fields = new[] { "country", "city", "isp", "asn", "installId" };
                var saved = fields.Select(f => t.GetField(f, TelemetryStatics).GetValue(null)).ToArray();
                try
                {
                    t.GetField("installId", TelemetryStatics).SetValue(null, null); // made again inside the temporary folder
                    t.GetField("country", TelemetryStatics).SetValue(null, "Saudi Arabia");
                    t.GetField("city", TelemetryStatics).SetValue(null, "Jeddah");
                    t.GetField("isp", TelemetryStatics).SetValue(null, "Saudi Telecom");
                    t.GetField("asn", TelemetryStatics).SetValue(null, "AS25019 Saudi Telecom");
                    Storage.SetSettings(new Dictionary<string, string> { { "nickname", "Tester Abu Fahad" }, { "consent", "1" } });
                    test();
                    Check(File.Exists(Path.Combine(dir, "settings.txt")), "settings.txt written in the temporary folder");
                }
                finally
                {
                    for (int i = 0; i < fields.Length; i++) t.GetField(fields[i], TelemetryStatics).SetValue(null, saved[i]);
                }
            });
        }

        static Dictionary<string, object> SampleLaunchInfo()
        {
            return new Dictionary<string, object>
            {
                { "cpu", "AMD Ryzen 7 7800X3D 8-Core Processor" }, { "cores", 16 }, { "ram_gb", 32 }, { "gpu", "NVIDIA GeForce RTX 4070" },
                { "screen", "2560x1440" }, { "screens", 2 }, { "scale", 100 }, { "windows", "Windows 11 Pro 24H2 (26100.1)" },
                { "dotnet", "533325" }, { "locale", "ar-SA" }, { "ui_lang", "ar-SA" }, { "timezone", "Arab Standard Time" },
                { "laptop", false }, { "net", "ethernet" }, { "net_mbps", 1000L }, { "games_installed", new List<string> { "Deadlock" } },
                { "wireguard", true }, { "autotest", true }, { "hidden_games", "dbd" }, { "startup_ms", 850L }, { "updated", false }
            };
        }

        static Dictionary<string, object> SampleTestData()
        {
            return new Dictionary<string, object> { { "regions", new[] { "Europe 61 ms", "SE Asia 95 ms" } }, { "ms", 4200 } };
        }

        static string Json(Dictionary<string, object> p) { return new JavaScriptSerializer().Serialize(p); }

        static readonly string[] PayloadKeys =
        {
            "install_id", "kind", "nickname", "device_name", "app_version", "os", "lang", "mode", "country", "city", "isp", "asn",
            "session", "game", "server", "ping", "detail"
        };

        static void ShareStatsIsOnByDefaultAndSwitchesAtOnce()
        {
            WithTelemetryData(() =>
            {
                Check(Prefs.ShareStats, "on by default");
                Check(Telemetry.HasConsent, "the consent gate is its own setting");
                Storage.SetSetting("share_stats", "0");
                Check(!Prefs.ShareStats, "off right after it is saved (no restart)");
                Check(Telemetry.HasConsent, "switching it off keeps the consent");
                Check(Telemetry.Payload(Prefs.ShareStats, "heartbeat", null, null, -1, null, null, null)["nickname"] as string == Telemetry.AnonymousName,
                      "the next payload is anonymous");
                Storage.SetSetting("share_stats", "1");
                Check(Prefs.ShareStats, "back on");
            });
        }

        static void PayloadWithSharingOnIsUnchanged()
        {
            WithTelemetryData(() =>
            {
                var info = SampleLaunchInfo();
                var data = SampleTestData();
                var p = Telemetry.Payload(true, "start", "Deadlock", "Europe", 61, "Microsoft Windows NT 10.0", data, info);
                Check(p.Keys.OrderBy(k => k).SequenceEqual(PayloadKeys.Concat(new[] { "data", "info" }).OrderBy(k => k)),
                      "the same fields as before: " + string.Join(",", p.Keys));
                Check((string)p["nickname"] == "Tester Abu Fahad", "name");
                Check((string)p["device_name"] == Environment.MachineName, "PC name");
                Check((string)p["country"] == "Saudi Arabia" && (string)p["city"] == "Jeddah", "location");
                Check((string)p["isp"] == "Saudi Telecom" && (string)p["asn"] == "AS25019 Saudi Telecom", "provider");
                var sentInfo = (Dictionary<string, object>)p["info"];
                Check(sentInfo.Count == info.Count && info.All(kv => sentInfo.ContainsKey(kv.Key) && Equals(sentInfo[kv.Key], kv.Value)),
                      "every launch detail");
                Check(!sentInfo.ContainsKey("anonymous") && !Json(p).Contains(Telemetry.AnonymousName), "no anonymous mark");
                Check((string)p["game"] == "Deadlock" && (string)p["server"] == "Europe" && Equals(p["ping"], 61), "game, server and ping");
                Check(((Dictionary<string, object>)p["data"]).ContainsKey("regions"), "the test's results");
            });
        }

        static void PayloadWithSharingOffHasNothingPersonal()
        {
            WithTelemetryData(() =>
            {
                var info = SampleLaunchInfo();
                var p = Telemetry.Payload(false, "start", "Deadlock", "Europe", 61, "Microsoft Windows NT 10.0", SampleTestData(), info);
                Check(p.Keys.OrderBy(k => k).SequenceEqual(PayloadKeys.Concat(new[] { "data", "info" }).OrderBy(k => k)),
                      "the same fields, so the database takes it as before: " + string.Join(",", p.Keys));
                Check((string)p["nickname"] == Telemetry.AnonymousName, "the name is the anonymous mark");
                foreach (var k in new[] { "device_name", "country", "city", "isp", "asn" })
                    Check((string)p[k] == "", k + " is empty (it replaces what the database kept)");
                var sentInfo = (Dictionary<string, object>)p["info"];
                Check(true.Equals(sentInfo["anonymous"]), "the launch details carry the anonymous mark");
                Check(sentInfo.Keys.All(k => k == "anonymous" || Telemetry.AnonymousInfoKeys.Contains(k)),
                      "only the Windows version, start time and update flag: " + string.Join(",", sentInfo.Keys));
                Check((string)sentInfo["windows"] == "Windows 11 Pro 24H2 (26100.1)" && Equals(sentInfo["startup_ms"], 850L), "kept details");
                Check(info.ContainsKey("cpu"), "the caller's launch details are left as they were");

                string json = Json(p);
                var personal = new List<string> { "Tester Abu Fahad", "Jeddah", "Saudi Arabia", "Saudi Telecom", "AS25019", "Ryzen", "RTX 4070",
                                                  "2560x1440", "ar-SA", "Arab Standard Time", "ethernet", "games_installed", "wireguard",
                                                  "\"cpu\"", "\"gpu\"", "\"ram_gb\"", "\"timezone\"", "\"locale\"" };
                if (Environment.MachineName.Length >= 3) personal.Add(Environment.MachineName);
                foreach (var s in personal) Check(json.IndexOf(s, StringComparison.OrdinalIgnoreCase) < 0, "not sent: " + s);

                // what stays the same
                Check((string)p["install_id"] == Telemetry.InstallId && (string)p["app_version"] == Brand.DisplayVersion, "install id and version");
                Check((string)p["os"] == Environment.OSVersion.Version.ToString(), "Windows version");
                Check((string)p["game"] == "Deadlock" && (string)p["server"] == "Europe" && Equals(p["ping"], 61), "game, server and ping");
                Check(((Dictionary<string, object>)p["data"]).ContainsKey("regions"), "the test's results");
            });
        }

        static void PayloadWithSharingOffKeepsEvents()
        {
            WithTelemetryData(() =>
            {
                // a match report, a problem and a button: the same details either way, and no launch details on them
                var match = new Dictionary<string, object> { { "verdict", "inside" }, { "dc", "fra" }, { "picked", new[] { "Europe" } } };
                var on = Telemetry.Payload(true, "match", "Deadlock", "Europe", 70, "inside, fra", match, null);
                var off = Telemetry.Payload(false, "match", "Deadlock", "Europe", 70, "inside, fra", match, null);
                Check(Json((Dictionary<string, object>)on["data"]) == Json((Dictionary<string, object>)off["data"]), "match report unchanged");
                Check(!off.ContainsKey("info") && (string)off["detail"] == "inside, fra" && (string)off["kind"] == "match", "event itself unchanged");
                var issue = Telemetry.Payload(false, "issue", null, null, -1, "crash: NullReferenceException",
                                              new Dictionary<string, object> { { "code", "crash" } }, null);
                Check((string)issue["detail"] == "crash: NullReferenceException" && issue["ping"] == null, "problem report kept");
                var use = Telemetry.Payload(false, "use", null, null, -1, "settings", null, null);
                Check((string)use["detail"] == "settings" && !use.ContainsKey("data"), "button use kept");
                // a heartbeat right after switching gets the shrunk details, so the database's copy follows at once
                var beat = Telemetry.Payload(false, "heartbeat", null, null, -1, null, null, SampleLaunchInfo());
                Check(((Dictionary<string, object>)beat["info"]).Count <= Telemetry.AnonymousInfoKeys.Length + 1, "heartbeat details shrunk");
            });
        }

        static void AnonymousInstallsAreMarkedForTheDeveloper()
        {
            Check(Telemetry.IsAnonymous(Telemetry.AnonymousName, null), "by name");
            Check(Telemetry.IsAnonymous("", new Dictionary<string, object> { { "anonymous", true } }), "by the launch details' mark");
            Check(!Telemetry.IsAnonymous("Fahad", new Dictionary<string, object> { { "cpu", "x" } }), "a named install");
            Check(Telemetry.AnonymousInfo(null).Count == 1 && true.Equals(Telemetry.AnonymousInfo(null)["anonymous"]), "the mark alone");
            var s = DashStats.Parse("{\"counters\":{\"installs_total\":40,\"installs_anonymous\":3}}");
            Check(s.Counter("installs_anonymous") == 3, "the dashboard reads the anonymous count");
            Check(!DashStats.Parse("{\"counters\":{\"installs_total\":40}}").Counters.ContainsKey("installs_anonymous"),
                  "an older dev_stats() has none (the tile shows -)");
        }

        static void RunTelemetryTests()
        {
            Run("ShareStatsIsOnByDefaultAndSwitchesAtOnce", ShareStatsIsOnByDefaultAndSwitchesAtOnce);
            Run("PayloadWithSharingOnIsUnchanged", PayloadWithSharingOnIsUnchanged);
            Run("PayloadWithSharingOffHasNothingPersonal", PayloadWithSharingOffHasNothingPersonal);
            Run("PayloadWithSharingOffKeepsEvents", PayloadWithSharingOffKeepsEvents);
            Run("AnonymousInstallsAreMarkedForTheDeveloper", AnonymousInstallsAreMarkedForTheDeveloper);
        }
    }
}
