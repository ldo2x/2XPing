using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;

namespace PingBoost
{
    // Readex Pro (SIL Open Font License, assets/fonts) embedded in the exe, so nobody has to install it.
    // Registered twice: with GDI+ (to build Font objects) and with GDI (TextRenderer and the controls draw with GDI).
    static class Fonts
    {
        [DllImport("gdi32.dll")]
        static extern IntPtr AddFontMemResourceEx(IntPtr data, uint size, IntPtr reserved, ref uint fonts);

        // Regular and Bold share a collection; Medium and SemiBold each get their own. SemiBold also names "Readex Pro"
        // as its family, and in one collection with Regular GDI+ sometimes folded it in there, so "Readex Pro SemiBold"
        // was missing on about 3 launches in 4 and the titles fell back to Segoe UI. Every collection is kept alive.
        static readonly Dictionary<string, PrivateFontCollection> collections = new Dictionary<string, PrivateFontCollection>();
        static readonly Dictionary<string, FontFamily> families = new Dictionary<string, FontFamily>();
        static bool loaded;

        // Readex Pro runs a little larger than Segoe UI; sizes in the code are Segoe UI sizes.
        const float Scale = 0.92f;

        public static void Load()
        {
            if (loaded) return;
            loaded = true;
            var asm = Assembly.GetExecutingAssembly();
            foreach (var name in asm.GetManifestResourceNames())
            {
                if (!name.StartsWith("font_") || !name.EndsWith(".ttf")) continue;
                try
                {
                    byte[] data;
                    using (var s = asm.GetManifestResourceStream(name))
                    {
                        data = new byte[s.Length];
                        s.Read(data, 0, data.Length);
                    }
                    // the memory must stay valid while the fonts are in use: it is never freed
                    IntPtr mem = Marshal.AllocCoTaskMem(data.Length);
                    Marshal.Copy(data, 0, mem, data.Length);
                    Collection(name).AddMemoryFont(mem, data.Length);
                    uint count = 0;
                    AddFontMemResourceEx(mem, (uint)data.Length, IntPtr.Zero, ref count);
                }
                catch { }
            }
            // the shared Regular/Bold collection first, so a weight's own collection never replaces "Readex Pro"
            PrivateFontCollection main;
            if (collections.TryGetValue("", out main)) foreach (var f in main.Families) families[f.Name] = f;
            foreach (var c in collections)
                foreach (var f in c.Value.Families)
                    if (!families.ContainsKey(f.Name)) families[f.Name] = f;
        }

        static PrivateFontCollection Collection(string resource)
        {
            string key = resource.Contains("Medium") ? "Medium" : resource.Contains("SemiBold") ? "SemiBold" : "";
            PrivateFontCollection c;
            if (!collections.TryGetValue(key, out c)) collections[key] = c = new PrivateFontCollection();
            return c;
        }

        static Font Make(string family, string fallback, float size, FontStyle style)
        {
            Load();
            FontFamily ff;
            if (families.TryGetValue(family, out ff)) return new Font(ff, size * Scale, style);
            return new Font(fallback, size, style);
        }

        public static Font Ui(float size) { return Make("Readex Pro", "Segoe UI", size, FontStyle.Regular); }
        public static Font Medium(float size) { return Make("Readex Pro Medium", "Segoe UI", size, FontStyle.Regular); }
        public static Font Semi(float size) { return Make("Readex Pro SemiBold", "Segoe UI Semibold", size, FontStyle.Regular); }
        public static Font Bold(float size) { return Make("Readex Pro", "Segoe UI Black", size, FontStyle.Bold); }
    }
}
