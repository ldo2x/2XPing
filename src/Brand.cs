using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

[assembly: AssemblyTitle("2X Ping")]
[assembly: AssemblyDescription("2X Ping - game connection optimizer by lDo2x")]
[assembly: AssemblyCompany("lDo2x")]
[assembly: AssemblyProduct("2X Ping")]
[assembly: AssemblyCopyright("Copyright © 2026 lDo2x. MIT License.")]
[assembly: AssemblyTrademark("2X Ping by lDo2x")]
// Release version: change it here (and tag the GitHub release v<version>-beta).
[assembly: AssemblyVersion("0.9.1.0")]
[assembly: AssemblyFileVersion("0.9.1.0")]
[assembly: AssemblyInformationalVersion("0.9.1-beta")]

namespace PingBoost
{
    // Product identity: name, developer, version, logo, project links.
    static class Brand
    {
        public const string Name = "2X Ping";
        public const string Developer = "lDo2x";
        public const string Year = "2026";
        public static readonly bool IsBeta = true;

        // GitHub repository (owner/name). Change the owner once the GitHub account exists.
        public const string Repo = "lDo2x/2XPing";
        public const string RepoUrl = "https://github.com/" + Repo;
        public const string IssuesUrl = RepoUrl + "/issues";
        public const string ReleasesUrl = RepoUrl + "/releases";
        public const string DiscussionsUrl = RepoUrl + "/discussions";

        // Community contact. Leave DiscordInvite empty until a server exists; it is hidden then.
        public const string DiscordUser = "2x.x";
        public const string DiscordInvite = "";

        public static readonly Version VersionNumber = Assembly.GetExecutingAssembly().GetName().Version;
        public static readonly string Version = VersionNumber.ToString(3);

        // "0.9.0 Beta"
        public static string DisplayVersion { get { return Version + (IsBeta ? " Beta" : ""); } }
        public static string Copyright { get { return "© " + Year + " " + Developer; } }

        public static void Open(string url)
        {
            try { Process.Start(url); } catch { }
        }

        static Image logo;
        public static Image Logo
        {
            get
            {
                if (logo == null)
                {
                    var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("logo.png");
                    logo = s != null ? Image.FromStream(s) : new Bitmap(1, 1);
                }
                return logo;
            }
        }
    }

    static class AboutDialog
    {
        public static void Show(IWin32Window owner)
        {
            using (var f = new Form())
            {
                f.Text = Brand.Name;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.MinimizeBox = f.MaximizeBox = false;
                f.ShowInTaskbar = false;
                f.BackColor = MainForm.Bg;
                f.ForeColor = MainForm.TextC;
                f.Font = new Font("Segoe UI", 10f);
                f.ClientSize = new Size(440, 420);

                var pic = new PictureBox
                {
                    Image = Brand.Logo, SizeMode = PictureBoxSizeMode.Zoom,
                    Bounds = new Rectangle(160, 22, 120, 120), BackColor = Color.Transparent
                };
                f.Controls.Add(pic);

                int y = 152;
                Func<string, Font, Color, int, Label> line = (text, font, color, h) =>
                {
                    var l = new Label
                    {
                        Text = text, Font = font, ForeColor = color, TextAlign = ContentAlignment.MiddleCenter,
                        Bounds = new Rectangle(20, y, 400, h), RightToLeft = Lang.Ar ? RightToLeft.Yes : RightToLeft.No
                    };
                    f.Controls.Add(l);
                    y += h;
                    return l;
                };
                line(Brand.Name.ToUpperInvariant(), new Font("Segoe UI Black", 22f), MainForm.Accent, 46);
                line(Lang.T("Version ", "الإصدار ") + Brand.Version, f.Font, MainForm.Dim, 24);
                if (Brand.IsBeta)
                    line(Lang.T("BETA - early test version", "نسخة تجريبية (Beta)"), new Font("Segoe UI Semibold", 9.5f), MainForm.Warn, 24);
                line(Lang.T("Made by ", "من تطوير ") + Brand.Developer, new Font("Segoe UI Semibold", 12f), MainForm.TextC, 34);
                line(Lang.T("Finds you the best server and route, and shows your ping live.",
                            "يلقى لك أحسن سيرفر وأحسن طريق، ويوريك البنق أول بأول."), f.Font, MainForm.Dim, 44);
                // separate lines so Arabic and Latin text never get reordered together
                line(Brand.Copyright, new Font("Segoe UI", 9f), MainForm.Dim, 20).RightToLeft = RightToLeft.No;
                line(Lang.T("Free and open source (MIT License)", "مجاني ومفتوح المصدر (رخصة MIT)"), new Font("Segoe UI", 9f), MainForm.Dim, 22);
                line("Discord: " + Brand.DiscordUser, new Font("Segoe UI", 9f), MainForm.Dim, 22).RightToLeft = RightToLeft.No;

                y += 8;
                Func<string, string, int, LinkLabel> link = (text, url, x) =>
                {
                    var l = new LinkLabel
                    {
                        Text = text, AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(x, y, 190, 24),
                        LinkColor = MainForm.Accent, ActiveLinkColor = Color.White, LinkBehavior = LinkBehavior.HoverUnderline
                    };
                    l.LinkClicked += delegate { Brand.Open(url); };
                    f.Controls.Add(l);
                    return l;
                };
                link(Lang.T("GitHub page", "صفحة البرنامج على GitHub"), Brand.RepoUrl, 30);
                link(Lang.T("Report a problem", "بلّغ عن مشكلة"), Brand.IssuesUrl, 220);
                y += 30;

                var ok = new Button
                {
                    Text = Lang.T("OK", "تمام"), DialogResult = DialogResult.OK, Bounds = new Rectangle(170, y + 8, 100, 34),
                    FlatStyle = FlatStyle.Flat, BackColor = MainForm.Accent, ForeColor = Color.Black
                };
                ok.FlatAppearance.BorderSize = 0;
                f.Controls.Add(ok);
                f.AcceptButton = ok;
                f.ClientSize = new Size(440, y + 58);
                f.ShowDialog(owner);
            }
        }
    }

