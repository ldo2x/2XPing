using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Xml;

namespace PingBoost
{
    // Developer tool: records the PC's traffic for a minute (pktmon: UDP, ICMP and TCP connection packets, drops
    // included) while the player starts the game and queues, then reads what Windows Firewall refused in that time
    // (WFP net events). It says which datacenters the traffic went to, which of them the lock blocks, whether the
    // lock's rule covered that traffic (protocol, program, port), and whether the game TRIED a blocked datacenter.
    static class LockTest
    {
        // One remote address and protocol.
        public class Row
        {
            public string Ip, Proto;
            public bool V6;
            public int Out, In, DropOut, DropIn;
            public int Wfp, WfpOurs;  // attempts Windows Firewall refused (WFP net events), and how many by the 2X Ping rule
            public SortedSet<int> RemotePorts = new SortedSet<int>();
            public SortedSet<int> LocalPorts = new SortedSet<int>();
            public SortedSet<string> WfpApps = new SortedSet<string>();
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
            public int V6Packets;          // of which IPv6
            public int DropEvents;         // "Drop:" snapshots, any address
            public int DropsUnattributed;  // drop snapshots without a readable packet line
            public bool HadIds;            // PktGroupId/PktNumber seen: each packet counted once across components
            public Dictionary<string, int> Reasons = new Dictionary<string, int>();
            public long EtlBytes;          // size of the capture file
            public bool Wrapped;           // the capture hit its size cap: the oldest packets were overwritten
            public bool WfpRead;           // netsh wfp show netevents produced a file
            public string WfpNote;         // why it didn't
            public int WfpTotal, WfpOurs;  // firewall drops in the test window (all addresses), and by the 2X Ping rule
        }

        class Seen { public Row Row; public bool Out, Dropped; }

        // "[Microsoft-Windows-PktMon] Drop: PktGroupId 8444249301423149, PktNumber 1, Appearance 0, Direction Rx , ..."
        static readonly Regex Header = new Regex(@"(Drop:\s*)?PktGroupId\s+(\d+),\s*PktNumber\s+(\d+)", RegexOptions.Compiled);
        static readonly Regex Reason = new Regex(@"DropReason\s+(.+?)\s*,\s*(?:DropLocation|OriginalSize|$)", RegexOptions.Compiled);
        // "10.0.0.5.52012 > 13.107.4.50.443: Flags [S]", "ip: 1.2.3.4.5 > 6.7.8.9.10: UDP, length 3", "1.2.3.4 > 5.6.7.8: ICMP echo request"
        static readonly Regex Packet = new Regex(
            @"(?<![\d.])(\d{1,3}(?:\.\d{1,3}){3})(?:\.(\d{1,5}))? > (\d{1,3}(?:\.\d{1,3}){3})(?:\.(\d{1,5}))?:\s*(.*)$", RegexOptions.Compiled);
        // "2001:db8::5.50000 > 2606:4700::1.443: UDP, length 3", "fe80::1 > ff02::1: ICMP6, ..."
        static readonly Regex Packet6 = new Regex(
            @"(?<![0-9A-Fa-f:.])([0-9A-Fa-f]{0,4}(?::[0-9A-Fa-f]{0,4}){2,7})(?:\.(\d{1,5}))? > ([0-9A-Fa-f]{0,4}(?::[0-9A-Fa-f]{0,4}){2,7})(?:\.(\d{1,5}))?:\s*(.*)$",
            RegexOptions.Compiled);

        // Ethernet addresses written with colons ("aa:bb:cc:dd:ee:ff", or its first five groups when the pattern backs off):
        // every group exactly two hex digits. No real IPv6 address in a capture looks like that.
        static readonly Regex MacLike = new Regex(@"^[0-9A-Fa-f]{2}(?::[0-9A-Fa-f]{2}){4,5}$", RegexOptions.Compiled);

