using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Web.Script.Serialization;

namespace PingBoost
{
    // Official address lists of the companies that host game servers, cached on disk for a day
    // (and used stale when offline), plus the IPv4 range math for "everything except my region".
    //   gcp:<region>  Google Cloud region (gcp:* = all of Google Cloud)   www.gstatic.com/ipranges/cloud.json
    //   aws:<region>  Amazon EC2 in one region                              ip-ranges.amazonaws.com
    //   asn:<number>  everything a company's network announces (RIPEstat), e.g. asn:57976 = Blizzard
    //   anything else is taken as a CIDR (1.2.3.0/24) written by hand
    static class IpLists
    {
        static string CacheDir { get { return Path.Combine(Storage.Dir, "cache"); } }

        // The text of a list (downloaded at most once a day) and until when it counts as current: a day after the file
        // was written, or an hour when a failed download fell back to an older copy (then it is asked again sooner).
        static string Cached(string name, string url, out DateTime validUntil)
        {
            Directory.CreateDirectory(CacheDir);
            string file = Path.Combine(CacheDir, name);
            if (File.Exists(file))
            {
                DateTime written = File.GetLastWriteTimeUtc(file);
                if (written > DateTime.UtcNow.AddHours(-24)) { validUntil = written.AddHours(24); return File.ReadAllText(file); }
            }
            try
            {
                string text = Http.Get(url, 30000);
                // a good download is used even if the cache can't be written this time
                try { AtomicFile.WriteAllText(file, text); } catch { }
                validUntil = DateTime.UtcNow.AddHours(24);
                return text;
            }
            catch (Exception ex)
            {
                // offline: yesterday's list is fine
                if (File.Exists(file)) { validUntil = DateTime.UtcNow.AddHours(1); return File.ReadAllText(file); }
                Issues.Report("ip_lists_failed", name + ": " + ex.Message, null, name);
                throw;
            }
        }

        // Each list file is parsed once and its prefixes grouped by region (scope): the AWS file is 2.7 MB, and a game
        // with 15 regions used to parse it 15 times on every load, lock and boost (performance audit item 1).
        class Parsed
        {
            public DateTime ValidUntil;
            public Dictionary<string, List<string>> ByKey;
        }

        static readonly Dictionary<string, Parsed> parsed = new Dictionary<string, Parsed>();

        // How many times a list file was parsed (tests and measurements).
        internal static int ParseCount;

        // Forgets the parsed lists (the developer "Refresh IP lists" button, tests): the next lookup reads the files again.
        public static void ClearParsed() { lock (parsed) parsed.Clear(); }

        static Dictionary<string, List<string>> Grouped(string name, string url, Func<string, Dictionary<string, List<string>>> parse)
        {
            lock (parsed)
            {
                Parsed p;
                if (parsed.TryGetValue(name, out p) && DateTime.UtcNow < p.ValidUntil) return p.ByKey;
                DateTime validUntil;
                string text = Cached(name, url, out validUntil);
                p = new Parsed { ValidUntil = validUntil, ByKey = parse(text) };
                ParseCount++;
                parsed[name] = p;
                return p.ByKey;
            }
        }

        static void Add(Dictionary<string, List<string>> by, string key, string cidr)
        {
            List<string> list;
            if (!by.TryGetValue(key ?? "", out list)) by[key ?? ""] = list = new List<string>();
            list.Add(cidr);
        }

        // Google Cloud: "*" holds every IPv4 prefix, each scope (region) its own.
        internal static Dictionary<string, List<string>> ParseGcp(string text)
        {
            var by = new Dictionary<string, List<string>> { { "*", new List<string>() } };
            var root = (Dictionary<string, object>)Json(text);
            foreach (var p in Items(root["prefixes"]))
            {
                string cidr = S(p, "ipv4Prefix");
                if (cidr == null) continue;
                by["*"].Add(cidr);
                Add(by, S(p, "scope"), cidr);
            }
            return by;
        }

        // Amazon: EC2 only, "*" every region together, each region its own.
        internal static Dictionary<string, List<string>> ParseAws(string text)
        {
            var by = new Dictionary<string, List<string>> { { "*", new List<string>() } };
            var root = (Dictionary<string, object>)Json(text);
            foreach (var p in Items(root["prefixes"]))
            {
                if (S(p, "service") != "EC2") continue;
                string cidr = S(p, "ip_prefix");
                by["*"].Add(cidr);
                Add(by, S(p, "region"), cidr);
            }
            return by;
        }

