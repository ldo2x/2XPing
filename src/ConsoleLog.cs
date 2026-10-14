using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace PingBoost
{
    // One line of the game's console log that names a Valve datacenter.
    class ConsoleLine
    {
        public string Code;   // the datacenter as the game wrote it, cut to its POP id ("fra"), lowercase
        public DateTime At;   // when it was written: the line's own time, or when the app first read it; MinValue = unknown (old)
    }

    // The relays the game asked for a session to the current match server, each with its delay to that server ("back").
    class SessionSet
    {
        public string Conn;   // the connection id ("#1671292347"): a new id is a new server
        public DateTime At;   // when its newest line was written (as ConsoleLine.At)
        public readonly List<KeyValuePair<string, int>> Backs = new List<KeyValuePair<string, int>>(); // POP id -> back ms
    }

    // Deadlock's own console log (game\citadel\console.log, written while the game runs with the launch option
    // -condebug). The game's networking library prints the match server's datacenter there: "Remote host is in data center
    // 'fra'" in its format strings, and in the real log the session lines of the relays next to the server (SessionLine):
    // the real answer, which a traffic capture can't see behind an entry relay.
    // Each Read looks only at the bytes added since the last one, so a log of many MB costs nothing to follow.
    // A new log (the game cleared or replaced it) is read from its start. It is new when the file got shorter, starts
    // differently, has another creation time, or no longer has the bytes we read last just before where we stopped (a
    // new game session that rewrote the same header and grew past the old end).
    // Lines without their own time that were already there at the first read are of unknown age, so they never count
    // as the current match (At = MinValue); lines that appear later are stamped with the time they were read.
    class GameConsole
    {
        public const int FirstReadMax = 1 << 20; // the first read looks at the last 1 MB only
        public const int ReadMax = 4 << 20;      // and no read takes more than 4 MB (the newest)
        const int HeadLen = 64;

        public readonly string Path;
        long offset = -1;  // read up to here; -1 = never read
        byte[] head;       // the file's first bytes at the last read
        byte[] mark;       // the last bytes before offset at the last read
        DateTime created;  // the file's creation time at the last read
        Decoder decoder;
        string partial = "";
        public ConsoleLine LastDc, LastRelay; // newest "Remote host is in data center '…'" / "Communicating via relay in '…'"
        public SessionSet LastSessions;       // the session relays of the newest connection (null = none)

        public GameConsole(string path) { Path = path; }

        // Where Deadlock writes it, from the game's exe (…\Deadlock\game\bin\win64\deadlock.exe → …\Deadlock\game\citadel\console.log).
        public static string PathFromExe(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return null;
            try
            {
                for (var dir = new DirectoryInfo(System.IO.Path.GetDirectoryName(exe)); dir != null; dir = dir.Parent)
                    if (Directory.Exists(System.IO.Path.Combine(dir.FullName, "citadel")))
                        return System.IO.Path.Combine(dir.FullName, @"citadel\console.log");
            }
            catch { }
            return null;
        }

        public bool Exists { get { try { return File.Exists(Path); } catch { return false; } } }

        // Reads what was added since the last time. False when there is no log (or it can't be read right now).
        public bool Read(DateTime now)
        {
            try
            {
                if (!File.Exists(Path)) { Forget(); return false; }
                using (var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long len = fs.Length;
                    var h = new byte[(int)Math.Min(HeadLen, len)];
                    ReadFully(fs, h);
                    DateTime made = DateTime.MinValue;
                    try { made = File.GetCreationTimeUtc(Path); } catch { }
                    bool first = offset < 0;
                    if (!first && (len < offset || !SameStart(head, h) || made != created || !SameBytesAt(fs, offset, mark)))
                    {
                        // cleared or replaced: a new log, every line of it is new
                        offset = 0;
                        partial = "";
                        decoder = null;
                    }
                    head = h;
                    created = made;
                    long start = first ? Math.Max(0, len - FirstReadMax) : offset;
                    bool cut = start > 0 && (first || len - start > ReadMax);
                    if (len - start > ReadMax) start = len - ReadMax;
                    if (cut) { partial = ""; decoder = null; }
                    if (decoder == null) decoder = new UTF8Encoding(false).GetDecoder();
                    fs.Seek(start, SeekOrigin.Begin);
                    var bytes = new byte[len - start];
                    int n = ReadFully(fs, bytes);
                    var chars = new char[decoder.GetCharCount(bytes, 0, n)];
                    decoder.GetChars(bytes, 0, n, chars, 0);
                    string text = new string(chars);
                    // a read that starts in the middle of the file starts in the middle of a line: that piece is dropped
                    if (cut) { int nl = text.IndexOf('\n'); text = nl >= 0 ? text.Substring(nl + 1) : ""; }
                    Feed(text, now, first);
                    offset = start + n;
                    int k = (int)Math.Min(HeadLen, offset);
                    mark = new byte[k];
                    fs.Seek(offset - k, SeekOrigin.Begin);
                    ReadFully(fs, mark);
                }
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        void Forget()
        {
            offset = -1;
            head = mark = null;
            created = DateTime.MinValue;
            partial = "";
            decoder = null;
            LastDc = LastRelay = null; // no log, nothing current
            LastSessions = null;
        }

        static int ReadFully(Stream s, byte[] buf)
        {
            int got = 0;
            while (got < buf.Length)
            {
                int k = s.Read(buf, got, buf.Length - got);
                if (k <= 0) break;
                got += k;
            }
            return got;
        }

        // Are the bytes just before offset still the ones read last time?
        static bool SameBytesAt(FileStream fs, long offset, byte[] mark)
        {
            if (mark == null || mark.Length == 0) return true;
            var now = new byte[mark.Length];
            fs.Seek(offset - mark.Length, SeekOrigin.Begin);
            if (ReadFully(fs, now) != mark.Length) return false;
            for (int i = 0; i < mark.Length; i++) if (now[i] != mark[i]) return false;
            return true;
        }

        // The same file when the shorter of the two starts agrees (a log shorter than HeadLen grows into it).
        static bool SameStart(byte[] a, byte[] b)
        {
            if (a == null || b == null) return false;
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // Takes in text read from the log. old: the text was already there at the first read (undated lines of unknown age).
        internal void Feed(string text, DateTime now, bool old)
        {
            string all = partial + text;
            int end = all.LastIndexOf('\n');
            if (end < 0) { partial = all; return; }
            partial = all.Substring(end + 1);
            foreach (var raw in all.Substring(0, end).Split('\n'))
            {
                string kind;
                string text1 = raw.TrimEnd('\r');
                Session(text1, now, old);
                var line = Parse(text1, now, out kind);
                if (line == null) continue;
                if (line.At == DateTime.MaxValue) line.At = old ? DateTime.MinValue : now;
                if (kind == "dc") LastDc = line; else LastRelay = line;
            }
        }

        static readonly Regex SessionConn = new Regex(@"\[(#\d+)\s", RegexOptions.CultureInvariant);

        // A session line adds its relay and back delay to its connection's set (a new connection starts a new set).
        void Session(string line, DateTime now, bool old)
        {
            var m = SessionLine.Match(line);
            int back;
            if (!m.Success || !int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out back)) return;
            var c = SessionConn.Match(line);
            string conn = c.Success ? c.Groups[1].Value : "";
            var at = TimeOf(line, now);
            if (at == DateTime.MaxValue) at = old ? DateTime.MinValue : now;
            if (LastSessions == null || LastSessions.Conn != conn) LastSessions = new SessionSet { Conn = conn };
            string pop = m.Groups[1].Value.ToLowerInvariant();
            if (!LastSessions.Backs.Any(kv => kv.Key == pop)) LastSessions.Backs.Add(new KeyValuePair<string, int>(pop, back));
            LastSessions.At = at;
        }

        // Where the match server is, from its session relays' back delays and Valve's own ping table (dist: shortest ms
        // between two POPs, -1 = unknown): the place whose distances to those relays fit them best, within ToleranceMs
        // each. Needs two relays or more (one delay fits a whole ring of places); null when nothing fits or when places
        // far apart fit as well. A relay next to the server (back <= ColocatedMs) is already named by Parse.
        public const int ToleranceMs = 4;

        public static string Locate(SessionSet set, IEnumerable<string> places, Func<string, string, int> dist)
        {
            if (set == null || set.Backs.Count < 2 || places == null || dist == null) return null;
            string best = null;
            int bestErr = int.MaxValue;
            var fits = new List<string>();
            foreach (var p in places)
            {
                int err = 0;
                foreach (var kv in set.Backs)
                {
                    int d = dist(kv.Key, p);
                    if (d < 0) { err = int.MaxValue; break; }
                    err = Math.Max(err, Math.Abs(d - kv.Value));
                }
                if (err > ToleranceMs) continue;
                fits.Add(p);
                if (err < bestErr) { bestErr = err; best = p; }
            }
            if (best == null) return null;
            // another fitting place far from the best one: the delays can't tell them apart
            foreach (var f in fits)
            {
                int apart = dist(best, f);
                if (f != best && (apart < 0 || apart > 2 * ToleranceMs)) return null;
            }
            return best;
        }

        // The line when it is about the current match, or null: written at or after the running game started and the
        // lock last changed, and after the last server check that saw no match (noMatchAt = when that check read the
        // log, so a line read in that same check, or before it, is about an earlier match). Lines of unknown age never count.
        public static ConsoleLine Current(ConsoleLine line, DateTime gameStart, DateTime lockChangedAt, DateTime noMatchAt)
        {
            if (line == null || line.At == DateTime.MinValue) return null;
            return line.At >= gameStart && line.At >= lockChangedAt && line.At > noMatchAt ? line : null;
        }

        static readonly Regex Stamp = new Regex(@"^\s*(\d{1,2})/(\d{1,2})(?:/(\d{4}))?\s+(\d{1,2}):(\d{2}):(\d{2})", RegexOptions.CultureInvariant);
        static readonly Regex DcLine = new Regex(@"Remote host is in data center '([^']*)'", RegexOptions.CultureInvariant);
        static readonly Regex RelayLine = new Regex(@"Communicating via relay in '([^']*)'", RegexOptions.CultureInvariant);
        static readonly Regex Pop = new Regex(@"^\s*([A-Za-z][A-Za-z0-9]*)", RegexOptions.CultureInvariant);
        // What Deadlock's log really holds (checked in a real match, 2026-10-14): no "data center" line, but the relays
        // the game asks for a session, each with its own delay to the match server ("back"):
        //   [#1671292347 SDR server …] Requesting session from dfra#51 (89.222.108.162:27019).  Ping = 78 = 78+0 (front+back).
        // A relay that reaches the server in ColocatedMs or less sits in the server's datacenter (fra 1 ms, dfra 0 ms):
        // that is the match's place. Behind a far entry relay (Dubai to Frankfurt, back 40) the line says nothing.
        static readonly Regex SessionLine = new Regex(@"Requesting session from ([A-Za-z][A-Za-z0-9]*)#\d+\s*\([^)]*\)\.?\s+Ping\s*=\s*\d+\s*=\s*\d+\+(\d+)\s*\(front\+back\)",
                                                      RegexOptions.CultureInvariant);
        //   … Selecting fra#328 (155.133.226.87:27020) as primary.  (…): the relay the game enters by
        static readonly Regex PrimaryLine = new Regex(@"Selecting ([A-Za-z][A-Za-z0-9]*)#\d+\s*\([^)]*\)\s+as primary", RegexOptions.CultureInvariant);
        public const int ColocatedMs = 2;

        // One log line: kind "dc" (the match server's datacenter) or "relay" (the relay the game enters by), or null.
        // At = the line's own time ("10/13 21:33:12 …", local time, the year the latest one not after now), or
        // MaxValue when the line has no time.
        internal static ConsoleLine Parse(string line, DateTime now, out string kind)
        {
            kind = null;
            if (string.IsNullOrEmpty(line)) return null;
            var m = DcLine.Match(line);
            if (m.Success) kind = "dc";
            else if ((m = RelayLine.Match(line)).Success || (m = PrimaryLine.Match(line)).Success) kind = "relay";
            else
            {
                m = SessionLine.Match(line);
                int back;
                if (!m.Success || !int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out back) ||
                    back > ColocatedMs)
                    return null;
                kind = "dc";
            }
            var p = Pop.Match(m.Groups[1].Value);
            if (!p.Success) { kind = null; return null; }
            return new ConsoleLine { Code = p.Groups[1].Value.ToLowerInvariant(), At = TimeOf(line, now) };
        }

        static DateTime TimeOf(string line, DateTime now)
        {
            var s = Stamp.Match(line);
            if (!s.Success) return DateTime.MaxValue;
            try
            {
                int mon = int.Parse(s.Groups[1].Value, CultureInfo.InvariantCulture), day = int.Parse(s.Groups[2].Value, CultureInfo.InvariantCulture);
                int hh = int.Parse(s.Groups[4].Value, CultureInfo.InvariantCulture), mm = int.Parse(s.Groups[5].Value, CultureInfo.InvariantCulture),
                    ss = int.Parse(s.Groups[6].Value, CultureInfo.InvariantCulture);
                if (s.Groups[3].Success) return new DateTime(int.Parse(s.Groups[3].Value, CultureInfo.InvariantCulture), mon, day, hh, mm, ss);
                var t = new DateTime(now.Year, mon, day, hh, mm, ss);
                return t > now.AddDays(1) ? t.AddYears(-1) : t; // "12/31" read on 1 January is last year's
            }
            catch (ArgumentException) { return DateTime.MaxValue; }
        }

        // The POP id the app knows for a code the game wrote: itself, or without a trailing number ("tyo1" → "tyo");
        // null when neither is known.
        public static string Known(string code, Func<string, bool> known)
        {
            if (string.IsNullOrEmpty(code)) return null;
            if (known(code)) return code;
            string bare = code.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            return bare.Length > 0 && bare != code && known(bare) ? bare : null;
        }
    }
}
