using System.Windows.Forms;

namespace PingBoost
{
    static class Lang
    {
        public static bool Ar = true;
        public static string T(string en, string ar) { return Ar ? ar : en; }
        public static TextFormatFlags Flags { get { return Ar ? TextFormatFlags.RightToLeft : TextFormatFlags.Default; } }
    }
}