        // The IPv6 packet on a line, skipping a colon-form MAC pair in front of it
        // ("aa:bb:cc:dd:ee:ff > 11:22:33:44:55:66, ethertype IPv6 (0x86dd), length 86: 2001:db8::5.500 > ...").
        static Match Match6(string line)
        {
            var m = Packet6.Match(line);
            while (m.Success && (MacLike.IsMatch(m.Groups[1].Value) || MacLike.IsMatch(m.Groups[3].Value)))
                m = Packet6.Match(line, m.Groups[3].Index + m.Groups[3].Length);
            return m;
        }

        // Reads pktmon etl2txt output. Every snapshot is a header line (timestamp, PktGroupId, PktNumber, "Drop:" when
        // the packet was dropped there, DropReason) followed by the parsed packet (tcpdump style). A packet shows up once
        // per component it crosses: snapshots with the same PktGroupId + PktNumber are one packet, counted once, and it
        // counts as dropped if any of its snapshots is a drop. Only traffic between this PC (localIps, or a private
        // address) and a public address is kept, IPv4 and IPv6. Lines without headers (older formats) count one packet each.
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
                bool v6 = false;
                var m = Packet.Match(line);
                if (!m.Success) { m = Match6(line); v6 = m.Success; }
                if (!m.Success) continue;
                string snapKey = sawHeader ? key : "line" + n;
                bool snapDrop = sawHeader ? drop : line.TrimStart().StartsWith("Drop", StringComparison.OrdinalIgnoreCase);
                string snapReason = sawHeader ? reason : (snapDrop ? "unknown" : null);
                if (!sawHeader && snapDrop) { cap.DropEvents++; Bump(cap.Reasons, snapReason); }
                used = true;

                string a = m.Groups[1].Value, b = m.Groups[3].Value;
                bool aLocal = IsLocal(a, localIps), bLocal = IsLocal(b, localIps);
                string remote; int rport, lport; bool isOut;
                if (aLocal && !bLocal) { remote = b; isOut = true; rport = Port(m.Groups[4]); lport = Port(m.Groups[2]); }
                else if (bLocal && !aLocal) { remote = a; isOut = false; rport = Port(m.Groups[2]); lport = Port(m.Groups[4]); }
                else continue;
                string proto = Proto(m.Groups[5].Value);

