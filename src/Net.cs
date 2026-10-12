using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;

namespace PingBoost
{
    class Endpoint
    {
        public const int Http = -1;
        public string Host;
        public int Port; // 0 = ICMP ping, Http (-1) = Host is a URL timed with a request, otherwise TCP connect time
        public bool Udp; // UDP echo (e.g. Amazon GameLift ping beacons on port 7770): time until the reply comes back

        public Endpoint(string host, int port) { Host = host; Port = port; }
        public Endpoint(string host, int port, bool udp) { Host = host; Port = port; Udp = udp; }

        public static Endpoint Parse(string s)
        {
            s = s.Trim();
            int i = s.LastIndexOf(':');
            int port;
            if (i > 0 && int.TryParse(s.Substring(i + 1), out port))
                return new Endpoint(s.Substring(0, i), port);
            return new Endpoint(s, 0);
        }

        public override string ToString() { return Port <= 0 ? Host : Host + ":" + Port; }
    }

    class PingResult
    {
        public double Avg = -1, Jitter, Loss = 100;
        public bool Ok { get { return Avg >= 0; } }
    }

    static class Pinger
    {
        static readonly Dictionary<string, IPAddress> dns = new Dictionary<string, IPAddress>();

        // Resolved once and cached, so the IP we measure is the same one we route through the tunnel.
        public static IPAddress Resolve(string host)
        {
            lock (dns)
            {
                IPAddress ip;
                if (dns.TryGetValue(host, out ip)) return ip;
            }
            IPAddress parsed;
            IPAddress result = IPAddress.TryParse(host, out parsed) ? parsed
                : Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (result != null) lock (dns) dns[host] = result;
            return result;
        }

        public static IPAddress TryResolve(string host)
        {
            try { return Resolve(host); } catch { return null; }
        }