    // "Feedback & contact": the beta tester's way to reach the developer.
    static class ContactDialog
    {
        public static void Show(IWin32Window owner, string log)
        {
            using (var f = new Form())
            {
                f.Text = Lang.T("Feedback & contact", "تواصل معنا");
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.MinimizeBox = f.MaximizeBox = false;
                f.ShowInTaskbar = false;
                f.BackColor = MainForm.Bg;
                f.ForeColor = MainForm.TextC;
                f.Font = new Font("Segoe UI", 10f);
                // RTL text without mirrored layout: the layout here is symmetric anyway
                f.RightToLeft = Lang.Ar ? RightToLeft.Yes : RightToLeft.No;

                int y = 16;
                f.Controls.Add(new Label
                {
                    Text = Lang.T("You are using a BETA. Your feedback decides what gets fixed next!",
                                  "إنت تجرّب نسخة بيتا، ورأيك هو اللي يحدد إيش نصلّح ونضيف بعدين!"),
                    ForeColor = MainForm.Warn, Bounds = new Rectangle(16, y, 448, 44)
                });
                y += 52;

                Action<string, string, Action> row = (title, sub, act) =>
                {
                    var b = new Button
                    {
                        Text = title, Bounds = new Rectangle(16, y, 448, 40), FlatStyle = FlatStyle.Flat,
                        BackColor = MainForm.Panel2, ForeColor = MainForm.TextC, Cursor = Cursors.Hand,
                        TextAlign = ContentAlignment.MiddleLeft, // flipped to the right automatically in RTL
                        Padding = new Padding(10, 0, 10, 0)
                    };
                    b.FlatAppearance.BorderSize = 0;
                    b.Click += delegate { act(); };
                    f.Controls.Add(b);
                    f.Controls.Add(new Label
                    {
                        Text = sub, ForeColor = MainForm.Dim, Font = new Font("Segoe UI", 8.5f),
                        Bounds = new Rectangle(18, y + 42, 446, 20)
                    });
                    y += 70;
                };

                row(Lang.T("Report a bug", "بلّغ عن مشكلة"),
                    Lang.T("GitHub Issues - tip: press 'Copy diagnostic info' first and paste it in",
                           "على GitHub. نصيحة: اضغط «انسخ معلومات التشخيص» أول، والصقها في البلاغ"),
                    () => Brand.Open(Brand.IssuesUrl + "/new/choose"));
                row(Lang.T("Suggest an idea / ask a question", "اقترح فكرة أو اسأل"),
                    Lang.T("GitHub Discussions", "ساحة النقاش على GitHub"),
                    () => Brand.Open(Brand.DiscussionsUrl));
                row("Discord: " + Brand.DiscordUser,
                    string.IsNullOrEmpty(Brand.DiscordInvite)
                        ? Lang.T("Click to copy the username, then add it as a friend on Discord", "اضغط عشان تنسخ اليوزر، وبعدين ضيفه في ديسكورد")
                        : Lang.T("Join the community server", "ادخل سيرفر المجتمع"),
                    () =>
                    {
                        if (!string.IsNullOrEmpty(Brand.DiscordInvite)) { Brand.Open(Brand.DiscordInvite); return; }
                        try { Clipboard.SetText(Brand.DiscordUser); } catch { }
                        MessageBox.Show(f, Lang.T("Copied: ", "نسخته: ") + Brand.DiscordUser, "‎" + Brand.Name);
                    });
                row(Lang.T("Copy diagnostic info", "انسخ معلومات التشخيص"),
                    Lang.T("Version, Windows and the recent log - no personal data", "الإصدار والويندوز وآخر السجل، بدون أي بيانات شخصية"),
                    () =>
                    {
                        try { Clipboard.SetText(Diagnostics(log)); } catch { }
                        MessageBox.Show(f, Lang.T("Copied - paste it into your report.", "نسخته! الصقه في البلاغ."), "‎" + Brand.Name);
                    });

                var ok = new Button
                {
                    Text = Lang.T("Close", "تمام"), DialogResult = DialogResult.OK, Bounds = new Rectangle(184, y + 4, 112, 34),
                    FlatStyle = FlatStyle.Flat, BackColor = MainForm.Accent, ForeColor = Color.Black
                };
                ok.FlatAppearance.BorderSize = 0;
                f.Controls.Add(ok);
                f.AcceptButton = ok;
                f.ClientSize = new Size(480, y + 52);
                f.ShowDialog(owner);
            }
        }

