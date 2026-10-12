using System;
using System.Collections.Generic;
using System.Linq;

namespace PingBoost
{
    static partial class Tests
    {
        static RegionChoice Choice(string key, string en, string ar, double ping, bool selected)
        {
            return new RegionChoice { Def = RegionDef.Of(key, en, ar), Ping = ping, Selected = selected };
        }

        static CardInput Lockable(params RegionChoice[] choices)
        {
            return new CardInput { Game = "Deadlock", Loaded = true, CanLock = true, Tested = true, Choices = choices.ToList() };
        }

        static void CardLoading()
        {
            Lang.Ar = true;
            var v = CardLogic.Decide(new CardInput { Game = "Deadlock" });
            Check(v.Kind == CardKind.Loading, "Loading kind");
            Check(v.Title == "أجيب سيرفرات Deadlock...", "Loading title: " + v.Title);
            Check(v.Button == null && !v.ShowTiles && !v.ShowLinks, "Loading has no button, tiles or links");
            Check(v.Accent == MainForm.Accent, "Loading accent");
        }

        static void CardLoadFailedIsOffline()
        {
            Lang.Ar = true;
            var i = Lockable(Choice("eu", "Europe", "أوروبا", 78, true));
            i.LoadFailed = true;
            var v = CardLogic.Decide(i);
            Check(v.Kind == CardKind.Offline, "load failed = Offline");
            Check(v.Title == "ما قدرت أوصل للسيرفرات. شيّك على النت.", "Offline title: " + v.Title);
            Check(v.Button == "جرّب مرة ثانية", "Offline button");
            var notLoaded = new CardInput { Game = "X", LoadFailed = true };
            Check(CardLogic.Decide(notLoaded).Kind == CardKind.Offline, "load failed beats not loaded");
        }

        static void CardTestingNoPingYet()
        {
            Lang.Ar = true;
            var i = Lockable(Choice("eu", "Europe", "أوروبا", double.MaxValue, false));
            i.Testing = true;
            var v = CardLogic.Decide(i);
            Check(v.Kind == CardKind.Testing, "Testing kind");
            Check(v.Title == "أدوّر لك أحسن منطقة...", "Testing title: " + v.Title);
            Check(v.Button == null && !v.ShowTiles, "no button or tiles while testing");
        }

        static void CardNotTestedYetAsksForTest()
        {
            Lang.Ar = true;
            var i = Lockable(Choice("eu", "Europe", "أوروبا", double.MaxValue, false));
            i.Tested = false;
            var v = CardLogic.Decide(i);
            Check(v.Kind == CardKind.Testing, "untested = Testing kind");
            Check(v.Title == "اضغط «افحص البنق» ونلقى لك أحسن منطقة", "untested title: " + v.Title);
            Check(v.Button == "افحص البنق", "untested button: " + v.Button);
            i.Testing = true;
            v = CardLogic.Decide(i);
            Check(v.Title == "أدوّر لك أحسن منطقة..." && v.Button == null, "test running: searching text, no button");
        }

        static void CardTestedButNoPingIsOffline()
        {
            Lang.Ar = true;
            var v = CardLogic.Decide(Lockable(Choice("eu", "Europe", "أوروبا", double.MaxValue, true), Choice("na", "North America", "أمريكا الشمالية", double.MaxValue, false)));
            Check(v.Kind == CardKind.Offline, "tested, nothing answered = Offline");
            Check(v.Button == "جرّب مرة ثانية" && !v.ShowTiles, "button, never tiles with no ping");
            v = CardLogic.Decide(Lockable());
            Check(v.Kind == CardKind.Offline, "tested, no choices = Offline");
        }

        static void CardChooseOne()
        {
            Lang.Ar = true;
            var v = CardLogic.Decide(Lockable(Choice("eu", "Europe", "أوروبا", 78, true), Choice("ru", "Russia", "روسيا", 110, false)));
            Check(v.Kind == CardKind.Choose, "Choose kind");
            Check(v.Title == "وين تبغا تلعب؟", "Choose title: " + v.Title);
            Check(v.ShowTiles && v.ShowLinks, "tiles and links shown");
            Check(v.Button == "العب على أوروبا", "button: " + v.Button);
            Check(v.Sub == null, "one selected = no sub");
            Check(v.Accent == MainForm.Accent, "Choose accent");
        }

