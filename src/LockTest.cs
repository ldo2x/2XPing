using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace PingBoost
{
    // Developer tool: records every packet the PC sends and receives for a minute (pktmon, all IPv4 protocols, drops
    // included) while the player starts the game and queues, then says which datacenters the traffic went to, which of
    // them the lock blocks, and whether the lock's rule actually covered that traffic (protocol, program, port).
    static class LockTest
    {
        // One remote address and protocol.
        public class Row
        {
            public string Ip, Proto;
            public int Out, In, DropOut, DropIn;
            public SortedSet<int> RemotePorts = new SortedSet<int>();
            public SortedSet<int> LocalPorts = new SortedSet<int>();
            public Dictionary<string, int> Reasons = new Dictionary<string, int>();
            public int Dropped { get { return DropOut + DropIn; } }
            public int Total { get { return Out + In; } }
            public int InPassed { get { return Math.Max(0, In - DropIn); } } // answers the firewall let through
        }

        public class Capture
        {
            public Dictionary<string, Row> Rows = new Dictionary<string, Row>(); // "ip proto"
            public int Snapshots;          // packet snapshots (header lines) read
            public int Packets;            // distinct public packets counted
            public int DropEvents;         // "Drop:" snapshots, any address
            public int DropsUnattributed;  // drop snapshots without a readable IPv4 packet line
            public bool HadIds;            // PktGroupId/PktNumber seen: each packet counted once across components
            public Dictionary<string, int> Reasons = new Dictionary<string, int>();
        }

        class Seen { public Row Row; public bool Out, Dropped; }

        // "[Microsoft-Windows-PktMon] Drop: PktGroupId 8444249301423149, PktNumber 1, Appearance 0, Direction Rx , ..."
        static readonly Regex Header = new Regex(@"(Drop:\s*)?PktGroupId\s+(\d+),\s*PktNumber\s+(\d+)", RegexOptions.Compiled);
        static readonly Regex Reason = new Regex(@"DropReason\s+(.+?)\s*,\s*(?:DropLocation|OriginalSize|$)", RegexOptions.Compiled);
        // "10.0.0.5.52012 > 13.107.4.50.443: Flags [S]", "ip: 1.2.3.4.5 > 6.7.8.9.10: UDP, length 3", "1.2.3.4 > 5.6.7.8: ICMP echo request"
        static readonly Regex Packet = new Regex(
            @"(?<![\d.])(\d{1,3}(?:\.\d{1,3}){3})(?:\.(\d{1,5}))? > (\d{1,3}(?:\.\d{1,3}){3})(?:\.(\d{1,5}))?:\s*(.*)$", RegexOptions.Compiled);

        // Reads pktmon etl2txt output. Every snapshot is a header line (timestamp, PktGroupId, PktNumber, "Drop:" when
        // the packet was dropped there, DropReason) followed by the parsed packet (tcpdump style). A packet shows up once
        // per component it crosses: snapshots with the same PktGroupId + PktNumber are one packet, counted once, and it
        // counts as dropped if any of its snapshots is a drop. Only traffic between this PC (localIps, or a private
        // address) and a public address is kept. Lines without headers (older formats) count one packet each.
        public static Capture Parse(IEnumerable<string> lines, ICollection<string> localIps)
        {
            var cap = new Capture();
            var seen = new Dictionary<string, Seen>();
            string key = null, reason = null;
            bool drop = false, used = true, sawHeader = false;
            long n = 0;
            foreach (var line in lines)
            {
                n++;
                var h = Header.Match(line);
                if (h.Success)
                {
                    if (!used && drop) cap.DropsUnattributed++;
                    sawHeader = true;
                    cap.HadIds = true;
                    cap.Snapshots++;
                    key = h.Groups[2].Value + ":" + h.Groups[3].Value;
                    drop = h.Groups[1].Success || line.Contains("DropReason");
                    reason = null;
                    if (drop)
                    {
                        cap.DropEvents++;
                        var r = Reason.Match(line);
                        reason = r.Success ? r.Groups[1].Value.Trim() : "unknown";
                        Bump(cap.Reasons, reason);
                    }
                    used = false;
                    continue;
                }
                if (sawHeader && used) continue; // inner lines of an encapsulated packet, or the tables at the end
                var m = Packet.Match(line);
                if (!m.Success) continue;
                string snapKey = sawHeader ? key : "line" + n;
                bool snapDrop = sawHeader ? drop : line.TrimStart().StartsWith("Drop", StringComparison.OrdinalIgnoreCase);
                string snapReason = sawHeader ? reason : (snapDrop ? "unknown" : null);
                if (!sawHeader && snapDrop) { cap.DropEvents++; Bump(cap.Reasons, snapReason); }
                used = true;

                string a = m.Groups[1].Value, b = m.Groups[3].Value;
                bool aLocal = IsLocal(a, localIps), bLocal = IsLocal(b, localIps);
                string remote; int rport, lport; bool isOut;
                if (aLocal && !bLocal && Detector.IsPublic(b)) { remote = b; isOut = true; rport = Port(m.Groups[4]); lport = Port(m.Groups[2]); }
                else if (bLocal && !aLocal && Detector.IsPublic(a)) { remote = a; isOut = false; rport = Port(m.Groups[2]); lport = Port(m.Groups[4]); }
                else continue;
                string proto = Proto(m.Groups[5].Value);

                Seen s;
                if (!seen.TryGetValue(snapKey, out s))
                {
                    Row row;
                    string rk = remote + " " + proto;
                    if (!cap.Rows.TryGetValue(rk, out row)) cap.Rows[rk] = row = new Row { Ip = remote, Proto = proto };
                    s = new Seen { Row = row, Out = isOut };
                    seen[snapKey] = s;
                    cap.Packets++;
                    if (isOut) row.Out++; else row.In++;
                    if (rport > 0) row.RemotePorts.Add(rport);
                    if (lport > 0) row.LocalPorts.Add(lport);
                }
                if (snapDrop && !s.Dropped)
                {
                    s.Dropped = true;
                    if (s.Out) s.Row.DropOut++; else s.Row.DropIn++;
                    Bump(s.Row.Reasons, snapReason ?? "unknown");
                }
            }
            if (!used && drop) cap.DropsUnattributed++;
            return cap;
        }

        static bool IsLocal(string ip, ICollection<string> localIps)
        {
            return (localIps != null && localIps.Contains(ip)) || !Detector.IsPublic(ip);
        }

        static int Port(Group g)
        {
            int p;
            return g.Success && int.TryParse(g.Value, out p) ? p : 0;
        }

        static string Proto(string rest)
        {
            rest = rest.TrimStart();
            if (rest.StartsWith("UDP", StringComparison.OrdinalIgnoreCase)) return "UDP";
            if (rest.StartsWith("ICMP", StringComparison.OrdinalIgnoreCase)) return "ICMP";
            if (rest.StartsWith("Flags [", StringComparison.Ordinal) || rest.StartsWith("TCP", StringComparison.OrdinalIgnoreCase)) return "TCP";
            return "IP";
        }

        static void Bump(Dictionary<string, int> d, string k)
        {
            int v;
            d.TryGetValue(k, out v);
            d[k] = v + 1;
        }

        // Does pktmon's drop reason point at the firewall (Windows Filtering Platform)?
        public static bool FirewallReason(string reason)
        {
            return reason != null && Regex.IsMatch(reason, @"WFP|Inspection|Firewall|Filtering Platform|\bALE\b", RegexOptions.IgnoreCase);
        }

        // "netstat -ano" -> { "UDP 50000": pid, "TCP 51234": pid } (local ports, IPv4 and IPv6)
        public static Dictionary<string, int> ParseNetstat(string text)
        {
            var d = new Dictionary<string, int>();
            foreach (var line in text.Split('\n'))
            {
                var f = line.Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 4 || (f[0] != "UDP" && f[0] != "TCP")) continue;
                int pid, port;
                string local = f[1];
                if (!int.TryParse(f[f.Length - 1], out pid) || pid <= 0) continue;
                if (!int.TryParse(local.Substring(local.LastIndexOf(':') + 1), out port)) continue;
                string k = f[0] + " " + port;
                if (!d.ContainsKey(k)) d[k] = pid;
            }
            return d;
        }

        // ---------------------------------------------------------------- the lock being tested

        public class LockInfo
        {
            public bool Active;
            public string Mode = "none";  // "full", "game-udp", "programs-all" (Valve), "none"
            public string Scope = "-";    // human-readable rule scope
            public string Ports;          // remote ports the rule covers (null = all)
            public string Program;        // program the rule is limited to (null = whole PC)
            public string Protocol;       // protocol the rule is limited to (null = any)
            public List<string> Picked = new List<string>();
            public List<string> Block;    // the addresses the rule blocks (null = no lock)
            public List<string> Allow = new List<string>();
        }

        public class Line
        {
            public Row Row;
            public string Dc = "-", Owner = "?", Status = "-";
            public bool Game, Covered, InBlock;
        }

        // Ranges like "1-3723,3725-65535" (null = every port).
        public static bool PortIn(int port, string ranges)
        {
            if (ranges == null) return true;
            foreach (var part in ranges.Split(','))
            {
                var ab = part.Trim().Split('-');
                int lo, hi;
                if (!int.TryParse(ab[0], out lo)) continue;
                if (ab.Length < 2 || !int.TryParse(ab[1], out hi)) hi = lo;
                if (port >= lo && port <= hi) return true;
            }
            return false;
        }

        class Net { public uint Addr, Mask; }

        static List<Net> Nets(IEnumerable<string> cidrs)
        {
            var list = new List<Net>();
            if (cidrs == null) return list;
            foreach (var c in cidrs)
            {
                try
                {
                    var parts = c.Split('/');
                    if (parts[0].Contains("-")) // a-b range: only exact first address supported here
                    {
                        var ab = parts[0].Split('-');
                        uint lo = ToUInt(ab[0]), hi = ToUInt(ab[1]);
                        for (uint x = lo; x <= hi && x - lo < 4096; x++) list.Add(new Net { Addr = x, Mask = uint.MaxValue });
                        continue;
                    }
                    int bits = parts.Length > 1 ? int.Parse(parts[1]) : 32;
                    uint mask = bits == 0 ? 0 : uint.MaxValue << (32 - bits);
                    list.Add(new Net { Addr = ToUInt(parts[0]) & mask, Mask = mask });
                }
                catch { }
            }
            return list;
        }

        static uint ToUInt(string ip)
        {
            var b = ip.Split('.').Select(uint.Parse).ToArray();
            return (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
        }

        static bool InAny(string ip, List<Net> nets)
        {
            uint a = ToUInt(ip);
            return nets.Any(x => (a & x.Mask) == x.Addr);
        }

        // Each address with its datacenter, whether the lock blocks it, and whether the rule's scope covers the traffic.
        public static List<Line> Classify(Game g, Capture cap, LockInfo info, Dictionary<string, string> owners)
        {
            var block = Nets(info.Block);
            var allow = Nets(info.Allow);
            var gameNames = new HashSet<string>(g.Processes.Select(p => p.ToLowerInvariant()));
            var lines = new List<Line>();
            foreach (var row in cap.Rows.Values.OrderByDescending(r => r.Total))
            {
                var l = new Line { Row = row };
                var dc = g.Regions.FirstOrDefault(r => r.Cidrs.Any(c => Detector.InCidr(row.Ip, c)));
                if (dc != null) l.Dc = dc.Id;
                var names = new SortedSet<string>();
                if (owners != null)
                    foreach (var p in row.LocalPorts)
                    {
                        string o;
                        if (owners.TryGetValue((row.Proto == "TCP" ? "TCP " : "UDP ") + p, out o)) names.Add(o);
                    }
                if (names.Count > 0) l.Owner = string.Join("+", names);
                l.Game = names.Any(x => gameNames.Contains(x.ToLowerInvariant()));
                l.InBlock = info.Block != null && InAny(row.Ip, block) && !InAny(row.Ip, allow);
                if (l.InBlock) l.Status = "BLOCKED";
                else if (InAny(row.Ip, allow)) l.Status = "allowed";
                else if (dc != null && dc.Picked) l.Status = "picked";
                else if (dc != null) l.Status = info.Block != null ? "open" : "not picked";
                // would the rule catch this traffic? (program, protocol and port scope)
                bool portOk;
                if (info.Ports == null) portOk = true;
                else if (row.Proto == "TCP" || row.Proto == "UDP") portOk = row.RemotePorts.Count == 0 || row.RemotePorts.Any(p => PortIn(p, info.Ports));
                else portOk = false; // a port-limited rule never matches ICMP or other protocols
                l.Covered = l.InBlock &&
                            (info.Protocol == null || string.Equals(info.Protocol, row.Proto, StringComparison.OrdinalIgnoreCase)) &&
                            (info.Program == null || l.Game) && portOk;
                lines.Add(l);
            }
            return lines;
        }

        // The full text saved to locktest-*.txt and shown in the developer console.
        public static string Report(Game g, Capture cap, LockInfo info, List<Line> lines, int seconds, DateTime at)
        {
            var sb = new StringBuilder();
            sb.AppendLine("2X Ping lock test  " + at.ToString("yyyy-MM-dd HH:mm:ss") + "  " + Brand.DisplayVersion);
            sb.AppendLine("Game: " + g.Name + "   recorded " + seconds + " s");
            sb.AppendLine("Lock: " + (info.Active ? "ON" : "off") + "   mode: " + info.Mode);
            sb.AppendLine("Picked: " + (info.Picked.Count > 0 ? string.Join(", ", info.Picked) : "-"));
            sb.AppendLine("Rule scope: " + info.Scope);
            if (info.Allow.Count > 0) sb.AppendLine("Never blocked: " + string.Join(", ", info.Allow.Take(10)) + (info.Allow.Count > 10 ? " ..." : ""));
            sb.AppendLine();
            sb.AppendLine("Packets: " + cap.Packets + " (public, IPv4)   snapshots read: " + cap.Snapshots +
                          "   drop snapshots: " + cap.DropEvents + (cap.DropsUnattributed > 0 ? " (" + cap.DropsUnattributed + " without a readable packet)" : ""));
            if (cap.Reasons.Count > 0)
                sb.AppendLine("Drop reasons: " + string.Join("; ", cap.Reasons.OrderByDescending(kv => kv.Value).Select(kv =>
                    kv.Key + " x" + kv.Value + (FirewallReason(kv.Key) ? " [firewall]" : ""))));
            if (!cap.HadIds)
                sb.AppendLine("NOTE: no PktGroupId/PktNumber in the log: a packet seen by several components may be counted more than once.");
            sb.AppendLine("NOTE: pktmon may not log packets the firewall stops before they reach a component (e.g. a blocked UDP send),");
            sb.AppendLine("      so also read OUT vs IN: a blocked server shows OUT>0 IN=0 (or IN all dropped); one that answers was NOT blocked for that traffic.");
            sb.AppendLine();
            sb.AppendLine(string.Format("{0,-16} {1,-5} {2,-18} {3,6} {4,6} {5,6}  {6,-10} {7,-10} {8,-5} {9}",
                "ADDRESS", "PROTO", "REMOTE PORTS", "OUT", "IN", "DROP", "DATACENTER", "STATUS", "RULE", "PROCESS"));
            foreach (var l in lines)
                sb.AppendLine(string.Format("{0,-16} {1,-5} {2,-18} {3,6} {4,6} {5,6}  {6,-10} {7,-10} {8,-5} {9}",
                    l.Row.Ip, l.Row.Proto, Ports(l.Row.RemotePorts, 18), l.Row.Out, l.Row.In, l.Row.Dropped, l.Dc, l.Status,
                    l.InBlock ? (l.Covered ? "hits" : "MISS") : "-", (l.Game ? "GAME " : "") + l.Owner));
            sb.AppendLine();
            sb.AppendLine("Findings:");
            int findings = 0;
            foreach (var l in lines.Where(x => x.InBlock && x.Row.InPassed > 0))
            {
                findings++;
                sb.AppendLine("  LEAK  " + l.Row.Ip + " (" + l.Dc + ", " + l.Row.Proto + ", " + (l.Game ? "game" : l.Owner) + ") answered " + l.Row.InPassed +
                              " times although its datacenter is blocked" + (l.Covered ? "" : ": the rule doesn't cover this traffic (" + Why(l, info) + ")"));
            }
            foreach (var l in lines.Where(x => x.InBlock && x.Row.InPassed == 0 && x.Row.Total > 0))
            {
                findings++;
                sb.AppendLine("  held  " + l.Row.Ip + " (" + l.Dc + ", " + l.Row.Proto + "): " + l.Row.Out + " sent, no answer got through" +
                              (l.Row.Dropped > 0 ? ", " + l.Row.Dropped + " dropped" : ""));
            }
            var gameDcs = lines.Where(x => x.Game && x.Dc != "-").Select(x => x.Dc + (x.InBlock ? "(blocked)" : "")).Distinct().ToList();
            if (gameDcs.Count > 0) { findings++; sb.AppendLine("  The game talked to datacenters: " + string.Join(", ", gameDcs)); }
            if (findings == 0) sb.AppendLine("  nothing in the blocked datacenters was seen");
            return sb.ToString();
        }

        static string Why(Line l, LockInfo info)
        {
            var r = new List<string>();
            if (info.Protocol != null && !string.Equals(info.Protocol, l.Row.Proto, StringComparison.OrdinalIgnoreCase)) r.Add("rule is " + info.Protocol + " only");
            if (info.Program != null && !l.Game) r.Add("another process");
            if (info.Ports != null && l.Row.RemotePorts.Count > 0 && !l.Row.RemotePorts.Any(p => PortIn(p, info.Ports))) r.Add("port " + Ports(l.Row.RemotePorts, 20) + " excluded");
            return r.Count > 0 ? string.Join(", ", r) : "scope";
        }

        static string Ports(SortedSet<int> ports, int max)
        {
            if (ports.Count == 0) return "-";
            string s = string.Join(",", ports.Take(6));
            if (ports.Count > 6) s += "+" + (ports.Count - 6);
            return s.Length > max ? s.Substring(0, max - 1) + "~" : s;
        }

        // Short line for the telemetry event.
        public static string Summary(Game g, Capture cap, LockInfo info, List<Line> lines)
        {
            int blocked = lines.Count(l => l.InBlock), leaks = lines.Count(l => l.InBlock && l.Row.InPassed > 0);
            return g.Name + ", mode " + info.Mode + ", " + lines.Count + " addresses, " + blocked + " in blocked datacenters, " +
                   leaks + " answered, drops " + cap.DropEvents + " (" + cap.Reasons.Where(kv => FirewallReason(kv.Key)).Sum(kv => kv.Value) + " firewall)";
        }

        // The structured part of the telemetry event: one compact array per address, kept under 20 KB of JSON text (the server drops data whose jsonb is 32 KB or more).
        public static Dictionary<string, object> Data(Capture cap, LockInfo info, List<Line> lines, int seconds)
        {
            var rows = lines.Select(l => (object)new object[]
            {
                l.Row.Ip, l.Row.Proto, Ports(l.Row.RemotePorts, 40), l.Row.Out, l.Row.In, l.Row.Dropped,
                l.Dc, l.Status, l.InBlock ? (l.Covered ? 1 : 0) : -1, l.Game ? 1 : 0, l.Owner.Length > 30 ? l.Owner.Substring(0, 30) : l.Owner
            }).ToList();
            var d = new Dictionary<string, object>
            {
                { "cols", "ip,proto,ports,out,in,drop,dc,status,rule(1 hits/0 miss/-1 not blocked),game,process" },
                { "mode", info.Mode }, { "active", info.Active }, { "scope", info.Scope }, { "picked", info.Picked.ToArray() },
                { "seconds", seconds }, { "packets", cap.Packets }, { "drops", cap.DropEvents }, { "drops_unattributed", cap.DropsUnattributed },
                { "reasons", cap.Reasons.OrderByDescending(kv => kv.Value).Take(10).ToDictionary(kv => kv.Key.Length > 60 ? kv.Key.Substring(0, 60) : kv.Key, kv => (object)kv.Value) },
                { "rows", rows.ToArray() }
            };
            var json = new JavaScriptSerializer();
            while (rows.Count > 0 && json.Serialize(d).Length > 20000)
            {
                rows.RemoveRange(Math.Max(0, rows.Count - Math.Max(1, rows.Count / 5)), Math.Max(1, rows.Count / 5));
                d["rows"] = rows.ToArray();
                d["truncated"] = true;
            }
            return d;
        }

        // ---------------------------------------------------------------- recording (needs administrator)

        // Records every IPv4 packet for the given seconds; tick(secondsLeft) once a second. pktmon is always stopped and
        // its filter removed at the end, even on error. owners gets "UDP 5000" -> process name for every local port seen.
        public static Capture Record(int seconds, Action<int> tick, Dictionary<string, string> owners)
        {
            string dir = Path.Combine(Storage.Dir, "capture");
            Directory.CreateDirectory(dir);
            string etl = Path.Combine(dir, "locktest.etl"), txt = Path.Combine(dir, "locktest.txt");
            try
            {
                if (File.Exists(txt)) File.Delete(txt);
                if (File.Exists(etl)) File.Delete(etl);
                Snapshot(owners);
                Detector.Run("pktmon", "filter remove");
                try
                {
                    Detector.Run("pktmon", "filter add 2XPingLockTest -d IPv4");
                    // all components (drops happen inside the stack, not on the network card), flows and drops
                    string started = Detector.Run("pktmon", "start --capture --type all --pkt-size 96 --file-size 400 --file-name \"" + etl + "\"");
                    if (started.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0) throw new Exception("pktmon: " + started.Trim());
                    for (int left = seconds; left > 0; left--)
                    {
                        tick(left);
                        Thread.Sleep(1000);
                        if (left % 10 == 0) Snapshot(owners); // short TCP connections and sockets opened while queueing
                    }
                    tick(0);
                }
                finally
                {
                    Detector.Run("pktmon", "stop");
                    Detector.Run("pktmon", "filter remove");
                }
                Snapshot(owners);
                Detector.Run("pktmon", "etl2txt \"" + etl + "\" --out \"" + txt + "\"");
                if (!File.Exists(txt)) throw new Exception("pktmon produced no output.");
                return Parse(File.ReadLines(txt), LocalIps());
            }
            finally
            {
                try { File.Delete(etl); File.Delete(txt); } catch { }
            }
        }

        static void Snapshot(Dictionary<string, string> owners)
        {
            try
            {
                foreach (var kv in ParseNetstat(Detector.Run("netstat", "-ano")))
                {
                    if (owners.ContainsKey(kv.Key)) continue;
                    try { using (var p = Process.GetProcessById(kv.Value)) owners[kv.Key] = p.ProcessName; }
                    catch { owners[kv.Key] = "pid " + kv.Value; }
                }
            }
            catch { }
        }

        static HashSet<string> LocalIps()
        {
            var set = new HashSet<string>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                    foreach (var a in ni.GetIPProperties().UnicastAddresses)
                        if (a.Address.AddressFamily == AddressFamily.InterNetwork) set.Add(a.Address.ToString());
            }
            catch { }
            return set;
        }
    }
}
