using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace PingBoost
{
    enum CardKind { Loading, Testing, Locking, Choose, Locked, InMatchOk, InMatchRelay, Outside, BestOnly, OnServer, Offline }

    // What the app knows right now; CardLogic.Decide turns it into the words on the main card.
    class CardInput
    {
        public string Game;
        public bool Loaded, LoadFailed, Testing, Tested, CanLock;
        public bool Locking;                  // a lock is being applied right now
        public string LockingNames;           // what that lock is for, in the card's words ("Europe"); null = not known
        // Overwatch 2 and Dead by Daylight: the lock works but is still being checked, so the card says so
        public bool InDevelopment;
        public List<RegionChoice> Choices;
        public string LockedNames;            // null = not locked
        public string MatchRegion;            // where the running match is, null = not in a match
        public bool MatchOnPicked, MatchViaRelay;
        // MatchViaRelay: where the game enters Valve's network ("Dubai"), null = not known
        public string MatchEntry;
        // the match server's own datacenter, known only from the game's console log ("Frankfurt"); null = not known
        public string MatchPlace;
        // Deadlock without its console log: how to switch it on so we can name the exact server (see ExactHint)
        public ExactHint Hint;
        public string BestServer, CurrentServer;
        public int BestMs = -1, CurrentMs = -1;
        // Overwatch: why the picked regions may find no match (Zones.Warning), shown before locking; null = no warning
        public string ZoneWarning;
        // Overwatch: locked for a few minutes and still no match
        public bool NoMatchYet;
        // Deadlock: Russia's own queue is explained under the region choices (it can't be a tile); its players, -1 = unknown
        public bool RussiaNote;
        public int RussiaPlayers = -1;
        // Valve: what the strict lock on the chosen regions costs (StrictLock), told on the card; null = nothing to tell
        public RouteNote Route;
    }

    // The "-condebug" hint: None (the log is on, or not Deadlock); Link (simple layout: a small link, only while the
    // card can't see the match server, that shows the steps); Shown (advanced layout and developer mode: the steps
    // themselves, on every locked state).
    enum ExactHint { None, Link, Shown }

    enum RouteNoteKind { ClosedEntry, BlockedHost, MayLeak }

    // The strict lock's trade-off in the card's words. Names are already in the app's language.
    class RouteNote
    {
        public RouteNoteKind Kind;
        public string Entry;    // where the game enters Valve's network ("Dubai"), or the region of that datacenter
        public string Reaches;  // the left-out region that entry reaches ("Europe")
        public string Target;   // the picked regions ("SE Asia")
        public int BeforeMs = -1, AfterMs = -1; // the ping to them without the lock and with it; -1 = unknown
    }

    class CardView
    {
        public CardKind Kind;
        public string Title, Sub, Button;
        public string Note; // a small footnote under the tiles, null = none
        // how to let us name the exact server: HintLink is a small link that shows HintSteps; with no link the steps show as they are
        public string HintLink, HintSteps;
        public bool ShowTiles, ShowLinks, ShowTest; // ShowTest: the "Test ping" link
        public Color Accent;
    }

    static class CardLogic
    {
        public static CardView Decide(CardInput i)
        {
            // a lock being applied, or one that is on, always shows: never hidden behind "Offline" or "Loading"
            if (i.CanLock && i.Locking)
                return Make(CardKind.Locking, i.LockingNames != null
                    ? Lang.T("Setting up " + i.LockingNames + "...", "نجهّز لك " + i.LockingNames + "...")
                    : Lang.T("Locking your servers...", "نثبّت لك السيرفرات..."), null, null);
            if (i.CanLock && i.LockedNames != null)
            {
                string unlock = Lang.T("Unlock", "فك التثبيت");
                string working = Lang.T("The lock is working. The ping shown in the game is the real one.", "التثبيت شغّال. وبنقك الحقيقي تشوفه داخل اللعبة.");
                string place = i.MatchPlace != null ? " (" + i.MatchPlace + ")" : "";
                if (i.MatchRegion != null && !i.MatchOnPicked)
                {
                    var v = Make(CardKind.Outside,
                        Lang.T("The game put you on " + i.MatchRegion + place, "اللعبة حطّتك على " + i.MatchRegion + place),
                        Lang.T("Close the game and open it again, then find a match.", "اقفل اللعبة وافتحها من جديد، وبعدين دوّر مباراة."), unlock);
                    v.Accent = MainForm.Warn;
                    return WithHint(v, i.Hint);
                }
                if (i.MatchRegion != null)
                    return WithHint(Good(Make(CardKind.InMatchOk,
                        Lang.T("You're playing in " + i.MatchRegion + place + " now ✓", "تلعب دحين في " + i.MatchRegion + place + " ✓"), working, unlock)), i.Hint);
                if (i.MatchViaRelay)
                {
                    // only the entry relay is in sight: say where the game enters, never that the lock worked (no ✓)
                    string title = i.MatchEntry != null
                        ? Lang.T("Entering Valve's network at " + i.MatchEntry + "; the match server can't be seen from here",
                                 "داخل شبكة Valve من " + i.MatchEntry + "، وسيرفر المباراة ما يبان من هنا")
                        : Lang.T("Entering Valve's network through a relay; the match server can't be seen from here",
                                 "داخل شبكة Valve من وسيط، وسيرفر المباراة ما يبان من هنا");
                    return WithHint(Make(CardKind.InMatchRelay, title,
                        Lang.T("Locked to " + i.LockedNames + ". The ping shown in the game is the real one.",
                               "مثبّت على " + i.LockedNames + ". وبنقك الحقيقي تشوفه داخل اللعبة."), unlock), i.Hint);
                }
                string find = Lang.T("Open the game and find a match. Try a normal match first.", "افتح اللعبة ودوّر مباراة. جرّب مباراة عادية أول.");
                if (i.NoMatchYet)
                    find += Lang.T(" No match found? The region you picked may be outside your game's region.",
                                   " ما لقيت مباراة؟ يمكن المنطقة اللي اخترتها مو من مناطق لعبتك.");
                // a lock kept leaky on purpose (G5) says so: it may not keep the other regions away
                if (i.Route != null && i.Route.Kind == RouteNoteKind.MayLeak) find = RouteText(i.Route) + " " + find;
                return WithHint(Good(Make(CardKind.Locked, Lang.T("✓ Locked to " + i.LockedNames, "✓ مثبّت على " + i.LockedNames), find, unlock)), i.Hint);
            }

            if (i.LoadFailed) return Offline();
            if (!i.Loaded) return Make(CardKind.Loading, Lang.T("Getting " + i.Game + " servers...", "نجيب سيرفرات " + i.Game + "..."), null, null);

            if (!i.CanLock)
            {
                if (i.CurrentServer != null)
                    return Make(CardKind.OnServer,
                        Lang.T("You're on: " + i.CurrentServer + PingPart(i.CurrentMs, "your ping is ", "بنقك "),
                                "إنت دحين على: " + i.CurrentServer + PingPart(i.CurrentMs, "your ping is ", "بنقك ")), null, null);
                if (i.BestServer != null)
                {
                    var best = Make(CardKind.BestOnly,
                        Lang.T("Best server for you: " + i.BestServer + PingPart(i.BestMs, "your ping is about ", "بنقك تقريباً "),
                                "أحسن سيرفر لك: " + i.BestServer + PingPart(i.BestMs, "your ping is about ", "بنقك تقريباً ")),
                        Lang.T("Pick it from inside the game.", "اختاره من داخل اللعبة."), null);
                    best.ShowTest = !i.Testing;
                    return best;
                }
            }

            var choices = i.Choices ?? new List<RegionChoice>();
            if (i.CanLock && choices.Any(c => c.Ping < double.MaxValue))
            {
                var v = Choose(choices, i.Testing, i.ZoneWarning);
                // the lock on these regions costs ping, or can't be strict: say it before the button is pressed
                if (i.Route != null && i.ZoneWarning == null && v.Button != null) v.Sub = RouteText(i.Route);
                if (i.RussiaNote) v.Note = RussiaNote(i.RussiaPlayers);
                else if (i.InDevelopment) v.Note = InDevelopmentNote();
                return v;
            }

            // nothing to choose from yet
            if (i.Testing) return Searching();
            if (!i.Tested)
                return Make(CardKind.Testing,
                    Lang.T("Press \"Test ping\" and we'll find your best region", "اضغط «افحص البنق» ونلقى لك أحسن منطقة"), null,
                    Lang.T("Test ping", "افحص البنق"));
            return Offline();
        }

        static CardView Choose(List<RegionChoice> choices, bool testing, string zoneWarning)
        {
            var v = ChooseView(choices, testing);
            // a lock that can't find a match: say so (the button still locks)
            if (zoneWarning != null && v.Button != null) v.Sub = zoneWarning;
            return v;
        }

        static CardView ChooseView(List<RegionChoice> choices, bool testing)
        {
            var picked = choices.Where(c => c.Selected).ToList();
            var v = Make(CardKind.Choose, Lang.T("Where do you want to play?", "وين تبغى تلعب؟"), null, null);
            v.ShowTiles = true;
            v.ShowLinks = true;
            v.ShowTest = !testing; // a test is already running: nothing to press
            if (picked.Count == 0)
            {
                v.Sub = Lang.T("Pick at least one region", "اختار منطقة وحدة على الأقل");
                return v;
            }
            v.Button = Lang.T("Play in ", "العب على ") + JoinNames(picked.Select(c => c.Name).ToList());
            if (picked.Count > 1)
            {
                var known = picked.Where(c => c.Ping < double.MaxValue).Select(c => (int)Math.Round(c.Ping)).ToList();
                if (known.Count > 0 && known.Min() == known.Max())
                    v.Sub = Lang.T("Ping " + known.Min() + ", and a shorter wait", "البنق " + known.Min() + "، والانتظار أقصر");
                else if (known.Count > 0)
                    v.Sub = Lang.T("Ping between " + known.Min() + " and " + known.Max() + ", and a shorter wait",
                                   "البنق بين " + known.Min() + " و" + known.Max() + "، والانتظار أقصر");
            }
            return v;
        }

        // The "-condebug" hint on a locked state: the simple layout only offers it (a link) where the match server can't be seen.
        static CardView WithHint(CardView v, ExactHint hint)
        {
            if (hint == ExactHint.None || (hint == ExactHint.Link && v.Kind != CardKind.InMatchRelay)) return v;
            v.HintLink = hint == ExactHint.Link ? Lang.T("Want to know the exact server?", "تبغى نعرف السيرفر بالضبط؟") : null;
            v.HintSteps = ExactSteps();
            return v;
        }

        // How to switch the game's console log on. "-condebug" stays left to right inside the Arabic sentence.
        public static string ExactSteps()
        {
            return Lang.T("In Steam: Deadlock → Properties → Launch options, add -condebug, then close the game and open it again. " +
                          "After that we can name the exact server.",
                          "في Steam: Deadlock ← الخصائص ← خيارات التشغيل، ضيف ‪-condebug‬ وبعدين اقفل اللعبة وافتحها من جديد. " +
                          "بعدها نقدر نعرف لك السيرفر بالضبط.");
        }

        // Deadlock's footnote under the region choices: Russia is its own queue, played on the European servers, so no
        // lock can choose it (and locking Europe doesn't keep Russian-queue players out). players < 0 = not known.
        public static string RussiaNote(int players)
        {
            string en = "Russia is a separate queue played on the European servers; the lock can't choose it",
                   ar = "روسيا لها كيو خاص، ومبارياتها على سيرفرات أوروبا، والتثبيت ما يقدر يختارها";
            if (players >= 0)
            {
                en += " (" + Stats.Approx(players) + " players)";
                ar += " (تقريباً " + Stats.Approx(players) + " لاعب)";
            }
            return Lang.T(en + ".", ar + ".");
        }

        // Overwatch 2 and Dead by Daylight ("in development" in the game list): said under the tiles before locking.
        public static string InDevelopmentNote()
        {
            return Lang.T("Server lock for this game is still in development. Try a normal match first.",
                          "تثبيت السيرفرات لهادي اللعبة لسا قيد التطوير. جرّب مباراة عادية أول.");
        }

        // The trade-off of a strict lock, with the numbers:
        //   ClosedEntry: "We have to close Dubai because it reaches Europe. Your ping to SE Asia becomes about 197 instead of 106."
        //   BlockedHost: the shortest way goes through a datacenter of a region not picked (I3)
        //   MayLeak:     no measured way to the picked regions but through an entry that reaches a left-out region (G5)
        public static string RouteText(RouteNote n)
        {
            if (n == null) return null;
            bool nums = n.AfterMs >= 0 && n.BeforeMs >= 0;
            string en, ar;
            if (n.Kind == RouteNoteKind.MayLeak)
            {
                en = "We couldn't measure a direct way to " + n.Target + ", so " + n.Entry + " stays open. " +
                     (n.Reaches != null ? n.Entry + " also reaches " + n.Reaches + ", so the lock may not hold." : "The lock may not hold.");
                ar = "ما قدرنا نقيس طريق مباشر " + To(n.Target) + "، فبنخلي " + n.Entry + " مفتوحة. " +
                     (n.Reaches != null ? "بس " + n.Entry + " توصل " + To(n.Reaches) + " بعد، فالتثبيت ممكن ما يمسك." : "فالتثبيت ممكن ما يمسك.");
                return Lang.T(en, ar);
            }
            if (n.Kind == RouteNoteKind.ClosedEntry)
            {
                en = "We have to close " + n.Entry + " because it reaches " + n.Reaches + ".";
                ar = "لازم نقفل " + n.Entry + " لأنها توصل " + To(n.Reaches) + ".";
                if (nums)
                {
                    en += " Your ping to " + n.Target + " becomes about " + n.AfterMs + " instead of " + n.BeforeMs + ".";
                    ar += " بنقك " + To(n.Target) + " بيصير تقريباً " + n.AfterMs + " بدل " + n.BeforeMs + ".";
                }
                return Lang.T(en, ar);
            }
            en = "Your shortest way to " + n.Target + " goes through " + n.Entry + ", and the lock closes it" +
                 (nums ? ": your ping becomes about " + n.AfterMs + " instead of " + n.BeforeMs : "") + ". Pick " + n.Entry + " too to keep it.";
            ar = "أقصر طريق لك " + To(n.Target) + " يمر من " + n.Entry + "، والتثبيت يقفله" +
                 (nums ? ": بنقك بيصير تقريباً " + n.AfterMs + " بدل " + n.BeforeMs : "") + ". اختار " + n.Entry + " بعد عشان يبقى مفتوح.";
            return Lang.T(en, ar);
        }

        // Arabic "to": "لأوروبا", "لجنوب شرق آسيا", "للشرق الأوسط".
        static string To(string name)
        {
            if (string.IsNullOrEmpty(name)) return "ل";
            return name.StartsWith("ال") ? "ل" + name.Substring(1) : "ل" + name;
        }

        // Arabic: "A وB وC"; English: "A, B and C".
        public static string JoinNames(List<string> names)
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
            return Make(CardKind.Testing, Lang.T("Looking for your best region...", "ندوّر لك أحسن منطقة..."), null, null);
        }

        static CardView Offline()
        {
            return Make(CardKind.Offline,
                Lang.T("Couldn't reach the servers. Check your internet.", "ما قدرنا نوصل للسيرفرات. شيّك على النت."), null,
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
