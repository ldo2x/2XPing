using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace PingBoost
{
    // Runs work on the UI thread. Services hand their results back through it, so their events always arrive on the
    // UI thread and subscribers never marshal.
    interface IUiDispatcher
    {
        void Post(Action a);   // runs a on the UI thread (now, if already on it); dropped once the window is disposed
        bool OnUiThread { get; }
    }

    // The main window's dispatcher. Created on the UI thread, before the window has a handle: anything posted from
    // another thread before the handle exists waits and runs once it does.
    sealed class ControlDispatcher : IUiDispatcher
    {
        readonly Control owner;
        readonly int uiThread;
        readonly List<Action> early = new List<Action>(); // posted from other threads before the window had a handle
        bool handleReady;

        public ControlDispatcher(Control owner)
        {
            this.owner = owner;
            uiThread = Thread.CurrentThread.ManagedThreadId;
            owner.HandleCreated += delegate { Flush(); };
        }

        public bool OnUiThread { get { return Thread.CurrentThread.ManagedThreadId == uiThread; } }

        public void Post(Action a)
        {
            if (owner.IsDisposed || owner.Disposing) return;
            if (OnUiThread) { a(); return; }
            // without a handle, InvokeRequired says false even on another thread: never run it here, keep it for later
            lock (early)
                if (!handleReady) { early.Add(a); return; }
            try { owner.BeginInvoke(a); }
            catch (InvalidOperationException) { } // also ObjectDisposedException: the window is closing
        }

        void Flush()
        {
            List<Action> waiting;
            lock (early)
            {
                handleReady = true;
                waiting = early.ToList();
                early.Clear();
            }
            foreach (var a in waiting)
                try { owner.BeginInvoke(a); } catch (InvalidOperationException) { }
        }
    }

    // Tests: runs everything at once, on the caller's thread.
    sealed class InlineDispatcher : IUiDispatcher
    {
        public bool OnUiThread { get { return true; } }
        public void Post(Action a) { a(); }
    }
}
