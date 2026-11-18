using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AslainAssistant
{
    public sealed class SignedUpdate
    {
        public string Payload { get; set; }
        public string Signature { get; set; }
    }
    public sealed class AssistantRelease
    {
        public int Schema { get; set; }
        public string Application { get; set; }
        public string Version { get; set; }
        public string[] Urls { get; set; }
        public string Sha256 { get; set; }
        public long Size { get; set; }
        public string PublishedUtc { get; set; }
        // Releases through v1.4.4 predate the user's reset to the 1.0.x series.
        // A larger number from that history must never replace a current build.
        [System.Web.Script.Serialization.ScriptIgnore]
        public bool IsHistorical
        {
            get { return PublicationTime <= new DateTimeOffset(2026,11,18,8,16,9,TimeSpan.Zero); }
        }
        [System.Web.Script.Serialization.ScriptIgnore]
        public DateTimeOffset PublicationTime
        {
            get
            {
                DateTimeOffset value;
                if(!DateTimeOffset.TryParse(PublishedUtc,CultureInfo.InvariantCulture,DateTimeStyles.None,out value))
                    throw new InvalidDataException("助手发布信息缺少有效的发布时间，请稍后重试。");
                return value;
            }
        }
        public static Version NormalizeVersion(string text)
        {
            Version version;
            if (!System.Version.TryParse(text,out version) || version.Major < 0 || version.Build < 0) throw new InvalidDataException("助手更新版本号无效。");
            return new Version(version.Major,version.Minor,version.Build,Math.Max(0,version.Revision));
        }
        public static AssistantRelease Parse(string text,string publicKey)
        {
            if (text.Length > 131072) throw new InvalidDataException("助手更新信息超过大小限制。");
            var signed = Storage.Json().Deserialize<SignedUpdate>(text);
            if (signed == null || signed.Payload == null || signed.Signature == null) throw new InvalidDataException("助手更新信息缺少签名。");
            byte[] payload = Convert.FromBase64String(signed.Payload), signature = Convert.FromBase64String(signed.Signature);
            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.PersistKeyInCsp = false; rsa.FromXmlString(publicKey);
                if (!rsa.VerifyData(payload,CryptoConfig.MapNameToOID("SHA256"),signature)) throw new InvalidDataException("助手更新签名不匹配，已阻止安装。");
            }
            var release = Storage.Json().Deserialize<AssistantRelease>(Encoding.UTF8.GetString(payload));
            if (release == null || release.Schema != 1 || release.Application != "WoWS-Aslain-Assistant") throw new InvalidDataException("更新信息不属于本助手。");
            NormalizeVersion(release.Version);
            var published = release.PublicationTime;
            if (release.Size < 1024 || release.Size > 64 * 1024 * 1024 || !Regex.IsMatch(release.Sha256 ?? "","^[a-fA-F0-9]{64}$")) throw new InvalidDataException("助手更新文件信息无效。");
            if (release.Urls == null || release.Urls.Length == 0 || release.Urls.Length > 4 || release.Urls.Any(x=>!AssistantUpdateClient.AllowedUrl(x))) throw new InvalidDataException("助手安装包下载地址不受支持。");
            return release;
        }
    }
    public sealed class AssistantUpdateClient : IDisposable
    {
        public const string Repository = "https://github.com/REREREGO/wows-aslain-assistant";
        public static readonly string[] Feeds = {
            Repository + "/releases/latest/download/latest.json",
            "https://raw.githubusercontent.com/REREREGO/wows-aslain-assistant/main/updates/latest.json"
        };
        readonly HttpClient client;
        readonly string publicKey;
        public AssistantUpdateClient() : this(new HttpClientHandler { AllowAutoRedirect=false, AutomaticDecompression=DecompressionMethods.GZip | DecompressionMethods.Deflate },UpdateTrust.PublicKey) { }
        public AssistantUpdateClient(HttpMessageHandler handler,string verificationKey)
        {
            publicKey = verificationKey;
            client = new HttpClient(handler) { Timeout=Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("WoWS-Aslain-Assistant/"+Assembly.GetExecutingAssembly().GetName().Version.ToString(3));
        }
        public static bool AllowedUrl(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value,UriKind.Absolute,out uri) || uri.Scheme != "https" || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)) return false;
            if (uri.Host == "github.com") return uri.AbsolutePath.StartsWith("/REREREGO/wows-aslain-assistant/",StringComparison.OrdinalIgnoreCase);
            if (uri.Host == "raw.githubusercontent.com") return uri.AbsolutePath.StartsWith("/REREREGO/wows-aslain-assistant/",StringComparison.OrdinalIgnoreCase);
            if (uri.Host == "api.github.com") return uri.AbsolutePath.StartsWith("/repos/REREREGO/wows-aslain-assistant/releases/assets/",StringComparison.OrdinalIgnoreCase);
            return uri.Host == "release-assets.githubusercontent.com" || uri.Host == "objects.githubusercontent.com";
        }
        async Task<HttpResponseMessage> Get(string address,bool binary,CancellationToken token)
        {
            var uri = new Uri(address);
            for (int i = 0; i < 6; i++)
            {
                if (!AllowedUrl(uri.AbsoluteUri)) throw new InvalidDataException("助手更新跳转到了未授权站点。");
                using (var request = new HttpRequestMessage(HttpMethod.Get,uri))
                {
                    if (binary) request.Headers.TryAddWithoutValidation("Accept","application/octet-stream");
                    var response = await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);
                    if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
                    {
                        var location = response.Headers.Location; response.Dispose();
                        if (location == null) throw new InvalidDataException("助手更新跳转缺少目标地址。");
                        uri = location.IsAbsoluteUri ? location : new Uri(uri,location); continue;
                    }
                    if (!response.IsSuccessStatusCode) { int status=(int)response.StatusCode; response.Dispose(); throw new HttpRequestException("助手更新服务返回 HTTP " + status + "。"); }
                    return response;
                }
            }
            throw new InvalidDataException("助手更新跳转次数过多。");
        }
        async Task Transfer(string url,Stream output,long limit,bool binary,IProgress<Transfer> progress,CancellationToken token)
        {
            using (var response = await Get(url,binary,token))
            using (var input = await response.Content.ReadAsStreamAsync())
            {
                long expected=response.Content.Headers.ContentLength ?? 0;
                if (expected > limit) throw new InvalidDataException("助手更新文件超过大小限制。");
                await BoundedTransfer.Copy(input,output,limit,expected,progress,token);
            }
        }
        public async Task<AssistantRelease> Latest(CancellationToken token)
        {
            Exception last=null;
            foreach (string feed in Feeds)
            {
                try
                {
                    using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token))
                    using(var buffer=new MemoryStream())
                    {
                        timeout.CancelAfter(TimeSpan.FromSeconds(25));
                        await Transfer(feed,buffer,131072,false,null,timeout.Token);
                        return AssistantRelease.Parse(Encoding.UTF8.GetString(buffer.ToArray()),publicKey);
                    }
                }
                catch(Exception ex) { if(token.IsCancellationRequested) throw; last=ex; }
            }
            if(last is OperationCanceledException) throw new TimeoutException("助手更新服务连接超时。");
            throw last ?? new IOException("没有可用的助手更新源。");
        }
        public static void Verify(string file,AssistantRelease release)
        {
            if(new FileInfo(file).Length!=release.Size) throw new InvalidDataException("助手安装包大小不匹配。");
            OfficialClient.Verify(file,release.Sha256);
            var info=FileVersionInfo.GetVersionInfo(file);
            if(info.ProductName!="窝窝斯坐牢小助手" || AssistantRelease.NormalizeVersion(info.FileVersion)!=AssistantRelease.NormalizeVersion(release.Version)) throw new InvalidDataException("助手安装包的产品或版本与更新声明不匹配。");
        }
        public async Task<string> Download(AssistantRelease release,string root,IProgress<Transfer> progress,CancellationToken token)
        {
            Directory.CreateDirectory(root);
            string target=Path.Combine(root,"AslainAssistant.Setup-"+AssistantRelease.NormalizeVersion(release.Version)+".exe");
            if(File.Exists(target))
            {
                try { await Task.Run(()=>Verify(target,release),token); return target; }
                catch(InvalidDataException) { File.Delete(target); }
            }
            string part=target+".part"; Exception last=null;
            foreach(string url in release.Urls)
            {
                try
                {
                    using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        timeout.CancelAfter(TimeSpan.FromMinutes(5));
                        using(var output=new FileStream(part,FileMode.Create,FileAccess.Write,FileShare.None,81920,true)) await Transfer(url,output,release.Size,true,progress,timeout.Token);
                        await Task.Run(()=>Verify(part,release),timeout.Token);
                        File.Move(part,target); return target;
                    }
                }
                catch(Exception ex) { if(token.IsCancellationRequested) throw; last=ex; }
                finally { if(File.Exists(part)) File.Delete(part); }
            }
            if(last is OperationCanceledException) throw new TimeoutException("助手安装包下载超时。");
            throw last ?? new IOException("助手安装包下载失败。");
        }
        public void Dispose() { client.Dispose(); }
    }
    public sealed class AssistantUpdateSettings
    {
        public bool Enabled { get; set; }
        public string AttemptedVersion { get; set; }
        public string LastCheck { get; set; }
        public AssistantUpdateSettings() { Enabled=true; }
        public static AssistantUpdateSettings Load(string root)
        {
            string file=Path.Combine(root,"assistant-update.json");
            if(!File.Exists(file)) return new AssistantUpdateSettings();
            try { return Storage.Json().Deserialize<AssistantUpdateSettings>(Storage.ReadSmall(file)) ?? new AssistantUpdateSettings {Enabled=false}; }
            catch { return new AssistantUpdateSettings {Enabled=false}; }
        }
        public void Save(string root) { Storage.AtomicWrite(Path.Combine(root,"assistant-update.json"),Storage.Json().Serialize(this)); }
    }
    public sealed class SelfUpdateEngine : IDisposable
    {
        readonly string root;
        readonly AssistantUpdateClient client;
        readonly bool allowed;
        public AssistantUpdateSettings Settings { get; private set; }
        public bool Busy { get; private set; }
        public bool Handoff { get; private set; }
        public bool Allowed { get { return allowed; } }
        public AssistantRelease PublishedRelease { get; private set; }
        public string VersionStatus { get; private set; }
        public string PublishedVersion { get { return PublishedRelease == null ? "待检查" : AssistantRelease.NormalizeVersion(PublishedRelease.Version).ToString(3); } }
        public bool UpdateAvailable { get { return PublishedRelease != null && !PublishedRelease.IsHistorical && AssistantRelease.NormalizeVersion(PublishedRelease.Version)>CurrentVersion; } }
        public DateTimeOffset NextVersionCheck=DateTimeOffset.Now.AddMinutes(2);
        public DateTimeOffset NextCheck=DateTimeOffset.Now.AddMinutes(2);
        public string Status="每 6 小时检查助手新版；发现后自动下载、校验并更新。";
        public Version CurrentVersion=Assembly.GetExecutingAssembly().GetName().Version;
        public Func<bool> CanInstall=()=>!GameDetector.GameRunning() && !InstanceControl.ModInstallerRunning();
        public Func<bool> NetworkAvailable=NetworkInterface.GetIsNetworkAvailable;
        public Func<string,Task<int>> Launch=RunSetup;
        public event Action Changed;
        public event Action<string> Log;
        CancellationTokenSource cancellation;
        AssistantRelease pending;
        string pendingFile;
        static async Task<int> RunSetup(string file)
        {
            using(var process=Process.Start(new ProcessStartInfo(file,"--silent --restart-background --parent-pid="+Process.GetCurrentProcess().Id) {UseShellExecute=true}))
            {
                if(process==null) throw new IOException("无法启动助手安装器。");
                await Task.Run(()=>process.WaitForExit()); return process.ExitCode;
            }
        }
        public SelfUpdateEngine(string directory,AssistantUpdateClient transport,bool allow)
        {
            root=directory; client=transport; allowed=allow; Settings=AssistantUpdateSettings.Load(root);
            VersionStatus="尚未自检。检查 GitHub 已发布的安装包版本。";
            if(!allowed) Status="演示 / 测试配置不执行助手自更新。";
            else if(!Settings.Enabled) Status="助手自动更新已暂停，可以手动检查。";
        }
        void State(string text) { Status=text; if(Changed!=null) Changed(); if(Log!=null) Log(text); }
        public void SetEnabled(bool enabled) { Settings.Enabled=enabled; Settings.Save(root); NextCheck=DateTimeOffset.Now.AddSeconds(15); State(enabled ? "已开启助手自动更新，每 6 小时检查。" : "助手自动更新已暂停，可以手动检查。"); }
        public void Cancel() { if(cancellation!=null && !Handoff) cancellation.Cancel(); }
        async Task<AssistantRelease> InspectVersion(CancellationToken token)
        {
            PublishedRelease=null;
            VersionStatus="正在检查助手版本…"; State(VersionStatus);
            if(!NetworkAvailable()) throw new IOException("网络不可用");
            var release=await client.Latest(token);
            Settings.LastCheck=DateTimeOffset.Now.ToString("o"); Settings.Save(root);
            PublishedRelease=release;
            if(release.IsHistorical) VersionStatus="GitHub 仍为重置版本号前的历史发行版，保留当前助手。";
            else if(UpdateAvailable) VersionStatus="发现助手新版，可以下载并安装。";
            else if(AssistantRelease.NormalizeVersion(release.Version)==CurrentVersion) VersionStatus="当前助手与已发布版本一致。";
            else VersionStatus="当前助手版本高于已发布版本，无需更新。";
            NextVersionCheck=DateTimeOffset.Now.AddHours(6);
            return release;
        }
        public async Task CheckVersion()
        {
            if(Busy || !allowed) return;
            Busy=true; cancellation=new CancellationTokenSource();
            try
            {
                await InspectVersion(cancellation.Token);
                State("助手版本自检 · 当前 "+CurrentVersion.ToString(3)+" · GitHub 已发布 "+PublishedVersion+" · "+VersionStatus);
            }
            catch(OperationCanceledException) { VersionStatus="版本自检已取消。"; NextVersionCheck=DateTimeOffset.Now.AddMinutes(15); State(VersionStatus); }
            catch(Exception ex)
            {
                PublishedRelease=null; NextVersionCheck=DateTimeOffset.Now.AddMinutes(15);
                VersionStatus="暂时无法确认已发布版本："+ex.Message+"。可稍后重试。"; State(VersionStatus);
            }
            finally { Busy=false; cancellation.Dispose(); cancellation=null; if(Changed!=null) Changed(); }
        }
        public async Task Run(bool manual)
        {
            if(Busy || !allowed || (!manual && !Settings.Enabled)) return;
            Busy=true; cancellation=new CancellationTokenSource(); NextCheck=DateTimeOffset.Now.AddHours(6);
            try
            {
                if(pending==null)
                {
                    var latest=await InspectVersion(cancellation.Token);
                    if(!UpdateAvailable) { State("GitHub 已发布 "+PublishedVersion+" · "+VersionStatus); return; }
                    if(!manual && Settings.AttemptedVersion==latest.Version) { State("上次助手升级未完成，当前版本继续可用；点击「下载并安装助手新版」可重试。"); return; }
                    State("正在下载助手 "+latest.Version+"，完成后校验发布签名和 SHA-256…");
                    pendingFile=await client.Download(latest,Path.Combine(root,"assistant-updates"),null,cancellation.Token); pending=latest;
                }
                if(!CanInstall()) { NextCheck=DateTimeOffset.Now.AddMinutes(1); State("新版助手已下载；等待游戏及模组安装结束后自动更新。"); return; }
                cancellation.Token.ThrowIfCancellationRequested();
                AssistantUpdateClient.Verify(pendingFile,pending);
                Settings.AttemptedVersion=pending.Version; Settings.Save(root);
                State("即将更新助手，旧版会自动退出；完成后在托盘重新启动。");
                Handoff=true;
                int exit=await Launch(pendingFile);
                Handoff=false;
                if(exit!=0) throw new IOException("助手安装器退出码 "+exit+"；请查看配置目录中的 setup.log。");
                pending=null; pendingFile=null;
                State("助手安装器已完成，请重新打开助手。");
            }
            catch(OperationCanceledException) { State("助手更新已取消，当前版本继续可用。"); }
            catch(Exception ex) { pending=null; pendingFile=null; if(PublishedRelease==null) VersionStatus="暂时无法确认已发布版本："+ex.Message; NextVersionCheck=NextCheck=DateTimeOffset.Now.AddMinutes(15); State("助手更新未完成，当前版本继续可用："+ex.Message); }
            finally { Busy=false; Handoff=false; cancellation.Dispose(); cancellation=null; if(Changed!=null) Changed(); }
        }
        public void Dispose() { Cancel(); client.Dispose(); }
    }
}
