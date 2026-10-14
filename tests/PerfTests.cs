using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace PingBoost
{
    // Performance and network (performance audit items 1-7, ruling R2): the parsed IP-list cache, the ip-api cache,
    // the capture's port filters and socket table, the test's parallelism, the update check's saved answer, the heartbeat.
    static partial class Tests
    {
        const string AwsSample = "{\"prefixes\":[" +
            "{\"ip_prefix\":\"3.5.0.0/19\",\"region\":\"eu-west-1\",\"service\":\"EC2\"}," +
            "{\"ip_prefix\":\"3.6.0.0/19\",\"region\":\"eu-west-1\",\"service\":\"EC2\"}," +
            "{\"ip_prefix\":\"3.7.0.0/19\",\"region\":\"eu-west-1\",\"service\":\"S3\"}," +
            "{\"ip_prefix\":\"15.1.0.0/16\",\"region\":\"me-south-1\",\"service\":\"EC2\"}]}";

        // Storage.Dir is read-only for the app; tests move it the way the measurement harnesses do.
        static void SetDataDir(string dir)
        {
            typeof(Storage).GetField("Dir", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static).SetValue(null, dir);
        }

        // Runs the test with Storage.Dir in a new empty folder, and puts everything back after.
        static void WithDataDir(Action<string> test)
        {
            string old = Storage.Dir, dir = TempDir();
            SetDataDir(dir);
            IpLists.ClearParsed();
            try { test(dir); }
            finally
            {
                SetDataDir(old);
                IpLists.ClearParsed();
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        static void IpListParsedOnceForEveryRegion()
        {
            WithDataDir(dir =>
            {
                Directory.CreateDirectory(Path.Combine(dir, "cache"));
                File.WriteAllText(Path.Combine(dir, "cache", "aws-ip-ranges.json"), AwsSample); // fresh: no download
                int before = IpLists.ParseCount;
                for (int i = 0; i < 15; i++)
                {
                    var eu = IpLists.Resolve("aws:eu-west-1");
                    Check(eu.Count == 2 && eu.Contains("3.5.0.0/19") && eu.Contains("3.6.0.0/19"), "EC2 of eu-west-1 only: " + string.Join(",", eu.ToArray()));
                    eu.Add("9.9.9.9/32"); // the caller's list is its own
                }
                Check(IpLists.Resolve("aws:eu-west-1").Count == 2, "a caller's change never reaches the cache");
                Check(IpLists.Resolve("aws:me-south-1").Single() == "15.1.0.0/16", "another region from the same parse");
                Check(IpLists.Resolve("aws:*").Count == 3, "* = every EC2 prefix");
                Check(IpLists.Resolve("aws:xx-none-1").Count == 0, "an unknown region is empty");
                Check(IpLists.ParseCount - before == 1, "parsed once for 19 lookups: " + (IpLists.ParseCount - before));
                Check(IpLists.Resolve("10.0.0.0/8").Single() == "10.0.0.0/8", "a hand-written range is itself");
            });
        }

        static void IpListParsersOfGoogleAndRipe()
        {
            var g = IpLists.ParseGcp("{\"prefixes\":[{\"ipv4Prefix\":\"34.1.0.0/20\",\"scope\":\"me-central2\"}," +
                                     "{\"ipv6Prefix\":\"2600::/40\",\"scope\":\"me-central2\"},{\"ipv4Prefix\":\"35.2.0.0/16\",\"scope\":\"us-east1\"}]}");
            Check(g["me-central2"].Single() == "34.1.0.0/20" && g["*"].Count == 2, "Google: IPv4 by scope, * holds all");
            var a = IpLists.ParseAsn("{\"data\":{\"prefixes\":[{\"prefix\":\"24.105.0.0/18\"},{\"prefix\":\"2620:1:1::/48\"}]}}");
            Check(a["*"].Single() == "24.105.0.0/18", "RIPE: IPv4 only");
        }

        static void LocateAsksOncePerAddress()
        {
            int calls = 0;
            Func<string, string> ok = x => { calls++; return "Germany - Frankfurt"; };
            var t = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
            Stats.ClearLocated(); // nothing remembered from before
            string ip = "203.0.113.7";
            Check(Stats.Locate(ip, t, ok) == "Germany - Frankfurt", "first answer");
            Check(Stats.Locate(ip, t.AddMinutes(30), ok) == "Germany - Frankfurt" && calls == 1, "remembered for the hour: " + calls);
            Stats.Locate(ip, t.AddMinutes(61), ok);
            Check(calls == 2, "asked again after an hour: " + calls);

            int fails = 0;
            Func<string, string> down = x => { fails++; return null; };
            string other = "198.51.100.7";
            Check(Stats.Locate(other, t, down) == null, "failed lookup is null");
            Stats.Locate(other, t.AddMinutes(5), down);
            Check(fails == 1, "a failure isn't retried every check: " + fails);
            Stats.Locate(other, t.AddMinutes(11), down);
            Check(fails == 2, "retried after 10 minutes: " + fails);
        }

        static void CaptureFiltersFollowTheGamePorts()
        {
            var none = Detector.CaptureFilters(new int[0]);
            Check(none.Count == 1 && none[0] == "2XPingUDP -t UDP", "no port known: every UDP packet");
            var two = Detector.CaptureFilters(new[] { 27015, 4380, 27015 });
            Check(two.Count == 2 && two[0] == "2XPingUDP4380 -t UDP -p 4380" && two[1] == "2XPingUDP27015 -t UDP -p 27015",
                  "one filter per port, no duplicates: " + string.Join(" | ", two.ToArray()));
            var many = Detector.CaptureFilters(Enumerable.Range(50000, Detector.MaxPortFilters + 1));
            Check(many.Count == 1 && many[0] == "2XPingUDP -t UDP", "too many ports: the plain filter");
            Check(Detector.CaptureFilters(Enumerable.Range(50000, Detector.MaxPortFilters)).Count == Detector.MaxPortFilters, "the cap itself is filtered");
        }

        static void SocketTableAndProcessListAreRight()
        {
            using (var u = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            {
                int port = ((IPEndPoint)u.Client.LocalEndPoint).Port, me = Process.GetCurrentProcess().Id;
                var table = Detector.UdpSockets();
                Check(table != null, "Windows gave the UDP table");
                Check(table != null && table.Any(s => s.Key == me && s.Value == port), "our socket with our pid and port " + port);
            }
            var procs = Detector.Processes();
            string mine = Process.GetCurrentProcess().ProcessName;
            Check(procs.ContainsKey(mine) && procs.ContainsKey(mine.ToUpperInvariant()), "own process found, any case");
            Check(Detector.IsRunning(procs, new[] { "no-such-game-2xping", mine }) && !Detector.IsRunning(procs, new[] { "no-such-game-2xping" }),
                  "IsRunning on one snapshot");
            Check(!Detector.IsRunning(new string[0]), "no names: not running");
        }

        static void TestParallelismIsCapped()
        {
            Check(PingTestService.Parallel == 16, "16 regions at a time: " + PingTestService.Parallel);
        }

        static void UpdateCheckKeepsTheAnswerWithItsETag()
        {
            string dir = TempDir();
            try
            {
                string file = Path.Combine(dir, "cache", "releases.txt"), etag;
                Check(Updater.ReadSaved(file, out etag) == null, "nothing saved yet");
                Updater.Save(file, "W/\"abc\"", "[{\"tag_name\":\"v1.0.0\"}]");
                Check(Updater.ReadSaved(file, out etag) == "[{\"tag_name\":\"v1.0.0\"}]" && etag == "W/\"abc\"", "round trip: " + etag);
                Updater.Save(file, null, "[]");
                Check(Updater.ReadSaved(file, out etag) == null, "no ETag: nothing kept to ask with");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }

            string json = "[{\"tag_name\":\"v99.1.0\",\"draft\":true,\"assets\":[]}," +
                          "{\"tag_name\":\"v99.0.2\",\"body\":\"fixes #optional\",\"assets\":[{\"name\":\"2XPing.zip\",\"browser_download_url\":\"https://x/z.zip\"}," +
                          "{\"name\":\"2XPing.zip.sha256\",\"browser_download_url\":\"https://x/z.zip.sha256\"}]}," +
                          "{\"tag_name\":\"v99.0.1\",\"assets\":[]},{\"tag_name\":\"v0.0.1\",\"assets\":[]}]";
            var r = Updater.Newest(json);
            Check(r != null && r.Tag == "v99.0.2" && r.Optional && r.ZipUrl == "https://x/z.zip" && r.ShaUrl == "https://x/z.zip.sha256",
                  "newest non-draft with its files: " + (r == null ? "null" : r.Tag));
            Check(Updater.Newest("[{\"tag_name\":\"v0.0.1\",\"assets\":[]}]") == null, "nothing newer: null");
        }

        static void HeartbeatEveryTwoMinutes()
        {
            Check(Telemetry.HeartbeatEvery == TimeSpan.FromSeconds(120), "R2: 120 s");
            Check(Telemetry.OnlineWindow > Telemetry.HeartbeatEvery + Telemetry.HeartbeatEvery, "online window covers two beats");
        }

        static void HeartbeatKeepsItsConnection()
        {
            Check(Telemetry.KeepConnection > Telemetry.HeartbeatEvery, "the connection outlives the gap between beats: " + Telemetry.KeepConnection);
            Telemetry.KeepConnectionOpen();
            var sp = System.Net.ServicePointManager.FindServicePoint(new Uri(Telemetry.SupabaseUrl));
            Check(sp.MaxIdleTime == (int)Telemetry.KeepConnection.TotalMilliseconds, "Supabase's service point keeps it: " + sp.MaxIdleTime);
        }

        // The game was open, so no test ran on its own: the card says the pings are old and how to refresh them,
        // in both layouts' cards (tiles: the note; "best server": the line under it), never while a test runs
        static void CardSaysOldPingsWhileTheGameIsOpen()
        {
            Lang.Ar = true;
            var i = Lockable(Choice("eu", "Europe", "أوروبا", 60, true));
            i.OldPings = true;
            var v = CardLogic.Decide(i);
            Check(v.Kind == CardKind.Choose && v.Note == "البنق من آخر مرة. Deadlock مفتوحة، اضغط «افحص البنق» عشان تحدّثه.", "Arabic note: " + v.Note);
            Check(v.ShowTest, "the test link is there to press");
            i.RussiaNote = true;
            v = CardLogic.Decide(i);
            Check(v.Note.StartsWith("البنق من آخر مرة.") && v.Note.Contains("\n") && v.Note.Contains("روسيا"), "Russia's note stays under it: " + v.Note);
            Lang.Ar = false;
            i.RussiaNote = false;
            Check(CardLogic.Decide(i).Note == "Pings from last time. Deadlock is open; press Test ping to refresh them.", "English note");
            i.Testing = true;
            Check(CardLogic.Decide(i).Note == null, "no old-pings note while the test runs");
            i.Testing = false;
            i.OldPings = false;
            Check(CardLogic.Decide(i).Note == null, "fresh pings: no note");

            var best = new CardInput { Game = "Overwatch 2", Loaded = true, BestServer = "Saudi Arabia", BestMs = 30, OldPings = true };
            v = CardLogic.Decide(best);
            Check(v.Kind == CardKind.BestOnly && v.Sub.EndsWith("Overwatch 2 is open; press Test ping to refresh them."), "best-server card: " + v.Sub);
        }

        static void RunPerfTests()
        {
            Run("HeartbeatKeepsItsConnection", HeartbeatKeepsItsConnection);
            Run("CardSaysOldPingsWhileTheGameIsOpen", CardSaysOldPingsWhileTheGameIsOpen);
            Run("IpListParsedOnceForEveryRegion", IpListParsedOnceForEveryRegion);
            Run("IpListParsersOfGoogleAndRipe", IpListParsersOfGoogleAndRipe);
            Run("LocateAsksOncePerAddress", LocateAsksOncePerAddress);
            Run("CaptureFiltersFollowTheGamePorts", CaptureFiltersFollowTheGamePorts);
            Run("SocketTableAndProcessListAreRight", SocketTableAndProcessListAreRight);
            Run("TestParallelismIsCapped", TestParallelismIsCapped);
            Run("UpdateCheckKeepsTheAnswerWithItsETag", UpdateCheckKeepsTheAnswerWithItsETag);
            Run("HeartbeatEveryTwoMinutes", HeartbeatEveryTwoMinutes);
        }
    }
}
