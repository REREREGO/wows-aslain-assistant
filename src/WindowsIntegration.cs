using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace AslainAssistant
{
    public sealed class GameState
    {
        public string Path, GameVersion, Evidence, ActiveBuildPath;
        public PackVersion Installed;
        public bool HasInstallation, HasActiveMods;
    }
    public static class GameDetector
    {
        const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Aslains_WoWs_Modpack_Installer_is1";
        public static bool IsGameFolder(string folder)
        {
            try { return !string.IsNullOrWhiteSpace(folder) && File.Exists(System.IO.Path.Combine(folder, "WorldOfWarships.exe")) && Directory.Exists(System.IO.Path.Combine(folder, "bin")); }
            catch { return false; }
        }
        public static string Normalize(string folder)
        {
            if (!IsGameFolder(folder)) throw new InvalidDataException("请选择包含 WorldOfWarships.exe 和 bin 文件夹的游戏根目录。");
            return System.IO.Path.GetFullPath(folder).TrimEnd('\\', '/');
        }
        public static List<string> Discover()
        {
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
                foreach (RegistryView view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
                    try
                    {
                        using (var root = RegistryKey.OpenBaseKey(hive, view))
                        using (var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                        {
                            if (uninstall == null) continue;
                            foreach (string name in uninstall.GetSubKeyNames())
                                using (var key = uninstall.OpenSubKey(name))
                                {
                                    if (key == null) continue;
                                    if (Regex.IsMatch((key.GetValue("DisplayName") ?? "").ToString(), "Warships|Aslain", RegexOptions.IgnoreCase))
                                    {
                                        string location = (key.GetValue("InstallLocation") ?? "").ToString();
                                        if (IsGameFolder(location)) candidates.Add(Normalize(location));
                                    }
                                }
                        }
                    }
                    catch (System.Security.SecurityException) { }
                    catch (UnauthorizedAccessException) { }
            var bases = new List<string>();
            foreach (DriveInfo drive in DriveInfo.GetDrives()) if (drive.DriveType == DriveType.Fixed && drive.IsReady)
            {
                bases.Add(drive.RootDirectory.FullName);
                foreach (string suffix in new[] { "Games", "Game", "Wargaming", "360 Wargaming", "Steam\\steamapps\\common", "SteamLibrary\\steamapps\\common", "Program Files (x86)\\Steam\\steamapps\\common" }) bases.Add(System.IO.Path.Combine(drive.RootDirectory.FullName, suffix));
            }
            try
            {
                using (var steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    string root = steam == null ? "" : (steam.GetValue("SteamPath") ?? "").ToString();
                    if (Directory.Exists(root))
                    {
                        bases.Add(System.IO.Path.Combine(root, "steamapps", "common"));
                        string libraries = System.IO.Path.Combine(root, "steamapps", "libraryfolders.vdf");
                        if (File.Exists(libraries)) foreach (Match m in Regex.Matches(Storage.ReadSmall(libraries), "\"path\"\\s+\"([^\"]+)\"")) bases.Add(System.IO.Path.Combine(m.Groups[1].Value.Replace("\\\\", "\\"), "steamapps", "common"));
                    }
                }
            }
            catch (IOException) { }
            foreach (string folder in bases.Distinct(StringComparer.OrdinalIgnoreCase))
                try
                {
                    if (!Directory.Exists(folder)) continue;
                    foreach (string child in Directory.GetDirectories(folder))
                        if (Regex.IsMatch(System.IO.Path.GetFileName(child), "Warships", RegexOptions.IgnoreCase) && IsGameFolder(child)) candidates.Add(Normalize(child));
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            return candidates.OrderBy(x => x).ToList();
        }
        static XDocument ReadXml(string file)
        {
            using (var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 })) return XDocument.Load(reader);
        }
        public static string ClientKind(string folder)
        {
            string file = System.IO.Path.Combine(folder, "game_info.xml");
            if (File.Exists(file))
            {
                string id = (ReadXml(file).Descendants("id").FirstOrDefault() ?? new XElement("id", "")).Value;
                if (id.IndexOf(".CN.", StringComparison.OrdinalIgnoreCase) >= 0) return "CN";
                if (id.StartsWith("WOWS.", StringComparison.OrdinalIgnoreCase) && !id.Contains(".RU.")) return "INTL";
            }
            return "";
        }
        static string GameVersion(string text)
        {
            var m = Regex.Match(text ?? "", @"(?<!\d)(\d{1,3})[.,](\d{1,3})[.,](\d{1,3})");
            return m.Success ? int.Parse(m.Groups[1].Value) + "." + int.Parse(m.Groups[2].Value) + "." + int.Parse(m.Groups[3].Value) : "";
        }
        public static GameState Inspect(string folder)
        {
            folder = Normalize(folder);
            var result = new GameState { Path = folder, GameVersion = "", Evidence = "没有安装记录", HasActiveMods = true };
            string activeBuild = "", info = System.IO.Path.Combine(folder, "game_info.xml");
            if (File.Exists(info))
            {
                var client = ReadXml(info).Descendants("version").FirstOrDefault(x => (string)x.Attribute("name") == "client");
                if (client != null)
                {
                    string version = (string)client.Attribute("installed") ?? "";
                    result.GameVersion = GameVersion(version);
                    activeBuild = version.Split('.').Last();
                }
            }
            if (string.IsNullOrEmpty(result.GameVersion))
            {
                string versionXml = System.IO.Path.Combine(folder, "version.xml");
                if (File.Exists(versionXml)) result.GameVersion = GameVersion(ReadXml(versionXml).Root.Value);
            }
            string bin = System.IO.Path.Combine(folder, "bin");
            // When game_info exists, never use a pre-downloaded higher build in place of the active client.
            string build = !string.IsNullOrEmpty(activeBuild) && Directory.Exists(System.IO.Path.Combine(bin, activeBuild)) ? System.IO.Path.Combine(bin, activeBuild) : "";
            if (string.IsNullOrEmpty(result.GameVersion))
            {
                var versions = new List<Tuple<string, string>>();
                foreach (string dir in Directory.GetDirectories(bin))
                {
                    string exe = System.IO.Path.Combine(dir, "bin64", "WorldOfWarships64.exe");
                    if (File.Exists(exe)) versions.Add(Tuple.Create(dir, GameVersion(FileVersionInfo.GetVersionInfo(exe).FileVersion)));
                }
                if (versions.Count == 1) { build = versions[0].Item1; result.GameVersion = versions[0].Item2; }
            }
            result.ActiveBuildPath = build;
            if (!string.IsNullOrEmpty(build))
            {
                string mods = System.IO.Path.Combine(build, "res_mods");
                result.HasActiveMods = Directory.Exists(mods) && (Directory.EnumerateDirectories(mods).Any() || Directory.EnumerateFiles(mods).Any(x => !System.IO.Path.GetFileName(x).Equals("readme.txt", StringComparison.OrdinalIgnoreCase)));
            }
            string modDir = System.IO.Path.Combine(folder, "Aslain_Modpack");
            // The uninstaller plus logs / installed executable are evidence. A downloaded installer alone is not.
            result.HasInstallation = File.Exists(System.IO.Path.Combine(modDir, "unins000.exe")) || File.Exists(System.IO.Path.Combine(modDir, "Uninstall", "unins000.exe"));
            if (!result.HasInstallation) return result;
            foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
                foreach (RegistryView view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
                    try
                    {
                        using (var root = RegistryKey.OpenBaseKey(hive, view)) using (var key = root.OpenSubKey(UninstallKey))
                        {
                            if (key == null) continue;
                            string location = (key.GetValue("InstallLocation") ?? key.GetValue("Inno Setup: App Path") ?? "").ToString().TrimEnd('\\');
                            if (!string.Equals(location, folder, StringComparison.OrdinalIgnoreCase)) continue;
                            try { result.Installed = PackVersion.Parse((key.GetValue("DisplayVersion") ?? "").ToString()); result.Evidence = "Windows 安装记录"; return result; } catch (InvalidDataException) { }
                        }
                    }
                    catch (System.Security.SecurityException) { }
                    catch (UnauthorizedAccessException) { }
            string installedExe = System.IO.Path.Combine(modDir, "Aslains_WoWs_Modpack_Installer.exe");
            if (File.Exists(installedExe))
                try { result.Installed = PackVersion.Parse(FileVersionInfo.GetVersionInfo(installedExe).ProductVersion); result.Evidence = "游戏目录内已安装的阿斯兰"; return result; } catch (InvalidDataException) { }
            string log = System.IO.Path.Combine(modDir, "_Aslains_Installer.log");
            if (!File.Exists(log)) log = System.IO.Path.Combine(modDir, "Logs", "Aslains_Installer.log");
            if (File.Exists(log))
            {
                string start;
                using (var reader = new StreamReader(log)) { char[] chars = new char[32000]; start = new string(chars, 0, reader.Read(chars, 0, chars.Length)); }
                var m = Regex.Match(start, @"Original Setup EXE:.*Aslains_WoWs_Modpack_Installer_v\.([\d._]+)\.exe", RegexOptions.IgnoreCase);
                if (m.Success) { result.Installed = PackVersion.Parse(m.Groups[1].Value); result.Evidence = "阿斯兰安装日志"; }
            }
            return result;
        }
        public static Preset ReadPreset(string folder)
        {
            Normalize(folder);
            // Modern Aslain writes a .reg. Prefer it over a stale legacy INF.
            string dir = System.IO.Path.Combine(folder, "Aslain_Modpack");
            foreach (string reg in new[] { System.IO.Path.Combine(dir, "Exported_settings", "Aslains_WoWs_Modpack_Exported_Settings.reg"), System.IO.Path.Combine(dir, "_Aslains_WoWs_Modpack_Exported_Settings.reg") })
                if (File.Exists(reg)) return Preset.FromReg(Storage.ReadSmall(reg));
            foreach (string root in new[] { dir, folder })
                foreach (string name in new[] { "_Aslains_Installer_Options.inf", "_Aslains_Installer_Options_.inf" })
                {
                    string file = System.IO.Path.Combine(root, name);
                    if (File.Exists(file)) return Preset.FromInf(Storage.ReadSmall(file));
                }
            return null;
        }
        public static bool GameRunning()
        {
            foreach (var process in Process.GetProcesses()) using (process)
                try
                {
                    if (Regex.IsMatch(process.ProcessName, @"^(WorldOfWarships(?:64|32)?|wows|wows64)$", RegexOptions.IgnoreCase)) return true;
                }
                catch (InvalidOperationException) { }
            return false;
        }
        public static bool InstallerRunning()
        {
            return InstanceControl.ModInstallerRunning();
        }
    }
    public static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "WoWSAslainAssistant";
        public static bool Enabled { get { using (var key = Registry.CurrentUser.OpenSubKey(RunKey)) return key != null && key.GetValue(Name) != null; } }
        public static void Set(bool enabled, string executable)
        {
            string command = "\"" + System.IO.Path.GetFullPath(executable) + "\" --background";
            if (command.Length > 260) throw new InvalidDataException("自启路径过长，请把软件放到更短的固定目录。");
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled) key.SetValue(Name, command, RegistryValueKind.String); else key.DeleteValue(Name, false);
            }
        }
    }
}
