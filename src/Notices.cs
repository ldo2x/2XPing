using System;
using System.Collections.Generic;
using System.Linq;

namespace PingBoost
{
    // Warnings that came while the window was minimized: kept (newest last, at most Max, no repeats) and shown when
    // the window is back.
    sealed class HeldNotices
    {
        public const int Max = 4;

        sealed class Held { public string Text; public ToastKind Kind; public bool AboutMatch; }
        readonly List<Held> held = new List<Held>();

        public int Count { get { return held.Count; } }

        public void Hold(string text, ToastKind kind, bool aboutMatch)
        {
            held.RemoveAll(n => n.Text == text);
            held.Add(new Held { Text = text, Kind = kind, AboutMatch = aboutMatch });
            while (held.Count > Max) held.RemoveAt(0);
        }

        // A new match or a lock change: the held warnings about the old match or lock check are stale. Errors stay.
        public void DropMatchWarnings()
        {
            held.RemoveAll(n => n.AboutMatch && n.Kind != ToastKind.Bad);
        }

        // Every held notice, oldest first; none are kept.
        public List<KeyValuePair<string, ToastKind>> TakeAll()
        {
            var list = held.Select(n => new KeyValuePair<string, ToastKind>(n.Text, n.Kind)).ToList();
            held.Clear();
            return list;
        }
    }

    // The live ping's lost replies over the last Window pings of one target; a big share is reported (Issues).
    sealed class LossTracker
    {
        public const int Window = 60;
        public const int ReportPercent = 15;

        readonly Queue<bool> window = new Queue<bool>();
        string label;

        // ms < 0 = no reply. True = report it: a full window with ReportPercent% or more lost. A new label starts over.
        public bool Add(string target, double ms, out int lost)
        {
            lost = 0;
            if (target != label) { label = target; window.Clear(); }
            window.Enqueue(ms < 0);
            if (window.Count > Window) window.Dequeue();
            if (window.Count < Window) return false;
            lost = window.Count(x => x);
            return lost * 100 / window.Count >= ReportPercent;
        }
    }
}
