using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace PingBoost
{

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
