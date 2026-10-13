using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;

namespace PingBoost
{
    // The lock test's pktmon parser and report. Sample lines follow pktmon etl2txt's documented text format:
    // a header line per snapshot ("[Microsoft-Windows-PktMon] [Drop: ]PktGroupId .., PktNumber .., Appearance ..,
    // Direction .., Type .., Component .., ..[, DropReason .., DropLocation ..], OriginalSize .., LoggedSize ..")
    // followed by the packet in tcpdump style (the drop sample is Microsoft's own, from "Diagnose packet loss").
    static partial class Tests
    {
        const string Me = "192.168.1.10";

        static string Hdr(long group, int num, int appearance, string dir, string type, int comp)
        {
            return "[2]0000.0000::2026-10-13 12:00:00.000000000 [Microsoft-Windows-PktMon] PktGroupId " + group + ", PktNumber " + num +
                   ", Appearance " + appearance + ", Direction " + dir + " , Type " + type + " , Component " + comp + ", Edge 1, Filter 1, OriginalSize 74, LoggedSize 74";
        }

        static string DropHdr(long group, int num, string dir, string reason)
        {
            return "[30]0000.0000::2026-10-13 12:00:01.000000000 [Microsoft-Windows-PktMon] Drop: PktGroupId " + group + ", PktNumber " + num +
                   ", Appearance 0, Direction " + dir + " , Type IP , Component 49, Filter 1, DropReason " + reason +
                   " , DropLocation 0xE000460A, OriginalSize 402, LoggedSize 148";
        }

        static List<string> SampleLog()
        {
            return new List<string>
            {
                // one UDP packet to Chicago, seen by two components: counted once
                Hdr(1001, 1, 1, "Tx", "IP", 49),
                "\tip: " + Me + ".50000 > 137.221.69.97.26503: UDP, length 46",
                Hdr(1001, 1, 2, "Tx", "Ethernet", 13),
                "\tAA-BB-CC-DD-EE-01 > AA-BB-CC-DD-EE-02, ethertype IPv4 (0x0800), length 88: " + Me + ".50000 > 137.221.69.97.26503: UDP, length 46",
                // a second UDP packet out
                Hdr(1002, 1, 1, "Tx", "IP", 49),
                "\tip: " + Me + ".50000 > 137.221.69.97.26503: UDP, length 46",
                // Chicago answers over TCP (the UDP-only rule doesn't cover it)
                Hdr(1003, 1, 1, "Tx", "IP", 49),
                "\tip: " + Me + ".51234 > 137.221.69.97.1119: Flags [S], seq 1, win 64240, length 0",
                Hdr(1004, 1, 1, "Rx", "Ethernet", 13),
                "\tAA-BB-CC-DD-EE-02 > AA-BB-CC-DD-EE-01, ethertype IPv4 (0x0800), length 66: 137.221.69.97.1119 > " + Me + ".51234: Flags [S.], seq 9, ack 2, win 65535, length 0",
                // a UDP answer reaches the network card, then the firewall drops it: one packet, in, dropped
                Hdr(1005, 1, 1, "Rx", "Ethernet", 13),
                "\tAA-BB-CC-DD-EE-02 > AA-BB-CC-DD-EE-01, ethertype IPv4 (0x0800), length 88: 137.221.69.97.26503 > " + Me + ".50000: UDP, length 46",
                DropHdr(1005, 1, "Rx", "WFP: Filtered by firewall rule"),
                "       Drop: ip: 137.221.69.97.26503 > " + Me + ".50000: UDP, length 46",
                // ICMP to Chicago
                Hdr(1006, 1, 1, "Tx", "IP", 49),
                "\tip: " + Me + " > 137.221.69.97: ICMP echo request, id 1, seq 5, length 40",
                // Singapore (picked), both ways
                Hdr(1007, 1, 1, "Tx", "IP", 49),
                "\tip: " + Me + ".50000 > 34.87.10.20.26600: UDP, length 46",
                Hdr(1008, 1, 1, "Rx", "IP", 49),
                "\tip: 34.87.10.20.26600 > " + Me + ".50000: UDP, length 46",
                // LAN broadcast: never counted
                Hdr(1009, 1, 1, "Tx", "IP", 49),
                "\tip: " + Me + ".5353 > 192.168.1.255.5353: UDP, length 30",
                // Microsoft's documented drop sample (LAN only: a drop event, but no public row)
                "[30]0000.0000::<DateTime> [Microsoft-Windows-PktMon] Drop: PktGroupId 8444249301423149, PktNumber 1, Appearance 0, Direction Rx , Type IP , Component 49, Filter 1, DropReason INET: transport endpoint was not found , DropLocation 0xE000460A, OriginalSize 402, LoggedSize 148",
                "       Drop: ip: 192.168.5.88.50005 > 192.168.5.68.50005: UDP, length 374",
                // a drop snapshot without a packet line
                DropHdr(1010, 1, "Tx", "WFP: Filtered by firewall rule"),
                // encapsulated packet: only the outer line counts
                Hdr(1011, 1, 1, "Rx", "IP", 49),
                "\tip: 20.1.1.1.4789 > " + Me + ".4789: UDP, length 100",
                "\tip: 10.9.9.9.1000 > 99.1.1.1.2000: UDP, length 50",
                // the components table at the end of the log
                "Component 49 ...",
                "    Name: TCPIP, Id 49",
            };
        }

