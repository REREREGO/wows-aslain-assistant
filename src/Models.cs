using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace AslainAssistant
{
    public sealed class PackVersion : IComparable<PackVersion>
    {
        public int Major, Minor, Patch, Revision;
        public string Game { get { return Major + "." + Minor + "." + Patch; } }
        public override string ToString() { return Game + " #" + Revision.ToString("00"); }
        public string FileTag { get { return Game + "_" + Revision.ToString("00"); } }
        public static PackVersion Parse(string text)
        {
            var m = Regex.Match(text ?? "", @"(?<!\d)(\d{1,3})\.(\d{1,3})\.(\d{1,3})(?:\s*#\s*|_|\.)(\d{1,4})(?!\d)");
            if (!m.Success) throw new InvalidDataException("无法识别阿斯兰版本号。");
            return new PackVersion { Major = int.Parse(m.Groups[1].Value), Minor = int.Parse(m.Groups[2].Value), Patch = int.Parse(m.Groups[3].Value), Revision = int.Parse(m.Groups[4].Value) };
        }
        public int CompareTo(PackVersion b)
        {
            if (b == null) return 1;
            int[] aParts = { Major, Minor, Patch, Revision }, bParts = { b.Major, b.Minor, b.Patch, b.Revision };
            for (int i = 0; i < 4; i++) { int c = aParts[i].CompareTo(bParts[i]); if (c != 0) return c; }
            return 0;
        }
    }

    public sealed class Release
    {
        public const string Page = "https://aslain.com/index.php?/topic/2020-download-%E2%98%85-world-of-warships-%E2%98%85-modpack/";
        public PackVersion Version;
        public Uri Download;
        public string Sha256;
        public static Release Parse(string html)
        {
            // Restrict parsing to the actual classic-package heading, not metadata or the changelog.
            var heading = Regex.Match(html, @"THE CLASSIC MODPACK\s+(\d+\.\d+\.\d+\s*#\s*\d+)", RegexOptions.IgnoreCase);
            if (!heading.Success) heading = Regex.Match(html, @"Aslain(?:&#0?39;|'|’|&apos;)s WoWs ModPack\s+v?(\d+\.\d+\.\d+\s*#\s*\d+)\s*<", RegexOptions.IgnoreCase);
            if (!heading.Success) throw new InvalidDataException("官网页面结构已变化，未找到经典版版本标题。请稍后重试或打开官网。");
            string block = html.Substring(heading.Index, Math.Min(12000, html.Length - heading.Index));
            var link = Regex.Match(block, "href=[\"'](https://filez\\.aslain\\.com/Aslains_WoWs_Modpack_Installer_v\\.[0-9._]+\\.exe)[\"']", RegexOptions.IgnoreCase);
            var hash = Regex.Match(block, @"SHA-256(?:</a>)?\s*:\s*(?:<[^>]+>\s*)*([a-fA-F0-9]{64})(?![a-fA-F0-9])", RegexOptions.IgnoreCase);
            if (!link.Success || !hash.Success) throw new InvalidDataException("官网未给出可信的直链和 SHA-256，已停止自动安装。");
            var version = PackVersion.Parse(heading.Groups[1].Value);
            var uri = new Uri(WebUtility.HtmlDecode(link.Groups[1].Value));
            if (version.CompareTo(PackVersion.Parse(uri.AbsolutePath)) != 0) throw new InvalidDataException("官网版本标题与下载链接不一致，请稍后重试。");
            return new Release { Version = version, Download = uri, Sha256 = hash.Groups[1].Value.ToLowerInvariant() };
        }
    }

    public sealed class Preset
    {
        public string Components { get; set; }
        public string Tasks { get; set; }
        public string Language { get; set; }
        public string Source { get; set; }
        public int Count { get { return (Components ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Length; } }
        public Preset() { Components = ""; Tasks = ""; Language = "cn"; Source = ""; }
        public Preset Validate()
        {
            if (string.IsNullOrWhiteSpace(Components) || Components.Length > 200000 || !Regex.IsMatch(Components, @"^[a-zA-Z0-9_\\,*!\-]+$")) throw new InvalidDataException("配置中的插件列表为空或格式无效。");
            if (Tasks == null || Tasks.Length > 10000 || !Regex.IsMatch(Tasks, @"^[a-zA-Z0-9_\\,*!\-]*$")) throw new InvalidDataException("配置中的安装选项格式无效。");
            if (string.IsNullOrEmpty(Language)) Language = "cn";
            if (!Regex.IsMatch(Language, @"^[a-zA-Z0-9_-]{1,24}$")) throw new InvalidDataException("配置语言无效。");
            if (Source == null || Source.Length > 500) Source = "导入配置";
            return this;
        }
        public string ToInf(string gamePath, bool preserveDownloadCache = false)
        {
            Validate();
            if ((gamePath ?? "").IndexOfAny(new[] { '\r', '\n', '"', '\0' }) >= 0) throw new InvalidDataException("游戏目录包含无效字符。");
            string tasks = preserveDownloadCache ? string.Join(",", Tasks.Split(',').Where(x => !x.TrimStart('*').Equals("dlc_folder", StringComparison.OrdinalIgnoreCase))) : Tasks;
            return "[Setup]\r\nLang=" + Language + "\r\nDir=" + (gamePath ?? "") + "\r\nNoIcons=1\r\nSetupType=custom\r\nComponents=" + Components + "\r\nTasks=" + tasks + "\r\n";
        }
        public static Preset FromInf(string text)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool setup = false;
            foreach (string raw in text.TrimStart('\uFEFF').Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("[")) { setup = line.Equals("[Setup]", StringComparison.OrdinalIgnoreCase); continue; }
                int eq = line.IndexOf('=');
                if (!setup || eq <= 0 || line.StartsWith(";")) continue;
                string key = line.Substring(0, eq).Trim(), val = line.Substring(eq + 1).Trim();
                if (val.StartsWith("\"") && val.EndsWith("\"") && val.Length >= 2) val = val.Substring(1, val.Length - 2);
                if (values.ContainsKey(key)) throw new InvalidDataException("配置有重复字段：" + key);
                values[key] = val;
            }
            return new Preset { Components = Value(values, "Components"), Tasks = Value(values, "Tasks"), Language = Value(values, "Lang"), Source = "INF 配置" }.Validate();
        }
        static string Value(Dictionary<string, string> values, string key) { string v; return values.TryGetValue(key, out v) ? v : ""; }
        public static Preset FromReg(string text)
        {
            // Never import a .reg into Windows. Read only selected fields from the exact WoWs key.
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool target = false, found = false;
            foreach (string raw in text.TrimStart('\uFEFF').Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("["))
                {
                    target = Regex.IsMatch(line, @"^\[HKEY_(?:LOCAL_MACHINE|CURRENT_USER)\\SOFTWARE\\(?:WOW6432Node\\)?Microsoft\\Windows\\CurrentVersion\\Uninstall\\Aslains_WoWs_Modpack_Installer_is1\]$", RegexOptions.IgnoreCase);
                    found |= target; continue;
                }
                if (!target) continue;
                var m = Regex.Match(line, "^\"(Inno Setup: (?:Selected Components|Selected Tasks|Language))\"=\"((?:[^\"\\\\]|\\\\.)*)\"$");
                if (m.Success) values[m.Groups[1].Value] = Regex.Replace(m.Groups[2].Value, @"\\([\\"" ])", "$1");
            }
            if (!found) throw new InvalidDataException("这不是阿斯兰 WoWs 的配置导出文件。");
            return new Preset { Components = Value(values, "Inno Setup: Selected Components"), Tasks = Value(values, "Inno Setup: Selected Tasks"), Language = Value(values, "Inno Setup: Language"), Source = "阿斯兰 REG 配置" }.Validate();
        }
    }

    public sealed class Settings
    {
        public string Server { get; set; }
        public string Id { get; set; }
        public string Name { get; set; }
        public string GamePath { get; set; }
        public bool AutoUpdate { get; set; }
        public int CheckHours { get; set; }
        public Preset Preset { get; set; }
        public bool PendingPreset { get; set; }
        public string FailedRelease { get; set; }
        public string LastCheck { get; set; }
        public string PendingRun { get; set; }
        public string IncompleteRelease { get; set; }
        public string InstallIssue { get; set; }
        public Settings() { Id = "cn"; Name = "国服客户端"; Server = "CN"; GamePath = ""; AutoUpdate = false; CheckHours = 1; FailedRelease = ""; LastCheck = ""; }
        public Settings Validate()
        {
            if (!new[] { "CN", "INTL" }.Contains(Server)) throw new InvalidDataException("客户端类型无效。");
            if (Id == null || !Regex.IsMatch(Id, "^[a-z0-9-]{1,40}$")) throw new InvalidDataException("客户端配置编号无效。");
            if (string.IsNullOrWhiteSpace(Name) || Name.Length > 40 || Name.Any(char.IsControl)) throw new InvalidDataException("客户端名称应为 1 至 40 个字符。");
            if (GamePath == null || GamePath.Length > 240 || GamePath.IndexOfAny(new[] { '"', '\r', '\n', '\0' }) >= 0) throw new InvalidDataException("游戏目录无效。");
            if (CheckHours < 1 || CheckHours > 24) throw new InvalidDataException("检查间隔应为 1 至 24 小时。");
            if (!string.IsNullOrEmpty(PendingRun) && !Regex.IsMatch(PendingRun, "^[0-9]{8}-[0-9]{6}-[a-f0-9]{6}$")) throw new InvalidDataException("安装记录编号无效。");
            if (!string.IsNullOrEmpty(IncompleteRelease)) PackVersion.Parse(IncompleteRelease);
            if (InstallIssue != null && InstallIssue.Length > 4000) throw new InvalidDataException("安装诊断信息过长。");
            if (Preset != null) Preset.Validate();
            return this;
        }
    }
    public sealed class Backup
    {
        public string Format { get; set; }
        public int Schema { get; set; }
        public string ExportedAt { get; set; }
        public Settings Settings { get; set; }
    }
    public static class Storage
    {
        public static string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WoWS-Aslain-Assistant");
        public static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = 1024 * 1024, RecursionLimit = 16 }; }
        public static string ReadSmall(string file)
        {
            if (new FileInfo(file).Length > 1024 * 1024) throw new InvalidDataException("配置文件超过 1 MB。");
            return File.ReadAllText(file); // StreamReader detects UTF-16 / UTF-8 BOM used by Regedit / Inno.
        }
        public static void AtomicWrite(string file, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file)));
            string temp = file + ".tmp";
            File.WriteAllText(temp, text, new UTF8Encoding(true));
            if (File.Exists(file)) File.Replace(temp, file, file + ".bak", true); else File.Move(temp, file);
        }
        public static List<Settings> LoadAll()
        {
            string dir = Path.Combine(Root, "profiles");
            var result = new List<Settings>();
            if (Directory.Exists(dir)) foreach (string file in Directory.GetFiles(dir, "*.json"))
            {
                var settings = Json().Deserialize<Settings>(ReadSmall(file)).Validate();
                if (settings.Id != Path.GetFileNameWithoutExtension(file)) throw new InvalidDataException("客户端配置与文件名不一致。");
                result.Add(settings);
            }
            // An existing, empty directory means the user removed every client.
            if (!Directory.Exists(dir)) { result.Add(new Settings()); result.Add(new Settings { Id = "intl", Name = "外服客户端", Server = "INTL" }); }
            if (result.Count > 20) throw new InvalidDataException("最多管理 20 个客户端。");
            return result.OrderBy(x => x.Server == "CN" ? 0 : 1).ThenBy(x => x.Name).ToList();
        }
        public static void Save(Settings settings) { settings.Validate(); AtomicWrite(Path.Combine(Root, "profiles", settings.Id + ".json"), Json().Serialize(settings)); }
        public static void DeleteClient(string id)
        {
            if (id == null || !Regex.IsMatch(id, "^[a-z0-9-]{1,40}$")) throw new InvalidDataException("客户端配置编号无效。");
            string directory = Path.Combine(Root,"profiles");
            Directory.CreateDirectory(directory);
            // Only the assistant's profile is removed. Never traverse the game path.
            File.Delete(Path.Combine(directory,id + ".json"));
        }
        public static string Export(Settings settings)
        {
            return Json().Serialize(new Backup { Format = "wows-aslain-assistant", Schema = 1, ExportedAt = DateTimeOffset.Now.ToString("o"), Settings = settings.Validate() });
        }
        public static Settings Import(string file, Settings current)
        {
            var result = Json().Deserialize<Settings>(Json().Serialize(current));
            string text = ReadSmall(file), ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext == ".inf") result.Preset = Preset.FromInf(text);
            else if (ext == ".reg") result.Preset = Preset.FromReg(text);
            else if (ext == ".json")
            {
                var backup = Json().Deserialize<Backup>(text);
                if (backup == null || backup.Format != "wows-aslain-assistant" || backup.Schema != 1 || backup.Settings == null) throw new InvalidDataException("不是本助手的有效配置备份。");
                backup.Settings.Validate();
                result.Preset = backup.Settings.Preset;
                result.CheckHours = backup.Settings.CheckHours;
                // Keep the current machine's target. Adopt a valid backup path only when no target is set.
                if (string.IsNullOrEmpty(result.GamePath) && GameDetector.IsGameFolder(backup.Settings.GamePath)) result.GamePath = backup.Settings.GamePath;
            }
            else throw new InvalidDataException("仅支持 .json、.inf 或阿斯兰导出的 .reg 配置。");
            result.AutoUpdate = false; // Import never starts a background install or registers a login item.
            result.PendingPreset = result.Preset != null;
            result.FailedRelease = "";
            result.PendingRun = "";
            if (result.Preset != null) result.Preset.Source = "导入：" + Path.GetFileName(file);
            return result.Validate();
        }
    }
}
