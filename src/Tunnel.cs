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

        // true while rules of ours may be in the firewall (set before adding any; cleared by a verified Remove)
        public static volatile bool MayExist = true;

        // Entries of the last Apply that were not valid addresses (skipped instead of failing the whole lock).
        public static int Skipped;

        // The valid, distinct remote addresses of the list (1.2.3.0/24, 1.2.3.4 or 1.2.3.4-1.2.3.9); bad entries are skipped.
        public static List<string> ValidAddresses(IEnumerable<string> cidrs, out int skipped)
        {
            var list = new List<string>();
            var seen = new HashSet<string>();
            skipped = 0;
            foreach (var raw in cidrs)
            {
                string c = raw == null ? null : raw.Trim();
                if (!RemoteAddress.Valid(c)) { skipped++; continue; }
                if (seen.Add(c)) list.Add(c);
            }
            return list;
        }

        // Blocks the given addresses. With a program, only that program's traffic is blocked (nothing else on the PC);
        // with a protocol (e.g. UDP), only that kind of traffic.
        public static int Apply(IEnumerable<string> cidrs, string program = null, string protocol = null, string remotePorts = null)
        {
            Remove();
            MayExist = true;
            int skipped;
            var list = ValidAddresses(cidrs, out skipped);
            Skipped = skipped;
            string scope = (program != null ? " program=\"" + program + "\"" : "") + (protocol != null ? " protocol=" + protocol : "") +
                           (remotePorts != null && protocol != null ? " remoteport=" + remotePorts : "");
            for (int i = 0; i < list.Count; i += 100)
            {
                string ips = string.Join(",", list.GetRange(i, Math.Min(100, list.Count - i)));
                foreach (var dir in new[] { "out", "in" })
                {
                    string output;
                    // the app is closing: no new rules (closing removes what is there)
                    if (FirewallWorker.Closing) { Remove(); throw new OperationCanceledException("closing"); }
                    int code = Netsh("advfirewall firewall add rule name=\"" + Rule + "\" dir=" + dir +
                                     " action=block enable=yes" + scope + " remoteip=" + ips, out output);
                    if (code != 0) { Remove(); throw new Exception("Windows Firewall: " + output); }
                }
            }
            return list.Count;
        }

        // Deletes every 2X Ping rule, then checks: true = none is left.
        public static bool Remove()
        {
            string output;
            Netsh("advfirewall firewall delete rule name=\"" + Rule + "\"", out output);
            Netsh("advfirewall firewall delete rule name=\"PingBoost-RegionLock\"", out output); // pre-2X Ping name
            // both names confirmed gone ("show rule" answers exactly 1 when no rule has the name; -1 = no answer)
            bool gone = Present(Rule) == false && Present("PingBoost-RegionLock") == false;
            if (gone) MayExist = false;
            return gone;
        }

        // true = a rule with this name exists, false = none (netsh answered 1), null = netsh didn't answer (unknown)
        static bool? Present(string name)
        {
            string output;
            int code = Netsh("advfirewall firewall show rule name=\"" + name + "\"", out output);
            return code == 0 ? true : code == 1 ? (bool?)false : null;
        }

        // Are 2X Ping firewall rules there? null when netsh couldn't tell.
        public static bool? Present() { return Present(Rule); }

        // Is Windows Firewall on for the network in use? (When it's off, block rules do nothing.)
        public static bool FirewallOn()
        {
            string output;
            if (Netsh("advfirewall show currentprofile state", out output) != 0) return true; // can't tell: assume on
            return !System.Text.RegularExpressions.Regex.IsMatch(output, @"State\s+OFF", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        // Any 2X Ping firewall rule still present?
        public static bool Exists() { return Present(Rule) == true; }

        static int Netsh(string args, out string output)
        {
            return Proc.Run("netsh", args, 20000, out output);
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

        // Is our tunnel service (WireGuardTunnel$2xping) installed? Read from the registry: no process is started.
        public static bool ServiceInstalled
        {
            get
            {
                try
                {
                    using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\WireGuardTunnel$" + Name))
                        return k != null;
                }
                catch { return false; }
            }
        }

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

        // the config handed to WireGuard (it holds the relay's private key): the tunnel service reads it while it runs
        static string ConfPath { get { return Path.Combine(Storage.Dir, Name + ".conf"); } }

        public static string Start(string conf)
        {
            Stop();
            string path = ConfPath;
            File.WriteAllText(path, conf);
            string output;
            int code = Run("/installtunnelservice \"" + path + "\"", out output);
            if (code != 0) throw new Exception("WireGuard failed (" + code + "): " + output);
            return output;
        }

        public static void Stop()
        {
            if (Installed)
            {
                string output;
                Run("/uninstalltunnelservice " + Name, out output);
            }
            // the private key never stays on disk once the tunnel is gone
            if (!ServiceInstalled) try { File.Delete(ConfPath); } catch { }
        }

        static int Run(string args, out string output)
        {
            return Proc.Run(WgExe, args, 20000, out output);
        }
    }
}
