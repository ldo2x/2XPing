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
[assembly: AssemblyVersion("0.11.0.0")]
[assembly: AssemblyFileVersion("0.11.0.0")]
[assembly: AssemblyInformationalVersion("0.11.0-beta")]

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

        // Window/message captions: an explicit left-to-right embedding keeps "2X" in Latin digits in Arabic title bars.
        public static string Caption { get { return "‪" + Name + "‬"; } }

        public static void Open(string url)
        {
            try { Process.Start(url); } catch { }
        }

        static readonly System.Collections.Generic.Dictionary<string, Image> icons = new System.Collections.Generic.Dictionary<string, Image>();

        // Game icon embedded at build time as "game_<key>.png" (assets/games); null when missing.
        public static Image GameIcon(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            Image img;
            if (icons.TryGetValue(key, out img)) return img;
            var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("game_" + key + ".png");
            img = s != null ? Image.FromStream(s) : null;
            icons[key] = img;
            return img;
        }

        // Country flag embedded as "flag_<cc>.png" (assets/flags); null when missing.
        public static Image Flag(string cc)
        {
            return string.IsNullOrEmpty(cc) ? null : GameIconResource("flag_" + cc + ".png");
        }

        static Image GameIconResource(string name)
        {
            Image img;
            if (icons.TryGetValue(name, out img)) return img;
            var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            img = s != null ? Image.FromStream(s) : null;
            icons[name] = img;
            return img;
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
                f.Text = Brand.Caption;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.MinimizeBox = f.MaximizeBox = false;
                f.ShowInTaskbar = false;
                f.BackColor = Theme.Bg;
                f.ForeColor = Theme.TextC;
                f.Font = Fonts.Ui(10f);
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
                line(Brand.Name.ToUpperInvariant(), Fonts.Bold(22f), Theme.Accent, 46);
                line(Lang.T("Version ", "الإصدار ") + Brand.Version, f.Font, Theme.Dim, 24);
                if (Brand.IsBeta)
                    line(Lang.T("BETA - early test version", "نسخة تجريبية (Beta)"), Fonts.Semi(9.5f), Theme.Warn, 24);
                line(Lang.T("Made by ", "من تطوير ") + Brand.Developer, Fonts.Semi(12f), Theme.TextC, 34);
                line(Lang.T("Pick the best region for your game, lock it, and see your ping.",
                            "تختار أحسن منطقة للعبتك، وتثبّتها، وتشوف بنقك."), f.Font, Theme.Dim, 44);
                // separate lines so Arabic and Latin text never get reordered together
                line(Brand.Copyright, Fonts.Ui(9f), Theme.Dim, 20).RightToLeft = RightToLeft.No;
                line(Lang.T("Free and open source (MIT License)", "مجاني ومفتوح المصدر (رخصة MIT)"), Fonts.Ui(9f), Theme.Dim, 22);
                line("Discord: " + Brand.DiscordUser, Fonts.Ui(9f), Theme.Dim, 22).RightToLeft = RightToLeft.No;

                y += 8;
                Func<string, string, int, LinkLabel> link = (text, url, x) =>
                {
                    var l = new LinkLabel
                    {
                        Text = text, AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(x, y, 190, 24),
                        RightToLeft = Lang.Ar ? RightToLeft.Yes : RightToLeft.No, // keeps "GitHub" after the Arabic words
                        LinkColor = Theme.Accent, ActiveLinkColor = Color.White, LinkBehavior = LinkBehavior.HoverUnderline
                    };
                    l.LinkClicked += delegate { Brand.Open(url); };
                    f.Controls.Add(l);
                    return l;
                };
                // first link on the reading side (right in Arabic)
                link(Lang.T("GitHub page", "صفحة البرنامج على GitHub"), Brand.RepoUrl, Lang.Ar ? 220 : 30);
                link(Lang.T("Report a problem", "بلّغ عن مشكلة"), Brand.IssuesUrl, Lang.Ar ? 30 : 220);
                y += 30;

                var ok = new RoundButton
                {
                    Text = Lang.T("OK", "تمام"), DialogResult = DialogResult.OK, Bounds = new Rectangle(170, y + 8, 100, 34),
                    FlatStyle = FlatStyle.Flat, BackColor = Theme.Accent, ForeColor = Color.Black
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
                f.BackColor = Theme.Bg;
                f.ForeColor = Theme.TextC;
                f.Font = Fonts.Ui(10f);
                // RTL text without mirrored layout: the layout here is symmetric anyway
                f.RightToLeft = Lang.Ar ? RightToLeft.Yes : RightToLeft.No;

                int y = 16;
                f.Controls.Add(new Label
                {
                    Text = Lang.T("You're using a beta. Your feedback decides what we fix and add next.",
                                  "إنت تجرّب نسخة بيتا، ورأيك هو اللي يحدد وش نصلّح ونضيف بعدين."),
                    ForeColor = Theme.Warn, Bounds = new Rectangle(16, y, 448, 44)
                });
                y += 52;

                Action<string, string, Action<Button>> row = (title, sub, act) =>
                {
                    var b = new RoundButton
                    {
                        Text = title, Bounds = new Rectangle(16, y, 448, 40), FlatStyle = FlatStyle.Flat,
                        BackColor = Theme.Panel2, ForeColor = Theme.TextC, Cursor = Cursors.Hand,
                        TextAlign = ContentAlignment.MiddleLeft, // flipped to the right automatically in RTL
                        Padding = new Padding(10, 0, 10, 0)
                    };
                    b.FlatAppearance.BorderSize = 0;
                    b.Click += delegate { act(b); };
                    f.Controls.Add(b);
                    f.Controls.Add(new Label
                    {
                        Text = sub, ForeColor = Theme.Dim, Font = Fonts.Ui(8.5f),
                        Bounds = new Rectangle(18, y + 42, 446, 20)
                    });
                    y += 70;
                };

                row(Lang.T("Report a bug", "بلّغ عن مشكلة"),
                    Lang.T("GitHub Issues - tip: press 'Copy diagnostic info' first and paste it in",
                           "على GitHub. نصيحة: اضغط «انسخ معلومات التشخيص» أول، والصقها في البلاغ"),
                    b => Brand.Open(Brand.IssuesUrl + "/new/choose"));
                row(Lang.T("Suggest an idea / ask a question", "اقترح فكرة أو اسأل"),
                    Lang.T("GitHub Discussions", "ساحة النقاش على GitHub"),
                    b => Brand.Open(Brand.DiscussionsUrl));
                // copying shows "Copied ✓" on the button itself for a moment, no popup
                Action<Button, string> copied = (b, text) =>
                {
                    string before = b.Text;
                    b.Text = text;
                    b.ForeColor = Theme.Good;
                    var t = new Timer { Interval = 2000 };
                    t.Tick += delegate { t.Stop(); t.Dispose(); if (!b.IsDisposed) { b.Text = before; b.ForeColor = Theme.TextC; } };
                    t.Start();
                };
                row("Discord: " + Brand.DiscordUser,
                    string.IsNullOrEmpty(Brand.DiscordInvite)
                        ? Lang.T("Click to copy the username, then add it as a friend on Discord", "اضغط عشان تنسخ اليوزر، وبعدين ضيفه في ديسكورد")
                        : Lang.T("Join the community server", "ادخل سيرفر المجتمع"),
                    b =>
                    {
                        if (!string.IsNullOrEmpty(Brand.DiscordInvite)) { Brand.Open(Brand.DiscordInvite); return; }
                        try { Clipboard.SetText(Brand.DiscordUser); } catch { }
                        copied(b, Lang.T("Copied ✓  ", "انتسخ ✓  ") + Brand.DiscordUser);
                    });
                row(Lang.T("Copy diagnostic info", "انسخ معلومات التشخيص"),
                    Lang.T("Version, Windows and the recent log - no personal data", "الإصدار والويندوز وآخر السجل، بدون أي بيانات شخصية"),
                    b =>
                    {
                        try { Clipboard.SetText(Diagnostics(log)); } catch { }
                        copied(b, Lang.T("Copied ✓  paste it into your report", "انتسخ ✓  الصقه في البلاغ"));
                    });

                var ok = new RoundButton
                {
                    Text = Lang.T("Close", "إغلاق"), DialogResult = DialogResult.OK, Bounds = new Rectangle(184, y + 4, 112, 34),
                    FlatStyle = FlatStyle.Flat, BackColor = Theme.Accent, ForeColor = Color.Black
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

    static class CrashHandler
    {
        public static void Install()
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Report(e.Exception, false);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Report(e.ExceptionObject as Exception, e.IsTerminating);
        }

        // Errors are reported to us by themselves (no popup asking the player to do it): saved in crash.log and sent
        // as an issue. When the app is about to die, the report is sent right away instead of in the background.
        static void Report(Exception ex, bool dying)
        {
            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(Storage.Dir, "crash.log"), "==== " + DateTime.Now.ToString("u") + "  " +
                    Brand.Name + " " + Brand.DisplayVersion + "  " + Environment.OSVersion + Environment.NewLine + ex + Environment.NewLine);
            }
            catch { }
            string detail = ex != null ? ex.GetType().Name + ": " + ex.Message : "unknown";
            var data = new System.Collections.Generic.Dictionary<string, object> { { "stack", Privacy.ShortStack(ex) }, { "fatal", dying } };
            if (dying) Telemetry.SendNow("issue", "crash: " + detail, data);
            else Issues.Report("crash", detail, data, ex != null ? ex.GetType().Name + ex.Message : null);
        }
    }
}
