using System.Drawing;

namespace PingBoost
{
    // Celeste theme: night purple, unicorn pink, lavender, sparkle gold. Every window, dialog and the card use these.
    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(20, 15, 30);
        public static readonly Color Panel1 = Color.FromArgb(30, 23, 44);
        public static readonly Color Panel2 = Color.FromArgb(46, 35, 68);
        public static readonly Color RowAlt = Color.FromArgb(36, 28, 52);
        public static readonly Color RowSel = Color.FromArgb(74, 40, 92);
        public static readonly Color RowHover = Color.FromArgb(54, 42, 78);
        public static readonly Color Accent = Color.FromArgb(255, 111, 200);   // pink
        public static readonly Color Lavender = Color.FromArgb(182, 158, 255);
        public static readonly Color Good = Color.FromArgb(104, 236, 204);     // aqua: good ping
        public static readonly Color TextC = Color.FromArgb(246, 240, 252);
        public static readonly Color Dim = Color.FromArgb(168, 154, 192);
        public static readonly Color Bad = Color.FromArgb(255, 92, 122);
        public static readonly Color Warn = Color.FromArgb(255, 198, 92);      // sparkle gold

        // developer mode: a colour no player ever sees
        public static readonly Color DevColor = Color.FromArgb(18, 48, 52), DevAccent = Color.FromArgb(104, 236, 204);
    }
}
