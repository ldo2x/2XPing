using System;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace PingBoost
{
    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool AllowSetForegroundWindow(int processId);

        // set by a second copy of the app: "show yourself"
        const string ShowSignal = "Local\\lDo2x.2XPing.Show";

        [STAThread]
        static void Main(string[] args)
        {
            // after a self-update: wait for the old version to close (it holds the single-instance lock), then remove it
            Updater.FinishUpdate(args);
            Updater.CleanupOld();
            // one copy at a time: two copies would fight over the tunnel and firewall rules
            Lang.Ar = Storage.LoadArabic();
            bool first;
            using (new Mutex(true, "Local\\lDo2x.2XPing", out first))
            {
                if (!first)
                {
                    // already open (maybe minimized): bring that window up instead of showing a message
                    try
                    {
                        AllowSetForegroundWindow(-1); // let the running copy take the foreground
                        using (var ev = EventWaitHandle.OpenExisting(ShowSignal)) ev.Set();
                    }
                    catch { }
                    return;
                }
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
                ServicePointManager.DefaultConnectionLimit = 64;
                // the ping test runs every server at once; don't let the thread pool add threads one by one
                ThreadPool.SetMinThreads(64, 64);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Fonts.Load();
                CrashHandler.Install();
                using (var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignal))
                {
                    var form = new MainForm();
                    var listener = new Thread(() =>
                    {
                        try
                        {
                            while (show.WaitOne())
                            {
                                if (form.IsDisposed) return;
                                try { form.BeginInvoke((Action)form.BringUp); } catch (InvalidOperationException) { }
                            }
                        }
                        catch (ObjectDisposedException) { } // the app is closing
                    }) { IsBackground = true };
                    listener.Start();
                    Application.Run(form);
                }
            }
        }
    }
}