                Seen s;
                if (!seen.TryGetValue(snapKey, out s))
                {
                    var row = RowOf(cap, remote, proto);
                    row.V6 = v6;
                    s = new Seen { Row = row, Out = isOut };
                    seen[snapKey] = s;
                    cap.Packets++;
                    if (v6) cap.V6Packets++;
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

        static Row RowOf(Capture cap, string ip, string proto)
        {
            Row row;
            string rk = ip + " " + proto;
            if (!cap.Rows.TryGetValue(rk, out row)) cap.Rows[rk] = row = new Row { Ip = ip, Proto = proto, V6 = ip.Contains(":") };
            return row;
        }

        // This PC's side of a packet: one of its own addresses, or anything that isn't a public internet address.
        static bool IsLocal(string ip, ICollection<string> localIps)
        {
            if (localIps != null && localIps.Contains(ip)) return true;
            return ip.Contains(":") ? !IsPublic6(ip) : !Detector.IsPublic(ip);
        }

        // Global unicast IPv6 (not link-local fe80::/10, unique-local fc00::/7, multicast ff00::/8, loopback or unspecified).
        public static bool IsPublic6(string ip)
        {
            string s = ip.ToLowerInvariant();
            if (s == "::1" || s == "::" || s.StartsWith("ff") || s.StartsWith("fc") || s.StartsWith("fd")) return false;
            if (s.StartsWith("fe8") || s.StartsWith("fe9") || s.StartsWith("fea") || s.StartsWith("feb")) return false;
            return s.Contains(":");
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

        // ---------------------------------------------------------------- what Windows Firewall refused (WFP net events)

        public class WfpDrop
        {
            public string Ip, Proto, App, FilterId, Direction;
            public int RemotePort, LocalPort;
            public DateTime At;
        }

        static XmlElement Child(XmlNode n, string name)
        {
            if (n == null) return null;
            foreach (XmlNode c in n.ChildNodes)
                if (c.NodeType == XmlNodeType.Element && c.Name == name) return (XmlElement)c;
            return null;
        }

        static string Text(XmlNode n, string name)
        {
            var c = Child(n, name);
            return c != null ? c.InnerText.Trim() : null;
        }

        static string ProtoName(string number)
        {
            switch (number)
            {
                case "6": return "TCP";
                case "17": return "UDP";
                case "1": case "58": return "ICMP";
                default: return "IP";
            }
        }

        // "netsh wfp show netevents" XML -> the classify-drop events (a filter refused the connection or packet) since sinceUtc.
        // Each event: <item><header><timeStamp/><ipProtocol/><localAddrV4/><remoteAddrV4/><localPort/><remotePort/>
        // <appId><asString/></appId>...</header><type>FWPM_NET_EVENT_TYPE_CLASSIFY_DROP</type><classifyDrop><filterId/>
        // <msFwpDirection/>...</classifyDrop></item>
        public static List<WfpDrop> ParseNetEvents(string xml, DateTime sinceUtc)
        {
            var list = new List<WfpDrop>();
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            foreach (XmlNode item in doc.GetElementsByTagName("item"))
            {
                var header = Child(item, "header");
                string type = Text(item, "type");
                if (header == null || type == null || type.IndexOf("CLASSIFY_DROP", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var d = new WfpDrop
                {
                    Ip = Text(header, "remoteAddrV4") ?? Text(header, "remoteAddrV6"),
                    Proto = ProtoName(Text(header, "ipProtocol") ?? ""),
                };
                if (string.IsNullOrEmpty(d.Ip)) continue;
                int p;
                if (int.TryParse(Text(header, "remotePort"), out p)) d.RemotePort = p;
                if (int.TryParse(Text(header, "localPort"), out p)) d.LocalPort = p;
                string app = AppPath(Text(Child(header, "appId"), "asString"));
                if (!string.IsNullOrEmpty(app)) d.App = Path.GetFileName(app).ToLowerInvariant();
                var cd = Child(item, "classifyDrop");
                d.FilterId = Text(cd, "filterId");
                d.Direction = Text(cd, "msFwpDirection");
                DateTime at;
                if (DateTime.TryParse(Text(header, "timeStamp"), CultureInfo.InvariantCulture,
                                      DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out at))
                {
                    d.At = at;
                    if (at < sinceUtc) continue;
                }
                list.Add(d);
            }
            return list;
        }

        // The app path of a net event. Some Windows versions print the UTF-16 bytes with a dot for each zero byte
        // ("\.d.e.v.i.c.e.\.h.a.r.d...") and a trailing "." for the terminating zero.
        public static string AppPath(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Trim();
            int odd = 0, dots = 0;
            for (int i = 1; i < s.Length; i += 2) { odd++; if (s[i] == '.') dots++; }
            if (s.Length >= 6 && dots * 10 >= odd * 9)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < s.Length; i += 2) sb.Append(s[i]);
                s = sb.ToString();
            }
            return s.TrimEnd('.', '\0', ' ');
        }

        // "netsh wfp show filters" XML -> ids of the filters whose display name is the given firewall rule name.
        public static HashSet<string> ParseFilterIds(string xml, string ruleName)
        {
            var ids = new HashSet<string>();
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            foreach (XmlNode item in doc.GetElementsByTagName("item"))
            {
                string id = Text(item, "filterId"), name = Text(Child(item, "displayData"), "name");
                if (id != null && name != null && string.Equals(name, ruleName, StringComparison.OrdinalIgnoreCase)) ids.Add(id);
            }
            return ids;
        }

        // Adds the firewall's refusals to the capture, per remote address and protocol.
        public static void AddWfp(Capture cap, IEnumerable<WfpDrop> drops, ICollection<string> ourFilterIds)
        {
            cap.WfpRead = true;
            foreach (var d in drops)
            {
                bool pub = d.Ip.Contains(":") ? IsPublic6(d.Ip) : Detector.IsPublic(d.Ip);
                if (!pub) continue;
                bool ours = d.FilterId != null && ourFilterIds != null && ourFilterIds.Contains(d.FilterId);
                cap.WfpTotal++;
                if (ours) cap.WfpOurs++;
                var row = RowOf(cap, d.Ip, d.Proto);
                row.Wfp++;
                if (ours) row.WfpOurs++;
                if (d.App != null) row.WfpApps.Add(d.App);
                if (d.RemotePort > 0) row.RemotePorts.Add(d.RemotePort);
                if (d.LocalPort > 0) row.LocalPorts.Add(d.LocalPort);
            }
        }

        // ---------------------------------------------------------------- the lock being tested

        public class LockInfo
        {
            public bool Active;
            public string Mode = "none";  // "full", "game-udp", "valve-all", "none (...)"
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
            public string Who { get { return Game ? "game" : Owner == "?" ? "process unknown" : Owner; } }
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

        class Span { public uint Lo, Hi; }

        // "a.b.c.d/n", "a.b.c.d" or "a.b.c.d-e.f.g.h" (the form IpLists.Subtract and Windows Firewall use) as lo..hi.
        static List<Span> Spans(IEnumerable<string> cidrs)
        {
            var list = new List<Span>();
            if (cidrs == null) return list;
            foreach (var c in cidrs)
            {
                try
                {
                    int dash = c.IndexOf('-');
                    if (dash > 0)
                    {
                        list.Add(new Span { Lo = ToUInt(c.Substring(0, dash)), Hi = ToUInt(c.Substring(dash + 1)) });
                        continue;
                    }
                    var parts = c.Split('/');
                    int bits = parts.Length > 1 ? int.Parse(parts[1]) : 32;
                    uint mask = bits == 0 ? 0 : uint.MaxValue << (32 - bits);
                    uint lo = ToUInt(parts[0]) & mask;
                    list.Add(new Span { Lo = lo, Hi = lo | ~mask });
                }
                catch { }
            }
            return list;
        }

        static uint ToUInt(string ip)
        {
            var b = ip.Trim().Split('.').Select(uint.Parse).ToArray();
            if (b.Length != 4) throw new FormatException(ip);
            return (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
        }

        static bool InAny(string ip, List<Span> spans)
        {
            if (ip.Contains(":")) return false; // the lock's ranges are IPv4
            uint a = ToUInt(ip);
            return spans.Any(x => a >= x.Lo && a <= x.Hi);
        }

        // Is this address inside the lock's block list (ranges as IpLists.Subtract returns them)?
        public static bool Blocks(IEnumerable<string> block, string ip)
        {
            return InAny(ip, Spans(block));
        }

        // Each address with its datacenter, whether the lock blocks it, and whether the rule's scope covers the traffic.
        public static List<Line> Classify(Game g, Capture cap, LockInfo info, Dictionary<string, string> owners)
        {
            var block = Spans(info.Block);
            var allow = Spans(info.Allow);
            var picked = new HashSet<string>(info.Picked ?? new List<string>());
            var gameNames = new HashSet<string>(g.Processes.Select(p => p.ToLowerInvariant()));
            var lines = new List<Line>();
            foreach (var row in cap.Rows.Values.OrderByDescending(r => r.Total + r.Wfp))
            {
                var l = new Line { Row = row };
                var dc = row.V6 ? null : g.Regions.FirstOrDefault(r => r.Cidrs.Any(c => Detector.InCidr(row.Ip, c)));
                if (dc != null) l.Dc = dc.Id;
                var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                if (owners != null)
                    foreach (var p in row.LocalPorts)
                    {
                        string o;
                        if (owners.TryGetValue((row.Proto == "TCP" ? "TCP " : "UDP ") + p, out o)) names.Add(o);
                    }
                foreach (var app in row.WfpApps) names.Add(Path.GetFileNameWithoutExtension(app));
                if (names.Count > 0) l.Owner = string.Join("+", names);
                l.Game = names.Any(x => gameNames.Contains(x.ToLowerInvariant()));
                bool allowed = InAny(row.Ip, allow);
                l.InBlock = info.Block != null && InAny(row.Ip, block) && !allowed;
                if (row.V6) l.Status = "IPv6";
                else if (l.InBlock) l.Status = "BLOCKED";
                else if (allowed) l.Status = "allowed";
                else if (dc != null && picked.Contains(dc.Id)) l.Status = "picked";
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

        // ---------------------------------------------------------------- the result

        public const string Procedure =
            "How to use: run it once UNLOCKED (baseline: which datacenters does the game use?), then lock, fully restart the game,\r\n" +
            "and run it again while queueing. Compare the DATACENTER column and the TRIED / LEAK / held findings of both runs.";

        // Header and findings: what the developer console shows.
        public static string Short(Game g, Capture cap, LockInfo info, List<Line> lines, int seconds, DateTime at)
        {
            var sb = new StringBuilder();
            sb.AppendLine("2X Ping lock test  " + at.ToString("yyyy-MM-dd HH:mm:ss") + "  " + Brand.DisplayVersion);
            sb.AppendLine("Game: " + g.Name + "   recorded " + seconds + " s");
            sb.AppendLine("Lock: " + (info.Active ? "ON" : "off") + "   mode: " + info.Mode);
            sb.AppendLine("Picked: " + (info.Picked.Count > 0 ? string.Join(", ", info.Picked) : "-"));
            sb.AppendLine("Rule scope: " + info.Scope);
            if (info.Allow.Count > 0) sb.AppendLine("Never blocked: " + string.Join(", ", info.Allow.Take(10)) + (info.Allow.Count > 10 ? " ..." : ""));
            sb.AppendLine();
            sb.AppendLine("Packets: " + cap.Packets + " public (" + cap.V6Packets + " IPv6)   snapshots read: " + cap.Snapshots +
                          "   drop snapshots: " + cap.DropEvents + (cap.DropsUnattributed > 0 ? " (" + cap.DropsUnattributed + " without a readable packet)" : "") +
                          "   capture file: " + (cap.EtlBytes / (1024 * 1024)) + " MB");
            sb.AppendLine("Recorded: UDP, ICMP and TCP connection packets (SYN/RST, not TCP data), so TCP counts are attempts and answers.");
            if (cap.Wrapped)
                sb.AppendLine("CAPTURE WRAPPED: the capture hit its size cap and the oldest packets (the game start) were overwritten. Run it again with less traffic (pause downloads).");
            if (cap.Reasons.Count > 0)
                sb.AppendLine("Drop reasons: " + string.Join("; ", cap.Reasons.OrderByDescending(kv => kv.Value).Select(kv =>
                    kv.Key + " x" + kv.Value + (FirewallReason(kv.Key) ? " [firewall]" : ""))));
            if (!cap.HadIds)
                sb.AppendLine("NOTE: no PktGroupId/PktNumber in the log: a packet seen by several components may be counted more than once.");
            if (!cap.WfpRead)
                sb.AppendLine("Windows Firewall drops (WFP net events): COULDN'T READ them" + (cap.WfpNote != null ? " (" + cap.WfpNote + ")" : "") +
                              ", so attempts the firewall refused are unknown.");
            else if (cap.WfpTotal == 0)
                sb.AppendLine("Windows Firewall drops (WFP net events): NONE in the test window. Either nothing was refused, or Windows isn't keeping " +
                              "drop events: this does NOT show that the game didn't try a blocked datacenter.");
            else
                sb.AppendLine("Windows Firewall drops (WFP net events): " + cap.WfpTotal + " in the test window, " + cap.WfpOurs + " by the 2X Ping rule.");
            sb.AppendLine("NOTE: a send the firewall refuses never becomes a packet, so pktmon can't see it; the WFP column and TRIED lines cover that.");
            sb.AppendLine("      OUT vs IN: a blocked server shows OUT>0 IN=0 (or IN all dropped); one that answers was NOT blocked for that traffic.");
            sb.AppendLine();
            sb.AppendLine("Findings:");
            int findings = 0;
            foreach (var l in lines.Where(x => x.InBlock && x.Row.Wfp > 0))
            {
                findings++;
                sb.AppendLine("  TRIED " + l.Row.Ip + " (" + l.Dc + ", " + l.Row.Proto + "): " + (l.Game ? "the game" : l.Who) + " tried this blocked datacenter " +
                              l.Row.Wfp + " times and Windows Firewall refused it" + (l.Row.WfpOurs > 0 ? " (" + l.Row.WfpOurs + " by the 2X Ping rule)" : ""));
            }
            foreach (var l in lines.Where(x => x.InBlock && x.Row.InPassed > 0))
            {
                findings++;
                sb.AppendLine("  LEAK  " + l.Row.Ip + " (" + l.Dc + ", " + l.Row.Proto + ", " + l.Who + ") answered " + l.Row.InPassed +
                              " times although its datacenter is blocked" + (l.Covered ? "" : ": the rule doesn't cover this traffic (" + Why(l, info) + ")"));
            }
            foreach (var l in lines.Where(x => x.InBlock && x.Row.InPassed == 0 && x.Row.Total > 0))
            {
                findings++;
                sb.AppendLine("  held  " + l.Row.Ip + " (" + l.Dc + ", " + l.Row.Proto + "): " + l.Row.Out + " sent, no answer got through" +
                              (l.Row.Dropped > 0 ? ", " + l.Row.Dropped + " dropped" : ""));
            }
            var v6 = lines.Where(x => x.Row.V6 && x.Row.Total > 0).GroupBy(x => x.Who).OrderByDescending(x => x.Sum(y => y.Row.Total)).ToList();
            if (v6.Count > 0)
            {
                findings++;
                sb.AppendLine("  IPv6  packets by process: " + string.Join(", ", v6.Select(x => x.Key + " " + x.Sum(y => y.Row.Total))) +
                              (v6.Any(x => x.Key == "game") ? "   THE GAME USED IPv6: the lock only covers IPv4." : ""));
            }
            var gameDcs = lines.Where(x => x.Game && x.Dc != "-").Select(x => x.Dc + (x.InBlock ? "(blocked)" : "")).Distinct().ToList();
            if (gameDcs.Count > 0) { findings++; sb.AppendLine("  The game talked to datacenters: " + string.Join(", ", gameDcs)); }
            var seenDcs = lines.Where(x => !x.Game && x.Dc != "-").Select(x => x.Dc + (x.InBlock ? "(blocked)" : "")).Distinct().ToList();
            if (seenDcs.Count > 0) { findings++; sb.AppendLine("  Other processes (or unknown) talked to datacenters: " + string.Join(", ", seenDcs)); }
            if (info.Block == null)
                sb.AppendLine("  Unlocked baseline: nothing is blocked, so this run only shows which datacenters the game uses. Now lock, restart the game and run it again.");
            else if (!lines.Any(x => x.InBlock))
                sb.AppendLine("  No traffic to or from blocked datacenters was captured" +
                              (cap.WfpRead && cap.WfpTotal > 0 ? " and the firewall refused nothing there." : "; that alone doesn't show the game didn't try (see the firewall line above)."));
            else if (findings == 0)
                sb.AppendLine("  nothing to report");
            sb.AppendLine();
            sb.AppendLine(Procedure);
            return sb.ToString();
        }

        // The full text saved to locktest-*.txt: Short() plus the table of every address.
        public static string Report(Game g, Capture cap, LockInfo info, List<Line> lines, int seconds, DateTime at)
        {
            var sb = new StringBuilder(Short(g, cap, info, lines, seconds, at));
            sb.AppendLine();
            const string F = "{0,-16} {1,-5} {2,-18} {3,6} {4,6} {5,6} {6,6}  {7,-10} {8,-10} {9,-5} {10}";
            sb.AppendLine(string.Format(F, "ADDRESS", "PROTO", "REMOTE PORTS", "OUT", "IN", "DROP", "WFP", "DATACENTER", "STATUS", "RULE", "PROCESS"));
            foreach (var l in lines)
                sb.AppendLine(string.Format(F, l.Row.Ip, l.Row.Proto, Ports(l.Row.RemotePorts, 18), l.Row.Out, l.Row.In, l.Row.Dropped,
                    l.Row.Wfp + (l.Row.WfpOurs > 0 ? "*" : ""), l.Dc, l.Status, l.InBlock ? (l.Covered ? "hits" : "MISS") : "-",
                    (l.Game ? "GAME " : "") + (l.Owner == "?" ? "unknown" : l.Owner)));
            sb.AppendLine("(WFP = attempts Windows Firewall refused, * = some by the 2X Ping rule. RULE: hits = the lock's rule covers this traffic, MISS = it doesn't.)");
            return sb.ToString();
        }

        static string Why(Line l, LockInfo info)
        {
            var r = new List<string>();
            if (info.Protocol != null && !string.Equals(info.Protocol, l.Row.Proto, StringComparison.OrdinalIgnoreCase)) r.Add("rule is " + info.Protocol + " only");
            if (info.Program != null && !l.Game) r.Add(l.Owner == "?" ? "process unknown" : "another process");
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
            int blocked = lines.Count(l => l.InBlock), leaks = lines.Count(l => l.InBlock && l.Row.InPassed > 0),
                tried = lines.Where(l => l.InBlock).Sum(l => l.Row.Wfp);
            return g.Name + ", mode " + info.Mode + ", " + lines.Count + " addresses, " + blocked + " in blocked datacenters, " +
                   leaks + " answered, tried blocked " + tried + (cap.WfpRead ? "" : " (WFP unreadable)") + ", drops " + cap.DropEvents + " (" +
                   cap.Reasons.Where(kv => FirewallReason(kv.Key)).Sum(kv => kv.Value) + " firewall)" + (cap.Wrapped ? ", WRAPPED" : "");
        }

        public const int DataLimit = 12000; // JSON text; the server drops data whose jsonb is 32000 bytes or more

        // The structured part of the telemetry event: one compact array per address. Rows that matter go first
        // (blocked datacenters, then any datacenter, then the game's own); unknown non-game rows are dropped first.
        public static Dictionary<string, object> Data(Capture cap, LockInfo info, List<Line> lines, int seconds)
        {
            var rows = lines
                .OrderByDescending(l => l.InBlock).ThenByDescending(l => l.Dc != "-").ThenByDescending(l => l.Game)
                .ThenByDescending(l => l.Row.Total + l.Row.Wfp)
                .Select(l => (object)new object[]
                {
                    l.Row.Ip, l.Row.Proto, Ports(l.Row.RemotePorts, 40), l.Row.Out, l.Row.In, l.Row.Dropped, l.Row.Wfp,
                    l.Dc, l.Status, l.InBlock ? (l.Covered ? 1 : 0) : -1, l.Game ? 1 : 0, l.Owner.Length > 30 ? l.Owner.Substring(0, 30) : l.Owner
                }).ToList();
            var d = new Dictionary<string, object>
            {
                { "cols", "ip,proto,ports,out,in,drop,wfp,dc,status,rule(1 hits/0 miss/-1 not blocked),game,process" },
                { "mode", info.Mode }, { "active", info.Active }, { "scope", info.Scope }, { "picked", info.Picked.ToArray() },
                { "seconds", seconds }, { "packets", cap.Packets }, { "v6", cap.V6Packets }, { "drops", cap.DropEvents },
                { "drops_unattributed", cap.DropsUnattributed }, { "wrapped", cap.Wrapped }, { "etl_mb", cap.EtlBytes / (1024 * 1024) },
                { "wfp_read", cap.WfpRead }, { "wfp", cap.WfpTotal }, { "wfp_ours", cap.WfpOurs },
                { "reasons", cap.Reasons.OrderByDescending(kv => kv.Value).Take(10).ToDictionary(kv => kv.Key.Length > 60 ? kv.Key.Substring(0, 60) : kv.Key, kv => (object)kv.Value) },
                { "rows", rows.ToArray() }
            };
            var json = new JavaScriptSerializer();
            while (rows.Count > 0 && json.Serialize(d).Length > DataLimit)
            {
                int cut = Math.Max(1, rows.Count / 10);
                rows.RemoveRange(rows.Count - cut, cut);
                d["rows"] = rows.ToArray();
                d["truncated"] = true;
            }
            return d;
        }

        // ---------------------------------------------------------------- recording (needs administrator)

        const int FileSizeMb = 512;

        // Records the given seconds; tick(secondsLeft) once a second. pktmon is always stopped and its filters removed
        // at the end, even on error. owners gets "UDP 5000" -> process name for every local port seen. Then reads
        // (only reads) the firewall's drop events for the same time.
        public static Capture Record(int seconds, Action<int> tick, Dictionary<string, string> owners)
        {
            string dir = Path.Combine(Storage.Dir, "capture");
            Directory.CreateDirectory(dir);
            string etl = Path.Combine(dir, "locktest.etl"), txt = Path.Combine(dir, "locktest.txt"),
                   events = Path.Combine(dir, "locktest-netevents.xml"), filters = Path.Combine(dir, "locktest-filters.xml");
            var files = new[] { etl, txt, events, filters };
            DateTime startUtc = DateTime.UtcNow;
            try
            {
                foreach (var f in files) if (File.Exists(f)) File.Delete(f);
                Snapshot(owners);
                Detector.Run("pktmon", "filter remove");
                try
                {
                    // layer-4 filters keep the volume down (no TCP data, which downloads flood) without a layer-2 filter
                    // that could hide drop snapshots: UDP and ICMP (v4 and v6), TCP connection packets only
                    Detector.Run("pktmon", "filter add 2XPingLT-UDP -t UDP");
                    Detector.Run("pktmon", "filter add 2XPingLT-ICMP -t ICMP");
                    Detector.Run("pktmon", "filter add 2XPingLT-ICMP6 -t ICMPv6");
                    Detector.Run("pktmon", "filter add 2XPingLT-SYN -t TCP SYN");
                    Detector.Run("pktmon", "filter add 2XPingLT-RST -t TCP RST");
                    // all components (drops happen inside the stack, not on the network card), flows and drops
                    string started = Detector.Run("pktmon", "start --capture --type all --pkt-size 80 --file-size " + FileSizeMb + " --file-name \"" + etl + "\"");
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
                long etlBytes = File.Exists(etl) ? new FileInfo(etl).Length : 0;
                Detector.Run("pktmon", "etl2txt \"" + etl + "\" --out \"" + txt + "\"");
                if (!File.Exists(txt)) throw new Exception("pktmon produced no output.");
                var cap = Parse(File.ReadLines(txt), LocalIps());
                cap.EtlBytes = etlBytes;
                cap.Wrapped = etlBytes >= (long)FileSizeMb * 1024 * 1024 * 95 / 100;
                ReadFirewall(cap, events, filters, startUtc, (int)(DateTime.UtcNow - startUtc).TotalSeconds + 30);
                return cap;
            }
            finally
            {
                foreach (var f in files) try { File.Delete(f); } catch { }
            }
        }

        // netsh wfp show netevents / filters only read; they change nothing.
        static void ReadFirewall(Capture cap, string events, string filters, DateTime startUtc, int window)
        {
            try
            {
                string o = Detector.Run("netsh", "wfp show netevents file=\"" + events + "\" timewindow=" + window);
                if (!File.Exists(events)) o = Detector.Run("netsh", "wfp show netevents file=\"" + events + "\"");
                if (!File.Exists(events)) { cap.WfpNote = "netsh: " + o.Trim(); return; }
                var drops = ParseNetEvents(File.ReadAllText(events), startUtc.AddSeconds(-5));
                HashSet<string> ours = null;
                if (drops.Count > 0)
                    try
                    {
                        Detector.Run("netsh", "wfp show filters file=\"" + filters + "\"");
                        if (File.Exists(filters)) ours = ParseFilterIds(File.ReadAllText(filters), "2XPing-RegionLock");
                    }
                    catch { }
                AddWfp(cap, drops, ours);
            }
            catch (Exception ex) { cap.WfpRead = false; cap.WfpNote = ex.Message; }
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
                    {
                        string s = a.Address.ToString();
                        int pct = s.IndexOf('%');
                        set.Add(pct > 0 ? s.Substring(0, pct) : s);
                    }
            }
            catch { }
            return set;
        }
    }
}
