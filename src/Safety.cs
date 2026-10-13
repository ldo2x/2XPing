using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PingBoost
{
    // Windows tools (netsh, pktmon, netstat, ipconfig) started by their full System32 path, never by a bare name:
    // the app runs as admin, and a bare name is looked up in the exe's own folder first (a planted netsh.exe would
    // run elevated). The timeout is real: both outputs are read in the background and a hung tool is killed.
    static class Proc
    {
        static readonly string[] SystemTools = { "netsh", "pktmon", "netstat", "ipconfig", "sc" };

        // "netsh" -> C:\Windows\System32\netsh.exe (Sysnative from a 32-bit process); full paths are kept as they are.
        public static string Resolve(string exe)
        {
            if (string.IsNullOrEmpty(exe) || exe.IndexOfAny(new[] { '\\', '/' }) >= 0) return exe;
            string name = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe.Substring(0, exe.Length - 4) : exe;
            if (!SystemTools.Contains(name.ToLowerInvariant())) return exe;
            string dir = Environment.SystemDirectory;
            if (!Environment.Is64BitProcess && Environment.Is64BitOperatingSystem)
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Sysnative");
            return Path.Combine(dir, name + ".exe");
        }

        // Exit code, or -1 when the tool couldn't start or was killed after timeoutMs. output = stdout + stderr.
        public static int Run(string exe, string args, int timeoutMs, out string output)
        {
            var psi = new ProcessStartInfo(Resolve(exe), args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            Process p;
            try { p = Process.Start(psi); }
            catch (Exception ex) { output = ex.Message; return -1; }
            using (p)
            {
                var o = p.StandardOutput.ReadToEndAsync();
                var e = p.StandardError.ReadToEndAsync();
                bool exited = p.WaitForExit(timeoutMs);
                if (!exited) { try { p.Kill(); } catch { } }
                try { Task.WaitAll(new Task[] { o, e }, 3000); } catch { }
                output = ((o.Status == TaskStatus.RanToCompletion ? o.Result : "") +
                          (e.Status == TaskStatus.RanToCompletion ? e.Result : "")).Trim();
                if (!exited) return -1;
                p.WaitForExit(); // flushes the exit code
                return p.ExitCode;
            }
        }
    }

    // What goes into usage statistics never carries the Windows user name or local folders: exception texts and
    // tool output often contain paths like C:\Users\<name>\AppData\..., which are replaced here.
    static class Privacy
    {
        public static string Scrub(string text)
        {
            string profile = null, temp = null;
            try { profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } catch { }
            try { temp = Path.GetTempPath(); } catch { }
            return Scrub(text, profile, temp, Environment.UserName);
        }

        public static string Scrub(string text, string profile, string temp, string user)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (!string.IsNullOrEmpty(temp))
            {
                temp = temp.TrimEnd('\\', '/');
                if (temp.Length > 3) text = Regex.Replace(text, Regex.Escape(temp), "%TEMP%", RegexOptions.IgnoreCase);
            }
            if (!string.IsNullOrEmpty(profile))
            {
                profile = profile.TrimEnd('\\', '/');
                if (profile.Length > 3) text = Regex.Replace(text, Regex.Escape(profile), "%USERPROFILE%", RegexOptions.IgnoreCase);
            }
            // anyone's profile folder (another account, a short 8.3 name...)
            text = Regex.Replace(text, @"(?i)([a-z]:\\(?:users|documents and settings)\\)[^\\/:*?""<>|\r\n]+", "$1<user>");
            // the account name by itself, as a whole word (short names would match inside ordinary words)
            if (!string.IsNullOrEmpty(user) && user.Length >= 3)
                text = Regex.Replace(text, @"(?<![\p{L}\p{N}_])" + Regex.Escape(user) + @"(?![\p{L}\p{N}_])", "<user>", RegexOptions.IgnoreCase);
            return text;
        }

        // Every string inside the event's details (dictionaries, arrays and lists of them), scrubbed.
        public static object ScrubData(object value)
        {
            var s = value as string;
            if (s != null) return Scrub(s);
            var d = value as IDictionary;
            if (d != null)
            {
                var copy = new Dictionary<string, object>();
                foreach (DictionaryEntry kv in d) copy[Convert.ToString(kv.Key)] = ScrubData(kv.Value);
                return copy;
            }
            if (value is IEnumerable && !(value is string))
            {
                var list = new List<object>();
                foreach (var x in (IEnumerable)value) list.Add(ScrubData(x));
                return list.ToArray();
            }
            return value;
        }

        // For crash reports: the exception type, its (scrubbed) message and the top frames of its stack, without
        // the file paths of the source code. Inner exceptions get one line each.
        public static string ShortStack(Exception ex, int frames = 8)
        {
            if (ex == null) return null;
            var sb = new StringBuilder();
            sb.Append(ex.GetType().FullName).Append(": ").Append(ex.Message);
            var lines = (ex.StackTrace ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var raw in lines.Take(frames))
            {
                string line = raw.Trim();
                int at = line.IndexOf(" in ", StringComparison.Ordinal);
                if (at > 0 && line.IndexOf(":line ", at, StringComparison.Ordinal) > 0) line = line.Substring(0, at);
                sb.Append('\n').Append(line);
            }
            for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                sb.Append("\n---> ").Append(inner.GetType().FullName).Append(": ").Append(inner.Message);
            return Scrub(sb.ToString());
        }
    }

    // Remote addresses for a firewall rule: 1.2.3.4, 1.2.3.0/24 or 1.2.3.4-1.2.3.9, with every octet 0-255,
    // prefixes 0-32 and ranges that go up. One bad entry in a downloaded list would make netsh refuse the whole rule.
    static class RemoteAddress
    {
        static readonly Regex Shape = new Regex(@"^(\d{1,3}(?:\.\d{1,3}){3})(?:/(\d{1,2})|-(\d{1,3}(?:\.\d{1,3}){3}))?\z");

        public static bool Valid(string s)
        {
            if (s == null) return false;
            var m = Shape.Match(s);
            if (!m.Success) return false;
            uint a, b;
            if (!Ip(m.Groups[1].Value, out a)) return false;
            if (m.Groups[2].Success) return int.Parse(m.Groups[2].Value) <= 32;
            if (m.Groups[3].Success) return Ip(m.Groups[3].Value, out b) && b >= a;
            return true;
        }

        static bool Ip(string s, out uint v)
        {
            v = 0;
            foreach (var part in s.Split('.'))
            {
                int o;
                if (!int.TryParse(part, out o) || o > 255) return false;
                v = (v << 8) | (uint)o;
            }
            return true;
        }
    }

    // A file written so that a crash, a power cut or a reader at the same moment never sees half of it:
    // the text goes to a temporary file next to it, which then replaces the old one.
    static class AtomicFile
    {
        public static void WriteAllText(string path, string text)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            try
            {
                if (File.Exists(path)) File.Replace(tmp, path, null, true);
                else File.Move(tmp, path);
            }
            catch (IOException)
            {
                // File.Replace can fail on some file systems or when another program holds the file: plain copy
                File.Copy(tmp, path, true);
                try { File.Delete(tmp); } catch { }
            }
        }

        public static void WriteAllLines(string path, IEnumerable<string> lines)
        {
            WriteAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
        }
    }

    // settings.txt (key=value lines), safe to use from any thread. Read once and kept in memory; every change is
    // written atomically. If the file exists but can't be read (another program has it open), nothing is written
    // over it: changes wait in memory until it can be read, so no other key is ever lost.
    class SettingsFile
    {
        readonly string path;
        readonly object gate = new object();
        Dictionary<string, string> data;                                    // null until the file was read
        readonly Dictionary<string, string> pending = new Dictionary<string, string>(); // changes made before that

        public SettingsFile(string path) { this.path = path; }

        static string Clean(string s) { return (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ').Trim(); }

        bool Loaded()
        {
            if (data != null) return true;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var d = new Dictionary<string, string>();
                    if (File.Exists(path))
                        foreach (var line in File.ReadAllLines(path))
                        {
                            int eq = line.IndexOf('=');
                            if (eq > 0) d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                        }
                    foreach (var kv in pending) d[kv.Key] = kv.Value;
                    data = d;
                    if (pending.Count > 0) { pending.Clear(); Save(); }
                    return true;
                }
                catch (IOException) { Thread.Sleep(20); }
                catch (UnauthorizedAccessException) { Thread.Sleep(20); }
            }
            return false;
        }

        bool Save()
        {
            try
            {
                AtomicFile.WriteAllLines(path, data.Select(kv => kv.Key + "=" + kv.Value));
                return true;
            }
            catch { return false; }
        }

        public string Get(string key)
        {
            lock (gate)
            {
                string v;
                if (pending.TryGetValue(key, out v)) return v;
                return Loaded() && data.TryGetValue(key, out v) ? v : null;
            }
        }

        // false when the change is only in memory for now (the file couldn't be read or written)
        public bool Set(string key, string value)
        {
            return Set(new Dictionary<string, string> { { key, value } });
        }

        public bool Set(IDictionary<string, string> values)
        {
            lock (gate)
            {
                if (!Loaded())
                {
                    foreach (var kv in values) pending[kv.Key] = Clean(kv.Value);
                    return false;
                }
                foreach (var kv in values) data[kv.Key] = Clean(kv.Value);
                return Save();
            }
        }

        // The value of key; when there is none, make() is stored and returned (once, even from many threads).
        public string GetOrAdd(string key, Func<string> make)
        {
            lock (gate)
            {
                string v = Get(key);
                if (!string.IsNullOrEmpty(v)) return v;
                v = make();
                Set(key, v);
                return v;
            }
        }
    }

    // Firewall and tunnel changes run one at a time, in the order they were asked for, on a background thread:
    // an Unlock can never overtake the Lock before it, and closing the app can wait for the last one.
    static class FirewallWorker
    {
        static readonly object gate = new object();
        static Task tail = Task.FromResult(0);

        // what happens to an unexpected error of a step (reported like a crash; the next steps still run)
        public static Action<Exception> OnError = ex =>
            Issues.Report("crash", "firewall worker: " + ex.GetType().Name + ": " + ex.Message,
                          new Dictionary<string, object> { { "stack", Privacy.ShortStack(ex) }, { "fatal", false } });

        public static Task Run(Action work)
        {
            lock (gate)
            {
                tail = tail.ContinueWith(t =>
                {
                    try { work(); }
                    catch (Exception ex) { try { OnError(ex); } catch { } }
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                return tail;
            }
        }

        // Waits (at most timeoutMs) for everything queued so far. true = all done.
        public static bool Drain(int timeoutMs)
        {
            Task t;
            lock (gate) t = tail;
            try { return t.Wait(timeoutMs); } catch { return true; }
        }
    }
}
