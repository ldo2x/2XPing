using System;
using System.Drawing;
using System.Windows.Forms;

namespace PingBoost
{
    // User preferences (stored in settings.txt).
    static class Prefs
    {
        public static bool Simple { get { return Storage.GetSetting("mode") != "advanced"; } }
        public static bool AutoTest { get { return Storage.GetSetting("autotest") != "0"; } }
        public static bool CheckUpdates { get { return Storage.GetSetting("updates") != "0"; } }
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

                int y = 14;
                const int W = 460;
                Func<string, Label> heading = text =>
                {
                    var l = new Label
                    {
                        Text = text, Font = new Font("Segoe UI Semibold", 10.5f), ForeColor = MainForm.Accent,
                        Bounds = new Rectangle(20, y, W, 26)
                    };
                    f.Controls.Add(l);
                    y += 30;
                    return l;
                };
                Func<string, string, bool, RadioButton> radio = (text, sub, on) =>
                {
                    var r = new RadioButton { Text = text, Checked = on, Bounds = new Rectangle(20, y, W, 26), ForeColor = MainForm.TextC };
                    f.Controls.Add(r);
                    y += 26;
                    if (sub != null)
                    {
                        f.Controls.Add(new Label
                        {
                            Text = sub, ForeColor = MainForm.Dim, Font = new Font("Segoe UI", 8.5f),
                            Bounds = new Rectangle(40, y, W - 20, 20)
                        });
                        y += 24;
                    }
                    return r;
                };
                Func<string, bool, CheckBox> check = (text, on) =>
                {
                    var c = new CheckBox { Text = text, Checked = on, Bounds = new Rectangle(20, y, W, 26), ForeColor = MainForm.TextC };
                    f.Controls.Add(c);
                    y += 30;
                    return c;
                };

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
                y += 8;

                heading(Lang.T("More", "أكثر"));
                Func<string, int, Action, Button> small = (text, x, act) =>
                {
                    var b = new Button
                    {
                        Text = text, Bounds = new Rectangle(x, y, 146, 34), FlatStyle = FlatStyle.Flat,
                        BackColor = MainForm.Panel2, ForeColor = MainForm.TextC, Cursor = Cursors.Hand
                    };
                    b.FlatAppearance.BorderSize = 0;
                    b.Click += delegate { act(); };
                    f.Controls.Add(b);
                    return b;
                };
                small(Lang.T("About", "عن البرنامج"), 20, () => AboutDialog.Show(f));
                small(Lang.T("Data folder", "مجلد البيانات"), 177, () => Brand.Open(Storage.Dir));
                small(Lang.T("Reset settings", "رجّع الافتراضي"), 334, () =>
                {
                    ar.Checked = true; simple.Checked = true; auto.Checked = true; upd.Checked = true;
                });
                y += 44;

                var reset = new Button
                {
                    Text = Lang.T("Put my PC back as it was", "رجّع جهازي زي ما كان"), Bounds = new Rectangle(20, y, W, 36),
                    FlatStyle = FlatStyle.Flat, BackColor = MainForm.Panel2, ForeColor = MainForm.Bad, Cursor = Cursors.Hand,
                    Font = new Font("Segoe UI Semibold", 10f)
                };
                reset.FlatAppearance.BorderSize = 0;
                reset.Click += delegate { wantReset = true; f.DialogResult = DialogResult.Cancel; f.Close(); };
                f.Controls.Add(reset);
                y += 38;
                f.Controls.Add(new Label
                {
                    Text = Lang.T("Removes the server lock and relay route, resets game priority and clears your ticks.",
                                  "يشيل تثبيت السيرفرات وطريق الوسيط، ويرجّع أولوية اللعبة، ويمسح اختياراتك."),
                    ForeColor = MainForm.Dim, Font = new Font("Segoe UI", 8.5f), Bounds = new Rectangle(20, y, W, 20)
                });
                y += 36;

                var save = new Button
                {
                    Text = Lang.T("Save", "حفظ"), DialogResult = DialogResult.OK, Bounds = new Rectangle(Lang.Ar ? 370 : 250, y, 110, 36),
                    FlatStyle = FlatStyle.Flat, BackColor = MainForm.Accent, ForeColor = Color.Black,
                    Font = new Font("Segoe UI Semibold", 10f)
                };
                var cancel = new Button
                {
                    Text = Lang.T("Cancel", "إلغاء"), DialogResult = DialogResult.Cancel, Bounds = new Rectangle(Lang.Ar ? 250 : 370, y, 110, 36),
                    FlatStyle = FlatStyle.Flat, BackColor = MainForm.Panel2, ForeColor = MainForm.TextC
                };
                save.FlatAppearance.BorderSize = cancel.FlatAppearance.BorderSize = 0;
                f.Controls.Add(save);
                f.Controls.Add(cancel);
                f.AcceptButton = save;
                f.CancelButton = cancel;
                f.ClientSize = new Size(W + 40, y + 52);

                if (f.ShowDialog(owner) != DialogResult.OK)
                {
                    resetAsked = wantReset;
                    return false;
                }

                Storage.SaveArabic(ar.Checked);
                Storage.SetSetting("mode", simple.Checked ? "simple" : "advanced");
                Storage.SetSetting("autotest", auto.Checked ? "1" : "0");
                Storage.SetSetting("updates", upd.Checked ? "1" : "0");
                Lang.Ar = ar.Checked;
                return wasAr != Lang.Ar || wasSimple != Prefs.Simple;
            }
        }
    }
}
