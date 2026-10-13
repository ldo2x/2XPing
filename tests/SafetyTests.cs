using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PingBoost
{
    // Stability and safety: settings.txt under concurrency, atomic writes, firewall address validation,
    // privacy scrubbing of what is sent, the firewall worker's order, and the geo answer parsing.
    static partial class Tests
    {
        static string TempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "2xping-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        static void SettingsConcurrentWritersLoseNothing()
        {
            string dir = TempDir();
            try
            {
                string path = Path.Combine(dir, "settings.txt");
                File.WriteAllLines(path, new[] { "consent=1", "nickname=maz" });
                var s = new SettingsFile(path);
                // 16 threads, each writing its own keys while the others write and read
                Parallel.For(0, 16, new ParallelOptions { MaxDegreeOfParallelism = 16 }, t =>
                {
                    for (int i = 0; i < 20; i++)
                    {
                        s.Set("k" + t + "_" + i, "v" + i);
                        s.Get("consent");
                    }
                });
                var fresh = new SettingsFile(path);
                Check(fresh.Get("consent") == "1" && fresh.Get("nickname") == "maz", "older keys survive concurrent writes");
                int missing = 0;
                for (int t = 0; t < 16; t++)
                    for (int i = 0; i < 20; i++)
                        if (fresh.Get("k" + t + "_" + i) != "v" + i) missing++;
                Check(missing == 0, "every concurrently written key is on disk, missing " + missing);
                Check(!File.Exists(path + ".tmp"), "no temporary file left behind");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        static void SettingsUnreadableFileIsNeverOverwritten()
        {
            string dir = TempDir();
            try
            {
                string path = Path.Combine(dir, "settings.txt");
                File.WriteAllLines(path, new[] { "consent=1", "install_id=abc" });
                var s = new SettingsFile(path);
                bool saved;
                // another program holds the file: the change waits in memory, the file keeps every key
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    saved = s.Set("lang", "en");
                    Check(s.Get("lang") == "en", "the change holds in memory meanwhile");
                }
                Check(!saved, "Set reports it couldn't save");
                var lines = File.ReadAllLines(path);
                Check(lines.Contains("consent=1") && lines.Contains("install_id=abc"), "file untouched while it couldn't be read");
                // next time it can be read, both the old keys and the waiting change are there
                s.Set("mode", "simple");
                var fresh = new SettingsFile(path);
                Check(fresh.Get("consent") == "1" && fresh.Get("install_id") == "abc", "old keys kept");
                Check(fresh.Get("lang") == "en" && fresh.Get("mode") == "simple", "waiting change written later: " + fresh.Get("lang"));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        static void SettingsGetOrAddMakesOneValue()
        {
            string dir = TempDir();
            try
            {
                string path = Path.Combine(dir, "settings.txt");
                var s = new SettingsFile(path);
                var ids = new System.Collections.Concurrent.ConcurrentBag<string>();
                int made = 0;
                Parallel.For(0, 32, i => ids.Add(s.GetOrAdd("install_id", () => { Interlocked.Increment(ref made); return Guid.NewGuid().ToString(); })));
                Check(made == 1, "one id made, not " + made);
                Check(ids.Distinct().Count() == 1, "every caller got the same id");
                Check(new SettingsFile(path).Get("install_id") == ids.First(), "the id is saved");
                // values are kept on one line, whatever the caller passed
                s.Set("nickname", "a\tb\nc");
                Check(new SettingsFile(path).Get("nickname") == "a b c", "tabs and newlines flattened");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        static void AtomicFileReplacesWhole()
        {
            string dir = TempDir();
            try
            {
                string path = Path.Combine(dir, "picks.txt");
                AtomicFile.WriteAllLines(path, new[] { "a", "b" });
                AtomicFile.WriteAllLines(path, new[] { "c" });
                var lines = File.ReadAllLines(path);
                Check(lines.Length == 1 && lines[0] == "c", "second write replaces the first: " + string.Join("|", lines));
                Check(!File.Exists(path + ".tmp"), "no temporary file left behind");
                Check(Directory.GetFiles(dir).Length == 1, "no backup files left behind");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        static void RemoteAddressValidation()
        {
            foreach (var ok in new[] { "1.2.3.4", "0.0.0.0/0", "10.0.0.0/8", "255.255.255.255", "1.2.3.0/32", "1.2.3.4-1.2.3.9", "1.2.3.4-1.2.3.4" })
                Check(RemoteAddress.Valid(ok), "valid: " + ok);
            foreach (var bad in new[] { null, "", "1.2.3", "1.2.3.256", "300.1.1.1", "1.2.3.0/33", "1.2.3.0/99", "1.2.3.9-1.2.3.4",
                                        "1.2.3.4-1.2.3.256", "1.2.3.4\n", "١.٢.٣.٤", "1.2.3.٤", "1.2.3.0/٢٤", "1.2.3.4/24\n", " 1.2.3.4", "1.2.3.4,5.6.7.8", "a.b.c.d", "1.2.3.4 any" })
                Check(!RemoteAddress.Valid(bad), "invalid: " + (bad == null ? "null" : bad.Replace("\n", "\\n")));

            int skipped;
            var list = ServerLock.ValidAddresses(new[] { "1.2.3.0/24", "1.2.3.0/24", "999.1.1.1", "5.6.7.8\n", " 9.9.9.0/24 ", "1.1.1.1/40" }, out skipped);
            Check(list.Count == 3 && list[0] == "1.2.3.0/24" && list[1] == "5.6.7.8" && list[2] == "9.9.9.0/24",
                  "bad entries skipped, good ones kept once: " + string.Join(",", list.ToArray()));
            Check(skipped == 2, "two skipped, not " + skipped);
        }

        static void PrivacyScrubsUserAndPaths()
        {
            string profile = @"C:\Users\Abdulrahman", temp = @"C:\Users\Abdulrahman\AppData\Local\Temp\";
            string s = Privacy.Scrub(@"Could not find file 'C:\Users\Abdulrahman\AppData\Roaming\2XPing\relays.txt'", profile, temp, "Abdulrahman");
            Check(s == @"Could not find file '%USERPROFILE%\AppData\Roaming\2XPing\relays.txt'", "profile path: " + s);
            s = Privacy.Scrub(@"Access to the path 'c:\users\abdulrahman\appdata\local\temp\2XPing-update-1\update.zip' is denied.", profile, temp, "Abdulrahman");
            Check(s == @"Access to the path '%TEMP%\2XPing-update-1\update.zip' is denied.", "temp path, any case: " + s);
            s = Privacy.Scrub(@"D:\Users\SomeoneElse\Desktop\x.conf and C:\Documents and Settings\Bob\a", profile, temp, "Abdulrahman");
            Check(!s.Contains("SomeoneElse") && !s.Contains("Bob") && s.Contains(@"D:\Users\<user>\Desktop"), "other profiles: " + s);
            s = Privacy.Scrub("Logon failure for abdulrahman on PC", profile, temp, "Abdulrahman");
            Check(s == "Logon failure for <user> on PC", "user name alone: " + s);
            s = Privacy.Scrub("Bobby and Bob's", null, null, "Bob");
            Check(s == "Bobby and <user>'s", "user name only as a whole word: " + s);
            Check(Privacy.Scrub("a timeout", null, null, "ti") == "a timeout", "very short user names are left alone");
            Check(Privacy.Scrub(null, profile, temp, "x") == null, "null stays null");
        }

        static void PrivacyScrubsEventData()
        {
            string user = Environment.UserName;
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var data = new Dictionary<string, object>
            {
                { "stack", "at " + profile + @"\x.cs" },
                { "list", new[] { profile + @"\a", "plain" } },
                { "nested", new Dictionary<string, object> { { "path", profile + @"\b" }, { "n", 5 } } },
                { "counts", new Dictionary<string, int> { { "eu", 3 } } },
                { "ok", true }
            };
            var clean = (Dictionary<string, object>)Privacy.ScrubData(data);
            string json = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(clean);
            Check(json.IndexOf(profile.Replace(@"\", @"\\"), StringComparison.OrdinalIgnoreCase) < 0, "no profile path anywhere: " + json);
            if (user.Length >= 3) Check(json.IndexOf(user, StringComparison.OrdinalIgnoreCase) < 0, "no user name anywhere: " + json);
            Check(json.Contains("\"n\":5") && json.Contains("\"ok\":true") && json.Contains("\"eu\":3") && json.Contains("plain"),
                  "other values unchanged: " + json);
        }

        static void ShortStackHasNoPathsAndFewFrames()
        {
            Exception caught = null;
            try { Deep(20); }
            catch (Exception ex) { caught = ex; }
            string s = Privacy.ShortStack(caught, 5);
            var lines = s.Split('\n');
            Check(lines[0].StartsWith("System.IO.FileNotFoundException: "), "type and message first: " + lines[0]);
            Check(lines.Length <= 6, "type line + at most 5 frames, got " + lines.Length);
            Check(!s.Contains(":line ") && !s.Contains(".cs"), "no source file paths: " + s);
            Check(s.IndexOf(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase) < 0,
                  "message scrubbed: " + lines[0]);
            Check(Privacy.ShortStack(null) == null, "null exception");
            var wrapped = Privacy.ShortStack(new InvalidOperationException("outer", new TimeoutException("inner")));
            Check(wrapped.Contains("---> System.TimeoutException: inner"), "inner exceptions named: " + wrapped);
        }

        static void Deep(int n)
        {
            if (n == 0)
                throw new FileNotFoundException("Could not find file '" +
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "secret.txt") + "'");
            Deep(n - 1);
        }

        static void FirewallWorkerKeepsOrder()
        {
            var order = new List<int>();
            for (int i = 0; i < 20; i++)
            {
                int k = i;
                FirewallWorker.Run(() =>
                {
                    if (k % 5 == 0) Thread.Sleep(5);
                    lock (order) order.Add(k);
                });
            }
            Check(FirewallWorker.Drain(5000), "drained in time");
            Check(order.SequenceEqual(Enumerable.Range(0, 20)), "ran in the order asked: " + string.Join(",", order));
            // a failing step never blocks the ones after it
            bool after = false;
            Exception seen = null;
            var report = FirewallWorker.OnError;
            FirewallWorker.OnError = ex => seen = ex; // no real crash report from a test
            try
            {
                FirewallWorker.Run(() => { throw new InvalidOperationException("boom"); });
                FirewallWorker.Run(() => { after = true; });
                Check(FirewallWorker.Drain(5000) && after, "the queue goes on after an error");
                Check(seen != null && seen.Message == "boom", "the error is reported");
            }
            finally { FirewallWorker.OnError = report; }
        }

        static void ProcResolvesSystemTools()
        {
            string sys = Environment.SystemDirectory;
            foreach (var t in new[] { "netsh", "pktmon", "netstat", "ipconfig", "NETSH.exe" })
            {
                string p = Proc.Resolve(t);
                Check(Path.IsPathRooted(p) && p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                      (p.StartsWith(sys, StringComparison.OrdinalIgnoreCase) || p.IndexOf("Sysnative", StringComparison.OrdinalIgnoreCase) >= 0),
                      t + " by full System32 path: " + p);
            }
            Check(Proc.Resolve(@"C:\Program Files\WireGuard\wireguard.exe") == @"C:\Program Files\WireGuard\wireguard.exe", "full paths kept");
            string output;
            Check(Proc.Run(Path.Combine(Path.GetTempPath(), "no-such-tool-" + Guid.NewGuid().ToString("N") + ".exe"), "", 1000, out output) == -1,
                  "a tool that can't start gives -1, not an exception");
        }

        static void ProcKillsAHungTool()
        {
            // cmd waits on a pipe that never closes for ~10 s: the 500 ms timeout must kill it
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string output;
            int code = Proc.Run(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c ping -n 11 127.0.0.1 >nul", 500, out output);
            Check(code == -1, "timed out = -1, got " + code);
            Check(sw.ElapsedMilliseconds < 6000, "returned soon after the timeout: " + sw.ElapsedMilliseconds + " ms");
            code = Proc.Run(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c echo hello& exit 3", 10000, out output);
            Check(code == 3 && output == "hello", "exit code and output: " + code + " " + output);
        }

        static void GeoAnswersOfBothServices()
        {
            string c, ci, isp, asn;
            Check(Telemetry.ParseGeo("{\"success\":true,\"country\":\"Saudi Arabia\",\"city\":\"Riyadh\",\"connection\":{\"asn\":25019,\"org\":\"Saudi Telecom\",\"isp\":\"STC\"}}",
                                     out c, out ci, out isp, out asn), "ipwho.is answer");
            Check(c == "Saudi Arabia" && ci == "Riyadh" && isp == "STC" && asn == "AS25019 Saudi Telecom", "ipwho.is fields: " + c + "|" + ci + "|" + isp + "|" + asn);
            Check(Telemetry.ParseGeo("{\"status\":\"success\",\"country\":\"Germany\",\"city\":\"Berlin\",\"isp\":\"DT\",\"as\":\"AS3320 Deutsche Telekom AG\"}",
                                     out c, out ci, out isp, out asn), "ip-api answer");
            Check(c == "Germany" && ci == "Berlin" && isp == "DT" && asn == "AS3320 Deutsche Telekom AG", "ip-api fields");
            Check(!Telemetry.ParseGeo("{\"success\":false,\"message\":\"rate limit\"}", out c, out ci, out isp, out asn), "failure answer");
            Check(!Telemetry.ParseGeo("<html>captive portal</html>", out c, out ci, out isp, out asn), "not JSON");
        }

        static void RunSafetyTests()
        {
            Run("SettingsConcurrentWritersLoseNothing", SettingsConcurrentWritersLoseNothing);
            Run("SettingsUnreadableFileIsNeverOverwritten", SettingsUnreadableFileIsNeverOverwritten);
            Run("SettingsGetOrAddMakesOneValue", SettingsGetOrAddMakesOneValue);
            Run("AtomicFileReplacesWhole", AtomicFileReplacesWhole);
            Run("RemoteAddressValidation", RemoteAddressValidation);
            Run("PrivacyScrubsUserAndPaths", PrivacyScrubsUserAndPaths);
            Run("PrivacyScrubsEventData", PrivacyScrubsEventData);
            Run("ShortStackHasNoPathsAndFewFrames", ShortStackHasNoPathsAndFewFrames);
            Run("FirewallWorkerKeepsOrder", FirewallWorkerKeepsOrder);
            Run("ProcResolvesSystemTools", ProcResolvesSystemTools);
            Run("ProcKillsAHungTool", ProcKillsAHungTool);
            Run("GeoAnswersOfBothServices", GeoAnswersOfBothServices);
        }
    }
}
