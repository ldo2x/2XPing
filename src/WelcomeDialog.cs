using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace PingBoost
{
    // First launch: name + clear notice of what is collected. Accepting is required to use the beta.
    static class WelcomeDialog
    {
        public static bool Show(IWin32Window owner)
        {
            using (var f = new Form())
            {
                f.Text = Brand.Caption;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterScreen;
                f.MinimizeBox = f.MaximizeBox = false;
                f.BackColor = Theme.Bg;
                f.ForeColor = Theme.TextC;
                f.Font = Fonts.Ui(10f);
                f.RightToLeft = Lang.Ar ? RightToLeft.Yes : RightToLeft.No;
                const int W = 480;

                f.Controls.Add(new PictureBox { Image = Brand.Logo, SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(228, 14, 64, 64) });
                f.Controls.Add(new Label
                {
                    Text = Lang.T("Welcome to 2X Ping", "هلا فيك في 2X Ping"), Font = Fonts.Semi(15f),
                    ForeColor = Theme.Accent, TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(20, 82, W, 36)
                });
                // a card with its title; content starts at y = 46 inside it
                Func<int, int, string, Card> section = (cy, h, title) =>
                {
                    var c = new Card { Bounds = new Rectangle(20, cy, W, h), Fill = Theme.Panel1, Radius = 14 };
                    c.Controls.Add(new Label
                    {
                        Text = title, Font = Fonts.Semi(10.5f), ForeColor = Theme.Accent, Bounds = new Rectangle(16, 12, W - 32, 26)
                    });
                    f.Controls.Add(c);
                    return c;
                };

                var nameCard = section(128, 104, Lang.T("What's your name?", "وش اسمك؟"));
                var name = new InputBox { Bounds = new Rectangle(16, 46, W - 32, 42), Placeholder = Lang.T("Your name", "اسمك") };
                name.Text = Telemetry.Nickname;
                name.Box.MaxLength = 30;
                nameCard.Controls.Add(name);

                // the games this player cares about, two per row, in reading order
                var all = GameDb.Available(GameDb.All());
                int gamesH = 70 + (all.Count + 1) / 2 * 54 + 8;
                var gamesCard = section(244, gamesH, Lang.T("Which games do you play?", "وش الألعاب اللي تلعبها؟"));
                gamesCard.Controls.Add(new Label
                {
                    Text = Lang.T("Only these show up in your list. You can change them later in Settings.",
                                  "اللي تختاره بس يطلع لك، وتقدر تغيّره بعدين من الإعدادات."),
                    Bounds = new Rectangle(16, 40, W - 32, 22), ForeColor = Theme.Dim, Font = Fonts.Ui(8.5f)
                });
                var picks = new List<GameTile>();
                const int tw = (W - 32 - 10) / 2;
                for (int i = 0; i < all.Count; i++)
                {
                    bool first = i % 2 == 0;
                    var tile = new GameTile
                    {
                        Text = all[i].Name, Icon = Brand.GameIcon(all[i].Icon), Tag = all[i].Icon,
                        Bounds = new Rectangle(first ^ Lang.Ar ? 16 : 16 + tw + 10, 70 + i / 2 * 54, tw, 46)
                    };
                    gamesCard.Controls.Add(tile);
                    picks.Add(tile);
                }
                int y = 244 + gamesH + 12;

                // what is sent, in general words (the full list is behind the details link, SECURITY.md). The statistics switch
                // has no box here: the text says where to make it anonymous (Settings).
                const int NoticeH = 120;
                var noticeCard = new Card { Bounds = new Rectangle(20, y, W, NoticeH), Fill = Theme.Panel1, Radius = 14 };
                f.Controls.Add(noticeCard);
                var notice = new Label
                {
                    Bounds = new Rectangle(14, 10, W - 28, NoticeH - 44), ForeColor = Theme.Dim, Font = Fonts.Ui(9.5f),
                    Text = Lang.T(
                        "This is a beta. To keep improving 2X Ping, it shares usage and performance info with us (like ping and errors). " +
                        "You can make it anonymous any time in Settings.\n" +
                        "Updates are required: you get " + Updater.Days(Updater.GraceDays) + " to install each one.",
                        "هادي نسخة تجريبية، وعشان نطوّر البرنامج يشارك معانا معلومات عن الاستخدام والأداء (زي البنق والأخطاء). " +
                        "وتقدر تخليها مجهولة أي وقت من الإعدادات.\n" +
                        "التحديثات إجبارية، وعندك " + Updater.Days(Updater.GraceDays) + " لكل تحديث.")
                };
                noticeCard.Controls.Add(notice);
                var details = new LinkLabel
                {
                    Text = Lang.T("Privacy and statistics details", "الخصوصية وتفاصيل الإحصائيات"), Bounds = new Rectangle(14, NoticeH - 32, W - 28, 24),
                    // the link inherits the right-to-left reading: MiddleLeft is the reading start (the right in Arabic)
                    BackColor = Theme.Panel1, TextAlign = ContentAlignment.MiddleLeft,
                    LinkColor = Theme.Lavender, ActiveLinkColor = Color.White, LinkBehavior = LinkBehavior.HoverUnderline
                };
                details.LinkClicked += delegate { Brand.Open(Brand.PrivacyUrl); };
                noticeCard.Controls.Add(details);
                y += NoticeH + 8;

                // why "Agree and start" can't be pressed yet
                var why = new Label
                {
                    Text = Lang.T("Write your name and pick at least one game", "اكتب اسمك واختار لعبة وحدة على الأقل"),
                    Bounds = new Rectangle(20, y, W, 22), ForeColor = Theme.Warn, Font = Fonts.Ui(9f),
                    TextAlign = ContentAlignment.MiddleCenter
                };
                f.Controls.Add(why);
                y += 26;
                var ok = new RoundButton
                {
                    Text = Lang.T("Agree and start", "موافق وأبدأ"), DialogResult = DialogResult.OK,
                    Bounds = new Rectangle(Lang.Ar ? 280 : 20, y, 220, 40), FlatStyle = FlatStyle.Flat,
                    BackColor = Theme.Accent, ForeColor = Color.Black, Font = Fonts.Semi(10.5f), Enabled = false
                };
                var exit = new RoundButton
                {
                    Text = Lang.T("Exit", "خروج"), DialogResult = DialogResult.Cancel,
                    Bounds = new Rectangle(Lang.Ar ? 20 : 280, y, 220, 40), FlatStyle = FlatStyle.Flat,
                    BackColor = Theme.Panel2, ForeColor = Theme.TextC
                };
                ok.FlatAppearance.BorderSize = exit.FlatAppearance.BorderSize = 0;
                // ready once there is a name and at least one game; until then quiet but readable, with the reason above it
                EventHandler refresh = delegate
                {
                    ok.Enabled = name.Text.Trim().Length >= 2 && picks.Any(p => p.Checked);
                    ok.BackColor = ok.Enabled ? Theme.Accent : Theme.Panel2;
                    ok.ForeColor = ok.Enabled ? Color.Black : Theme.TextC;
                    why.Visible = !ok.Enabled;
                };
                name.Box.TextChanged += refresh;
                foreach (var p in picks) p.CheckedChanged += refresh;
                refresh(null, EventArgs.Empty);
                f.Controls.Add(ok);
                f.Controls.Add(exit);
                f.AcceptButton = ok;
                f.CancelButton = exit;
                f.ClientSize = new Size(W + 40, y + 56);

                if (f.ShowDialog(owner) != DialogResult.OK) return false;
                Telemetry.Nickname = name.Text;
                Prefs.HiddenGames = new HashSet<string>(picks.Where(p => !p.Checked).Select(p => (string)p.Tag));
                Storage.SetSetting("consent", "1");
                return true;
            }
        }
    }
}
