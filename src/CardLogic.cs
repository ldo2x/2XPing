using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace PingBoost
{
    enum CardKind { Loading, Testing, Choose, Locked, InMatchOk, InMatchRelay, Outside, BestOnly, OnServer, Offline }

    // What the app knows right now; CardLogic.Decide turns it into the words on the main card.
    class CardInput
    {
        public string Game;
        public bool Loaded, LoadFailed, Testing, Tested, CanLock;
        public List<RegionChoice> Choices;
        public string LockedNames;            // null = not locked
        public string MatchRegion;            // where the running match is, null = not in a match
        public bool MatchOnPicked, MatchViaRelay;
        public string BestServer, CurrentServer;
        public int BestMs = -1, CurrentMs = -1;
    }

    class CardView
    {
        public CardKind Kind;
        public string Title, Sub, Button;
        public bool ShowTiles, ShowLinks;
        public Color Accent;
    }

    static class CardLogic
    {
        public static CardView Decide(CardInput i)
        {
            if (i.LoadFailed) return Offline();
            if (!i.Loaded) return Make(CardKind.Loading, Lang.T("Getting " + i.Game + " servers...", "أجيب سيرفرات " + i.Game + "..."), null, null);

            if (i.CanLock && i.LockedNames != null)
            {
                string unlock = Lang.T("Unlock", "فك التثبيت");
                string working = Lang.T("The lock is working. The ping inside the game is the most accurate.", "التثبيت شغّال. البنق داخل اللعبة هو الأدق.");
                if (i.MatchRegion != null && !i.MatchOnPicked)
                {
                    var v = Make(CardKind.Outside,
                        Lang.T("The game put you on " + i.MatchRegion, "اللعبة حطّتك على " + i.MatchRegion),
                        Lang.T("Close the game and open it again, then find a match.", "اقفل اللعبة وافتحها من جديد، وبعدين دوّر مباراة."), unlock);
                    v.Accent = MainForm.Warn;
                    return v;
                }
                if (i.MatchViaRelay)
                    return Good(Make(CardKind.InMatchRelay,
                        Lang.T("You're playing in one of your regions now ✓ (" + i.LockedNames + ")", "تلعب الحين في واحد من مناطقك ✓ (" + i.LockedNames + ")"), working, unlock));
                if (i.MatchRegion != null)
                    return Good(Make(CardKind.InMatchOk,
                        Lang.T("You're playing in " + i.MatchRegion + " now ✓", "تلعب الحين في " + i.MatchRegion + " ✓"), working, unlock));
                return Good(Make(CardKind.Locked,
                    Lang.T("✓ Locked to " + i.LockedNames, "✓ مثبّت على " + i.LockedNames),
                    Lang.T("Open the game and find a match. Try a normal match first.", "افتح اللعبة ودوّر مباراة. جرّب مباراة عادية أول."), unlock));
            }

            if (!i.CanLock)
            {
                if (i.CurrentServer != null)
                    return Make(CardKind.OnServer,
                        Lang.T("You're on: " + i.CurrentServer + PingPart(i.CurrentMs, "your ping is ", "بنقك "),
                                "إنت الحين على: " + i.CurrentServer + PingPart(i.CurrentMs, "your ping is ", "بنقك ")), null, null);
                if (i.BestServer != null)
                    return Make(CardKind.BestOnly,
                        Lang.T("Best server for you: " + i.BestServer + PingPart(i.BestMs, "your ping is about ", "بنقك تقريباً "),
                                "أحسن سيرفر لك: " + i.BestServer + PingPart(i.BestMs, "your ping is about ", "بنقك تقريباً ")),
                        Lang.T("Pick it from inside the game.", "اختاره من داخل اللعبة."), null);
            }

            var choices = i.Choices ?? new List<RegionChoice>();
            if (i.CanLock && choices.Any(c => c.Ping < double.MaxValue)) return Choose(choices);

            // nothing to choose from yet
            if (i.Testing) return Searching();
            if (!i.Tested)
                return Make(CardKind.Testing,
                    Lang.T("Press \"Test ping\" and we'll find your best region", "اضغط «افحص البنق» ونلقى لك أحسن منطقة"), null,
                    Lang.T("Test ping", "افحص البنق"));
            return Offline();
        }

        static CardView Choose(List<RegionChoice> choices)
        {
            var picked = choices.Where(c => c.Selected).ToList();
            var v = Make(CardKind.Choose, Lang.T("Where do you want to play?", "وين تبغا تلعب؟"), null, null);
            v.ShowTiles = true;
            v.ShowLinks = true;
            if (picked.Count == 0)
            {
                v.Sub = Lang.T("Pick at least one region", "اختار منطقة وحدة على الأقل");
                return v;
            }
            v.Button = Lang.T("Play in ", "العب على ") + JoinNames(picked.Select(c => c.Name).ToList());
            if (picked.Count > 1)
            {
                var known = picked.Where(c => c.Ping < double.MaxValue).Select(c => (int)Math.Round(c.Ping)).ToList();
                if (known.Count > 0)
                    v.Sub = Lang.T("Ping between " + known.Min() + " and " + known.Max() + ", and a faster queue",
                                   "البنق بين " + known.Min() + " و" + known.Max() + "، والبحث أسرع");
            }
            return v;
        }

        // Arabic: "A وB وC"; English: "A, B and C".
        static string JoinNames(List<string> names)
        {
            if (names.Count == 1) return names[0];
            if (Lang.Ar) return string.Join(" و", names.ToArray());
            return string.Join(", ", names.Take(names.Count - 1).ToArray()) + " and " + names[names.Count - 1];
        }

        // " · your ping is 30" or nothing when the ping is not known.
        static string PingPart(int ms, string en, string ar)
        {
            if (ms < 0) return "";
            return " · " + Lang.T(en, ar) + ms;
        }

        static CardView Searching()
        {
            return Make(CardKind.Testing, Lang.T("Looking for your best region...", "أدوّر لك أحسن منطقة..."), null, null);
        }

        static CardView Offline()
        {
            return Make(CardKind.Offline,
                Lang.T("Couldn't reach the servers. Check your internet.", "ما قدرت أوصل للسيرفرات. شيّك على النت."), null,
                Lang.T("Try again", "جرّب مرة ثانية"));
        }

        static CardView Good(CardView v)
        {
            v.Accent = MainForm.Good;
            return v;
        }

        static CardView Make(CardKind kind, string title, string sub, string button)
        {
            return new CardView { Kind = kind, Title = title, Sub = sub, Button = button, Accent = MainForm.Accent };
        }
    }
}
