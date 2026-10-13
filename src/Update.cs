using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace PingBoost
{
    class Release
    {
        public string Tag, ZipUrl, ShaUrl;
        public Version Ver;
        public bool Optional; // "#optional" in the release notes: never forced
    }

    // Updates are required: a newer release is installed with one click, and after GraceDays the old version stops.
    static class Updater
    {
        public const int GraceDays = 3;

        static Version Mine { get { return new Version(Brand.VersionNumber.Major, Brand.VersionNumber.Minor, Brand.VersionNumber.Build); } }

        // The three newest releases are enough (GitHub lists the newest first), and the answer is asked for with the ETag of
        // the last one: when nothing changed GitHub sends a 304 with no body (performance audit item 6).
        const string ReleasesUrl = "https://api.github.com/repos/" + Brand.Repo + "/releases?per_page=3";

        static string ReleasesFile { get { return Path.Combine(Path.Combine(Storage.Dir, "cache"), "releases.txt"); } }

        // The newest release on GitHub that is newer than this one (pre-releases included), or null.
        public static Release Latest()
        {
            try
            {
                string file = ReleasesFile, etag, fresh;
                string saved = ReadSaved(file, out etag);
                string json = Http.GetIfChanged(ReleasesUrl, 10000, saved != null ? etag : null, out fresh);
                if (json == null)
                {
                    // 304: the same releases as last time. A saved copy that doesn't read is dropped, so the next check
                    // asks for the whole answer again instead of getting 304 for it forever.
                    try { return Newest(saved); }
                    catch { Save(file, null, null); throw; }
                }
                var r = Newest(json);
                Save(file, fresh, json); // only an answer that reads is kept
                return r;
            }
            catch { return null; }
        }

        // The saved answer (its ETag on the first line, the JSON after it), or null when there is none.
        internal static string ReadSaved(string file, out string etag)
        {
            etag = null;
            try
            {
                if (!File.Exists(file)) return null;
                string all = File.ReadAllText(file);
                int nl = all.IndexOf('\n');
                if (nl <= 0) return null;
                etag = all.Substring(0, nl).Trim();
                return etag.Length > 0 ? all.Substring(nl + 1) : null;
            }
            catch { return null; }
        }

        // Kept only with an ETag: without one there is nothing to ask "changed?" with.
        internal static void Save(string file, string etag, string json)
        {
            try
            {
                if (string.IsNullOrEmpty(etag) || etag.IndexOf('\n') >= 0) { if (File.Exists(file)) File.Delete(file); return; }
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                AtomicFile.WriteAllText(file, etag + "\n" + json);
            }
            catch { }
        }

        // The newest non-draft release in GitHub's answer that is newer than this version, or null.
        internal static Release Newest(string json)
        {
            Release best = null;
            foreach (var o in (object[])new JavaScriptSerializer().DeserializeObject(json))
            {
                var d = (Dictionary<string, object>)o;
                if (d.ContainsKey("draft") && true.Equals(d["draft"])) continue;
                var tag = d["tag_name"] as string;
                var m = Regex.Match(tag ?? "", @"(\d+)\.(\d+)\.(\d+)");
                if (!m.Success) continue;
                var r = new Release
                {
                    Tag = tag,
                    Ver = new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)),
                    Optional = ((d.ContainsKey("body") ? d["body"] as string : null) ?? "").Contains("#optional")
                };
                var assets = d.ContainsKey("assets") ? d["assets"] as object[] : null;
                foreach (var a in assets ?? new object[0])
                {
                    var ad = (Dictionary<string, object>)a;
                    string name = ad["name"] as string ?? "", url = ad["browser_download_url"] as string;
                    if (name.EndsWith(".zip")) r.ZipUrl = url;
                    else if (name.EndsWith(".zip.sha256")) r.ShaUrl = url;
                }
                if (best == null || r.Ver > best.Ver) best = r;
            }
            return best != null && best.Ver > Mine ? best : null;
        }

        // The grace period starts the first time this PC sees the release.
        static DateTime FirstSeen(Release r)
        {
            var v = (Storage.GetSetting("update_seen") ?? "").Split('|');
            long ticks;
            if (v.Length == 2 && v[0] == r.Tag && long.TryParse(v[1], out ticks)) return new DateTime(ticks, DateTimeKind.Utc);
            Storage.SetSetting("update_seen", r.Tag + "|" + DateTime.UtcNow.Ticks);
            return DateTime.UtcNow;
        }

        public static double DaysLeft(Release r) { return GraceDays - (DateTime.UtcNow - FirstSeen(r)).TotalDays; }

        public static bool Forced(Release r) { return !r.Optional && DaysLeft(r) <= 0; }

        // "2 days" / "يومين" for the banner.
        public static string LeftText(Release r)
        {
            double d = DaysLeft(r);
            if (d <= 0) return Lang.T("today", "اليوم");
            if (d < 1) { int h = Math.Max(1, (int)Math.Ceiling(d * 24)); return Lang.T(h + (h == 1 ? " hour" : " hours"), h == 1 ? "ساعة" : h == 2 ? "ساعتين" : h + (h <= 10 ? " ساعات" : " ساعة")); }
            return Days((int)Math.Ceiling(d));
        }

        // "3 days" / "يوم", "يومين", "3 أيام", "11 يوم" (Arabic plural: 3-10 take أيام, 11 and up يوم).
        public static string Days(int n)
        {
            return Lang.T(n + (n == 1 ? " day" : " days"), n == 1 ? "يوم" : n == 2 ? "يومين" : n + (n <= 10 ? " أيام" : " يوم"));
        }

        // Downloads the release, checks its SHA256, swaps the exe and starts the new version.
        // Returns null when the new version is starting (the caller then closes), otherwise what went wrong.
        // Set when the new exe is in place but Windows (e.g. an antivirus) refused to start it: the update itself worked.
        public static bool InstalledNotStarted;

        public static string Install(Release r, Action<int> progress)
        {
            InstalledNotStarted = false;
            // the new exe is extracted next to the running one (same disk, so the swap below is a rename, never a copy)
            string exe = Application.ExecutablePath, old = exe + ".old", fresh = exe + ".new";
            string freshHash;
            string err = Fetch(r, progress, fresh, out freshHash);
            if (err != null) return err;

            // the file checked again right before the swap: nothing may have changed it since it was extracted
            if (Sha256(fresh) != freshHash)
            {
                try { File.Delete(fresh); } catch { }
                return Lang.T("the download is damaged (checksum mismatch)", "الملف وصل خربان (البصمة ما تطابق)");
            }

            // a running exe can be renamed but not overwritten
            try { if (File.Exists(old)) File.Delete(old); } catch { }
            File.Move(exe, old);
            try { File.Move(fresh, exe); }
            catch
            {
                // never leave a half-written exe behind: put the old one back
                try { if (File.Exists(exe)) File.Delete(exe); } catch { }
                File.Move(old, exe);
                try { File.Delete(fresh); } catch { }
                throw;
            }

            try { Process.Start(new ProcessStartInfo(exe, "--updated " + Process.GetCurrentProcess().Id) { UseShellExecute = true }); }
            catch (Exception ex)
            {
                InstalledNotStarted = true;
                return Lang.T("the new version is installed, but Windows didn't let it start (" + ex.Message + "). Open 2X Ping again.",
                              "النسخة الجديدة انثبتت، بس ويندوز ما خلاها تشتغل (" + ex.Message + "). افتح البرنامج مرة ثانية.");
            }
            return null;
        }

        static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var s = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
        }

        // Downloads the release zip, checks its SHA256 and extracts 2XPing.exe to target. Returns null when target is
        // ready; targetHash is the SHA256 of the extracted file, to check again before it is used.
        public static string Fetch(Release r, Action<int> progress, string target, out string targetHash)
        {
            targetHash = null;
            if (r.ZipUrl == null || r.ShaUrl == null) return Lang.T("the release has no download file", "الإصدار ما فيه ملف تحميل");
            // a new folder with a random name each time: no other program can prepare a file at a known path
            string dir = Path.Combine(Path.GetTempPath(), "2XPing-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string zip = Path.Combine(dir, "update.zip");
                Download(r.ZipUrl, zip, progress);

                string expected = Http.Get(r.ShaUrl, 15000).Trim().Split(' ', '\t')[0].ToLowerInvariant();
                if (expected != Sha256(zip)) return Lang.T("the download is damaged (checksum mismatch)", "الملف وصل خربان (البصمة ما تطابق)");

                using (var archive = ZipFile.OpenRead(zip))
                {
                    var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals("2XPing.exe", StringComparison.OrdinalIgnoreCase));
                    if (entry == null) return Lang.T("2XPing.exe is missing from the download", "ما لقينا 2XPing.exe داخل الملف");
                    // hashed while it is written, from the verified zip
                    using (var src = entry.Open())
                    using (var dst = File.Create(target))
                    using (var sha = SHA256.Create())
                    {
                        var buf = new byte[65536];
                        int n;
                        while ((n = src.Read(buf, 0, buf.Length)) > 0)
                        {
                            sha.TransformBlock(buf, 0, n, null, 0);
                            dst.Write(buf, 0, n);
                        }
                        sha.TransformFinalBlock(buf, 0, 0);
                        targetHash = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                    }
                }
                return null;
            }
            catch
            {
                try { if (File.Exists(target)) File.Delete(target); } catch { }
                throw;
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        static void Download(string url, string path, Action<int> progress)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "2XPing/" + Brand.Version;
            req.Timeout = 20000;
            req.ReadWriteTimeout = 20000;
            using (var resp = req.GetResponse())
            using (var src = resp.GetResponseStream())
            using (var dst = File.Create(path))
            {
                long total = resp.ContentLength, got = 0;
                var buf = new byte[65536];
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    dst.Write(buf, 0, n);
                    got += n;
                    if (total > 0) progress((int)(got * 100 / total));
                }
            }
        }

        // Started by the old version after an update: wait for it to close, then delete it.
        public static void FinishUpdate(string[] args)
        {
            int i = Array.IndexOf(args, "--updated");
            if (i < 0) return;
            int pid;
            if (i + 1 < args.Length && int.TryParse(args[i + 1], out pid))
                try { using (var p = Process.GetProcessById(pid)) p.WaitForExit(20000); } catch { }
            JustUpdated = true;
        }

        public static bool JustUpdated;

        public static void CleanupOld()
        {
            try { var old = Application.ExecutablePath + ".old"; if (File.Exists(old)) File.Delete(old); } catch { }
            try { var fresh = Application.ExecutablePath + ".new"; if (File.Exists(fresh)) File.Delete(fresh); } catch { }
        }
    }

    // "A new version is out": update now (with progress), or later while the grace period lasts.
    static class UpdateDialog
    {
        // true = the new version is starting and this one must close
        public static bool Show(IWin32Window owner, Release r, bool forced)
        {
            using (var f = new Form())
            {
                f.Text = Brand.Caption;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = owner == null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent;
                f.MinimizeBox = f.MaximizeBox = false;
                f.ShowInTaskbar = forced;
                f.BackColor = Theme.Bg;
                f.ForeColor = Theme.TextC;
                f.Font = Fonts.Ui(10f);
                f.RightToLeft = Lang.Ar ? RightToLeft.Yes : RightToLeft.No;
                const int W = 440;

                f.Controls.Add(new PictureBox { Image = Brand.Logo, SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(208, 18, 64, 64) });
                f.Controls.Add(new Label
                {
                    Text = Lang.T("New version: ", "نسخة جديدة: ") + "‎" + r.Ver.ToString(3) + "‎",
                    Font = Fonts.Semi(14f), ForeColor = Theme.Accent, TextAlign = ContentAlignment.MiddleCenter,
                    Bounds = new Rectangle(20, 90, W, 34)
                });
                var text = new Label
                {
                    ForeColor = Theme.TextC, TextAlign = ContentAlignment.TopCenter, Bounds = new Rectangle(20, 130, W, 52),
                    Text = forced
                        ? Lang.T("This version is too old to keep using. Update to carry on, it takes a few seconds.",
                                 "هالنسخة قديمة وما تشتغل بعد كذا. حدّث عشان تكمل، ياخذ كم ثانية بس.")
                        : r.Optional
                            ? Lang.T("Update now, it takes a few seconds and the app reopens by itself.",
                                     "حدّث دحين، ياخذ كم ثانية والبرنامج يفتح لحاله.")
                            : Lang.T("Update now, it takes a few seconds. In " + Updater.LeftText(r) + " the old version stops working.",
                                     "حدّث دحين، ياخذ كم ثانية. وبعد " + Updater.LeftText(r) + " النسخة القديمة توقف.")
                };
                f.Controls.Add(text);

                var bar = new ProgressPanel { Bounds = new Rectangle(20, 188, W, 6), Visible = false };
                f.Controls.Add(bar);
                var status = new Label { ForeColor = Theme.Dim, TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(20, 198, W, 24), Font = Fonts.Ui(9f) };
                f.Controls.Add(status);

                var go = new RoundButton
                {
                    Text = Lang.T("Update now", "حدّث دحين"), Bounds = new Rectangle(Lang.Ar ? 240 : 20, 232, 220, 40),
                    FlatStyle = FlatStyle.Flat, BackColor = Theme.Accent, ForeColor = Color.Black, Font = Fonts.Semi(10.5f), Cursor = Cursors.Hand
                };
                var later = new RoundButton
                {
                    Text = forced ? Lang.T("Close the app", "اقفل البرنامج") : Lang.T("Later", "بعدين"),
                    DialogResult = DialogResult.Cancel, Bounds = new Rectangle(Lang.Ar ? 20 : 240, 232, 200, 40),
                    FlatStyle = FlatStyle.Flat, BackColor = Theme.Panel2, ForeColor = Theme.TextC, Cursor = Cursors.Hand
                };
                go.FlatAppearance.BorderSize = later.FlatAppearance.BorderSize = 0;
                f.Controls.Add(go);
                f.Controls.Add(later);
                f.AcceptButton = go;
                f.CancelButton = later;
                f.ClientSize = new Size(W + 40, 290);

                bool started = false, working = false, failed = false;
                f.FormClosing += (s, e) => { if (working) e.Cancel = true; };
                go.Click += delegate
                {
                    if (failed) { Brand.Open(Brand.ReleasesUrl + "/latest"); return; }
                    working = true;
                    go.Enabled = later.Enabled = false;
                    bar.Visible = true;
                    status.ForeColor = Theme.Dim;
                    status.Text = Lang.T("Downloading...", "ننزّل التحديث...");
                    var sw = Stopwatch.StartNew();
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        string err;
                        try
                        {
                            err = Updater.Install(r, p => f.BeginInvoke((Action)(() => { bar.Value = p; })));
                        }
                        catch (Exception ex) { err = ex.Message; }
                        if (err != null) Issues.Report("update_failed", Brand.Version + " -> " + r.Ver.ToString(3) + ": " + err);
                        Telemetry.Send("update", null, -1, Brand.Version + " -> " + r.Ver.ToString(3) + (err == null ? " ok" : " failed: " + err),
                            new Dictionary<string, object> { { "from", Brand.Version }, { "to", r.Ver.ToString(3) }, { "ok", err == null },
                                                              { "seconds", Math.Round(sw.Elapsed.TotalSeconds, 1) }, { "forced", forced } });
                        f.BeginInvoke((Action)(() =>
                        {
                            working = false;
                            if (err == null)
                            {
                                started = true;
                                status.Text = Lang.T("Done, reopening...", "خلص، يفتح من جديد...");
                                f.Close();
                                return;
                            }
                            bar.Visible = false;
                            status.ForeColor = Updater.InstalledNotStarted ? Theme.Warn : Theme.Bad;
                            status.Text = Updater.InstalledNotStarted ? err : Lang.T("Couldn't update: ", "ما زبط التحديث: ") + err;
                            failed = true;
                            go.Text = Lang.T("Download page", "صفحة التحميل");
                            go.Enabled = later.Enabled = true;
                        }));
                    });
                };
                f.ShowDialog(owner);
                return started;
            }
        }

        // Thin pink progress bar that matches the theme.
        class ProgressPanel : Panel
        {
            int value;
            public int Value { get { return value; } set { this.value = Math.Max(0, Math.Min(100, value)); Invalidate(); } }
            public ProgressPanel() { DoubleBuffered = true; BackColor = Theme.Panel2; }
            protected override void OnPaint(PaintEventArgs e)
            {
                int w = Width * value / 100;
                using (var b = new SolidBrush(Theme.Accent))
                    e.Graphics.FillRectangle(b, RightToLeft == RightToLeft.Yes ? Width - w : 0, 0, w, Height);
            }
        }
    }
}
