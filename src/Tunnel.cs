using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace PingBoost
{
    // Region lock: blocks the Valve relays/servers of every region the player did NOT pick,
    // so matchmaking can only see (and place you on) the picked ones.
    static class ServerLock
    {
        const string Rule = "2XPing-RegionLock";
        static readonly System.Text.RegularExpressions.Regex Cidr =
            new System.Text.RegularExpressions.Regex(@"^\d{1,3}(\.\d{1,3}){3}(/\d{1,2})?$");

        public static int Apply(IEnumerable<string> cidrs)
        {
            Remove();
            var list = new List<string>();
            foreach (var c in cidrs) if (Cidr.IsMatch(c) && !list.Contains(c)) list.Add(c);
            for (int i = 0; i < list.Count; i += 100)
            {
                string ips = string.Join(",", list.GetRange(i, Math.Min(100, list.Count - i)));
                foreach (var dir in new[] { "out", "in" })
                {
                    string output;
                    int code = Netsh("advfirewall firewall add rule name=\"" + Rule + "\" dir=" + dir +
                                     " action=block enable=yes remoteip=" + ips, out output);
                    if (code != 0) { Remove(); throw new Exception("Windows Firewall: " + output); }
                }
            }
            return list.Count;
        }

        public static void Remove()
        {
            string output;
            Netsh("advfirewall firewall delete rule name=\"" + Rule + "\"", out output);
            Netsh("advfirewall firewall delete rule name=\"PingBoost-RegionLock\"", out output); // pre-2X Ping name
        }

        // Any 2X Ping firewall rule still present?
        public static bool Exists()
        {
            string output;
            return Netsh("advfirewall firewall show rule name=\"" + Rule + "\"", out output) == 0;
        }

        static int Netsh(string args, out string output)
        {
            var psi = new ProcessStartInfo("netsh", args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var p = Process.Start(psi))
            {
                output = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
                p.WaitForExit(20000);
                return p.HasExited ? p.ExitCode : -1;
            }
        }
    }

    // Split tunnel through WireGuard for Windows: only the game's IP ranges go through the relay.
    static class Tunnel
    {
        public const string Name = "2xping";

        public static string WgExe
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "WireGuard", "wireguard.exe");
            }
        }

        public static bool Installed { get { return File.Exists(WgExe); } }

        public static string BuildConf(Relay relay, ICollection<string> cidrs)
        {
            var sb = new StringBuilder();
            bool inPeer = false, wroteAllowed = false;
            foreach (var raw in File.ReadAllLines(relay.ConfPath))
            {
                var line = raw.Trim();
                if (line.StartsWith("["))
                {
                    inPeer = line.Equals("[Peer]", StringComparison.OrdinalIgnoreCase);
                    sb.AppendLine(raw);
                    if (inPeer && !wroteAllowed)
                    {
                        sb.AppendLine("AllowedIPs = " + string.Join(", ", cidrs));
                        wroteAllowed = true;
                    }
                    continue;
                }
                int eq = line.IndexOf('=');
                string key = eq > 0 ? line.Substring(0, eq).Trim().ToLowerInvariant() : "";
                if (!inPeer && key == "dns") continue;       // keep the system DNS
                if (inPeer && key == "allowedips") continue; // replaced by game ranges
                sb.AppendLine(raw);
            }
            if (!wroteAllowed) throw new Exception("The relay config has no [Peer] section.");
            return sb.ToString();
        }

        public static string Start(string conf)
        {
            Stop();
            string path = Path.Combine(Storage.Dir, Name + ".conf");
            File.WriteAllText(path, conf);
            string output;
            int code = Run("/installtunnelservice \"" + path + "\"", out output);
            if (code != 0) throw new Exception("WireGuard failed (" + code + "): " + output);
            return output;
        }

        public static void Stop()
        {
            if (!Installed) return;
            string output;
            Run("/uninstalltunnelservice " + Name, out output);
        }

        static int Run(string args, out string output)
        {
            var psi = new ProcessStartInfo(WgExe, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var p = Process.Start(psi))
            {
                output = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
                p.WaitForExit(20000);
                return p.HasExited ? p.ExitCode : -1;
            }
        }
    }
}
