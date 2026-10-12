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

        // The newest release on GitHub that is newer than this one (pre-releases included), or null.
        public static Release Latest()
        {
            try
            {
                string json = Http.Get("https://api.github.com/repos/" + Brand.Repo + "/releases?per_page=10", 10000);
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
            catch { return null; }
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
            int n = (int)Math.Ceiling(d);
            return Lang.T(n + (n == 1 ? " day" : " days"), n == 1 ? "يوم" : n == 2 ? "يومين" : n + " أيام");
        }

        // Downloads the release, checks its SHA256, swaps the exe and starts the new version.
        // Returns null when the new version is starting (the caller then closes), otherwise what went wrong.
        public static string Install(Release r, Action<int> progress)
        {
            string fresh;
            string err = Fetch(r, progress, out fresh);
            if (err != null) return err;

            // a running exe can be renamed but not overwritten
            string exe = Application.ExecutablePath, old = exe + ".old";
            try { if (File.Exists(old)) File.Delete(old); } catch { }
            File.Move(exe, old);
            try { File.Move(fresh, exe); }
            catch { File.Move(old, exe); throw; }

            Process.Start(new ProcessStartInfo(exe, "--updated " + Process.GetCurrentProcess().Id) { UseShellExecute = true });
            return null;
        }

        // Downloads the release zip, checks its SHA256 and extracts 2XPing.exe. Returns null when fresh is ready.
        public static string Fetch(Release r, Action<int> progress, out string fresh)
        {
            fresh = null;
            if (r.ZipUrl == null || r.ShaUrl == null) return Lang.T("the release has no download file", "الإصدار ما فيه ملف تحميل");
            string dir = Path.Combine(Path.GetTempPath(), "2XPing-update");
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            Directory.CreateDirectory(dir);
            string zip = Path.Combine(dir, "update.zip");
            Download(r.ZipUrl, zip, progress);

            string expected = Http.Get(r.ShaUrl, 15000).Trim().Split(' ', '\t')[0].ToLowerInvariant();
            string actual;
            using (var sha = SHA256.Create())
            using (var s = File.OpenRead(zip))
                actual = BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
            if (expected != actual) return Lang.T("the download is damaged (checksum mismatch)", "الملف وصل خربان (البصمة ما تطابق)");

            string exePath = Path.Combine(dir, "2XPing.exe");
            using (var archive = ZipFile.OpenRead(zip))
            {
                var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals("2XPing.exe", StringComparison.OrdinalIgnoreCase));
                if (entry == null) return Lang.T("2XPing.exe is missing from the download", "ما لقيت 2XPing.exe داخل الملف");
                entry.ExtractToFile(exePath, true);
            }
            fresh = exePath;
            return null;
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
                f.BackColor = MainForm.Bg;
                f.ForeColor = MainForm.TextC;
                f.Font = Fonts.Ui(10f);
                f.RightToLeft = Lang.Ar ? RightToLeft.Yes : RightToLeft.No;
                const int W = 440;

                f.Controls.Add(new PictureBox { Image = Brand.Logo, SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(208, 18, 64, 64) });
                f.Controls.Add(new Label
                {
                    Text = Lang.T("New version: ", "نسخة جديدة: ") + "‎" + r.Ver.ToString(3) + "‎",
                    Font = Fonts.Semi(14f), ForeColor = MainForm.Accent, TextAlign = ContentAlignment.MiddleCenter,
                    Bounds = new Rectangle(20, 90, W, 34)
                });
                var text = new Label
                {
                    ForeColor = MainForm.TextC, TextAlign = ContentAlignment.TopCenter, Bounds = new Rectangle(20, 130, W, 52),
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
                var status = new Label { ForeColor = MainForm.Dim, TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(20, 198, W, 24), Font = Fonts.Ui(9f) };
                f.Controls.Add(status);

                var go = new RoundButton
                {
                    Text = Lang.T("Update now", "حدّث دحين"), Bounds = new Rectangle(Lang.Ar ? 240 : 20, 232, 220, 40),
                    FlatStyle = FlatStyle.Flat, BackColor = MainForm.Accent, ForeColor = Color.Black, Font = Fonts.Semi(10.5f), Cursor = Cursors.Hand
                };
                var later = new RoundButton
                {
                    Text = forced ? Lang.T("Close the app", "اقفل البرنامج") : Lang.T("Later", "بعدين"),
                    DialogResult = DialogResult.Cancel, Bounds = new Rectangle(Lang.Ar ? 20 : 240, 232, 200, 40),
                    FlatStyle = FlatStyle.Flat, BackColor = MainForm.Panel2, ForeColor = MainForm.TextC, Cursor = Cursors.Hand
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
                    status.ForeColor = MainForm.Dim;
                    status.Text = Lang.T("Downloading...", "أنزّل التحديث...");
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
                            status.ForeColor = MainForm.Bad;
                            status.Text = Lang.T("Couldn't update: ", "ما زبط التحديث: ") + err;
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
            public ProgressPanel() { DoubleBuffered = true; BackColor = MainForm.Panel2; }
            protected override void OnPaint(PaintEventArgs e)
            {
                int w = Width * value / 100;
                using (var b = new SolidBrush(MainForm.Accent))
                    e.Graphics.FillRectangle(b, RightToLeft == RightToLeft.Yes ? Width - w : 0, 0, w, Height);
            }
        }
    }
}
