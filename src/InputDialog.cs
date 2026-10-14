using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace PingBoost
{
    static class InputDialog
    {
        public static string[] Show(IWin32Window owner, string title, string[] labels, string[] defaults)
        {
            using (var f = new Form())
            {
                f.Text = title;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.MinimizeBox = f.MaximizeBox = false;
                f.BackColor = Theme.Bg;
                f.ForeColor = Theme.TextC;
                f.Font = Fonts.Ui(10f);
                f.RightToLeft = Lang.Ar ? RightToLeft.Yes : RightToLeft.No;
                f.ClientSize = new Size(520, 24 + labels.Length * 62 + 56);

                var boxes = new TextBox[labels.Length];
                for (int i = 0; i < labels.Length; i++)
                {
                    f.Controls.Add(new Label { Text = labels[i], ForeColor = Theme.Dim, Bounds = new Rectangle(16, 14 + i * 62, 488, 22) });
                    boxes[i] = new TextBox
                    {
                        Text = defaults[i], Bounds = new Rectangle(16, 38 + i * 62, 488, 26), RightToLeft = RightToLeft.No,
                        BackColor = Theme.Panel2, ForeColor = Theme.TextC, BorderStyle = BorderStyle.FixedSingle
                    };
                    f.Controls.Add(boxes[i]);
                }
                int y = 24 + labels.Length * 62 + 8;
                var ok = new RoundButton { Text = Lang.T("OK", "تمام"), DialogResult = DialogResult.OK, Bounds = new Rectangle(Lang.Ar ? 112 : 320, y, 88, 34), FlatStyle = FlatStyle.Flat, BackColor = Theme.Accent, ForeColor = Color.Black };
                var cancel = new RoundButton { Text = Lang.T("Cancel", "إلغاء"), DialogResult = DialogResult.Cancel, Bounds = new Rectangle(Lang.Ar ? 16 : 416, y, 88, 34), FlatStyle = FlatStyle.Flat, BackColor = Theme.Panel2 };
                ok.FlatAppearance.BorderSize = cancel.FlatAppearance.BorderSize = 0;
                f.Controls.Add(ok);
                f.Controls.Add(cancel);
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                return f.ShowDialog(owner) == DialogResult.OK ? boxes.Select(b => b.Text.Trim()).ToArray() : null;
            }
        }
    }
}