        // RIPEstat announced prefixes of one network: IPv4 only, all under "*".
        internal static Dictionary<string, List<string>> ParseAsn(string text)
        {
            var list = new List<string>();
            var root = (Dictionary<string, object>)Json(text);
            var data = (Dictionary<string, object>)root["data"];
            foreach (var p in Items(data["prefixes"]))
            {
                string cidr = S(p, "prefix");
                if (cidr != null && cidr.IndexOf(':') < 0) list.Add(cidr);
            }
            return new Dictionary<string, List<string>> { { "*", list } };
        }

        static object Json(string text)
        {
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(text);
        }

        static IEnumerable<Dictionary<string, object>> Items(object o)
        {
            var e = o as IEnumerable;
            if (e == null) yield break;
            foreach (var x in e) { var d = x as Dictionary<string, object>; if (d != null) yield return d; }
        }

        static string S(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) ? v as string : null;
        }

        public static List<string> Resolve(IEnumerable<string> specs)
        {
            var all = new List<string>();
            foreach (var s in specs) all.AddRange(Resolve(s));
            return all;
        }

        // A new list on every call (callers add to it); the parsed lists behind it are shared and never changed.
        public static List<string> Resolve(string spec)
        {
            int i = spec.IndexOf(':');
            string kind = i > 0 ? spec.Substring(0, i) : "", arg = i > 0 ? spec.Substring(i + 1) : spec;
            Dictionary<string, List<string>> by;
            if (kind == "gcp") by = Grouped("google-cloud.json", "https://www.gstatic.com/ipranges/cloud.json", ParseGcp);
            else if (kind == "aws") by = Grouped("aws-ip-ranges.json", "https://ip-ranges.amazonaws.com/ip-ranges.json", ParseAws);
            else if (kind == "asn")
            {
                by = Grouped("asn-" + arg + ".json", "https://stat.ripe.net/data/announced-prefixes/data.json?resource=AS" + arg, ParseAsn);
                arg = "*";
            }
            else return new List<string> { spec };
            List<string> list;
            return by.TryGetValue(arg, out list) ? new List<string>(list) : new List<string>();
        }

        // ---------------------------------------------------------------- IPv4 ranges

        struct Range { public uint Start, End; }

        static uint Ip(string s)
        {
            var b = IPAddress.Parse(s).GetAddressBytes();
            return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        }

        static string Ip(uint v) { return (v >> 24) + "." + ((v >> 16) & 255) + "." + ((v >> 8) & 255) + "." + (v & 255); }

        static Range Parse(string cidr)
        {
            string[] p = cidr.Trim().Split('/');
            uint ip = Ip(p[0]);
            int bits = p.Length > 1 ? int.Parse(p[1]) : 32;
            uint mask = bits == 0 ? 0 : uint.MaxValue << (32 - bits);
            return new Range { Start = ip & mask, End = (ip & mask) | ~mask };
        }

        static List<Range> Merge(IEnumerable<string> cidrs)
        {
            var list = new List<Range>();
            foreach (var c in cidrs)
            {
                if (string.IsNullOrWhiteSpace(c) || c.IndexOf(':') >= 0) continue;
                try { list.Add(Parse(c)); } catch { }
            }
            list.Sort((a, b) => a.Start.CompareTo(b.Start));
            var merged = new List<Range>();
            foreach (var r in list)
            {
                if (merged.Count > 0 && r.Start <= merged[merged.Count - 1].End + 1UL)
                {
                    var last = merged[merged.Count - 1];
                    if (r.End > last.End) { last.End = r.End; merged[merged.Count - 1] = last; }
                }
                else merged.Add(r);
            }
            return merged;
        }

        // The address space minus the allowed ranges, as "a.b.c.d-e.f.g.h" ranges (the form Windows Firewall takes).
        public static List<string> Subtract(IEnumerable<string> space, IEnumerable<string> allowed)
        {
            var keep = Merge(allowed);
            var result = new List<string>();
            foreach (var r in Merge(space))
            {
                ulong start = r.Start;
                foreach (var k in keep)
                {
                    if (k.End < start || k.Start > r.End) continue;
                    if (k.Start > start) result.Add(Ip((uint)start) + "-" + Ip(k.Start - 1));
                    start = (ulong)k.End + 1;
                    if (start > r.End) break;
                }
                if (start <= r.End) result.Add(Ip((uint)start) + "-" + Ip(r.End));
            }
            return result;
        }

        public static bool Contains(IEnumerable<string> cidrs, string ip)
        {
            uint v;
            try { v = Ip(ip); } catch { return false; }
            foreach (var c in cidrs)
            {
                try { var r = Parse(c); if (v >= r.Start && v <= r.End) return true; } catch { }
            }
            return false;
        }
    }
}
