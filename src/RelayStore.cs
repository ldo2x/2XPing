using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PingBoost
{
    // The player's own relays (WireGuard configs copied into the data folder, listed in relays.txt).
    sealed class RelayStore
    {
        readonly List<Relay> relays;
        readonly string dir;                  // where the configs are copied (<data>\relays)
        readonly Action<List<Relay>> save;    // Storage.SaveRelays

        public RelayStore() : this(Storage.LoadRelays(), Path.Combine(Storage.Dir, "relays"), Storage.SaveRelays) { }

        // tests: a temp folder and a fake save
        public RelayStore(List<Relay> relays, string dir, Action<List<Relay>> save)
        {
            this.relays = relays;
            this.dir = dir;
            this.save = save;
        }

        // the list itself (the route box indexes it, Routing reads it); change it only through Add and Remove
        public List<Relay> All { get { return relays; } }

        public bool Has(string name) { return relays.Any(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); }

        // The config's file name: the relay's name with anything but ASCII letters and digits turned into '_'.
        public static string SafeName(string name)
        {
            return new string(name.Select(c => char.IsLetterOrDigit(c) && c < 128 ? c : '_').ToArray());
        }

        // Copies the config into the data folder (replacing one of the same file name) and saves the list.
        public Relay Add(string name, string confPath, string agentUrl, string token)
        {
            Directory.CreateDirectory(dir);
            string dest = Path.Combine(dir, SafeName(name) + ".conf");
            File.Copy(confPath, dest, true);
            var r = new Relay { Name = name, ConfPath = dest, AgentUrl = agentUrl, Token = token };
            relays.Add(r);
            save(relays);
            return r;
        }

        public void Remove(Relay r)
        {
            relays.Remove(r);
            save(relays);
        }
    }
}