        static void CardChooseTwo()
        {
            Lang.Ar = true;
            var v = CardLogic.Decide(Lockable(Choice("eu", "Europe", "أوروبا", 78.4, true), Choice("ru", "Russia", "روسيا", 110.2, true), Choice("na", "North America", "أمريكا الشمالية", 150, false)));
            Check(v.Button == "العب على أوروبا وروسيا", "button: " + v.Button);
            Check(v.Sub == "البنق بين 78 و110، والبحث أسرع", "sub: " + v.Sub);
        }

        static void CardChooseThreeNamesAndEnglish()
        {
            Lang.Ar = true;
            var i = Lockable(Choice("eu", "Europe", "أوروبا", 78, true), Choice("na", "North America", "أمريكا الشمالية", 150, true), Choice("ru", "Russia", "روسيا", 110, true));
            var v = CardLogic.Decide(i);
            Check(v.Button == "العب على أوروبا وأمريكا الشمالية وروسيا", "three names: " + v.Button);
            Lang.Ar = false;
            v = CardLogic.Decide(i);
            Check(v.Button == "Play in Europe, North America and Russia", "english three: " + v.Button);
            Check(v.Sub == "Ping between 78 and 150, and a faster queue", "english sub: " + v.Sub);
            v = CardLogic.Decide(Lockable(Choice("eu", "Europe", "أوروبا", 78, true), Choice("ru", "Russia", "روسيا", 110, true)));
            Check(v.Button == "Play in Europe and Russia", "english two: " + v.Button);
            Check(CardLogic.Decide(Lockable(Choice("eu", "Europe", "أوروبا", 78, true))).Title == "Where do you want to play?", "english title");
            Lang.Ar = true;
        }

        static void CardChooseNoneSelected()
        {
            Lang.Ar = true;
            var v = CardLogic.Decide(Lockable(Choice("eu", "Europe", "أوروبا", 78, false), Choice("ru", "Russia", "روسيا", 110, false)));
            Check(v.Kind == CardKind.Choose, "still Choose");
            Check(v.Button == null, "no button when nothing is selected");
            Check(v.Sub == "اختار منطقة وحدة على الأقل", "sub: " + v.Sub);
        }

        static CardInput LockedInput()
        {
            var i = Lockable(Choice("eu", "Europe", "أوروبا", 78, true));
            i.LockedNames = "أوروبا";
            return i;
        }

        static void CardLockedNoMatch()
        {
            Lang.Ar = true;
            var v = CardLogic.Decide(LockedInput());
            Check(v.Kind == CardKind.Locked, "Locked kind");
            Check(v.Title == "✓ مثبّت على أوروبا", "title: " + v.Title);
            Check(v.Sub != null && v.Sub.Contains("جرّب مباراة عادية أول"), "sub: " + v.Sub);
            Check(v.Sub == "افتح اللعبة ودوّر مباراة. جرّب مباراة عادية أول.", "sub exact");
            Check(v.Button == "فك التثبيت" && !v.ShowTiles && !v.ShowLinks, "button, no tiles");
            Check(v.Accent == MainForm.Good, "Locked accent is Good");
        }

        static void CardInMatchOnPicked()
        {
            Lang.Ar = true;
            var i = LockedInput();
            i.MatchRegion = "أوروبا"; i.MatchOnPicked = true;
            var v = CardLogic.Decide(i);
            Check(v.Kind == CardKind.InMatchOk, "InMatchOk kind");
            Check(v.Title == "تلعب الحين في أوروبا ✓", "title: " + v.Title);
            Check(v.Sub == "التثبيت شغّال. البنق داخل اللعبة هو الأدق.", "sub: " + v.Sub);
            Check(v.Button == "فك التثبيت" && v.Accent == MainForm.Good, "button and accent");
        }

        static void CardInMatchViaRelay()
        {
            Lang.Ar = true;
            var i = LockedInput();
            i.MatchRegion = "أوروبا"; i.MatchOnPicked = true; i.MatchViaRelay = true;
            var v = CardLogic.Decide(i);
            Check(v.Kind == CardKind.InMatchRelay, "InMatchRelay kind");
            Check(v.Title == "تلعب الحين في واحد من مناطقك ✓ (أوروبا)", "title: " + v.Title);
            Check(v.Sub == "التثبيت شغّال. البنق داخل اللعبة هو الأدق.", "sub");
            Check(v.Button == "فك التثبيت" && v.Accent == MainForm.Good, "button and accent");
        }