        static LockTest.Row R(LockTest.Capture c, string ip, string proto)
        {
            LockTest.Row r;
            return c.Rows.TryGetValue(ip + " " + proto, out r) ? r : null;
        }

        static void LockTestCountsEachPacketOnce()
        {
            var c = LockTest.Parse(SampleLog(), new[] { Me });
            Check(c.HadIds, "ids seen");
            var udp = R(c, "137.221.69.97", "UDP");
            Check(udp != null && udp.Out == 2, "2 UDP out to Chicago: " + (udp == null ? -1 : udp.Out));
            Check(udp != null && udp.In == 1 && udp.DropIn == 1 && udp.DropOut == 0, "1 UDP in, dropped");
            Check(udp != null && udp.RemotePorts.SequenceEqual(new[] { 26503 }) && udp.LocalPorts.SequenceEqual(new[] { 50000 }), "UDP ports");
            Check(udp != null && udp.Reasons.ContainsKey("WFP: Filtered by firewall rule"), "drop reason on the row");
        }

        static void LockTestProtocols()
        {
            var c = LockTest.Parse(SampleLog(), new[] { Me });
            var tcp = R(c, "137.221.69.97", "TCP");
            Check(tcp != null && tcp.Out == 1 && tcp.In == 1 && tcp.Dropped == 0, "TCP one out one in");
            Check(tcp != null && tcp.RemotePorts.Contains(1119), "TCP port 1119");
            var icmp = R(c, "137.221.69.97", "ICMP");
            Check(icmp != null && icmp.Out == 1 && icmp.RemotePorts.Count == 0, "ICMP out, no ports");
            var sgp = R(c, "34.87.10.20", "UDP");
            Check(sgp != null && sgp.Out == 1 && sgp.In == 1, "Singapore both ways");
            Check(R(c, "192.168.1.255", "UDP") == null, "LAN not counted");
            Check(R(c, "99.1.1.1", "UDP") == null, "inner encapsulated line ignored");
            Check(R(c, "20.1.1.1", "UDP") != null, "outer encapsulated line counted");
        }

        static void LockTestDropEvents()
        {
            var c = LockTest.Parse(SampleLog(), new[] { Me });
            Check(c.DropEvents == 3, "3 drop snapshots: " + c.DropEvents);
            Check(c.DropsUnattributed == 1, "1 drop without a packet line: " + c.DropsUnattributed);
            Check(c.Reasons.ContainsKey("INET: transport endpoint was not found") && c.Reasons["INET: transport endpoint was not found"] == 1, "Microsoft's sample reason");
            Check(c.Reasons["WFP: Filtered by firewall rule"] == 2, "WFP reason x2");
            Check(LockTest.FirewallReason("WFP: Filtered by firewall rule") && !LockTest.FirewallReason("INET: transport endpoint was not found") &&
                  !LockTest.FirewallReason("Filtered VLAN"), "firewall reasons");
        }

