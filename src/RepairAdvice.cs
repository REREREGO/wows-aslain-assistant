using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AslainAssistant
{
    public static class RepairAdvice
    {
        public static bool IsOfficialArchive(string name, Uri url)
        {
            return Regex.IsMatch(name ?? "", @"^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,180}\.7z$") && url != null && url.IsAbsoluteUri && url.Scheme == "https" && url.Host == "repo.aslain.com" && url.IsDefaultPort && url.UserInfo == "" && url.Query == "" && url.Fragment == "" && url.AbsolutePath == "/Warships/" + name;
        }
        public static string CacheDirectory(string gamePath) { return Path.Combine(Path.GetFullPath(gamePath), "Aslain_Modpack", "DL_cache"); }
        public static string DownloadFor(InstallReport report, string name)
        {
            string value; Uri uri;
            return report.ArchiveSources.TryGetValue(name, out value) && Uri.TryCreate(value, UriKind.Absolute, out uri) && IsOfficialArchive(name, uri) ? value : "";
        }
        public static string Build(string gamePath, InstallReport report)
        {
            var text = new StringBuilder();
            if (report.Incomplete)
            {
                text.AppendLine("部分模组解压失败，版本号更新不代表所有模组已装好。");
                text.AppendLine("1. 先退出战舰世界和阿斯兰安装器。");
                text.AppendLine("2. 从下面的官方链接下载列出的 .7z 文件；保留完整文件名，不要自行解压。");
                text.AppendLine("3. 把文件放到这个客户端的缓存目录：");
                text.AppendLine(CacheDirectory(gamePath));
                text.AppendLine("   如果已有同名文件，可先把旧文件移到其他目录备份，再放入重新下载的文件。");
                text.AppendLine("4. 重新运行阿斯兰安装器，确认游戏目录和插件选择；不要勾选“删除 / 清空下载缓存（DL_cache）”。");
                text.AppendLine("5. 若通过助手重试仍失败，可直接双击游戏目录内 Aslain_Modpack\\Aslains_WoWs_Modpack_Installer.exe，手动完成安装，再回助手检查。");
                text.AppendLine();
                foreach (string name in report.FailedArchives)
                {
                    text.AppendLine(name);
                    string link = DownloadFor(report, name);
                    text.AppendLine(link.Length == 0 ? "日志没有记录可验证的官方直链。请从阿斯兰官网获取对应安装器重新下载，不要猜测文件链接。" : link);
                    text.AppendLine();
                }
            }
            else text.AppendLine("当前安装日志没有报告下载包解压失败，无需手动补齐缓存。");
            if (report.FailedScripts.Count > 0)
            {
                text.AppendLine("以下插件脚本另有非零退出码：");
                foreach (string script in report.FailedScripts) text.AppendLine("• " + script);
                text.AppendLine("补齐下载缓存不能解决脚本错误。请在游戏中确认对应功能，保留安装日志；若仍异常，可暂时取消对应选项，或向 Aslain 官方反馈日志。");
            }
            if (report.VerifiedScripts.Count > 0)
            {
                text.AppendLine("以下脚本虽然返回非零退出码，但已核对当前游戏版本的配置，目标设置已经生效，无需重装：");
                foreach (string script in report.VerifiedScripts) text.AppendLine("• " + script);
                text.AppendLine("原始返回码仍保留在安装日志中。");
            }
            return text.ToString();
        }
    }
}