        static void CardInMatchOutside()
        {
            Lang.Ar = true;
            var i = LockedInput();
            i.MatchRegion = "أمريكا الشمالية"; i.MatchOnPicked = false;
            var v = CardLogic.Decide(i);
            Check(v.Kind == CardKind.Outside, "Outside kind");
            Check(v.Title == "اللعبة حطّتك على أمريكا الشمالية", "title: " + v.Title);
            Check(v.Sub == "اقفل اللعبة وافتحها من جديد، وبعدين دوّر مباراة.", "sub: " + v.Sub);
            Check(v.Button == "فك التثبيت", "button");
            Check(v.Accent == MainForm.Warn, "Outside accent is Warn");
        }

        static void CardLockedEnglish()
        {
            Lang.Ar = false;
            var i = LockedInput();
            i.LockedNames = "Europe";
            var v = CardLogic.Decide(i);
            Check(v.Title == "✓ Locked to Europe" && v.Button == "Unlock", "english locked: " + v.Title + " / " + v.Button);
            i.MatchRegion = "Europe"; i.MatchOnPicked = true;
            Check(CardLogic.Decide(i).Title == "You're playing in Europe now ✓", "english in match");
            Lang.Ar = true;
        }

        static void CardBestOnly()
        {
            Lang.Ar = true;
            var v = CardLogic.Decide(new CardInput { Game = "Overwatch 2", Loaded = true, BestServer = "السعودية", BestMs = 30 });
            Check(v.Kind == CardKind.BestOnly, "BestOnly kind");
            Check(v.Title == "أحسن سيرفر لك: السعودية · بنقك تقريباً 30", "title: " + v.Title);
            Check(v.Sub == "اختاره من داخل اللعبة." && v.Button == null, "sub, no button");
            v = CardLogic.Decide(new CardInput { Game = "Overwatch 2", Loaded = true, BestServer = "السعودية" });
            Check(v.Title == "أحسن سيرفر لك: السعودية", "no ping part when unknown: " + v.Title);
        }

        static void CardOnServer()
        {
            Lang.Ar = true;
            var v = CardLogic.Decide(new CardInput { Game = "Overwatch 2", Loaded = true, BestServer = "السعودية", BestMs = 30, CurrentServer = "السعودية", CurrentMs = 32 });
            Check(v.Kind == CardKind.OnServer, "OnServer kind");
            Check(v.Title == "إنت الحين على: السعودية · بنقك 32", "title: " + v.Title);
            Check(v.Button == null, "no button");
            v = CardLogic.Decide(new CardInput { Game = "Overwatch 2", Loaded = true, CurrentServer = "السعودية" });
            Check(v.Title == "إنت الحين على: السعودية", "no ping part when unknown: " + v.Title);
        }

        static void CardNotLockableNothingKnown()
        {
            Lang.Ar = true;
            var v = CardLogic.Decide(new CardInput { Game = "Overwatch 2", Loaded = true, Testing = true });
            Check(v.Kind == CardKind.Testing && v.Title == "أدوّر لك أحسن منطقة...", "testing");
            v = CardLogic.Decide(new CardInput { Game = "Overwatch 2", Loaded = true, Tested = true });
            Check(v.Kind == CardKind.Offline, "tested, nothing = Offline");
        }

        static void RunCardTests()
        {
            Run("CardLoading", CardLoading);
            Run("CardLoadFailedIsOffline", CardLoadFailedIsOffline);
            Run("CardTestingNoPingYet", CardTestingNoPingYet);
            Run("CardNotTestedYetAsksForTest", CardNotTestedYetAsksForTest);
            Run("CardTestedButNoPingIsOffline", CardTestedButNoPingIsOffline);
            Run("CardChooseOne", CardChooseOne);
            Run("CardChooseTwo", CardChooseTwo);
            Run("CardChooseThreeNamesAndEnglish", CardChooseThreeNamesAndEnglish);
            Run("CardChooseNoneSelected", CardChooseNoneSelected);
            Run("CardLockedNoMatch", CardLockedNoMatch);
            Run("CardInMatchOnPicked", CardInMatchOnPicked);
            Run("CardInMatchViaRelay", CardInMatchViaRelay);
            Run("CardInMatchOutside", CardInMatchOutside);
            Run("CardLockedEnglish", CardLockedEnglish);
            Run("CardBestOnly", CardBestOnly);
            Run("CardOnServer", CardOnServer);
            Run("CardNotLockableNothingKnown", CardNotLockableNothingKnown);
        }
    }
}
