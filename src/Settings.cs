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
        public static bool CheckUpdates { get { return Storage.GetSetting("updates") != "0"; } }

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
        // Returns true when the language or the layout changed, so the main window must be rebuilt.
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
                f.BackColor = MainForm.Bg;
                f.ForeColor = MainForm.TextC;
                f.Font = new Font("Segoe UI", 10f);
                // RTL text, no mirrored layout: every control spans the full width
                f.RightToLeft = Lang.Ar ? RightToLeft.Yes : RightToLeft.No;

                // two columns: settings on the reading side, games + actions on the other
                const int W = 400;
                int colA = Lang.Ar ? 440 : 20, colB = Lang.Ar ? 20 : 440;
                int x = colA, y = 14;
                Func<string, Label> heading = text =>
                {
                    var l = new Label
                    {
                        Text = text, Font = new Font("Segoe UI Semibold", 10.5f), ForeColor = MainForm.Accent,
                        Bounds = new Rectangle(x, y, W, 26)
                    };
                    f.Controls.Add(l);
                    y += 30;
                    return l;
                };
                Func<string, string, bool, RadioButton> radio = (text, sub, on) =>
                {
                    var r = new RadioButton { Text = text, Checked = on, Bounds = new Rectangle(x, y, W, 26), ForeColor = MainForm.TextC };
                    f.Controls.Add(r);
                    y += 26;
                    if (sub != null)
                    {
                        f.Controls.Add(new Label
                        {
                            Text = sub, ForeColor = MainForm.Dim, Font = new Font("Segoe UI", 8.5f),
                            Bounds = new Rectangle(x, y, W, 20)
                        });
                        y += 24;
                    }
                    return r;
                };
                Func<string, bool, CheckBox> check = (text, on) =>
                {
                    var c = new CheckBox { Text = text, Checked = on, Bounds = new Rectangle(x, y, W, 26), ForeColor = MainForm.TextC };
                    f.Controls.Add(c);
                    y += 30;
                    return c;
                };

                heading(Lang.T("Your name", "اسمك"));
                var nick = new TextBox
                {
                    Text = Telemetry.Nickname, Bounds = new Rectangle(x, y, W, 28), MaxLength = 30,
                    BackColor = MainForm.Panel2, ForeColor = MainForm.TextC, BorderStyle = BorderStyle.FixedSingle
                };
                f.Controls.Add(nick);
                y += 40;

                heading(Lang.T("Language", "اللغة"));
                var ar = radio("العربية", null, Lang.Ar);
                var en = radio("English", null, !Lang.Ar);
                y += 8;

                heading(Lang.T("Layout", "شكل البرنامج"));
                var simple = radio(Lang.T("Simple", "بسيط"),
                    Lang.T("Just what you need: pick a server, see your ping, play.",
                           "الأشياء المهمة بس: اختار سيرفرك، شوف البنق، والعب."), Prefs.Simple);
                var adv = radio(Lang.T("Advanced", "متقدم"),
                    Lang.T("Everything: relays, jitter, packet loss, tools and the log.",
                           "كل شي: سيرفرات الوسيط، التذبذب، الفقد، الأدوات والسجل."), !Prefs.Simple);
                simple.CheckedChanged += delegate { if (simple.Checked) adv.Checked = false; };
                adv.CheckedChanged += delegate { if (adv.Checked) simple.Checked = false; };
                ar.CheckedChanged += delegate { if (ar.Checked) en.Checked = false; };
                en.CheckedChanged += delegate { if (en.Checked) ar.Checked = false; };
                // WinForms groups every radio on a form together; keep the two pairs independent
                ar.AutoCheck = en.AutoCheck = simple.AutoCheck = adv.AutoCheck = false;
                ar.Click += delegate { ar.Checked = true; };
                en.Click += delegate { en.Checked = true; };
                simple.Click += delegate { simple.Checked = true; };
                adv.Click += delegate { adv.Checked = true; };
                y += 8;

                heading(Lang.T("Options", "خيارات"));
                var auto = check(Lang.T("Test ping automatically when I pick a game", "افحص البنق تلقائي لما أختار لعبة"), Prefs.AutoTest);
                var upd = check(Lang.T("Check for updates when the app starts", "شيّك على التحديثات لما يفتح البرنامج"), Prefs.CheckUpdates);
                int bottomA = y;

                // ---- second column
                x = colB;
                y = 14;
                heading(Lang.T("Games in your list", "الألعاب اللي تظهر لك"));
                var hiddenBefore = Prefs.HiddenGames;
                var gameChecks = new List<KeyValuePair<string, CheckBox>>();
                foreach (var g in GameDb.All())
                {
                    var cb = new CheckBox
                    {
                        Text = "  " + g.Name, Checked = !hiddenBefore.Contains(g.Icon), ForeColor = MainForm.TextC,
                        Bounds = new Rectangle(x, y, W, 28),
                        TextImageRelation = TextImageRelation.ImageBeforeText, ImageAlign = ContentAlignment.MiddleLeft
                    };
                    var ic = Brand.GameIcon(g.Icon);
                    if (ic != null) cb.Image = new Bitmap(ic, 20, 20);
                    f.Controls.Add(cb);
                    gameChecks.Add(new KeyValuePair<string, CheckBox>(g.Icon, cb));
                    y += 28;
                }
                y += 12;

                heading(Lang.T("More", "أكثر"));
                // two calm buttons side by side, the first on the reading side
                Func<string, int, Action, Button> small = (text, i, act) =>
                {
                    const int bw = (W - 8) / 2;
                    int bx = (i == 0) ^ Lang.Ar ? x : x + bw + 8;
                    var b = new Button
                    {
                        Text = text, Bounds = new Rectangle(bx, y, bw, 36), FlatStyle = FlatStyle.Flat,
                        BackColor = MainForm.Panel2, ForeColor = MainForm.TextC, Cursor = Cursors.Hand
                    };
                    b.FlatAppearance.BorderSize = 0;
                    b.Click += delegate { act(); };
                    f.Controls.Add(b);
                    return b;
                };
                small(Lang.T("About 2X Ping", "عن البرنامج"), 0, () => AboutDialog.Show(f));
                small(Lang.T("Back to normal", "رجّع كل شي طبيعي"), 1,
                    () => { wantReset = true; f.DialogResult = DialogResult.Cancel; f.Close(); });
                y += 42;
                f.Controls.Add(new Label
                {
                    Text = Lang.T("\"Back to normal\" unlocks your servers. Closing the app does it too.",
                                  "«رجّع كل شي طبيعي» يفك تثبيت السيرفرات، ونفس الشي يصير لما تقفل البرنامج."),
                    ForeColor = MainForm.Dim, Font = new Font("Segoe UI", 8.5f), Bounds = new Rectangle(x, y, W, 20)
                });
                y = Math.Max(y + 36, bottomA + 20);

                var save = new Button
                {
                    Text = Lang.T("Save", "حفظ"), DialogResult = DialogResult.OK, Bounds = new Rectangle(Lang.Ar ? 730 : 610, y, 110, 36),
                    FlatStyle = FlatStyle.Flat, BackColor = MainForm.Accent, ForeColor = Color.Black,
                    Font = new Font("Segoe UI Semibold", 10f)
                };
                var cancel = new Button
                {
                    Text = Lang.T("Cancel", "إلغاء"), DialogResult = DialogResult.Cancel, Bounds = new Rectangle(Lang.Ar ? 610 : 730, y, 110, 36),
                    FlatStyle = FlatStyle.Flat, BackColor = MainForm.Panel2, ForeColor = MainForm.TextC
                };
                save.FlatAppearance.BorderSize = cancel.FlatAppearance.BorderSize = 0;
                f.Controls.Add(save);
                f.Controls.Add(cancel);
                f.AcceptButton = save;
                f.CancelButton = cancel;
                f.ClientSize = new Size(2 * W + 60, y + 52);

                if (f.ShowDialog(owner) != DialogResult.OK)
                {
                    resetAsked = wantReset;
                    return false;
                }

                if (nick.Text.Trim().Length >= 2) Telemetry.Nickname = nick.Text;
                Storage.SaveArabic(ar.Checked);
                Storage.SetSetting("mode", simple.Checked ? "simple" : "advanced");
                Storage.SetSetting("autotest", auto.Checked ? "1" : "0");
                Storage.SetSetting("updates", upd.Checked ? "1" : "0");
                Lang.Ar = ar.Checked;

                // nothing ticked = show every game, so the list is never empty
                var hiddenNow = new HashSet<string>(gameChecks.Where(gc => !gc.Value.Checked).Select(gc => gc.Key));
                if (hiddenNow.Count == gameChecks.Count) hiddenNow.Clear();
                Prefs.HiddenGames = hiddenNow;
                bool gamesChanged = !hiddenNow.SetEquals(hiddenBefore);

                return wasAr != Lang.Ar || wasSimple != Prefs.Simple || gamesChanged;
            }
        }
    }
}
