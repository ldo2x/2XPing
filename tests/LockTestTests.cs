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
            // one blocked Chicago address with little traffic: must survive the trimming
            lines.Add(Hdr(9999, 1, 1, "Tx", "IP", 49));
            lines.Add("\tip: " + Me + ".50000 > 137.221.69.97.26503: UDP, length 46");
            var c = LockTest.Parse(lines, new[] { Me });
            Check(c.Rows.Count == 3001, "3001 rows");
            var info = new LockTest.LockInfo { Mode = "game-udp", Block = new List<string> { "137.221.64.0/18" } };
            var d = LockTest.Data(c, info, LockTest.Classify(FakeOverwatch(), c, info, null), 60);
            int size = new JavaScriptSerializer().Serialize(d).Length;
            var rows = (object[])d["rows"];
            Check(size <= LockTest.DataLimit && d.ContainsKey("truncated") && rows.Length > 50, "data under the limit, truncated: " + size);
            Check((string)((object[])rows[0])[0] == "137.221.69.97", "the blocked datacenter row goes first and is kept");
        }

        // IpLists.Subtract returns "a-b" ranges far bigger than 4096 addresses: every address inside must count as blocked
        static void LockTestBlockRangesFromSubtract()
        {
            var block = IpLists.Subtract(new[] { "137.221.0.0/16" }, new[] { "137.221.70.0/24" });
            Check(block.Count == 2 && block[0] == "137.221.0.0-137.221.69.255", "Subtract gives ranges: " + string.Join(" ", block.ToArray()));
            Check(LockTest.Blocks(block, "137.221.69.97"), "Chicago 137.221.69.97 is 17,000+ addresses into the range and blocked");
            Check(LockTest.Blocks(block, "137.221.200.1") && !LockTest.Blocks(block, "137.221.70.5") && !LockTest.Blocks(block, "8.8.8.8"), "range edges");
            var info = new LockTest.LockInfo { Mode = "game-udp", Block = block, Picked = new List<string> { "SGP1" } };
            var lines = LockTest.Classify(FakeOverwatch(), LockTest.Parse(SampleLog(), new[] { Me }), info, Owners());
            Check(lines.Where(l => l.Row.Ip == "137.221.69.97").All(l => l.Status == "BLOCKED" && l.InBlock), "classified BLOCKED");
        }

        const string NetEventsXml =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<netEvents>\r\n" +
            // the game's UDP to Chicago, refused by our rule
            " <item>\r\n  <header>\r\n   <timeStamp>2026-10-13T09:00:30.123Z</timeStamp>\r\n" +
            "   <flags numItems=\"2\"><item>FWPM_NET_EVENT_FLAG_IP_PROTOCOL_SET</item><item>FWPM_NET_EVENT_FLAG_APP_ID_SET</item></flags>\r\n" +
            "   <ipVersion>FWP_IP_VERSION_V4</ipVersion>\r\n   <ipProtocol>17</ipProtocol>\r\n   <localAddrV4>192.168.1.10</localAddrV4>\r\n" +
            "   <remoteAddrV4>137.221.69.97</remoteAddrV4>\r\n   <localPort>50000</localPort>\r\n   <remotePort>26503</remotePort>\r\n   <scopeId>0</scopeId>\r\n" +
            "   <appId><data>5c006400</data><asString>\\device\\harddiskvolume3\\games\\overwatch\\_retail_\\overwatch.exe.</asString></appId>\r\n" +
            "  </header>\r\n  <type>FWPM_NET_EVENT_TYPE_CLASSIFY_DROP</type>\r\n" +
            "  <classifyDrop><filterId>70123</filterId><layerId>48</layerId><msFwpDirection>MS_FWP_DIRECTION_OUT</msFwpDirection></classifyDrop>\r\n </item>\r\n" +
            // again, app path printed byte by byte
            " <item><header><timeStamp>2026-10-13T09:00:31.000Z</timeStamp><ipProtocol>17</ipProtocol><remoteAddrV4>137.221.69.97</remoteAddrV4>" +
            "<localPort>50000</localPort><remotePort>26503</remotePort><appId><asString>\\.d.e.v.i.c.e.\\.o.v.e.r.w.a.t.c.h...e.x.e...</asString></appId></header>" +
            "<type>FWPM_NET_EVENT_TYPE_CLASSIFY_DROP</type><classifyDrop><filterId>70124</filterId></classifyDrop></item>\r\n" +
            // before the test started: ignored
            " <item><header><timeStamp>2026-10-13T08:00:00.000Z</timeStamp><ipProtocol>17</ipProtocol><remoteAddrV4>137.221.69.98</remoteAddrV4></header>" +
            "<type>FWPM_NET_EVENT_TYPE_CLASSIFY_DROP</type><classifyDrop><filterId>70123</filterId></classifyDrop></item>\r\n" +
            // an allow event: ignored
            " <item><header><timeStamp>2026-10-13T09:00:32.000Z</timeStamp><ipProtocol>6</ipProtocol><remoteAddrV4>34.87.10.20</remoteAddrV4></header>" +
            "<type>FWPM_NET_EVENT_TYPE_CLASSIFY_ALLOW</type></item>\r\n" +
            // someone scanning the PC, dropped by Windows' own rules (another filter): a public row, not ours
            " <item><header><timeStamp>2026-10-13T09:00:33.000Z</timeStamp><ipProtocol>6</ipProtocol><remoteAddrV4>45.1.2.3</remoteAddrV4>" +
            "<localPort>445</localPort><remotePort>51000</remotePort></header>" +
            "<type>FWPM_NET_EVENT_TYPE_CLASSIFY_DROP</type><classifyDrop><filterId>9</filterId><msFwpDirection>MS_FWP_DIRECTION_IN</msFwpDirection></classifyDrop></item>\r\n" +
            "</netEvents>\r\n";

        const string FiltersXml =
            "<?xml version=\"1.0\"?>\r\n<wfpdiag><filters>" +
            "<item><filterKey>{1}</filterKey><displayData><name>2XPing-RegionLock</name><description/></displayData><flags numItems=\"1\"><item>FWPM_FILTER_FLAG_INDEXED</item></flags><filterId>70123</filterId></item>" +
            "<item><filterKey>{2}</filterKey><displayData><name>2XPing-RegionLock</name></displayData><filterId>70124</filterId></item>" +
            "<item><filterKey>{3}</filterKey><displayData><name>Core Networking - Something</name></displayData><filterId>9</filterId></item>" +
            "</filters></wfpdiag>";

        static void LockTestNetEvents()
        {
            var since = new DateTime(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc);
            var drops = LockTest.ParseNetEvents(NetEventsXml, since);
            Check(drops.Count == 3, "3 drops in the window: " + drops.Count);
            var d = drops[0];
            Check(d.Ip == "137.221.69.97" && d.Proto == "UDP" && d.RemotePort == 26503 && d.LocalPort == 50000 && d.App == "overwatch.exe" &&
                  d.FilterId == "70123" && d.Direction == "MS_FWP_DIRECTION_OUT", "first drop fields: " + d.App);
            Check(drops[1].App == "overwatch.exe", "byte-by-byte app path: " + drops[1].App);
            Check(drops[2].Proto == "TCP" && drops[2].App == null, "inbound scan, no app");
            var ours = LockTest.ParseFilterIds(FiltersXml, "2XPing-RegionLock");
            Check(ours.Count == 2 && ours.Contains("70123") && ours.Contains("70124") && !ours.Contains("9"), "our filter ids");
            Check(LockTest.AppPath("\\device\\x\\a.b.exe") == "\\device\\x\\a.b.exe", "a normal path is kept");
        }

        // Hypothesis A: the game tries the blocked datacenter, the firewall refuses it, and pktmon sees nothing.
        static void LockTestTriedFinding()
        {
            var g = FakeOverwatch();
            var cap = LockTest.Parse(new string[0], new[] { Me });
            LockTest.AddWfp(cap, LockTest.ParseNetEvents(NetEventsXml, new DateTime(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc)),
                            LockTest.ParseFilterIds(FiltersXml, "2XPing-RegionLock"));
            Check(cap.WfpRead && cap.WfpTotal == 3 && cap.WfpOurs == 2, "WFP totals " + cap.WfpTotal + "/" + cap.WfpOurs);
            var info = new LockTest.LockInfo { Active = true, Mode = "game-udp", Protocol = "UDP", Program = "x", Block = new List<string> { "137.221.0.0-137.221.255.255" },
                                               Picked = new List<string> { "SGP1" } };
            var lines = LockTest.Classify(g, cap, info, null);
            var ord = lines.First(l => l.Row.Ip == "137.221.69.97");
            Check(ord.Row.Wfp == 2 && ord.Row.WfpOurs == 2 && ord.Game && ord.InBlock && ord.Row.Total == 0, "game tried Chicago twice, no packets");
            string text = LockTest.Report(g, cap, info, lines, 60, DateTime.Now);
            Check(text.Contains("TRIED 137.221.69.97 (ORD1, UDP): the game tried this blocked datacenter 2 times") && text.Contains("(2 by the 2X Ping rule)"),
                  "TRIED finding");
            Check(text.Contains("3 in the test window, 2 by the 2X Ping rule"), "WFP line");
            Check(text.Contains("run it once UNLOCKED"), "procedure in the result");
        }

        static void LockTestNothingSeenNeverClaimsNoTry()
        {
            var g = FakeOverwatch();
            var info = new LockTest.LockInfo { Active = true, Mode = "full", Block = new List<string> { "137.221.0.0/16" }, Picked = new List<string> { "SGP1" } };
            var cap = LockTest.Parse(new string[0], new[] { Me });
            string text = LockTest.Short(g, cap, info, LockTest.Classify(g, cap, info, null), 60, DateTime.Now);
            Check(text.Contains("COULDN'T READ") && text.Contains("doesn't show the game didn't try"), "WFP unreadable");
            LockTest.AddWfp(cap, new LockTest.WfpDrop[0], null);
            text = LockTest.Short(g, cap, info, LockTest.Classify(g, cap, info, null), 60, DateTime.Now);
            Check(text.Contains("NONE in the test window") && text.Contains("does NOT show that the game didn't try"), "WFP empty");
            Check(!text.Contains("nothing in the blocked datacenters was seen"), "old wording gone");
        }

        static void LockTestIpv6()
        {
            var lines = new[]
            {
                Hdr(1, 1, 1, "Tx", "IP", 49), "\tip: 2001:db8::5.50000 > 2606:4700::1.26503: UDP, length 46",
                Hdr(2, 1, 1, "Rx", "IP", 49), "\tip: 2606:4700::1.26503 > 2001:db8::5.50000: UDP, length 46",
                Hdr(3, 1, 1, "Tx", "IP", 49), "\tip: fe80::1.546 > ff02::1:2.547: UDP, length 46",
                Hdr(4, 1, 1, "Tx", "IP", 49), "\tip: 2001:db8::5 > 2606:4700::2: ICMP6, echo request, id 1, seq 1, length 16",
            };
            var c = LockTest.Parse(lines, new[] { "2001:db8::5" });
            var r = R(c, "2606:4700::1", "UDP");
            Check(r != null && r.V6 && r.Out == 1 && r.In == 1 && r.RemotePorts.Contains(26503) && r.LocalPorts.Contains(50000), "IPv6 UDP row");
            Check(R(c, "2606:4700::2", "ICMP") != null, "ICMPv6 row");
            Check(R(c, "ff02::1:2", "UDP") == null && c.V6Packets == 3, "link-local/multicast skipped: " + c.V6Packets);
            var g = FakeOverwatch();
            var info = new LockTest.LockInfo { Mode = "game-udp", Block = new List<string> { "137.221.0.0/16" } };
            var cl = LockTest.Classify(g, c, info, new Dictionary<string, string> { { "UDP 50000", "Overwatch" } });
            Check(cl.First(l => l.Row.Ip == "2606:4700::1").Status == "IPv6", "IPv6 status");
            string text = LockTest.Short(g, c, info, cl, 60, DateTime.Now);
            Check(text.Contains("IPv6  packets by process: game 2") && text.Contains("THE GAME USED IPv6"), "IPv6 finding");

            // IPv6 entries in the block list (Valve's IPv6 relays, once it lists any) are matched too
            Check(LockTest.Blocks(new[] { "2606:4700::1/128" }, "2606:4700::1") && !LockTest.Blocks(new[] { "2606:4700::1/128" }, "2606:4700::2"), "IPv6 /128");
            Check(LockTest.Blocks(new[] { "2606:4700::/32" }, "2606:4700:ffff::9") && !LockTest.Blocks(new[] { "2606:4700::/32" }, "2606:4701::1"), "IPv6 prefix");
            Check(LockTest.Blocks(new[] { "2001:db8::1-2001:db8::9" }, "2001:db8::5") && !LockTest.Blocks(new[] { "2001:db8::1-2001:db8::9" }, "2001:db8::a"), "IPv6 range");
            Check(!LockTest.Blocks(new[] { "2606:4700::/32" }, "1.2.3.4") && !LockTest.Blocks(new[] { "0.0.0.0/0" }, "2606:4700::1"), "kinds never mix");
            info = new LockTest.LockInfo { Mode = "game-udp", Block = new List<string> { "137.221.0.0/16", "2606:4700::1/128" } };
            cl = LockTest.Classify(g, c, info, new Dictionary<string, string> { { "UDP 50000", "Overwatch" } });
            Check(cl.First(l => l.Row.Ip == "2606:4700::1").Status == "BLOCKED", "an IPv6 row in the block list is BLOCKED");
            text = LockTest.Short(g, c, info, cl, 60, DateTime.Now);
            Check(!text.Contains("THE GAME USED IPv6"), "no IPv6 warning once the game's IPv6 traffic is blocked");
        }

        static void LockTestUnknownProcess()
        {
            var g = FakeOverwatch();
            var info = new LockTest.LockInfo { Mode = "game-udp", Protocol = "UDP", Program = "x", Block = new List<string> { "137.221.0.0/16" } };
            var lines = new[]
            {
                Hdr(1, 1, 1, "Tx", "IP", 49), "\tip: " + Me + ".40000 > 137.221.69.97.26503: UDP, length 46",
                Hdr(2, 1, 1, "Rx", "IP", 49), "\tip: 137.221.69.97.26503 > " + Me + ".40000: UDP, length 46",
            };
            var c = LockTest.Parse(lines, new[] { Me });
            string text = LockTest.Report(g, c, info, LockTest.Classify(g, c, info, null), 60, DateTime.Now);
            Check(text.Contains("process unknown") && !text.Contains("another process") && text.Contains("unknown"), "unknown owner wording");
        }

        static void LockTestEthernetIpv6()
        {
            var lines = new[]
            {
                Hdr(1, 1, 1, "Tx", "Ethernet", 13),
                "\taa:bb:cc:dd:ee:ff > 11:22:33:44:55:66, ethertype IPv6 (0x86dd), length 86: 2001:db8::5.50000 > 2606:4700::1.26503: UDP, length 32",
                Hdr(2, 1, 1, "Rx", "Ethernet", 13),
                "\t11:22:33:44:55:66 > aa:bb:cc:dd:ee:ff, ethertype IPv6 (0x86dd), length 86: 2606:4700::1.26503 > 2001:db8::5.50000: UDP, length 32",
                // a MAC pair with nothing behind it: no row
                Hdr(3, 1, 1, "Tx", "Ethernet", 13),
                "\taa:bb:cc:dd:ee:ff > 11:22:33:44:55:66, ethertype ARP (0x0806), length 42: Request who-has 192.168.1.1 tell 192.168.1.10",
            };
            var c = LockTest.Parse(lines, new[] { "2001:db8::5" });
            var r = R(c, "2606:4700::1", "UDP");
            Check(r != null && r.V6 && r.Out == 1 && r.In == 1 && r.RemotePorts.SequenceEqual(new[] { 26503 }), "IPv6 behind a colon MAC pair");
            Check(c.Rows.Count == 1, "no bogus MAC rows: " + string.Join(" | ", c.Rows.Keys.ToArray()));
        }

        static void LockTestOwnerCaseAndBaseline()
        {
            var g = FakeOverwatch();
            var info = new LockTest.LockInfo { Mode = "none (not locked)", Picked = new List<string> { "SGP1" } };
            var cap = LockTest.Parse(SampleLog(), new[] { Me });
            LockTest.AddWfp(cap, new[] { new LockTest.WfpDrop { Ip = "34.87.10.20", Proto = "UDP", App = "overwatch.exe", LocalPort = 50000 } }, null);
            var lines = LockTest.Classify(g, cap, info, Owners());
            var sgp = lines.First(l => l.Row.Ip == "34.87.10.20" && l.Row.Proto == "UDP");
            Check(sgp.Owner == "Overwatch" && sgp.Game, "one owner, any case: " + sgp.Owner);
            string text = LockTest.Short(g, cap, info, lines, 60, DateTime.Now);
            Check(text.Contains("Unlocked baseline") && !text.Contains("No traffic to or from blocked datacenters"), "baseline wording");
        }

        static void RunLockTestTests()
        {
            Run("LockTestEthernetIpv6", LockTestEthernetIpv6);
            Run("LockTestOwnerCaseAndBaseline", LockTestOwnerCaseAndBaseline);
            Run("LockTestCountsEachPacketOnce", LockTestCountsEachPacketOnce);
            Run("LockTestProtocols", LockTestProtocols);
            Run("LockTestDropEvents", LockTestDropEvents);
            Run("LockTestHeaderlessAndPublicLocalIp", LockTestHeaderlessAndPublicLocalIp);
            Run("LockTestNetstat", LockTestNetstat);
            Run("LockTestClassifyGameUdpRule", LockTestClassifyGameUdpRule);
            Run("LockTestClassifyFullBlock", LockTestClassifyFullBlock);
            Run("LockTestPorts", LockTestPorts);
            Run("LockTestDataStaysUnderCap", LockTestDataStaysUnderCap);
            Run("LockTestBlockRangesFromSubtract", LockTestBlockRangesFromSubtract);
            Run("LockTestNetEvents", LockTestNetEvents);
            Run("LockTestTriedFinding", LockTestTriedFinding);
            Run("LockTestNothingSeenNeverClaimsNoTry", LockTestNothingSeenNeverClaimsNoTry);
            Run("LockTestIpv6", LockTestIpv6);
            Run("LockTestUnknownProcess", LockTestUnknownProcess);
        }
    }
}
