using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;

namespace PingBoost
{
    // Finds the game server the game is talking to right now, using Windows' built-in packet monitor (pktmon).
    static class Detector
    {
        static readonly Regex PacketLine = new Regex(
            @"(\d+\.\d+\.\d+\.\d+)\.(\d+) > (\d+\.\d+\.\d+\.\d+)\.(\d+): UDP", RegexOptions.Compiled);

        public class Hit
        {
            public string Ip;
            public int Port, Packets;
            public double Share; // this address's part of all the game's public packets in the capture (0..1)
        }

        // A Valve address carries the match only with at least half of the game's packets; a smaller one is a relay the
        // game also keeps open (SDR keeps a backup router alive next to the primary), or nothing (Deadlock audit I4).
        public const double MatchShare = 0.5;

        public static bool CarriesMatch(Hit h) { return h != null && h.Share >= MatchShare; }

        // The addresses worth naming: each one's share of every packet counted, then those with at least 20 packets,
        // the five busiest first.
        internal static List<Hit> Top(IEnumerable<Hit> all)
        {
            var list = all.ToList();
            double total = list.Sum(h => (double)h.Packets);
            foreach (var h in list) h.Share = total > 0 ? h.Packets / total : 0;
            return list.Where(h => h.Packets >= 20).OrderByDescending(h => h.Packets).Take(5).ToList();
        }

        // Apps that send a lot of UDP (voice chat, browsers, music) and must never be taken for a game server.
        static readonly string[] Noisy =
        {
            "discord", "discordptb", "discordcanary", "chrome", "msedge", "brave", "firefox", "opera", "opera_gx",
            "spotify", "steam", "steamwebhelper", "teams", "ms-teams", "zoom", "whatsapp", "telegram", "skype", "onedrive"
        };

        // Process ids by name (any case), from one look at the process list: each GetProcessesByName call reads the
        // whole list again (about 11 ms), and one check used to make 40 of them (performance audit item 2).
        public static Dictionary<string, List<int>> Processes()
        {
            var by = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
                using (p)
                {
                    string name;
                    try { name = p.ProcessName; } catch { continue; } // it ended meanwhile
                    List<int> ids;
                    if (!by.TryGetValue(name, out ids)) by[name] = ids = new List<int>();
                    ids.Add(p.Id);
                }
            return by;
        }

        public static bool IsRunning(Dictionary<string, List<int>> processes, string[] processNames)
        {
            return processNames.Any(processes.ContainsKey);
        }

        public static bool IsRunning(string[] processNames)
        {
            return processNames.Length > 0 && IsRunning(Processes(), processNames);
        }

        // Local UDP ports opened by the given processes.
        static HashSet<int> PortsOf(string[] processNames)
        {
            var all = Processes();
            var pids = new HashSet<int>(processNames.Where(all.ContainsKey).SelectMany(n => all[n]));
            var ports = new HashSet<int>();
            if (pids.Count == 0) return ports;
            // Windows' own socket table; netstat (a process start) only if that call fails
            var sockets = UdpSockets();
            if (sockets != null)
            {
                foreach (var s in sockets) if (pids.Contains(s.Key)) ports.Add(s.Value);
                return ports;
            }
            foreach (var line in Run("netstat", "-ano -p UDP").Split('\n'))
            {
                var f = line.Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                int pid, port;
                if (f.Length >= 4 && f[0] == "UDP" && int.TryParse(f[f.Length - 1], out pid) && pids.Contains(pid))
                {
                    var local = f[1];
                    if (int.TryParse(local.Substring(local.LastIndexOf(':') + 1), out port)) ports.Add(port);
                }
            }
            return ports;
        }

        [System.Runtime.InteropServices.DllImport("iphlpapi.dll")]
        static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool sorted, int family, int tableClass, uint reserved);

