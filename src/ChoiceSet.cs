using System;
using System.Collections.Generic;
using System.Linq;

namespace PingBoost
{
    // The region tiles of the game on screen: every region, the two or three shown first, and the player's selection.
    sealed class ChoiceSet
    {
        public ChoiceSet()
        {
            All = new List<RegionChoice>();
            Featured = new List<RegionChoice>();
            Every = new List<RegionChoice>();
        }

        public List<RegionChoice> All { get; private set; }      // every region of the game on screen
        public List<RegionChoice> Featured { get; private set; } // the two or three tiles shown first
        public List<RegionChoice> Every { get; private set; }    // all of them, featured first ("Other regions")
        public Game Game { get; private set; }                   // the game the choices were built for; null = build again
        string sig;                                              // what they show, so a rebuild that changes nothing keeps the same tiles

        public bool UserPicked;   // the player clicked a tile: keep their selection when the numbers change
        public bool Expanded;     // "Other regions" is open
        public bool ManualOpen;   // simple layout: "Pick by hand" shows the server list

        // the next Rebuild builds the tiles again, whatever they show
        public void Forget() { Game = null; }

        public static string Signature(List<RegionChoice> all)
        {
            return string.Join("|", all.Select(c => c.Def.Key + ":" + (c.Ping < double.MaxValue ? Math.Round(c.Ping).ToString() : "-") +
                                                    ":" + c.Via + ":" + c.Matches + ":" + c.Players).ToArray());
        }

        // New pings, player numbers or a new game: take the freshly built tiles. The player's own selection stays as long
        // as those regions are still there; otherwise the best one starts selected. False = nothing changed (kept as is).
        public bool Rebuild(Game g, List<RegionChoice> all)
        {
            string s = Signature(all);
            if (g == Game && s == sig) return false;
            var keep = g == Game && UserPicked ? All.Where(c => c.Selected).Select(c => c.Def.Key).ToList() : null;
            var feat = RegionGroups.Featured(all);
            if (keep != null && (keep.Count == 0 || all.Any(c => keep.Contains(c.Def.Key))))
                foreach (var c in all) c.Selected = keep.Contains(c.Def.Key);
            All = all;
            Featured = feat;
            Every = RegionGroups.FeaturedFirst(all, feat);
            Game = g;
            sig = s;
            return true;
        }
    }
}