        // Time of a small HTTPS request on a kept-alive connection (Google's regional test endpoints).
        static double HttpOnce(string url, int timeout)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = timeout;
                req.ReadWriteTimeout = timeout;
                req.KeepAlive = true;
                req.UserAgent = "2XPing";
                var sw = Stopwatch.StartNew();
                using (var resp = req.GetResponse())
                using (var s = resp.GetResponseStream()) { var buf = new byte[256]; while (s.Read(buf, 0, buf.Length) > 0) { } }
                return sw.Elapsed.TotalMilliseconds;
            }
            catch { return -1; }
        }

        public static double Once(Endpoint ep, int timeout)
        {
            if (ep.Port == Endpoint.Http) return HttpOnce(ep.Host, timeout);
            IPAddress ip = TryResolve(ep.Host);
            if (ip == null) return -1;

            if (ep.Udp)
            {
                using (var u = new UdpClient(AddressFamily.InterNetwork))
                {
                    try
                    {
                        u.Client.ReceiveTimeout = timeout;
                        var msg = new byte[] { (byte)'p', (byte)'i', (byte)'n', (byte)'g' };
                        var sw = Stopwatch.StartNew();
                        u.Send(msg, msg.Length, new IPEndPoint(ip, ep.Port));
                        var from = new IPEndPoint(IPAddress.Any, 0);
                        u.Receive(ref from);
                        return Math.Max(sw.Elapsed.TotalMilliseconds, 1);
                    }
                    catch { return -1; }
                }
            }

            if (ep.Port == 0)
            {
                using (var p = new Ping())
                {
                    try
                    {
                        var r = p.Send(ip, timeout);
                        return r.Status == IPStatus.Success ? Math.Max(r.RoundtripTime, 1) : -1;
                    }
                    catch { return -1; }
                }
            }

            using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                s.NoDelay = true;
                var sw = Stopwatch.StartNew();
                try
                {
                    var ar = s.BeginConnect(ip, ep.Port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeout)) return -1;
                    s.EndConnect(ar);
                    return sw.Elapsed.TotalMilliseconds;
                }
                catch { return -1; }
            }
        }

        public static PingResult Measure(Endpoint ep, int count, int timeout)
        {
            var samples = new List<double>();
            int lost = 0;
            // the first HTTPS request also pays for DNS + TLS; warm the connection up and don't count it
            if (ep.Port == Endpoint.Http) Once(ep, timeout);
            int sent = 0;
            for (int i = 0; i < count; i++)
            {
                double v = Once(ep, timeout);
                sent++;
                if (v < 0) lost++; else samples.Add(v);
                // two misses in a row before any reply: the server is down, don't wait for the rest
                if (samples.Count == 0 && lost >= 2) break;
                if (i < count - 1) Thread.Sleep(30);
            }
            var r = new PingResult { Loss = 100.0 * lost / sent };
            if (samples.Count > 0)
            {
                r.Avg = samples.Average();
                double j = 0;
                for (int i = 1; i < samples.Count; i++) j += Math.Abs(samples[i] - samples[i - 1]);
                r.Jitter = samples.Count > 1 ? j / (samples.Count - 1) : 0;
            }
            return r;
        }
    }

    static class Http
    {
        // Request with custom headers (used for the statistics backend). Returns the response body.
        public static string Send(string method, string url, string body, IDictionary<string, string> headers, int timeout)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.Timeout = timeout;
            req.ReadWriteTimeout = timeout;
            req.UserAgent = "2XPing/" + Brand.Version;
            if (headers != null) foreach (var h in headers) req.Headers[h.Key] = h.Value;
            if (body != null)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                req.ContentType = "application/json";
                req.ContentLength = bytes.Length;
                using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
            }
            using (var resp = req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream()))
                return sr.ReadToEnd();
        }

        public static string Get(string url, int timeout)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = timeout;
            req.ReadWriteTimeout = timeout;
            req.UserAgent = "2XPing/" + Brand.Version;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            using (var resp = req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream()))
                return sr.ReadToEnd();
        }
    }

    class Relay
    {
        public string Name, ConfPath, AgentUrl, Token;
        public double Ping = -1;

        // Host part of the "Endpoint = host:port" line in the WireGuard config.
        public string EndpointHost
        {
            get
            {
                try
                {
                    foreach (var raw in File.ReadAllLines(ConfPath))
                    {
                        var line = raw.Trim();
                        int eq = line.IndexOf('=');
                        if (eq < 0 || !line.Substring(0, eq).Trim().Equals("Endpoint", StringComparison.OrdinalIgnoreCase)) continue;
                        var val = line.Substring(eq + 1).Trim();
                        int colon = val.LastIndexOf(':');
                        if (colon > 0) val = val.Substring(0, colon);
                        return val.Trim('[', ']');
                    }
                }
                catch { }
                return null;
            }
        }

        public bool HasAgent { get { return !string.IsNullOrEmpty(AgentUrl); } }

        public double MeasurePing()
        {
            var host = EndpointHost;
            if (host == null) return -1;
            var r = Pinger.Measure(new Endpoint(host, 0), 3, 1500);
            if (r.Ok) return r.Avg;
            Uri u;
            if (HasAgent && Uri.TryCreate(AgentUrl, UriKind.Absolute, out u))
            {
                r = Pinger.Measure(new Endpoint(u.Host, u.Port), 3, 1500);
                if (r.Ok) return r.Avg;
            }
            return -1;
        }

        // Asks the agent on the relay how far it is from the target.
        public double ProbeFromRelay(Endpoint target)
        {
            if (!HasAgent) return -1;
            string url = AgentUrl.TrimEnd('/') + "/probe?host=" + Uri.EscapeDataString(target.Host)
                + "&port=" + target.Port + "&n=3&token=" + Uri.EscapeDataString(Token ?? "");
            try
            {
                double v;
                if (double.TryParse(Http.Get(url, 8000).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                    return v;
            }
            catch { }
            return -1;
        }
    }
}