        public static string Diagnostics(string log)
        {
            var lines = (log ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var tail = string.Join(Environment.NewLine, lines, Math.Max(0, lines.Length - 30), Math.Min(30, lines.Length));
            return "```" + Environment.NewLine +
                   Brand.Name + " " + Brand.DisplayVersion + Environment.NewLine +
                   "Windows: " + Environment.OSVersion.Version + (Environment.Is64BitOperatingSystem ? " x64" : " x86") + Environment.NewLine +
                   ".NET: " + Environment.Version + Environment.NewLine +
                   "Language: " + (Lang.Ar ? "ar" : "en") + Environment.NewLine +
                   "WireGuard: " + (Tunnel.Installed ? "installed" : "not installed") + Environment.NewLine +
                   "--- log ---" + Environment.NewLine + tail + Environment.NewLine + "```";
        }
    }

    static class BetaNotice
    {
        // Shown on first launch of each beta version.
        public static void ShowOnce(IWin32Window owner)
        {
            if (!Brand.IsBeta) return;
            string key = "beta_ack";
            if (Storage.GetSetting(key) == Brand.Version) return;
            string text = Lang.T(
                "Welcome to 2X Ping " + Brand.DisplayVersion + "!\n\n" +
                "This is a BETA version: features may change and you may hit bugs.\n\n" +
                "- Results (player numbers, match time) are estimates.\n" +
                "- Region lock uses Windows Firewall and is removed when you close the app.\n" +
                "- Use it at your own risk; 2X Ping does not modify any game files.\n\n" +
                "Found a problem or have an idea? Press the Feedback button (top of the window).",
                "يا هلا فيك في ‎2X Ping " + Brand.DisplayVersion + "‎!\n\n" +
                "هادي نسخة بيتا، يعني لسا نجرّب ونطوّر، وممكن تطلع لك أخطاء بسيطة.\n\n" +
                "- عدد اللاعبين ووقت الانتظار أرقام تقريبية.\n" +
                "- تثبيت السيرفرات يشتغل عن طريق جدار حماية ويندوز، وينشال لحاله لما تقفل البرنامج.\n" +
                "- البرنامج ما يلمس ملفات اللعبة أبداً، والاستخدام على مسؤوليتك.\n\n" +
                "لقيت مشكلة أو عندك فكرة؟ اضغط «تواصل معنا» فوق.");
            MessageBox.Show(owner, text, Brand.Name + " Beta", MessageBoxButtons.OK, MessageBoxIcon.Information,
                MessageBoxDefaultButton.Button1, Lang.Ar ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : 0);
            Storage.SetSetting(key, Brand.Version);
        }
    }

    static class Updater
    {
        // Newest release tag on GitHub (pre-releases included), or null when there is nothing newer / no network.
        public static string NewerVersion()
        {
            try
            {
                string json = Http.Get("https://api.github.com/repos/" + Brand.Repo + "/releases?per_page=5", 10000);
                var list = (object[])new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(json);
                Version best = null;
                string bestTag = null;
                foreach (var o in list)
                {
                    var d = (System.Collections.Generic.Dictionary<string, object>)o;
                    var tag = (string)d["tag_name"];
                    var m = System.Text.RegularExpressions.Regex.Match(tag ?? "", @"(\d+)\.(\d+)\.(\d+)");
                    if (!m.Success) continue;
                    var v = new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
                    if (best == null || v > best) { best = v; bestTag = tag; }
                }
                var mine = new Version(Brand.VersionNumber.Major, Brand.VersionNumber.Minor, Brand.VersionNumber.Build);
                return best != null && best > mine ? bestTag : null;
            }
            catch { return null; }
        }
    }

    static class CrashHandler
    {
        public static void Install()
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Report(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Report(e.ExceptionObject as Exception);
        }

        static void Report(Exception ex)
        {
            string path = System.IO.Path.Combine(Storage.Dir, "crash.log");
            try
            {
                System.IO.File.AppendAllText(path, "==== " + DateTime.Now.ToString("u") + "  " + Brand.Name + " " + Brand.DisplayVersion +
                    "  " + Environment.OSVersion + Environment.NewLine + ex + Environment.NewLine);
            }
            catch { }
            var answer = MessageBox.Show(Lang.T(
                    "Something went wrong:\n" + (ex != null ? ex.Message : "") + "\n\nDetails were saved to:\n" + path +
                    "\n\nOpen the issue page to report it?",
                    "صار خطأ ما توقعناه:\n" + (ex != null ? ex.Message : "") + "\n\nحفظت التفاصيل هنا:\n" + path +
                    "\n\nتبغى تفتح صفحة البلاغات وترسله لنا؟"),
                Brand.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Error);
            if (answer == DialogResult.Yes) Brand.Open(Brand.IssuesUrl);
        }
    }
}
