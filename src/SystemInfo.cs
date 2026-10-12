using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PingBoost
{
    // Hardware, system and setup details sent once per launch with the beta statistics (see SECURITY.md).
    // Nothing personal: no files, no IP address, no list of other programs.
    static class SystemInfo
    {
        [StructLayout(LayoutKind.Sequential)]
        class MemoryStatus
        {
            public uint Length = (uint)Marshal.SizeOf(typeof(MemoryStatus));
            public uint Load;
            public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll")]
        static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatus status);

        static string Reg(RegistryKey root, string path, string name)
        {
            try { using (var k = root.OpenSubKey(path)) return k == null ? null : Convert.ToString(k.GetValue(name)); } catch { return null; }
        }

        public static Dictionary<string, object> Collect(List<Game> games)
        {
            var d = new Dictionary<string, object>();
            Try(() => d["cpu"] = (Reg(Registry.LocalMachine, @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString") ?? "").Trim());
            d["cores"] = Environment.ProcessorCount;
            Try(() => { var m = new MemoryStatus(); if (GlobalMemoryStatusEx(m)) d["ram_gb"] = (int)Math.Round(m.TotalPhys / 1073741824.0); });
            Try(() => d["gpu"] = string.Join(" + ", Gpus()));
            Try(() =>
            {
                var s = Screen.PrimaryScreen.Bounds;
                d["screen"] = s.Width + "x" + s.Height;
                d["screens"] = Screen.AllScreens.Length;
                using (var g = Graphics.FromHwnd(IntPtr.Zero)) d["scale"] = (int)Math.Round(g.DpiX / 96f * 100);
            });
            Try(() => d["windows"] = WindowsName());
            Try(() => d["dotnet"] = Reg(Registry.LocalMachine, @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", "Release"));
            d["locale"] = CultureInfo.CurrentCulture.Name;
            d["ui_lang"] = CultureInfo.CurrentUICulture.Name;
            Try(() => d["timezone"] = TimeZoneInfo.Local.Id);
            Try(() => d["laptop"] = SystemInformation.PowerStatus.BatteryChargeStatus != BatteryChargeStatus.NoSystemBattery);
            Try(() => Network(d));
            Try(() => d["games_installed"] = InstalledGames(games));
            Try(() => d["wireguard"] = Tunnel.Installed);
            d["autotest"] = Prefs.AutoTest;
            d["hidden_games"] = string.Join(",", Prefs.HiddenGames);
            return d;
        }

        static void Try(Action a) { try { a(); } catch { } }

        static IEnumerable<string> Gpus()
        {
            const string cls = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
            var list = new List<string>();
            using (var k = Registry.LocalMachine.OpenSubKey(cls))
            {
                if (k == null) return list;
                foreach (var sub in k.GetSubKeyNames().Where(n => Regex.IsMatch(n, @"^\d{4}$")))
                {
                    var name = Reg(Registry.LocalMachine, cls + "\\" + sub, "DriverDesc");
                    if (!string.IsNullOrEmpty(name) && !name.Contains("Basic Display") && !name.Contains("Virtual") && !list.Contains(name)) list.Add(name);
                }
            }
            return list;
        }

        static string WindowsName()
        {
            const string cv = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
            string product = Reg(Registry.LocalMachine, cv, "ProductName") ?? "Windows";
            string build = Reg(Registry.LocalMachine, cv, "CurrentBuild") ?? "";
            int b;
            // Windows 11 still reports "Windows 10" in ProductName
            if (int.TryParse(build, out b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");
            return product + " " + (Reg(Registry.LocalMachine, cv, "DisplayVersion") ?? "") + " (" + build + "." + Reg(Registry.LocalMachine, cv, "UBR") + ")";
        }

        // The adapter that carries the internet traffic (the one with a default gateway): Wi-Fi or cable, and its link speed.
        static void Network(Dictionary<string, object> d)
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                            n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                                                                         !g.Address.ToString().StartsWith("0.")))
                .OrderByDescending(n => n.Speed).FirstOrDefault();
            if (nic == null) return;
            string desc = (nic.Description ?? "").ToLowerInvariant();
            d["net"] = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "wifi"
                     : desc.Contains("wireguard") || desc.Contains("tap-") || desc.Contains("vpn") ? "vpn"
                     : nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? "ethernet"
                     : nic.NetworkInterfaceType.ToString().ToLowerInvariant();
            if (nic.Speed > 0) d["net_mbps"] = (long)(nic.Speed / 1000000);
        }

        // Every Steam library folder on this PC.
        static List<string> SteamLibraries()
        {
            string steam = Reg(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
            var libs = new List<string>();
            if (string.IsNullOrEmpty(steam)) return libs;
            libs.Add(steam.Replace('/', '\\'));
            string vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
            if (File.Exists(vdf))
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            return libs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // Full path of the game's exe, for firewall rules that only touch the game: the running game first,
        // then the last path seen, then the Steam and Battle.net install folders. Null if it can't be found.
        public static string FindExe(Game g)
        {
            foreach (var name in g.Processes)
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
                    try
                    {
                        string path = p.MainModule.FileName;
                        Storage.SetSetting("exe_" + g.Icon, path);
                        return path;
                    }
                    catch { }
                    finally { p.Dispose(); }

            var candidates = new List<string>();
            string last = Storage.GetSetting("exe_" + g.Icon);
            if (!string.IsNullOrEmpty(last)) candidates.Add(last);
            string exe = g.Processes.Length > 0 ? g.Processes[0] + ".exe" : null;
            if (exe == null) return null;
            // Steam: steamapps\common\<installdir>\ (and \_retail_\)
            string id = g.SteamAppId ?? g.AppId;
            foreach (var lib in SteamLibraries())
            {
                string manifest = Path.Combine(lib, @"steamapps\appmanifest_" + id + ".acf");
                if (id == null || !File.Exists(manifest)) continue;
                var m = Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s+\"([^\"]+)\"");
                if (!m.Success) continue;
                string dir = Path.Combine(lib, @"steamapps\common", m.Groups[1].Value);
                candidates.Add(Path.Combine(dir, exe));
                candidates.Add(Path.Combine(dir, "_retail_", exe));
                // Unreal games keep it deeper, e.g. DeadByDaylight\Binaries\Win64
                try { candidates.AddRange(Directory.EnumerateFiles(dir, exe, SearchOption.AllDirectories).Take(1)); } catch { }
            }
            // Riot (Valorant): the install folder is written in the Riot Client's product settings
            if (exe.StartsWith("VALORANT", StringComparison.OrdinalIgnoreCase))
            {
                string root = @"C:\Riot Games\VALORANT\live";
                string riot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                                           @"Riot Games\Metadata\valorant.live\valorant.live.product_settings.yaml");
                try
                {
                    var m = Regex.Match(File.ReadAllText(riot), "product_install_full_path:\\s*\"?([^\"\\r\\n]+)");
                    if (m.Success) root = m.Groups[1].Value.Trim().Replace('/', '\\');
                }
                catch { }
                candidates.Add(Path.Combine(root, @"ShooterGame\Binaries\Win64", exe));
            }
            // Epic Games launcher (Fortnite): InstallLocation in its manifests, the exe a few folders down
            string epic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
            if (Directory.Exists(epic))
                foreach (var item in Directory.GetFiles(epic, "*.item"))
                {
                    try
                    {
                        string text = File.ReadAllText(item);
                        if (text.IndexOf(g.Name.Split(':')[0], StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var m = Regex.Match(text, "\"InstallLocation\"\\s*:\\s*\"([^\"]+)\"");
                        if (!m.Success) continue;
                        string dir = m.Groups[1].Value.Replace(@"\\", @"\").Replace('/', '\\');
                        candidates.AddRange(Directory.EnumerateFiles(dir, exe, SearchOption.AllDirectories).Take(1));
                    }
                    catch { }
                }
            // Battle.net (Overwatch): InstallLocation\_retail_\Overwatch.exe
            string bnet = Reg(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\" + g.Processes[0], "InstallLocation");
            if (!string.IsNullOrEmpty(bnet))
            {
                candidates.Add(Path.Combine(bnet, "_retail_", exe));
                candidates.Add(Path.Combine(bnet, exe));
            }
            return candidates.FirstOrDefault(File.Exists);
        }

        // Which of the supported games are installed (Steam, Epic, Riot, Battle.net). Names only.
        static List<string> InstalledGames(List<Game> games)
        {
            var found = new List<string>();
            // Steam: every library folder, then appmanifest_<appid>.acf
            var libs = SteamLibraries();
            foreach (var g in games)
            {
                string id = g.SteamAppId ?? g.AppId;
                if (id != null && libs.Any(l => File.Exists(Path.Combine(l, @"steamapps\appmanifest_" + id + ".acf")))) found.Add(g.Name);
            }
            // Epic Games launcher manifests
            string epic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
            if (Directory.Exists(epic))
                foreach (var f in Directory.GetFiles(epic, "*.item"))
                {
                    var m = Regex.Match(File.ReadAllText(f), "\"DisplayName\"\\s*:\\s*\"([^\"]+)\"");
                    if (!m.Success) continue;
                    var g = games.FirstOrDefault(x => m.Groups[1].Value.IndexOf(x.Name, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (g != null && !found.Contains(g.Name)) found.Add(g.Name + " (Epic)");
                }
            // Riot (Valorant) and Battle.net (Overwatch)
            string data = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (Directory.Exists(Path.Combine(data, @"Riot Games\Metadata\valorant.live"))) found.Add("Valorant");
            if (Reg(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Overwatch", "DisplayName") != null &&
                !found.Contains("Overwatch 2")) found.Add("Overwatch 2 (Battle.net)");
            return found;
        }
    }
}
