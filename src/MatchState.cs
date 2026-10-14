using System;

namespace PingBoost
{
    // What the server checks saw of the running match: the region (or, for games that can't be locked, the server).
    sealed class MatchState
    {
        public string Region;     // where the running match is, in the player's words; null = not in a match
        public bool OnPicked;     // ...and it is one of the picked regions
        public bool ViaRelay;     // only the entry relay is in sight
        public string Entry;      // ViaRelay: where the game enters Valve's network ("Dubai")
        public string Place;      // the match server's datacenter, known only from Deadlock's console log
        public string Server;     // games that can't be locked: the server (city) the game is on
        public int Ms = -1;       // ...and its ping; -1 = unknown

        public bool Any { get { return Region != null || ViaRelay || Server != null; } }

        // True = a different match than before (the warnings held about the last one are stale; the same match keeps them).
        public bool Set(string region, bool onPicked, bool viaRelay, string entry, string place)
        {
            bool different = region != Region || onPicked != OnPicked || viaRelay != ViaRelay || place != Place;
            Region = region;
            OnPicked = onPicked;
            ViaRelay = viaRelay;
            Entry = entry;
            Place = place;
            return different;
        }

        public void SetServer(string server, int ms)
        {
            Server = server;
            Ms = ms;
        }

        public void Clear()
        {
            Region = Entry = Place = null;
            OnPicked = ViaRelay = false;
            Server = null;
            Ms = -1;
        }
    }
}
