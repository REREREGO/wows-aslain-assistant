using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AslainAssistant
{
    public sealed class MirrorSettings
    {
        public static readonly string[] DefaultFeeds = {
            "https://gitee.com/rererergo/aslain_versions/raw/master/catalog.json",
            "https://gitee.com/api/v5/repos/rererergo/aslain_versions/contents/catalog.json?ref=master"
        };
        public const string LanzouFolder = "https://wwbcm.lanzouq.com/b01i60xlde";
        public const string LanzouPassword = "6fpw";
        public string[] Feeds { get; set; }
        public MirrorSettings() { Feeds = DefaultFeeds; }
        public static bool Https(string value)
        {
            Uri uri;
            return Uri.TryCreate(value,UriKind.Absolute,out uri) && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo == "" && uri.Fragment == "" && uri.HostNameType == UriHostNameType.Dns && uri.Host.Contains(".") && !uri.Host.EndsWith(".local",StringComparison.OrdinalIgnoreCase) && !uri.IsLoopback;
        }
        public MirrorSettings Validate()
        {
            if (Feeds == null || Feeds.Length > 3 || Feeds.Any(x=>x == null || x.Length > 2048 || !Https(x))) throw new InvalidDataException("镜像清单需要 0–3 个 HTTPS 地址。");
            Feeds = Feeds.Distinct().ToArray(); return this;
        }
        public static MirrorSettings Load()
        {
            string path = Path.Combine(Storage.Root,"mirror-sources.json");
            return (File.Exists(path) ? Storage.Json().Deserialize<MirrorSettings>(Storage.ReadSmall(path)) : new MirrorSettings()).Validate();
        }
        public void Save() { Storage.AtomicWrite(Path.Combine(Storage.Root,"mirror-sources.json"),Storage.Json().Serialize(Validate())); }
    }
    public sealed class MirrorPackage
    {
        public string Version { get; set; }
        public string Sha256 { get; set; }
        public long Size { get; set; }
        public string OfficialUrl { get; set; }
        public string[] Urls { get; set; }
        public string PublishedUtc { get; set; }
        public Release ToRelease(string source)
        {
            return new Release {Version=PackVersion.Parse(Version),Download=new Uri(OfficialUrl),Sha256=Sha256,MirrorUrls=Urls,Size=Size,Source=source};
        }
    }
    public sealed class MirrorCatalog
    {
        public int Schema { get; set; }
        public string Application { get; set; }
        public string GeneratedUtc { get; set; }
        public string ExpiresUtc { get; set; }
        public MirrorPackage[] Packages { get; set; }
        public static MirrorCatalog Parse(string text,string publicKey,DateTimeOffset now,bool requireFresh = true)
        {
            if (string.IsNullOrEmpty(text) || text.Length > 1024*1024) throw new InvalidDataException("镜像清单大小无效。");
            var signed = Storage.Json().Deserialize<SignedUpdate>(text);
            if (signed == null || signed.Payload == null || signed.Signature == null) throw new InvalidDataException("镜像清单缺少签名。");
            byte[] payload = Convert.FromBase64String(signed.Payload);
            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.PersistKeyInCsp = false; rsa.FromXmlString(publicKey);
                if (!rsa.VerifyData(payload,CryptoConfig.MapNameToOID("SHA256"),Convert.FromBase64String(signed.Signature))) throw new InvalidDataException("镜像清单签名不匹配。");
            }
            var catalog = Storage.Json().Deserialize<MirrorCatalog>(Encoding.UTF8.GetString(payload));
            if (catalog == null || catalog.Schema != 1 || catalog.Application != "WoWS-Aslain-Classic-Mirror") throw new InvalidDataException("这不是阿斯兰镜像清单。");
            DateTimeOffset generated,expires;
            if (!DateTimeOffset.TryParse(catalog.GeneratedUtc,out generated) || !DateTimeOffset.TryParse(catalog.ExpiresUtc,out expires) || generated > now.AddMinutes(10) || (requireFresh && expires <= now) || expires <= generated || expires > generated.AddDays(3)) throw new InvalidDataException("镜像清单已过期或时间无效，请维护者检查同步任务。");
            if (catalog.Packages == null || catalog.Packages.Length == 0 || catalog.Packages.Length > 512) throw new InvalidDataException("镜像版本列表无效。");
            var versions = new HashSet<string>();
            foreach (var pack in catalog.Packages)
            {
                if (pack == null || !Regex.IsMatch(pack.Version ?? "",@"^\d{1,3}\.\d{1,3}\.\d{1,3}_\d{2,4}$") || !versions.Add(PackVersion.Parse(pack.Version).FileTag)) throw new InvalidDataException("镜像版本号重复或无效。");
                Uri official;
                if (!Uri.TryCreate(pack.OfficialUrl,UriKind.Absolute,out official) || !OfficialClient.Allowed(official) || official.Host != "filez.aslain.com" || official.AbsolutePath != "/Aslains_WoWs_Modpack_Installer_v." + PackVersion.Parse(pack.Version).FileTag + ".exe" || official.Query != "") throw new InvalidDataException("镜像包的官网来源不匹配。");
                if (!Regex.IsMatch(pack.Sha256 ?? "","^[a-fA-F0-9]{64}$") || pack.Size < 1024 || pack.Size > 1024L*1024*1024) throw new InvalidDataException("镜像安装包校验信息无效。");
                if (pack.Urls == null || pack.Urls.Length == 0 || pack.Urls.Length > 6 || pack.Urls.Any(x=>x == null || x.Length > 4096 || !MirrorSettings.Https(x))) throw new InvalidDataException("镜像安装包地址无效。");
                DateTimeOffset published;
                if (!DateTimeOffset.TryParse(pack.PublishedUtc,out published) || published > generated.AddMinutes(10)) throw new InvalidDataException("镜像安装包发布时间无效。");
            }
            return catalog;
        }
        public static string Unwrap(string text)
        {
            // Gitee Contents API is a second domestic read route when raw files are unavailable.
            var json=new System.Web.Script.Serialization.JavaScriptSerializer {MaxJsonLength=2*1024*1024,RecursionLimit=16};
            var wrapper=json.Deserialize<Dictionary<string,object>>(text);
            object encoding,content;
            if (wrapper != null && wrapper.TryGetValue("encoding",out encoding) && (encoding as string)=="base64" && wrapper.TryGetValue("content",out content) && content is string)
                return Encoding.UTF8.GetString(Convert.FromBase64String((string)content));
            return text;
        }
        public Release Select(string game,string source)
        {
            var ordered = Packages.OrderByDescending(x=>PackVersion.Parse(x.Version));
            var match = ordered.FirstOrDefault(x=>PackVersion.Parse(x.Version).Game == game);
            return (match ?? ordered.First()).ToRelease(source);
        }
    }
    public sealed partial class OfficialClient
    {
        public string SourceDetail { get; private set; }
        public Action<string> SourceNotice;
        public Func<MirrorSettings> ReadMirrorSettings = MirrorSettings.Load;
        public string MirrorPublicKey = MirrorTrust.PublicKey;
        public TimeSpan FeedTimeout = TimeSpan.FromSeconds(8);
        async Task<HttpResponseMessage> GetMirror(Uri url,CancellationToken token)
        {
            for (int i=0;i<6;i++)
            {
                if (!MirrorSettings.Https(url.AbsoluteUri)) throw new InvalidDataException("镜像跳转地址无效。");
                var response = await client.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,token);
                if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
                {
                    var next=response.Headers.Location; response.Dispose();
                    if (next == null) throw new InvalidDataException("镜像跳转缺少地址。");
                    url=next.IsAbsoluteUri ? next : new Uri(url,next); continue;
                }
                if (!response.IsSuccessStatusCode) { int status=(int)response.StatusCode; response.Dispose(); throw new HttpRequestException("镜像返回 HTTP "+status+"。"); }
                return response;
            }
            throw new InvalidDataException("镜像跳转次数过多。");
        }
        public async Task<Release> LatestForGame(string game,CancellationToken token)
        {
            SourceDetail = "";
            foreach (string url in ReadMirrorSettings().Feeds)
            {
                try
                {
                    using (var timeout=CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        timeout.CancelAfter(FeedTimeout);
                        using (var response=await GetMirror(new Uri(url),timeout.Token))
                        using (var stream=await response.Content.ReadAsStreamAsync())
                        using (var buffer=new MemoryStream())
                        {
                            await BoundedTransfer.Copy(stream,buffer,2*1024*1024,0,null,timeout.Token);
                            string text=MirrorCatalog.Unwrap(Encoding.UTF8.GetString(buffer.ToArray()));
                            var catalog=MirrorCatalog.Parse(text,MirrorPublicKey,DateTimeOffset.UtcNow);
                            SourceDetail="国内镜像 · 同步于 "+DateTimeOffset.Parse(catalog.GeneratedUtc).ToLocalTime().ToString("MM-dd HH:mm");
                            return catalog.Select(game,SourceDetail);
                        }
                    }
                }
                catch (Exception ex)
                {
                    token.ThrowIfCancellationRequested();
                    if (!(ex is HttpRequestException || ex is OperationCanceledException || ex is IOException || ex is InvalidDataException || ex is ArgumentException || ex is InvalidOperationException || ex is CryptographicException || ex is FormatException)) throw;
                    if (SourceNotice != null) SourceNotice("国内版本源不可用，尝试下一来源："+ex.Message);
                }
            }
            var official=await Latest(token); SourceDetail="阿斯兰官网"; return official;
        }
        public static string ImportInstaller(string source,Release release,string directory)
        {
            if (release == null) throw new InvalidDataException("请先检查版本，取得可信的校验信息。");
            Directory.CreateDirectory(directory);
            string target=Path.Combine(directory,"Aslains_WoWs_Modpack_Installer_v."+release.Version.FileTag+".exe");
            string stage=target+"."+Guid.NewGuid().ToString("N")+".import";
            try
            {
                if (new FileInfo(source).Length > 1024L*1024*1024) throw new InvalidDataException("安装包超过 1 GB 限制。");
                File.Copy(source,stage,true); Verify(stage,release.Sha256);
                if (release.Size > 0 && new FileInfo(stage).Length != release.Size) throw new InvalidDataException("安装包大小不匹配。");
                if (File.Exists(target)) File.Replace(stage,target,null); else File.Move(stage,target);
                return target;
            }
            finally { if (File.Exists(stage)) File.Delete(stage); }
        }
        public async Task<string> DownloadMirrorProbe(string url,string hash,long size,string directory)
        {
            Directory.CreateDirectory(directory);
            string path=Path.Combine(directory,"probe.exe");
            try
            {
                using (var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(10)))
                using (var response=await GetMirror(new Uri(url),timeout.Token))
                using (var input=await response.Content.ReadAsStreamAsync())
                {
                    using (var output=new FileStream(path,FileMode.Create,FileAccess.Write,FileShare.None))
                        await BoundedTransfer.Copy(input,output,size,size,null,timeout.Token);
                    Verify(path,hash);
                }
                return path;
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
    }
}
