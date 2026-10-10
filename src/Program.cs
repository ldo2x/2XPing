using System;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace PingBoost
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            // one copy at a time: two copies would fight over the tunnel and firewall rules
            Lang.Ar = Storage.LoadArabic();
            bool first;
            using (new Mutex(true, "Local\\D7oom2x.2XPing", out first))
            {
                if (!first)
                {
                    MessageBox.Show(Lang.T("2X Ping is already running.", "البرنامج شغّال أصلاً."), Brand.Name,
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                CrashHandler.Install();
                Application.Run(new MainForm());
            }
        }
    }
}
