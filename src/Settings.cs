using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace PingBoost
{
    // User preferences (stored in settings.txt).
    static class Prefs
    {
        public static bool Simple { get { return Storage.GetSetting("mode") != "advanced"; } }
        public static bool AutoTest { get { return Storage.GetSetting("autotest") != "0"; } }
        // Deadlock: keep Dubai open as the way in when the lock doesn't pick SE Asia (DeadlockData.Gateways); on by default
        public static bool DubaiWayIn { get { return Storage.GetSetting("dubai_way_in") != "0"; } }
        // «شارك إحصائيات البيتا»: on by default; off, the beta statistics are sent without anything personal (Telemetry.Payload)
        public static bool ShareStats { get { return Storage.GetSetting("share_stats") != "0"; } }

        // games the player unticked in Settings (by icon key)
        public static HashSet<string> HiddenGames
        {
            get
            {
                var v = Storage.GetSetting("hidden_games") ?? "";
                return new HashSet<string>(v.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
            }
            set { Storage.SetSetting("hidden_games", string.Join(",", value)); }
        }
    }

    static class SettingsDialog
    {
        // Returns true when the language, the layout or the games changed, so the main window must be rebuilt.
        public static bool Show(IWin32Window owner, out bool resetAsked)
        {
            resetAsked = false;
            bool wantReset = false;
            bool wasAr = Lang.Ar, wasSimple = Prefs.Simple;
            using (var f = new Form())
            {
                f.Text = Lang.T("Settings", "الإعدادات");
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.MinimizeBox = f.MaximizeBox = false;
                f.ShowInTaskbar = false;
                f.BackColor = Theme.Bg;
                f.ForeColor = Theme.TextC;
                f.Font = Fonts.Ui(10f);
                f.RightToLeft = Lang.Ar ? RightToLeft.Yes : RightToLeft.No;

                // two columns of cards: settings on the reading side, games and more on the other
                const int W = 400, M = 18, Gap = 12;
                int colA = Lang.Ar ? M + W + M : M, colB = Lang.Ar ? M : M + W + M;

                // a card with its title; content starts at y = 46 inside it
                Func<int, int, int, string, Card> section = (cx, cy, h, title) =>
                {
                    var c = new Card { Bounds = new Rectangle(cx, cy, W, h), Fill = Theme.Panel1, Radius = 14 };
                    c.Controls.Add(new Label
                    {
                        Text = title, Font = Fonts.Semi(10.5f), ForeColor = Theme.Accent, Bounds = new Rectangle(16, 12, W - 32, 26)
                    });
                    f.Controls.Add(c);
                    return c;
                };

                // ---- your name
                int y = M;
                var nameCard = section(colA, y, 104, Lang.T("Your name", "اسمك"));
                var nick = new InputBox { Bounds = new Rectangle(16, 46, W - 32, 42), Placeholder = Lang.T("Your name", "اسمك") };
                nick.Text = Telemetry.Nickname;
                nick.Box.MaxLength = 30;
                nameCard.Controls.Add(nick);
                y += 104 + Gap;

                // ---- language
                var langCard = section(colA, y, 104, Lang.T("Language", "اللغة"));
                var lang = new Segmented { Bounds = new Rectangle(16, 46, W - 32, 42), Items = new[] { "العربية", "English" }, Font = Fonts.Ui(10f) };
                lang.SelectedIndex = Lang.Ar ? 0 : 1;
                langCard.Controls.Add(lang);
                y += 104 + Gap;

                // ---- layout, with a line that says what the chosen one shows
                var layoutCard = section(colA, y, 132, Lang.T("Layout", "شكل البرنامج"));
                var layout = new Segmented { Bounds = new Rectangle(16, 46, W - 32, 42), Items = new[] { Lang.T("Simple", "بسيط"), Lang.T("Advanced", "متقدم") }, Font = Fonts.Ui(10f) };
                layout.SelectedIndex = Prefs.Simple ? 0 : 1;
                var layoutHint = new Label { Bounds = new Rectangle(16, 94, W - 32, 26), ForeColor = Theme.Dim, Font = Fonts.Ui(9f) };
                Action hint = () => layoutHint.Text = layout.SelectedIndex == 0
                    ? Lang.T("Just what you need: pick a region, see your ping, play.", "الأشياء المهمة بس: اختار منطقتك، شوف البنق، والعب.")
                    : Lang.T("Everything: relays, jitter, packet loss, tools and the log.", "كل شي: سيرفرات الوسيط، التذبذب، الفقد، الأدوات والسجل.");
                hint();
                layout.SelectedIndexChanged += delegate { hint(); };
                layoutCard.Controls.Add(layout);
                layoutCard.Controls.Add(layoutHint);
                y += 132 + Gap;

                // ---- options, with the beta statistics switch and what turning it off means
                const int OptH = 190;
                var optCard = section(colA, y, OptH, Lang.T("Options", "خيارات"));
                var auto = new Toggle { Bounds = new Rectangle(16, 46, W - 32, 32), Text = Lang.T("Test the ping when I pick a game", "افحص البنق لما أختار لعبة") };
                auto.SetInstant(Prefs.AutoTest);
                optCard.Controls.Add(auto);
                var share = new Toggle { Bounds = new Rectangle(16, 84, W - 32, 32), Text = Lang.T("Share beta statistics", "شارك إحصائيات البيتا") };
                share.SetInstant(Prefs.ShareStats);
                optCard.Controls.Add(share);
                optCard.Controls.Add(new Label
                {
                    Text = Lang.T("Off: we only get anonymous usage, without your name, PC name, city, internet provider or PC details.",
                                  "إذا طفّيته: يوصلنا استخدام مجهول بس، بدون اسمك واسم جهازك ومدينتك وشركة النت ومواصفات جهازك."),
                    ForeColor = Theme.Dim, Font = Fonts.Ui(8.5f), Bounds = new Rectangle(16, 120, W - 32, 36)
                });
                var shareDetails = new LinkLabel
                {
                    Text = Lang.T("What's sent", "وش اللي ينرسل"), Bounds = new Rectangle(16, 158, W - 32, 22), Font = Fonts.Ui(8.5f),
                    BackColor = Theme.Panel1, TextAlign = Lang.Ar ? ContentAlignment.MiddleRight : ContentAlignment.MiddleLeft,
                    LinkColor = Theme.Lavender, ActiveLinkColor = Color.White, LinkBehavior = LinkBehavior.HoverUnderline
                };
                shareDetails.LinkClicked += delegate { Brand.Open(Brand.PrivacyUrl); };
                optCard.Controls.Add(shareDetails);
                int bottomA = y + OptH;

                // ---- games: tiles, two per row, the first on the reading side
                var list = GameDb.Available(GameDb.All());
                var hiddenBefore = Prefs.HiddenGames;
                int rows = (list.Count + 1) / 2;
                // the games whose server lock is still in development say so under the tiles (the game list's tag)
                var dev = list.Where(g => g.InDevelopment).Select(g => g.Name).ToList();
                var check = list.Where(g => g.ServerCheckOnly).Select(g => g.Name).ToList();
                const int DevNoteH = 40;
                int gamesH = 46 + rows * 54 + 8 + (dev.Count > 0 || check.Count > 0 ? DevNoteH : 0);
                y = M;
                var gamesCard = section(colB, y, gamesH, Lang.T("Games in your list", "الألعاب اللي تظهر لك"));
                var tiles = new List<GameTile>();
                const int tw = (W - 32 - 10) / 2;
                for (int i = 0; i < list.Count; i++)
                {
                    bool first = i % 2 == 0;
                    var t = new GameTile
                    {
                        Text = list[i].Name, Icon = Brand.GameIcon(list[i].Icon), Tag = list[i].Icon,
                        Bounds = new Rectangle(first ^ Lang.Ar ? 16 : 16 + tw + 10, 46 + i / 2 * 54, tw, 46)
                    };
                    t.Checked = !hiddenBefore.Contains(list[i].Icon);
                    gamesCard.Controls.Add(t);
                    tiles.Add(t);
                }
                if (dev.Count > 0 || check.Count > 0)
                    gamesCard.Controls.Add(new Label
                    {
                        Text = Lang.T(
                            (check.Count > 0 ? CardLogic.JoinNames(check) + " only shows which server you're on. " : "") +
                            (dev.Count > 0 ? "The server lock for " + CardLogic.JoinNames(dev) + " is still in development." : ""),
                            (check.Count > 0 ? CardLogic.JoinNames(check) + " يعرض سيرفرك بس. " : "") +
                            (dev.Count > 0 ? "تثبيت السيرفرات في " + CardLogic.JoinNames(dev) + " لسا قيد التطوير." : "")).Trim(),
                        ForeColor = Theme.Warn, Font = Fonts.Ui(8.5f), Bounds = new Rectangle(16, 46 + rows * 54, W - 32, DevNoteH)
                    });
                y += gamesH + Gap;

                // ---- more
                var moreCard = section(colB, y, 144, Lang.T("More", "أكثر"));
                const int bw = (W - 32 - 10) / 2;
                var about = new RoundButton { Text = Lang.T("About 2X Ping", "عن البرنامج"), BackColor = Theme.Panel2, ForeColor = Theme.TextC,
                                              Bounds = new Rectangle(Lang.Ar ? 16 + bw + 10 : 16, 46, bw, 38) };
                var reset = new RoundButton { Text = Lang.T("Back to normal", "رجّع كل شي طبيعي"), BackColor = Theme.Panel2, ForeColor = Theme.TextC,
                                              Bounds = new Rectangle(Lang.Ar ? 16 : 16 + bw + 10, 46, bw, 38) };
                about.Click += delegate { AboutDialog.Show(f); };
                reset.Click += delegate { wantReset = true; f.DialogResult = DialogResult.Cancel; f.Close(); };
                moreCard.Controls.Add(about);
                moreCard.Controls.Add(reset);
                moreCard.Controls.Add(new Label
                {
                    Text = Lang.T("\"Back to normal\" unlocks your servers. Closing the app does it too.",
                                  "«رجّع كل شي طبيعي» يفك تثبيت السيرفرات، ونفس الشي يصير لما تقفل البرنامج."),
                    ForeColor = Theme.Dim, Font = Fonts.Ui(8.5f), Bounds = new Rectangle(16, 94, W - 32, 40)
                });
                y += 144;

                // ---- Deadlock: Dubai as a way to Europe (DeadlockData.Gateways), with what each side means. In the
                // shorter column so the dialog fits a 768-px screen; only while Deadlock is in the game list.
                Toggle dubai = null;
                if (list.Any(g => DeadlockData.Is(g)) && !hiddenBefore.Contains("deadlock"))
                {
                    y += Gap;
                    string dubaiText = Lang.T(
                        "Valve's Dubai servers host SE Asia matches, and they're also the Gulf's shortest way to Europe's servers.\n" +
                        "On: when you lock to a region other than SE Asia (Europe, North America...), Dubai stays open so your ping is lower. Sometimes you may get a match in Dubai (the app tells you).\n" +
                        "Off: Dubai is closed unless you pick SE Asia, so you never get a match there, but your ping may go up.",
                        "سيرفرات Valve في دبي تستضيف مباريات جنوب شرق آسيا، وهي كمان أقصر طريق للخليج لسيرفرات أوروبا.\n" +
                        "إذا شغّلته: لما تثبّت على منطقة غير جنوب شرق آسيا (أوروبا، أمريكا الشمالية...) نخلّي دبي مفتوحة عشان بنقك يكون أقل، وأحياناً ممكن تطلع لك مباراة في دبي (البرنامج ينبهك).\n" +
                        "إذا طفّيته: نقفل دبي إلا لو اخترت جنوب شرق آسيا، فما تطلع لك مباراة فيها أبداً، بس بنقك ممكن يزيد.");
                    var dubaiFont = Fonts.Ui(8.5f);
                    int dubaiH = TextRenderer.MeasureText(dubaiText, dubaiFont, new Size(W - 32, 0),
                                                          TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height + 6;
                    int dubaiCardH = 46 + 32 + 6 + dubaiH + 10;
                    var dubaiCard = section(colB, y, dubaiCardH, "Deadlock");
                    dubai = new Toggle { Bounds = new Rectangle(16, 46, W - 32, 32), Text = CardLogic.GatewaySettingName() };
                    dubai.SetInstant(Prefs.DubaiWayIn);
                    dubaiCard.Controls.Add(dubai);
                    dubaiCard.Controls.Add(new Label
                    {
                        Text = dubaiText, ForeColor = Theme.Dim, Font = dubaiFont, Bounds = new Rectangle(16, 46 + 32 + 6, W - 32, dubaiH)
                    });
                    y += dubaiCardH;
                }
                int bottomB = y;

                // ---- save / cancel, on the reading side's far end
                y = Math.Max(bottomA, bottomB) + 16;
                int right = M + W + M + W;
                var save = new RoundButton { Text = Lang.T("Save", "حفظ"), DialogResult = DialogResult.OK, BackColor = Theme.Accent, ForeColor = Color.Black,
                                             Font = Fonts.Semi(10f), Bounds = new Rectangle(Lang.Ar ? M : right - 120, y, 120, 40) };
                var cancel = new RoundButton { Text = Lang.T("Cancel", "إلغاء"), DialogResult = DialogResult.Cancel, BackColor = Theme.Panel2, ForeColor = Theme.TextC,
                                               Bounds = new Rectangle(Lang.Ar ? M + 130 : right - 250, y, 120, 40) };
                f.Controls.Add(save);
                f.Controls.Add(cancel);
                f.AcceptButton = save;
                f.CancelButton = cancel;
                f.ClientSize = new Size(right + M, y + 40 + M);

                if (f.ShowDialog(owner) != DialogResult.OK)
                {
                    resetAsked = wantReset;
                    return false;
                }

                if (nick.Text.Trim().Length >= 2) Telemetry.Nickname = nick.Text;
                bool ar = lang.SelectedIndex == 0;
                Storage.SaveArabic(ar);
                Storage.SetSetting("mode", layout.SelectedIndex == 0 ? "simple" : "advanced");
                Storage.SetSetting("autotest", auto.Checked ? "1" : "0");
                Storage.SetSetting("share_stats", share.Checked ? "1" : "0"); // counts from the next statistics sent
                if (dubai != null) Storage.SetSetting("dubai_way_in", dubai.Checked ? "1" : "0");
                Lang.Ar = ar;

                // nothing ticked = show every game, so the list is never empty (games hidden in the public
                // version stay as they were)
                var hiddenNow = new HashSet<string>(hiddenBefore.Where(k => !tiles.Any(t => (string)t.Tag == k)));
                foreach (var t in tiles) if (!t.Checked) hiddenNow.Add((string)t.Tag);
                if (tiles.All(t => !t.Checked)) foreach (var t in tiles) hiddenNow.Remove((string)t.Tag);
                Prefs.HiddenGames = hiddenNow;
                bool gamesChanged = !hiddenNow.SetEquals(hiddenBefore);

                return wasAr != Lang.Ar || wasSimple != Prefs.Simple || gamesChanged;
            }
        }
    }
}