        // (process id, local port) of every IPv4 UDP socket (what "netstat -ano -p UDP" lists), or null when Windows
        // wouldn't give the table. MIB_UDPTABLE_OWNER_PID: a count, then rows of local address, port, owner pid.
        internal static List<KeyValuePair<int, int>> UdpSockets()
        {
            const int AfInet = 2, OwnerPid = 1, TooSmall = 122;
            int size = 0;
            GetExtendedUdpTable(IntPtr.Zero, ref size, false, AfInet, OwnerPid, 0);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                size += 1024; // sockets opened between the two calls
                IntPtr buf = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
                try
                {
                    uint r = GetExtendedUdpTable(buf, ref size, false, AfInet, OwnerPid, 0);
                    if (r == TooSmall) continue;
                    if (r != 0) return null;
                    int n = System.Runtime.InteropServices.Marshal.ReadInt32(buf);
                    var list = new List<KeyValuePair<int, int>>(n);
                    for (int i = 0; i < n; i++)
                    {
                        int row = 4 + i * 12;
                        int port = System.Runtime.InteropServices.Marshal.ReadInt32(buf, row + 4);
                        int pid = System.Runtime.InteropServices.Marshal.ReadInt32(buf, row + 8);
                        list.Add(new KeyValuePair<int, int>(pid, ((port & 0xFF) << 8) | ((port >> 8) & 0xFF))); // network byte order
                    }
                    return list;
                }
                catch { return null; }
                finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buf); }
            }
            return null;
        }

        // At most this many port filters; a game with more sockets is captured with the plain UDP filter.
        public const int MaxPortFilters = 8;

        // The pktmon filters for a capture: one per local UDP port the game owns, so only its packets are copied
        // (performance audit item 3); every UDP packet when no port is known yet or there are too many.
        public static List<string> CaptureFilters(IEnumerable<int> ports)
        {
            var list = ports.Distinct().OrderBy(p => p).ToList();
            if (list.Count == 0 || list.Count > MaxPortFilters) return new List<string> { "2XPingUDP -t UDP" };
            return list.Select(p => "2XPingUDP" + p + " -t UDP -p " + p).ToList();
        }

        public static bool IsPublic(string ip)
        {
            var b = ip.Split('.').Select(int.Parse).ToArray();
            if (b[0] == 10 || b[0] == 127 || b[0] == 0 || b[0] >= 224) return false;
            if (b[0] == 192 && b[1] == 168) return false;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;
            if (b[0] == 169 && b[1] == 254) return false;
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false;
            return true;
        }

        // Game known (processNames given): only that game's traffic counts, never a guess.
        // Custom servers (no process): any UDP except well-known chat/browser apps.
        public static List<Hit> Capture(string[] processNames, int seconds, out bool matchedGame)
        {
            string dir = Path.Combine(Storage.Dir, "capture");
            Directory.CreateDirectory(dir);
            string etl = Path.Combine(dir, "cap.etl"), txt = Path.Combine(dir, "cap.txt");
            if (File.Exists(txt)) File.Delete(txt);

            bool knownGame = processNames.Length > 0;
            // a known game: its own ports, nothing else is looked at; custom servers: the chat/browser apps' ports to skip
            var watched = knownGame ? processNames : Noisy;
            var ports = PortsOf(watched);
            Run("pktmon", "filter remove");
            bool filtered = true;
            foreach (var f in knownGame ? CaptureFilters(ports) : CaptureFilters(new int[0]))
            {
                string o;
                if (Proc.Run("pktmon", "filter add " + f, 60000, out o) != 0) { filtered = false; break; }
            }
            if (!filtered)
            {
                // a port filter pktmon didn't take: every UDP packet, as before
                Run("pktmon", "filter remove");
                Run("pktmon", "filter add 2XPingUDP -t UDP");
            }
            try
            {
                Run("pktmon", "start --capture --comp nics --pkt-size 96 --file-name \"" + etl + "\"");
                Thread.Sleep(seconds * 1000);
            }
            finally
            {
                Run("pktmon", "stop");
                Run("pktmon", "filter remove");
            }
            Run("pktmon", "etl2txt \"" + etl + "\" --out \"" + txt + "\"");
            if (!File.Exists(txt)) throw new Exception("pktmon produced no output.");

            // also refresh after capture: the game may have opened its socket during it
            // (with a port filter only the ports known before were captured; a new one counts once it is seen next time)
            ports.UnionWith(PortsOf(watched));
            matchedGame = knownGame && ports.Count > 0;
            if (knownGame && !matchedGame)
            {
                try { File.Delete(etl); File.Delete(txt); } catch { }
                return new List<Hit>();
            }

            var hits = new Dictionary<string, Hit>();
            foreach (var line in File.ReadLines(txt))
            {
                var m = PacketLine.Match(line);
                if (!m.Success) continue;
                string a = m.Groups[1].Value, b = m.Groups[3].Value;
                int pa = int.Parse(m.Groups[2].Value), pb = int.Parse(m.Groups[4].Value);
                string remote; int rport, lport;
                if (IsPublic(b) && !IsPublic(a)) { remote = b; rport = pb; lport = pa; }
                else if (IsPublic(a) && !IsPublic(b)) { remote = a; rport = pa; lport = pb; }
                else continue;
                if (rport == 53 || rport == 123 || rport == 443) continue; // DNS, time sync, QUIC web traffic
                if (knownGame ? !ports.Contains(lport) : ports.Contains(lport)) continue;
                Hit h;
                if (!hits.TryGetValue(remote, out h)) hits[remote] = h = new Hit { Ip = remote, Port = rport };
                h.Packets++;
            }
            try { File.Delete(etl); File.Delete(txt); } catch { }
            return Top(hits.Values);
        }

        // Is the address inside a.b.c.d/n (or an IPv6 prefix)? An address and a range of different kinds never match.
        public static bool InCidr(string ip, string cidr)
        {
            if (ip == null || cidr == null) return false;
            if (RemoteAddress.IsV6(ip) || RemoteAddress.IsV6(cidr)) return InCidr6(ip, cidr);
            try
            {
                var parts = cidr.Split('/');
                int bits = parts.Length > 1 ? int.Parse(parts[1]) : 32;
                uint a = ToUInt(ip), n = ToUInt(parts[0]);
                uint mask = bits == 0 ? 0 : uint.MaxValue << (32 - bits);
                return (a & mask) == (n & mask);
            }
            catch { return false; }
        }

        static bool InCidr6(string ip, string cidr)
        {
            var parts = cidr.Split('/');
            IPAddress a, n;
            int bits = 128;
            if (!RemoteAddress.IsV6(ip) || !RemoteAddress.IsV6(parts[0]) || !IPAddress.TryParse(ip, out a) || !IPAddress.TryParse(parts[0], out n) ||
                (parts.Length > 1 && (!int.TryParse(parts[1], out bits) || bits < 0 || bits > 128)))
                return false;
            byte[] x = a.GetAddressBytes(), y = n.GetAddressBytes();
            if (x.Length != 16 || y.Length != 16) return false;
            for (int i = 0; i < 16 && bits > 0; i++, bits -= 8)
            {
                int mask = bits >= 8 ? 0xFF : (0xFF << (8 - bits)) & 0xFF;
                if ((x[i] & mask) != (y[i] & mask)) return false;
            }
            return true;
        }

        static uint ToUInt(string ip)
        {
            var b = ip.Split('.').Select(uint.Parse).ToArray();
            return (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
        }

        // Removes any capture filter left behind (e.g. if the app was killed mid-capture).
        public static void Cleanup()
        {
            try { Run("pktmon", "stop"); Run("pktmon", "filter remove"); } catch { }
        }

        // A Windows tool (pktmon, netstat, netsh) by its System32 path; killed if it hangs for a minute.
        public static string Run(string exe, string args)
        {
            string o;
            Proc.Run(exe, args, 60000, out o);
            return o;
        }
    }
}
