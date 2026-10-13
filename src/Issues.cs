using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PingBoost
{
    // Silent problem detection. The app notices likely problems by itself and reports each one once per session
    // (beta statistics "issue" events + issues.txt in the data folder), without interrupting the player, so glitches
    // get collected and fixed even when testers don't notice or don't write in.
    static class Issues
    {
        // code -> what it means (shown in the developer view)
        public static readonly Dictionary<string, string> Codes = new Dictionary<string, string>
        {
            { "lock_outside", "Played on a server outside the lock" },
            { "unknown_server", "Game server not in any datacenter list" },
            { "lock_rules_missing", "Lock is on but its firewall rules are gone" },
            { "lock_apply_failed", "Couldn't add the firewall rules" },
            { "firewall_off", "Windows Firewall is off, locks can't work" },
            { "exe_not_found", "Couldn't find the game's exe for the lock" },
            { "queue_long", "Locked and no match found for a long time" },
            { "region_down", "Datacenters that don't answer" },
            { "all_down", "No server answered the ping test" },
            { "servers_load_failed", "Server list didn't load" },
            { "sdr_new_pop", "Valve lists a datacenter the Deadlock table doesn't know" },
            { "ip_lists_failed", "Official address list didn't download" },
            { "detect_failed", "Traffic capture (pktmon) failed" },
            { "leftover_lock", "Lock rules left from a previous session" },
            { "leftover_tunnel", "Relay tunnel service left from a previous session" },
            { "unlock_failed", "Firewall rules still there after unlocking" },
            { "connection_loss", "Heavy packet loss while playing" },
            { "slow_startup", "The app took long to open" },
            { "update_failed", "Self-update failed" },
            { "crash", "Unexpected error" },
        };

        public class Record
        {
            public DateTime At;
            public string Code, Detail;
        }

        static readonly HashSet<string> sent = new HashSet<string>();
        static readonly List<Record> recent = new List<Record>();
        public static event Action<Record> Reported;

        public static List<Record> Recent { get { lock (recent) return recent.ToList(); } }

        // key: what makes two reports of the same code different (e.g. the server address); null = once per session
        public static void Report(string code, string detail, Dictionary<string, object> data = null, string key = null)
        {
            lock (sent) if (!sent.Add(code + "|" + (key ?? ""))) return;
            var r = new Record { At = DateTime.Now, Code = code, Detail = detail ?? "" };
            lock (recent) { recent.Add(r); if (recent.Count > 200) recent.RemoveAt(0); }
            try
            {
                File.AppendAllText(Path.Combine(Storage.Dir, "issues.txt"),
                    r.At.ToString("yyyy-MM-dd HH:mm:ss") + "\t" + code + "\t" + r.Detail.Replace('\n', ' ') + Environment.NewLine);
            }
            catch { }
            var d = data != null ? new Dictionary<string, object>(data) : new Dictionary<string, object>();
            d["code"] = code;
            Telemetry.Send("issue", null, -1, code + ": " + r.Detail, d);
            var h = Reported;
            if (h != null) try { h(r); } catch { }
        }
    }
}
