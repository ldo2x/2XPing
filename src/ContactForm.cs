using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace PingBoost
{
    // «تواصل معنا» / Contact us: one form. The player picks what it is (problem, idea, question), writes it, optionally
    // says how to reach them, and presses Send; the diagnostics go with it by themselves (Feedback). After sending, the
    // form turns into a thank-you. A failed send keeps the text, offers a retry, and the message waits in the outbox.
    class ContactForm : Form
    {
        const int M = 20, W = 480;
        static readonly Color Rest = Color.FromArgb(72, 60, 100);

        readonly string diagnostics;
        readonly Segmented type;
        readonly TextBox text;
        readonly Label hint, counter, status;
        readonly InputBox contact;
        readonly RoundButton send, cancel;
        readonly Panel form, done;
        Feedback.Message message; // made on the first Send; a retry sends the same one (same id, replaces it in the outbox)
        bool sending;

        static string T(string en, string ar) { return Lang.T(en, ar); }

        public ContactForm(string log)
        {
            // what is attached, exactly as it is sent (folders and the Windows user name already replaced)
            diagnostics = Privacy.Scrub(ContactDialog.Diagnostics(log));

            Text = T("Contact us", "تواصل معنا");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;
            BackColor = Theme.Bg;
            ForeColor = Theme.TextC;
            Font = Fonts.Ui(10f);
            RightToLeft = Lang.Ar ? RightToLeft.Yes : RightToLeft.No;
            // labels inherit the right-to-left reading: "Left" is the reading start (the right in Arabic), "Right" its end
            const ContentAlignment lead = ContentAlignment.MiddleLeft, end = ContentAlignment.MiddleRight;

            form = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            done = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Visible = false };
            Controls.Add(done);
            Controls.Add(form);

            int y = 16;
            form.Controls.Add(new Label
            {
                Text = T("Contact us", "تواصل معنا"), Font = Fonts.Semi(15f), ForeColor = Theme.Accent,
                Bounds = new Rectangle(M, y, W, 34), TextAlign = lead
            });
            y += 36;
            form.Controls.Add(new Label
            {
                Text = T("Write us a problem, an idea or a question, and it reaches us directly.",
                         "اكتب لنا مشكلة، فكرة، أو سؤال، وتوصلنا مباشرة."),
                ForeColor = Theme.Dim, Font = Fonts.Ui(9.5f), Bounds = new Rectangle(M, y, W, 24), TextAlign = lead
            });
            y += 34;

            // what it is: only a tag for the developer's list, «مشكلة» unless picked
            type = new Segmented
            {
                Items = new[] { T("Problem", "مشكلة"), T("Idea", "فكرة"), T("Question", "سؤال") },
                Bounds = new Rectangle(M, y, W, 38), Font = Fonts.Ui(9.5f)
            };
            form.Controls.Add(type);
            y += 48;

            // the message: a multiline box in a rounded card; the grey hint sits inside it while it's empty
            var card = new Card { Bounds = new Rectangle(M, y, W, 170), Fill = Theme.Panel2, Radius = 10, Border = Rest, Padding = new Padding(12, 10, 12, 10) };
            text = new TextBox
            {
                Multiline = true, AcceptsReturn = true, BorderStyle = BorderStyle.None, BackColor = Theme.Panel2, ForeColor = Theme.TextC,
                Dock = DockStyle.Fill, MaxLength = Feedback.MaxLength, Font = Fonts.Ui(10f)
            };
            hint = new Label
            {
                Text = T("What happened, or what would you like? The more detail, the better.",
                         "وش صار معك، أو وش تبغى؟ كل ما فصّلت أكثر كان أحسن."),
                ForeColor = Theme.Dim, BackColor = Theme.Panel2, AutoSize = false, Bounds = new Rectangle(0, 0, W - 24, 44),
                TextAlign = ContentAlignment.TopLeft, Cursor = Cursors.IBeam
            };
            hint.Click += delegate { text.Focus(); };
            text.Controls.Add(hint);
            text.GotFocus += delegate { card.Border = Theme.Accent; card.Invalidate(); };
            text.LostFocus += delegate { card.Border = Rest; card.Invalidate(); };
            card.Click += delegate { text.Focus(); };
            card.Controls.Add(text);
            form.Controls.Add(card);
            y += 172;

            // how much is left, only near the limit
            counter = new Label
            {
                Bounds = new Rectangle(M, y, W, 18), Font = Fonts.Ui(8.5f), ForeColor = Theme.Warn, Visible = false,
                TextAlign = Lang.Ar ? ContentAlignment.MiddleLeft : ContentAlignment.MiddleRight, RightToLeft = RightToLeft.No
            };
            form.Controls.Add(counter);
            y += 22;

            contact = new InputBox
            {
                Bounds = new Rectangle(M, y, W, 42), Placeholder = T("How can we reply? (Discord or e-mail, optional)", "كيف نرد عليك؟ (ديسكورد أو إيميل، اختياري)")
            };
            contact.Box.MaxLength = Feedback.MaxContact;
            form.Controls.Add(contact);
            y += 52;

            // what goes with it, and a look at the exact text
            string attachedText = T("Sent with it automatically: app and Windows version, recent log.",
                                    "ينرسل معها تلقائياً: إصدار البرنامج والويندوز وآخر السجل.");
            string whatText = T("What's sent?", "وش ينرسل؟");
            var small = Fonts.Ui(8.5f);
            // one line, the link part at its end
            var what = new LinkLabel
            {
                Text = attachedText + "  " + whatText, Font = small, TextAlign = lead, ForeColor = Theme.Dim,
                LinkArea = new LinkArea(attachedText.Length + 2, whatText.Length), Bounds = new Rectangle(M, y, W, 22),
                LinkColor = Theme.Lavender, ActiveLinkColor = Color.White, LinkBehavior = LinkBehavior.HoverUnderline
            };
            what.LinkClicked += delegate { ShowDiagnostics(); };
            form.Controls.Add(what);
            y += 26;

            status = new Label { Bounds = new Rectangle(M, y, W, 40), Font = Fonts.Ui(9f), ForeColor = Theme.Dim, TextAlign = lead };
            form.Controls.Add(status);
            y += 44;

            const int bw = (W - 10) / 2;
            send = new RoundButton
            {
                Text = T("Send", "أرسل"), BackColor = Theme.Accent, ForeColor = Color.Black, Font = Fonts.Semi(10.5f),
                Bounds = new Rectangle(Lang.Ar ? M + bw + 10 : M, y, bw, 42), Enabled = false
            };
            cancel = new RoundButton
            {
                Text = T("Cancel", "إلغاء"), BackColor = Theme.Panel2, ForeColor = Theme.TextC, DialogResult = DialogResult.Cancel,
                Bounds = new Rectangle(Lang.Ar ? M : M + bw + 10, y, bw, 42)
            };
            send.Click += delegate { Send(); };
            form.Controls.Add(send);
            form.Controls.Add(cancel);
            CancelButton = cancel;
            y += 56;

            // for people who prefer other ways: Discord (copies the username) and the GitHub page
            form.Controls.Add(new Label { BackColor = Theme.Panel2, Bounds = new Rectangle(M, y, W, 1) });
            y += 8;
            var discord = new LinkLabel
            {
                Text = T("Discord: ", "ديسكورد: ") + Brand.DiscordUser, Font = small, Bounds = new Rectangle(Lang.Ar ? M + W / 2 : M, y, W / 2, 22),
                TextAlign = lead, LinkColor = Theme.Dim, ActiveLinkColor = Color.White, LinkBehavior = LinkBehavior.HoverUnderline
            };
            discord.LinkClicked += delegate
            {
                if (!string.IsNullOrEmpty(Brand.DiscordInvite)) { Brand.Open(Brand.DiscordInvite); return; }
                try { Clipboard.SetText(Brand.DiscordUser); } catch { }
                discord.Text = T("Copied ✓  ", "انتسخ ✓  ") + Brand.DiscordUser;
            };
            form.Controls.Add(discord);
            var github = new LinkLabel
            {
                Text = T("GitHub page", "صفحة البرنامج على GitHub"), Font = small, Bounds = new Rectangle(Lang.Ar ? M : M + W / 2, y, W / 2, 22),
                TextAlign = end,
                LinkColor = Theme.Dim, ActiveLinkColor = Color.White, LinkBehavior = LinkBehavior.HoverUnderline
            };
            github.LinkClicked += delegate { Brand.Open(Brand.RepoUrl); };
            form.Controls.Add(github);
            y += 30;
            ClientSize = new Size(W + 2 * M, y);

            BuildDone();

            text.TextChanged += delegate { UpdateControls(); };
            UpdateControls();
            Shown += delegate { text.Focus(); };
        }

        // the thank-you that replaces the form once the message is through
        void BuildDone()
        {
            int h = ClientSize.Height;
            done.Controls.Add(new Label
            {
                Text = "✓", Font = Fonts.Semi(40f), ForeColor = Theme.Good, TextAlign = ContentAlignment.MiddleCenter,
                Bounds = new Rectangle(M, h / 2 - 140, W, 80)
            });
            done.Controls.Add(new Label
            {
                Text = T("We got your message ✓ Thank you!", "وصلتنا رسالتك ✓ شكراً لك!"), Font = Fonts.Semi(15f), ForeColor = Theme.TextC,
                TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(M, h / 2 - 54, W, 36)
            });
            done.Controls.Add(new Label
            {
                Text = T("We read every message. If you left a way to reach you, we'll reply there.",
                         "نقرا كل رسالة، ولو كتبت لنا طريقة تواصل نرد عليك فيها."),
                ForeColor = Theme.Dim, Font = Fonts.Ui(9.5f), TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(M, h / 2 - 14, W, 44)
            });
            var close = new RoundButton
            {
                Text = T("Close", "إغلاق"), BackColor = Theme.Accent, ForeColor = Color.Black, Font = Fonts.Semi(10.5f),
                DialogResult = DialogResult.OK, Bounds = new Rectangle(M + (W - 180) / 2, h / 2 + 50, 180, 42)
            };
            done.Controls.Add(close);
        }

        void UpdateControls()
        {
            int n = text.TextLength;
            hint.Visible = n == 0;
            counter.Visible = n >= Feedback.MaxLength - 200;
            counter.Text = n + " / " + Feedback.MaxLength;
            counter.ForeColor = n >= Feedback.MaxLength ? Theme.Bad : Theme.Warn;
            send.Enabled = !sending && Feedback.LongEnough(text.Text);
        }

        void ShowDiagnostics()
        {
            using (var f = new Form())
            {
                f.Text = T("What's sent with your message", "وش ينرسل مع رسالتك");
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.MinimizeBox = f.MaximizeBox = false;
                f.ShowInTaskbar = false;
                f.BackColor = Theme.Bg;
                f.ForeColor = Theme.TextC;
                f.Font = Fonts.Ui(9.5f);
                f.ClientSize = new Size(560, 420);
                f.Controls.Add(new TextBox
                {
                    Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Text = diagnostics, BorderStyle = BorderStyle.None,
                    BackColor = Theme.Panel1, ForeColor = Theme.TextC, Font = new Font("Consolas", 9f), RightToLeft = RightToLeft.No,
                    Bounds = new Rectangle(16, 16, 528, 330)
                });
                var ok = new RoundButton
                {
                    Text = T("Close", "إغلاق"), DialogResult = DialogResult.OK, BackColor = Theme.Panel2, ForeColor = Theme.TextC,
                    Bounds = new Rectangle(220, 362, 120, 40)
                };
                f.Controls.Add(ok);
                f.AcceptButton = f.CancelButton = ok;
                f.ShowDialog(this);
            }
        }

        void Send()
        {
            if (sending || !Feedback.LongEnough(text.Text)) return;
            string kind = Feedback.Types[Math.Max(0, Math.Min(Feedback.Types.Length - 1, type.SelectedIndex))];
            if (message == null) message = Feedback.Make(kind, text.Text, contact.Text, diagnostics);
            else
            {
                message.Type = kind;
                message.Text = Feedback.Cap(text.Text);
                message.Contact = Feedback.CapContact(contact.Text);
            }
            var m = message;
            ShowSending();
            ThreadPool.QueueUserWorkItem(delegate
            {
                Feedback.Result r;
                try { r = Feedback.Send(m); } catch { r = Feedback.Result.Failed; }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (r == Feedback.Result.Sent) ShowSent();
                        else ShowFailed(r == Feedback.Result.TooMany);
                    }));
                }
                catch { } // closed meanwhile: a failed message is in the outbox already
            });
        }

        // ------------------------------------------------------------------ states (also used by the offscreen renders)

        internal void Fill(int kind, string message, string reachMe)
        {
            type.SelectedIndex = kind;
            text.Text = message;
            contact.Text = reachMe;
            text.Select(text.TextLength, 0);
        }

        internal void ShowSending()
        {
            sending = true;
            send.Text = T("Sending...", "جاري الإرسال...");
            status.Text = "";
            UpdateControls();
        }

        internal void ShowSent()
        {
            sending = false;
            form.Visible = false;
            done.Visible = true;
            AcceptButton = null;
        }

        internal void ShowFailed(bool tooMany)
        {
            sending = false;
            send.Text = T("Try again", "جرّب مرة ثانية");
            status.ForeColor = tooMany ? Theme.Warn : Theme.Bad;
            status.Text = tooMany
                ? T("You've sent a lot of messages. Wait a few minutes and try again.", "أرسلت رسايل كثير، استنى كم دقيقة وجرّب مرة ثانية.")
                : T("We couldn't send it. Check your internet and try again. It's saved, and goes by itself next time you open 2X Ping.",
                    "ما قدرنا نرسل، تأكد من النت وجرّب مرة ثانية. رسالتك محفوظة، وتنرسل لحالها المرة الجاية اللي تفتح فيها البرنامج.");
            UpdateControls();
        }
    }
}
