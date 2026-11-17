using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace AslainAssistant
{
    // Stream the entire log: extraction failures may be far outside its final 128 KB.
    public sealed class InstallReport
    {
        public readonly List<string> FailedArchives = new List<string>();
        public readonly List<string> FailedScripts = new List<string>();
        public readonly List<string> VerifiedScripts = new List<string>();
        public readonly Dictionary<string, string> ArchiveSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public bool Completed, Closed;
        public string ReleaseTag = "";
        public string OriginalExecutable = "", Destination = "";
        public bool Incomplete { get { return FailedArchives.Count != 0; } }
        public string Issues
        {
            get
            {
                string result = Incomplete ? "以下模组解压失败：" + string.Join("、", FailedArchives.Take(8)) + "。请点「查看处理方案」获取官方下载链接和放置目录，补齐后重试。" : "";
                if (FailedScripts.Count > 0) result += (result.Length == 0 ? "" : " ") + "插件脚本报告异常：" + string.Join("、", FailedScripts.Take(4)) + "。";
                return result;
            }
        }
        public string Summary { get { return Issues + (VerifiedScripts.Count > 0 ? (Issues.Length > 0 ? " " : "") + "录像设置已核对生效；脚本原始退出码保留在安装日志中。" : ""); } }
        public void CheckReplaySettings(string activeBuild)
        {
            if (string.IsNullOrEmpty(activeBuild) || FailedScripts.Count == 0) return;
            string file = Path.Combine(activeBuild, "res_mods", "engine_config.xml");
            if (!File.Exists(file)) file = Path.Combine(activeBuild, "res", "engine_config.xml");
            if (!File.Exists(file)) return;
            try
            {
                XDocument document;
                using (var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024 })) document = XDocument.Load(reader);
                var replays = document.Descendants("replays").ToArray();
                if (replays.Length != 1) return;
                foreach (string script in FailedScripts.ToArray())
                {
                    string setting, expected;
                    switch (script.ToLowerInvariant())
                    {
                        case "engine_config_enablereplays.bat": setting = "isEnabled"; expected = "true"; break;
                        case "engine_config_replayslimitoff.bat": setting = "maxReplaysToSave"; expected = "0"; break;
                        case "engine_config_versionfolderon.bat": setting = "versioned"; expected = "true"; break;
                        default: continue;
                    }
                    var values = replays[0].Elements(setting).ToArray();
                    if (values.Length == 1 && values[0].Value.Trim().Equals(expected, StringComparison.OrdinalIgnoreCase))
                    {
                        FailedScripts.Remove(script); if (!VerifiedScripts.Contains(script)) VerifiedScripts.Add(script);
                    }
                }
            }
            catch (XmlException) { /* Keep warnings when the actual result cannot be verified. */ }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        public static InstallReport Read(string file)
        {
            var report = new InstallReport();
            string script = "插件脚本";
            using (var reader = new StreamReader(file))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Contains("Original Setup EXE:"))
                    {
                        report.OriginalExecutable = line.Substring(line.IndexOf("Original Setup EXE:", StringComparison.Ordinal) + 19).Trim();
                        var tag = Regex.Match(line, @"Installer_v\.(\d+\.\d+\.\d+_\d+)\.exe", RegexOptions.IgnoreCase);
                        if (tag.Success) report.ReleaseTag = tag.Groups[1].Value;
                    }
                    var destination = Regex.Match(line, @"DefaultDirName\s*=\s*(.+)$");
                    if (destination.Success) report.Destination = destination.Groups[1].Value.Trim().TrimEnd('\\', '/');
                    var download = Regex.Match(line, @"Downloading temporary file from (https://\S+?\.7z)(?=[:\s])", RegexOptions.IgnoreCase);
                    if (download.Success)
                    {
                        Uri url;
                        if (Uri.TryCreate(download.Groups[1].Value, UriKind.Absolute, out url))
                        {
                            string name = Uri.UnescapeDataString(url.Segments.Last());
                            if (RepairAdvice.IsOfficialArchive(name, url) && report.ArchiveSources.Count < 100) report.ArchiveSources[name] = url.AbsoluteUri;
                        }
                    }
                    var archive = Regex.Match(line, @"\[ExtractDL\]\s+Extracting\s+(.+?)\s+--\s+FAILED", RegexOptions.IgnoreCase);
                    if (!archive.Success) archive = Regex.Match(line, @"\[ExtractDL\] Archive is not in DL_cache,.*?:\s*(.+)$", RegexOptions.IgnoreCase);
                    if (archive.Success)
                    {
                        string name = archive.Groups[1].Value.Trim();
                        if (name.Length > 200) name = name.Substring(0, 200);
                        if (report.FailedArchives.Count < 50 && !report.FailedArchives.Contains(name)) report.FailedArchives.Add(name);
                    }
                    var filename = Regex.Match(line, @"(?:^|\s)Filename:\s+(.+)$");
                    if (filename.Success) script = Path.GetFileName(filename.Groups[1].Value.Trim());
                    if (Regex.IsMatch(line, @"Process exit code:\s*[1-9]\d*\s*$") && report.FailedScripts.Count < 20 && !report.FailedScripts.Contains(script)) report.FailedScripts.Add(script);
                    if (line.Contains("Current step: ssDone")) report.Completed = true;
                    if (line.Contains("Log closed.")) report.Closed = true;
                }
            }
            return report;
        }
    }
}
