using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace PingBoost
{
    // Which window is in front: 2X Ping (the live ping and the automatic check run at full pace), a game (someone is
    // playing: nothing that adds traffic or starts netsh), or something else. Asked of Windows each time (focus events
    // can be missed, e.g. when a notice or a dialog takes the focus), so the app never stays paused while it's looked at.
    sealed class FrontTracker
    {
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        // How long the app must have been away before coming back counts as a return (Activated)
        public static readonly TimeSpan Absence = TimeSpan.FromSeconds(20);

        readonly Func<uint> foregroundPid;
        readonly Func<uint, string> processName; // null = gone, or Windows wouldn't say
        readonly uint myPid;
        readonly Func<DateTime> now;
        DateTime lastInFront;

        public FrontTracker()
            : this(ForegroundPid, NameOf, (uint)Process.GetCurrentProcess().Id, () => DateTime.Now) { }

        // tests: a fake foreground window, process names and clock
        public FrontTracker(Func<uint> foregroundPid, Func<uint, string> processName, uint myPid, Func<DateTime> now)
        {
            this.foregroundPid = foregroundPid;
            this.processName = processName;
            this.myPid = myPid;
            this.now = now;
            lastInFront = now();
        }

        static uint ForegroundPid()
        {
            uint pid;
            GetWindowThreadProcessId(GetForegroundWindow(), out pid);
            return pid;
        }

        static string NameOf(uint pid)
        {
            try
            {
                using (var p = Process.GetProcessById((int)pid)) return p.ProcessName;
            }
            catch { return null; } // it closed meanwhile, or Windows wouldn't say
        }

        // Is one of 2X Ping's windows in front? (also remembers when it last was)
        public bool AppFront
        {
            get
            {
                bool front = foregroundPid() == myPid;
                if (front) lastInFront = now();
                return front;
            }
        }

        // The window was activated: true after a real absence (Absence or longer since 2X Ping was last seen in front).
        public bool CameBack() { return now() - lastInFront >= Absence; }

        // Is one of the listed games the window in front (someone is playing)?
        public bool GameInFront(IEnumerable<Game> games)
        {
            uint pid = foregroundPid();
            if (pid == 0 || pid == myPid) return false;
            string name = processName(pid);
            if (name == null) return false;
            return games.Any(g => g.Processes.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)));
        }
    }
}
