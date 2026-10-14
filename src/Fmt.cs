using System;
using System.Drawing;

namespace PingBoost
{
    // How numbers and names are written on screen (one copy for the main window and the card).
    static class Fmt
    {
        // LRM marks keep "74 ms" in order inside right-to-left text.
        public static string Ms(double v) { return v < 0 ? Lang.T("no reply", "ما يرد") : "‎" + Math.Round(v) + " ms‎"; }

        public static string ShortName(Region r) { return ShortName(r.Name); }

        // "Frankfurt  [fra]" / "Frankfurt  (Germany)" -> "Frankfurt"
        public static string ShortName(string name) { return name.Split(new[] { "  [", "  (" }, StringSplitOptions.None)[0]; }

        // double.MaxValue = not measured yet (dim); below 0 = no reply (bad)
        public static Color PingColor(double v)
        {
            if (v >= double.MaxValue) return Theme.Dim;
            if (v < 0) return Theme.Bad;
            if (v < 60) return Theme.Good;
            return v < 120 ? Theme.Warn : Theme.Bad;
        }
    }
}
