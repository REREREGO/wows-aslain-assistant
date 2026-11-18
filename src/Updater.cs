using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace AslainAssistant
{
    public sealed class Transfer
    {
        public long Received, Total;
    }
    public sealed class OfficialClient : IDisposable
    {
        readonly HttpClient client;
        public OfficialClient() : this(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate }) { }
        public OfficialClient(HttpMessageHandler handler)
        {
            client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("WoWS-Aslain-Assistant/1.4 (+https://aslain.com)");
        }
        public static bool Allowed(Uri url)
        {
            return url != null && url.IsAbsoluteUri && url.Scheme == "https" && url.IsDefaultPort && string.IsNullOrEmpty(url.UserInfo) && (url.Host == "aslain.com" || url.Host == "www.aslain.com" || url.Host == "filez.aslain.com");
        }
        async Task<HttpResponseMessage> Get(Uri url, CancellationToken token)
        {
            for (int i = 0; i < 6; i++)
            {
                if (!Allowed(url)) throw new InvalidDataException("下载跳转到了未授权的站点，已停止。");
                var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
                if ((int)response.StatusCode >= 300 && (int)response.StatusCode <= 399)
                {
                    var location = response.Headers.Location;
                    response.Dispose();
                    if (location == null) throw new InvalidDataException("服务器跳转缺少目标地址。");
                    url = location.IsAbsoluteUri ? location : new Uri(url, location); continue;
                }
                if (!response.IsSuccessStatusCode) { int code = (int)response.StatusCode; response.Dispose(); throw new HttpRequestException("官网请求失败（HTTP " + code + "）。"); }
                return response;
            }
            throw new InvalidDataException("官网跳转次数过多。");
        }
        public async Task<Release> Latest(CancellationToken token)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(45));
                using (var response = await Get(new Uri(Release.Page), timeout.Token))
                using (var stream = await response.Content.ReadAsStreamAsync())
                using (var buffer = new MemoryStream())
                {
                    await CopyBounded(stream, buffer, 8 * 1024 * 1024, 0, null, timeout.Token);
                    return Release.Parse(System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
                }
            }
        }
        static async Task CopyBounded(Stream source, Stream destination, long maximum, long expected, IProgress<Transfer> progress, CancellationToken token)
        {
            byte[] buffer = new byte[81920]; long total = 0; int read;
            var watch = Stopwatch.StartNew();
            while ((read = await source.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
            {
                total += read;
                if (total > maximum) throw new InvalidDataException("服务器返回文件超过大小限制。");
                await destination.WriteAsync(buffer, 0, read, token);
                if (progress != null && watch.ElapsedMilliseconds > 200) { progress.Report(new Transfer { Received = total, Total = expected }); watch.Restart(); }
            }
            if (expected > 0 && total != expected) throw new InvalidDataException("下载不完整，请重试。");
            if (progress != null) progress.Report(new Transfer { Received = total, Total = expected });
        }
        public static void Verify(string file, string expectedHash)
        {
            if (string.IsNullOrEmpty(expectedHash) || !System.Text.RegularExpressions.Regex.IsMatch(expectedHash, "^[a-fA-F0-9]{64}$")) throw new InvalidDataException("缺少官网校验值。");
            using (var stream = File.OpenRead(file))
            {
                if (stream.Length < 1024 || stream.ReadByte() != 'M' || stream.ReadByte() != 'Z') throw new InvalidDataException("下载结果不是 Windows 安装程序。");
                stream.Position = 0x3c;
                var reader = new BinaryReader(stream);
                int pe = reader.ReadInt32();
                if (pe < 64 || pe > stream.Length - 4) throw new InvalidDataException("安装包 PE 格式无效。");
                stream.Position = pe;
                if (reader.ReadUInt32() != 0x4550) throw new InvalidDataException("安装包 PE 标识无效。");
                stream.Position = 0;
                using (var sha = SHA256.Create())
                {
                    string actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                    if (!actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SHA-256 与官网不一致，已阻止安装。请稍后重试。");
                }
            }
        }
        public async Task<string> Download(Release release, string directory, IProgress<Transfer> progress, CancellationToken token)
        {
            Directory.CreateDirectory(directory);
            string target = Path.Combine(directory, "Aslains_WoWs_Modpack_Installer_v." + release.Version.FileTag + ".exe");
            if (File.Exists(target))
            {
                try { await Task.Run(() => Verify(target, release.Sha256), token); return target; }
                catch (InvalidDataException) { File.Delete(target); }
            }
            string part = target + ".part";
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(TimeSpan.FromMinutes(30));
                    using (var response = await Get(release.Download, timeout.Token))
                    using (var source = await response.Content.ReadAsStreamAsync())
                    using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        long size = response.Content.Headers.ContentLength ?? 0;
                        if (size > 1024L * 1024 * 1024) throw new InvalidDataException("安装包超过 1 GB 限制。");
                        await CopyBounded(source, file, 1024L * 1024 * 1024, size, progress, timeout.Token);
                    }
                    await Task.Run(() => Verify(part, release.Sha256), timeout.Token);
                    File.Move(part, target);
                }
                return target;
            }
            finally { if (File.Exists(part)) File.Delete(part); }
        }
        public void Dispose() { client.Dispose(); }
    }
    public enum UpdateDecision { Current, Install, Incompatible, UnknownGame, NewerInstalled }
    public static class UpdatePolicy
    {
        public static UpdateDecision Decide(GameState game, Release release)
        {
            if (string.IsNullOrEmpty(game.GameVersion)) return UpdateDecision.UnknownGame;
            if (game.GameVersion != release.Version.Game) return UpdateDecision.Incompatible;
            if (game.Installed != null && game.Installed.CompareTo(release.Version) > 0) return UpdateDecision.NewerInstalled;
            if (!game.HasInstallation || game.Installed == null || game.Installed.CompareTo(release.Version) < 0 || !game.HasActiveMods) return UpdateDecision.Install;
            return UpdateDecision.Current;
        }
    }
    public class Installer
    {
        public static string Arguments(string gamePath, string inputInf, string outputInf, string log, bool interactive)
        {
            Func<string, string> quote = value =>
            {
                if (value.IndexOfAny(new[] { '"', '\r', '\n', '\0' }) >= 0 || value.EndsWith("\\")) throw new InvalidDataException("安装参数路径无效。");
                return "\"" + value + "\"";
            };
            string args = "/SP- /NORESTART /RESTARTEXITCODE=3010 /NOCLOSEAPPLICATIONS /NORESTARTAPPLICATIONS /DIR=" + quote(gamePath) + " /SAVEINF=" + quote(outputInf) + " /LOG=" + quote(log);
            if (!string.IsNullOrEmpty(inputInf)) args += " /LOADINF=" + quote(inputInf);
            if (!interactive) args += " /VERYSILENT /SUPPRESSMSGBOXES";
            return args;
        }
        public virtual async Task<int> Run(string executable, string args)
        {
            try
            {
                // ShellExecute invokes the normal Windows UAC consent path; it does not bypass it.
                using (var process = Process.Start(new ProcessStartInfo { FileName = executable, Arguments = args, WorkingDirectory = Path.GetDirectoryName(executable), UseShellExecute = true, Verb = "runas" }))
                {
                    if (process == null) throw new IOException("安装程序未能启动。");
                    await Task.Run(() => process.WaitForExit());
                    return process.ExitCode;
                }
            }
            catch (Win32Exception ex) { if (ex.NativeErrorCode == 1223) throw new OperationCanceledException("已取消 Windows 管理员授权。"); throw; }
        }
    }
    public sealed class ClientSnapshot
    {
        public Settings Settings;
        public GameState Game;
        public InstallReport Report;
        public string Status, Detail;
        internal string ReconciledLog;
    }
    public sealed class UpdateEngine : IDisposable
    {
        public Settings Settings;
        public GameState Game;
        public Release Release;
        public InstallReport LastReport { get; private set; }
        public bool Busy { get; private set; }
        public bool Installing { get; private set; }
        public bool WaitingForNetwork { get; private set; }
        public string Status = "准备就绪", Detail = "选择游戏目录后即可检查阿斯兰。";
        public DateTimeOffset NextCheck = DateTimeOffset.Now.AddSeconds(45);
        public event Action Changed;
        public event Action<string> Log;
        public event Action<Transfer> Progress;
        public event Action<string> Notify;
        public readonly OfficialClient Client;
        public Func<string, GameState> InspectGame = GameDetector.Inspect;
        public Func<bool> IsGameRunning = GameDetector.GameRunning;
        public Func<bool> NetworkAvailable = NetworkInterface.GetIsNetworkAvailable;
        readonly Installer installer;
        string reconciledLog = "";
        CancellationTokenSource cancellation;
        public UpdateEngine(Settings settings, OfficialClient client, Installer setup) { Settings = settings; Client = client; installer = setup; }
        public static ClientSnapshot ReadSnapshot(Settings settings,CancellationToken token)
        {
            using(var reader = new UpdateEngine(settings,new OfficialClient(),new Installer()))
            {
                token.ThrowIfCancellationRequested();
                if(GameDetector.IsGameFolder(settings.GamePath)) reader.RefreshLocal();
                token.ThrowIfCancellationRequested(); Storage.Save(settings);
                return new ClientSnapshot {Settings=settings,Game=reader.Game,Report=reader.LastReport,Status=reader.Status,Detail=reader.Detail,ReconciledLog=reader.reconciledLog};
            }
        }
        public void ApplySnapshot(ClientSnapshot snapshot)
        {
            Settings=snapshot.Settings; Game=snapshot.Game; LastReport=snapshot.Report;
            Status=snapshot.Status; Detail=snapshot.Detail; reconciledLog=snapshot.ReconciledLog;
        }
        void State(string status, string detail)
        {
            Status = status; Detail = detail;
            if (Changed != null) Changed();
            if (Log != null) Log(status + " · " + detail);
        }
        public void RefreshLocal() { Game = InspectGame(Settings.GamePath); if (!Installing) RecoverCompletedRun(); if (Changed != null) Changed(); }
        public void RecoverCompletedRun()
        {
            if (Game == null || Game.Installed == null) return;
            string runs = Path.Combine(Storage.Root, "runs");
            if (!Directory.Exists(runs)) { RecoverExternalRun(DateTime.MinValue); return; }
            // Also reconcile a run made by the first preview build, which did not save PendingRun yet.
            var candidates = Directory.GetDirectories(runs).OrderByDescending(x => File.Exists(Path.Combine(x, "installer.log")) ? File.GetLastWriteTimeUtc(Path.Combine(x, "installer.log")) : DateTime.MinValue).Take(50);
            foreach (string dir in candidates)
            {
                string inf = Path.Combine(dir, "selection.inf"), log = Path.Combine(dir, "installer.log");
                if (!File.Exists(inf) || !File.Exists(log)) continue;
                string text = Storage.ReadSmall(inf);
                var destination = System.Text.RegularExpressions.Regex.Match(text, @"(?im)^Dir=(.+)$");
                if (!destination.Success || !string.Equals(destination.Groups[1].Value.Trim().Trim('"').TrimEnd('\\'), Game.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                if (RecoverExternalRun(File.GetLastWriteTimeUtc(log))) return;
                string stamp = log + File.GetLastWriteTimeUtc(log).Ticks;
                if (stamp == reconciledLog) return;
                var report = InstallReport.Read(log);
                if (report.ReleaseTag != Game.Installed.FileTag || !report.Completed || !report.Closed) continue;
                reconciledLog = stamp;
                bool recover = Settings.PendingRun == Path.GetFileName(dir) || (string.IsNullOrEmpty(Settings.PendingRun) && Settings.FailedRelease == Game.Installed.FileTag);
                if (recover)
                {
                    var preset = Preset.FromInf(text); preset.Source = "从已完成的安装恢复";
                    Settings.Preset = preset; Settings.FailedRelease = ""; Settings.PendingRun = "";
                }
                ApplyReport(report, Game.Installed.FileTag); Storage.Save(Settings);
                Status = report.Incomplete ? "部分模组未安装完整" : report.FailedScripts.Count > 0 ? "插件脚本需要检查" : "已核对安装结果";
                Detail = string.IsNullOrEmpty(report.Summary) ? Game.Installed + " 已安装。" : report.Summary;
                if (Log != null) Log(Detail); return;
            }
            RecoverExternalRun(DateTime.MinValue);
        }
        bool RecoverExternalRun(DateTime after)
        {
            if (GameDetector.InstallerRunning()) return false;
            string log = Path.Combine(Game.Path, "Aslain_Modpack", "Logs", "Aslains_Installer.log");
            if (!File.Exists(log) || File.GetLastWriteTimeUtc(log) <= after) return false;
            string stamp = log + File.GetLastWriteTimeUtc(log).Ticks;
            if (stamp == reconciledLog) return true;
            var report = InstallReport.Read(log);
            if (!report.Completed || !string.Equals(report.Destination, Game.Path.TrimEnd('\\','/'), StringComparison.OrdinalIgnoreCase)) return false;
            string release = report.ReleaseTag;
            if (release == "")
            {
                string installedExe = Path.Combine(Game.Path, "Aslain_Modpack", "Aslains_WoWs_Modpack_Installer.exe");
                if (!string.Equals(report.OriginalExecutable, installedExe, StringComparison.OrdinalIgnoreCase) || !File.Exists(installedExe)) return false;
                try { release = PackVersion.Parse(FileVersionInfo.GetVersionInfo(installedExe).ProductVersion).FileTag; }
                catch (InvalidDataException) { return false; }
            }
            if (release != Game.Installed.FileTag) return false;
            // Aslain copies its log into the game folder at ssDone, before writing "Log closed".
            // Require the exact target, matching version, completion, newer log, and no live installer.
            reconciledLog = stamp;
            Settings.FailedRelease = ""; Settings.PendingRun = "";
            ApplyReport(report, release);
            if (GameDetector.IsGameFolder(Game.Path))
            {
                var preset = GameDetector.ReadPreset(Game.Path);
                if (preset != null) { preset.Source = "从手动完成的安装读取"; Settings.Preset = preset; }
            }
            Storage.Save(Settings);
            Status = report.Incomplete ? "部分模组未安装完整" : report.FailedScripts.Count > 0 ? "插件脚本需要检查" : "手动安装已核对";
            Detail = report.Summary.Length > 0 ? report.Summary : "已核对游戏目录中的完整安装记录，旧的缺失模组提醒已清除。";
            if (Log != null) Log(Detail);
            return true;
        }
        void ApplyReport(InstallReport report, string release)
        {
            if (Game != null) report.CheckReplaySettings(Game.ActiveBuildPath);
            LastReport = report;
            Settings.IncompleteRelease = report.Incomplete ? release : "";
            Settings.InstallIssue = report.Issues;
            if (report.Incomplete) Settings.FailedRelease = release;
        }
        public void Cancel() { if (!Installing && cancellation != null) cancellation.Cancel(); }
        public async Task Run(bool install, bool interactive, bool manual)
        {
            if (Busy) return;
            Busy = true; WaitingForNetwork = false; cancellation = new CancellationTokenSource();
            NextCheck = DateTimeOffset.Now.AddHours(Settings.CheckHours);
            try
            {
                State("检查本地安装", "读取游戏版本与阿斯兰安装记录…");
                RefreshLocal();
                if (!NetworkAvailable()) { NetworkFailure("系统未检测到可用网络连接。",manual); return; }
                State("查询官网版本", "正在连接 aslain.com…");
                Release = await Client.Latest(cancellation.Token);
                Settings.LastCheck = DateTimeOffset.Now.ToString("o"); Storage.Save(Settings);
                UpdateDecision decision = UpdatePolicy.Decide(Game, Release);
                if (decision == UpdateDecision.UnknownGame) { State("需要确认游戏版本", "无法确定当前游戏版本。请先用游戏启动器完成更新，再重试。"); return; }
                if (decision == UpdateDecision.Incompatible)
                {
                    if (install && new Version(Release.Version.Game).CompareTo(new Version(Game.GameVersion)) > 0)
                    {
                        State("提前缓存新版", "官网插件包适配 " + Release.Version.Game + "，当前游戏为 " + Game.GameVersion + "。仅下载，暂不安装。");
                        await Client.Download(Release, Path.Combine(Storage.Root, "downloads"), new Progress<Transfer>(p => { if (Progress != null) Progress(p); }), cancellation.Token);
                    }
                    State("等待版本匹配", "游戏 " + Game.GameVersion + " 与官网插件包 " + Release.Version.Game + " 不匹配。客户端升级后会自动复查。"); return;
                }
                if (decision == UpdateDecision.NewerInstalled) { State("本地版本更高", "官网当前发布的版本更旧，已保留本地版本。"); return; }
                string releaseKey = Release.Version.FileTag;
                bool retry = Settings.FailedRelease == releaseKey || Settings.IncompleteRelease == releaseKey;
                if (retry && (!manual || !install)) { State(Settings.IncompleteRelease == releaseKey ? "部分模组未安装完整" : "需要处理上次安装", string.IsNullOrEmpty(Settings.InstallIssue) ? "此版本上次安装失败或被取消。点击「立即更新」重试；不会自动反复安装。" : Settings.InstallIssue); return; }
                if (decision == UpdateDecision.Current && !interactive && !retry) { State(string.IsNullOrEmpty(Settings.InstallIssue) ? "已是最新版本" : "版本最新，插件需要检查", string.IsNullOrEmpty(Settings.InstallIssue) ? Release.Version + " · 下次按设定时间自动检查。" : Settings.InstallIssue); return; }
                if (!install) { State("可以更新", "官网 " + Release.Version + "；点击「立即更新」，或开启自动更新。"); return; }
                if (!manual && Settings.FailedRelease == releaseKey) { State("需要处理上次安装", "此版本上次安装失败或被取消。点击「立即更新」重试；后续新版本仍会检查。"); return; }
                if (IsGameRunning()) { NextCheck = DateTimeOffset.Now.AddMinutes(1); State("等待游戏退出", "检测到战舰世界正在运行，1 分钟后重试。"); return; }
                if (manual) { Settings.FailedRelease = ""; Storage.Save(Settings); }
                State("下载安装包", "仅使用官网直链，下载后自动校验 SHA-256。");
                var progress = new Progress<Transfer>(p => { if (Progress != null) Progress(p); });
                string file = await Client.Download(Release, Path.Combine(Storage.Root, "downloads"), progress, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                // Download may take minutes; re-check the target and running game immediately before execution.
                RefreshLocal();
                decision = UpdatePolicy.Decide(Game, Release);
                if (decision != UpdateDecision.Install && !((interactive || retry) && decision == UpdateDecision.Current)) { State("安装条件已变化", "游戏或阿斯兰版本在下载期间发生变化，请重新检查。"); return; }
                if (IsGameRunning()) { NextCheck = DateTimeOffset.Now.AddMinutes(1); State("等待游戏退出", "安装包已校验并缓存；游戏退出后会再次检查并安装。"); return; }
                await Task.Run(() => OfficialClient.Verify(file, Release.Sha256), cancellation.Token);
                string runDir = Path.Combine(Storage.Root, "runs", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                Directory.CreateDirectory(runDir);
                string input = "", output = Path.Combine(runDir, "selection.inf"), log = Path.Combine(runDir, "installer.log");
                if (Settings.Preset != null) { input = Path.Combine(runDir, "input.inf"); File.WriteAllText(input, Settings.Preset.ToInf(Game.Path, true), System.Text.Encoding.Unicode); }
                bool wizard = interactive || Settings.Preset == null;
                // Persist before starting: a crash or restart cannot accidentally repeat the same installer forever.
                Settings.FailedRelease = releaseKey; Settings.PendingRun = Path.GetFileName(runDir); Storage.Save(Settings);
                Installing = true;
                State(wizard ? "请在安装器中选择插件" : "正在自动安装", wizard ? "在阿斯兰窗口中选好插件并完成安装，助手会保存选择。" : "沿用保存的插件配置；Windows 可能要求管理员授权。");
                if (Notify != null) Notify(Status + "：" + Detail);
                DateTime started = DateTime.UtcNow;
                int exit = await installer.Run(file, Installer.Arguments(Game.Path, input, output, log, wizard));
                if (exit != 0 && exit != 3010) throw new IOException("阿斯兰安装未完成（退出码 " + exit + "），保留原配置。可手动重试；详情见安装日志。");
                RefreshLocal();
                if (Game.Installed == null || Game.Installed.CompareTo(Release.Version) != 0 || !File.Exists(log) || File.GetLastWriteTimeUtc(log) < started.AddSeconds(-3)) throw new IOException("安装程序已退出，但未验证到目标目录的新版本，暂不标记为成功。请检查安装目标和日志。");
                var report = InstallReport.Read(log);
                if (!report.Completed || !report.Closed) throw new IOException("安装日志没有完整结束记录，暂不标记为成功。请查看安装日志。");
                Preset saved = File.Exists(output) ? Preset.FromInf(Storage.ReadSmall(output)) : GameDetector.ReadPreset(Game.Path);
                if (saved != null) { saved.Source = "安装完成后保存 · " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"); Settings.Preset = saved; }
                Settings.FailedRelease = ""; Settings.PendingRun = "";
                ApplyReport(report, releaseKey); Storage.Save(Settings);
                State(report.Incomplete ? "部分模组未安装完整" : report.FailedScripts.Count > 0 ? "已更新，请留意插件日志" : "更新完成", !string.IsNullOrEmpty(report.Summary) ? report.Summary : Release.Version + (exit == 3010 ? " · 安装程序要求重启，请在方便时手动重启。" : " · 插件选择已保存。"));
                if (Notify != null) Notify(Status + "：" + Detail);
            }
            catch (OperationCanceledException)
            {
                NextCheck = DateTimeOffset.Now.AddMinutes(15);
                if (cancellation.IsCancellationRequested) State("已取消", "本次检查或下载已取消；已安装模组和插件配置保持不变。");
                else if (Installing) State("安装授权已取消", "Windows 安装授权被取消，点击「立即更新」可重试。");
                else NetworkFailure("连接超时，未能完成版本查询或下载。",manual);
            }
            catch (HttpRequestException ex)
            {
                NetworkFailure("无法访问更新服务：" + ex.Message,manual);
            }
            catch (WebException ex)
            {
                NetworkFailure("网络请求失败：" + ex.Message,manual);
            }
            catch (Exception ex)
            {
                NextCheck = DateTimeOffset.Now.AddMinutes(15);
                State("检查或更新未完成", ex.Message);
                if (Notify != null) Notify(Detail);
            }
            finally { Installing = false; Busy = false; cancellation.Dispose(); cancellation = null; if (Changed != null) Changed(); }
        }
        void NetworkFailure(string reason,bool manual)
        {
            WaitingForNetwork = true; NextCheck = DateTimeOffset.Now.AddMinutes(15);
            State("等待网络恢复",reason + " 已保留当前模组和配置；" + (Settings.AutoUpdate ? "15 分钟后重试，检测到网络重新连接时会提前检查。" : "连接恢复后可手动重试。"));
            if (manual && Notify != null) Notify(Detail);
        }
        public void Dispose() { Cancel(); Client.Dispose(); }
    }
}