        static void LockTestHeaderlessAndPublicLocalIp()
        {
            var lines = new[]
            {
                "192.168.1.10.50000 > 1.2.3.4.27015: UDP, length 10",
                "192.168.1.10.50000 > 1.2.3.4.27015: UDP, length 10",
                "5.6.7.8.4000 > 9.9.9.9.27015: UDP, length 10",
                "9.9.9.9.27015 > 5.6.7.8.4000: UDP, length 10",
            };
            var c = LockTest.Parse(lines, new[] { "5.6.7.8" });
            Check(!c.HadIds, "no ids");
            var a = R(c, "1.2.3.4", "UDP");
            Check(a != null && a.Out == 2, "headerless lines count one packet each");
            var b = R(c, "9.9.9.9", "UDP");
            Check(b != null && b.Out == 1 && b.In == 1, "PC with a public address: its own address is local");
            Check(R(c, "5.6.7.8", "UDP") == null, "own public address isn't a remote");
        }

        static void LockTestNetstat()
        {
            var d = LockTest.ParseNetstat(
                "\r\nActive Connections\r\n\r\n  Proto  Local Address          Foreign Address        State           PID\r\n" +
                "  TCP    0.0.0.0:135            0.0.0.0:0              LISTENING       1234\r\n" +
                "  TCP    192.168.1.10:51234     137.221.69.97:1119     ESTABLISHED     4321\r\n" +
                "  UDP    0.0.0.0:50000          *:*                                    4321\r\n" +
                "  UDP    [::]:500               *:*                                    999\r\n" +
                "  UDP    0.0.0.0:60000          *:*                                    0\r\n");
            Check(d.Count == 4, "4 ports: " + d.Count);
            Check(d["TCP 51234"] == 4321 && d["UDP 50000"] == 4321 && d["UDP 500"] == 999 && d["TCP 135"] == 1234, "pids by port");
            Check(!d.ContainsKey("UDP 60000"), "pid 0 skipped");
        }

        static Game FakeOverwatch()
        {
            var g = new Game { Name = "Fake Overwatch", Processes = new[] { "Overwatch" } };
            g.Regions.Add(new Region { Id = "ORD1", NameEn = "Chicago", Cidrs = new List<string> { "137.221.69.0/24" } });
            g.Regions.Add(new Region { Id = "SGP1", NameEn = "Singapore", Picked = true, Cidrs = new List<string> { "34.87.0.0/16" } });
            return g;
        }

        static Dictionary<string, string> Owners()
        {
            return new Dictionary<string, string> { { "UDP 50000", "Overwatch" }, { "TCP 51234", "Battle.net" } };
        }

        static void LockTestClassifyGameUdpRule()
        {
            var g = FakeOverwatch();
            var info = new LockTest.LockInfo
            {
                Active = true, Mode = "game-udp", Program = @"C:\Games\Overwatch\Overwatch.exe", Protocol = "UDP", Ports = "1-3723,3725-65535",
                Picked = new List<string> { "SGP1" }, Block = new List<string> { "137.221.64.0/18" }, Allow = new List<string> { "24.105.0.0/18" }
            };
            var lines = LockTest.Classify(g, LockTest.Parse(SampleLog(), new[] { Me }), info, Owners());
            Func<string, string, LockTest.Line> L = (ip, p) => lines.First(x => x.Row.Ip == ip && x.Row.Proto == p);
            var udp = L("137.221.69.97", "UDP");
            Check(udp.Dc == "ORD1" && udp.Status == "BLOCKED" && udp.InBlock && udp.Covered && udp.Game, "Chicago UDP from the game: blocked and covered");
            var tcp = L("137.221.69.97", "TCP");
            Check(tcp.InBlock && !tcp.Covered && !tcp.Game && tcp.Owner == "Battle.net", "Chicago TCP: blocked datacenter, rule misses it");
            Check(!L("137.221.69.97", "ICMP").Covered, "ICMP not covered by a UDP rule");
            var sgp = L("34.87.10.20", "UDP");
            Check(sgp.Status == "picked" && !sgp.InBlock && sgp.Game, "Singapore picked");
            Check(L("20.1.1.1", "UDP").Dc == "-", "unknown datacenter");

            string text = LockTest.Report(g, LockTest.Parse(SampleLog(), new[] { Me }), info, lines, 60, new DateTime(2026, 10, 13, 12, 0, 0));
            Check(text.Contains("mode: game-udp") && text.Contains("Picked: SGP1"), "report header");
            Check(text.Contains("LEAK  137.221.69.97 (ORD1, TCP") && text.Contains("rule is UDP only") && text.Contains("another process"), "report names the TCP leak and why");
            Check(text.Contains("[firewall]"), "firewall drop reason marked");
            Check(!text.Contains("LEAK  137.221.69.97 (ORD1, UDP") && text.Contains("held  137.221.69.97 (ORD1, UDP): 2 sent, no answer got through, 1 dropped"),
                  "a dropped answer is held, not a leak");
            Check(text.Contains("ORD1(blocked)"), "game talked to blocked Chicago");
            string sum = LockTest.Summary(g, LockTest.Parse(SampleLog(), new[] { Me }), info, lines);
            Check(sum.Contains("mode game-udp") && sum.Contains("1 answered") && sum.Contains("2 firewall"), "summary: " + sum);
        }

        static void LockTestClassifyFullBlock()
        {
            var g = FakeOverwatch();
            var info = new LockTest.LockInfo
            {
                Active = true, Mode = "full", Picked = new List<string> { "SGP1" }, Block = new List<string> { "137.221.64.0/18" },
                Allow = new List<string> { "137.221.69.97" }
            };
            var lines = LockTest.Classify(g, LockTest.Parse(SampleLog(), new[] { Me }), info, Owners());
            Check(lines.Where(l => l.Row.Ip == "137.221.69.97").All(l => l.Status == "allowed" && !l.InBlock), "LockAllow is never blocked");
            info.Allow = new List<string>();
            lines = LockTest.Classify(g, LockTest.Parse(SampleLog(), new[] { Me }), info, Owners());
            Check(lines.Where(l => l.Row.Ip == "137.221.69.97").All(l => l.InBlock && l.Covered), "full block covers every protocol and process");
        }

        static void LockTestPorts()
        {
            Check(LockTest.PortIn(26503, "1-3723,3725-65535") && !LockTest.PortIn(3724, "1-3723,3725-65535") && LockTest.PortIn(3724, null), "port ranges");
            Check(LockTest.PortIn(53, "53") && !LockTest.PortIn(54, "53"), "single port");
        }

        static void LockTestDataStaysUnderCap()
        {
            var lines = new List<string>();
            for (int i = 0; i < 3000; i++)
            {
                lines.Add(Hdr(5000 + i, 1, 1, "Tx", "IP", 49));
                lines.Add("\tip: " + Me + ".50000 > 45." + (i / 250) + "." + (i % 250) + ".1." + (20000 + i) + ": UDP, length 46");
            }
            var c = LockTest.Parse(lines, new[] { Me });
            Check(c.Rows.Count == 3000, "3000 rows");
            var info = new LockTest.LockInfo { Mode = "none" };
            var d = LockTest.Data(c, info, LockTest.Classify(FakeOverwatch(), c, info, null), 60);
            int size = new JavaScriptSerializer().Serialize(d).Length;
            Check(size <= 20000 && d.ContainsKey("truncated") && ((object[])d["rows"]).Length > 50, "data under 20 KB, truncated: " + size);
        }

        static void RunLockTestTests()
        {
            Run("LockTestCountsEachPacketOnce", LockTestCountsEachPacketOnce);
            Run("LockTestProtocols", LockTestProtocols);
            Run("LockTestDropEvents", LockTestDropEvents);
            Run("LockTestHeaderlessAndPublicLocalIp", LockTestHeaderlessAndPublicLocalIp);
            Run("LockTestNetstat", LockTestNetstat);
            Run("LockTestClassifyGameUdpRule", LockTestClassifyGameUdpRule);
            Run("LockTestClassifyFullBlock", LockTestClassifyFullBlock);
            Run("LockTestPorts", LockTestPorts);
            Run("LockTestDataStaysUnderCap", LockTestDataStaysUnderCap);
        }
    }
}
