using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AslainAssistant
{
    sealed class StalledMirrorStream : MemoryStream
    {
        readonly TaskCompletionSource<int> read=new TaskCompletionSource<int>();
        public bool Closed;
        public override Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken token) { return read.Task; }
        protected override void Dispose(bool disposing) { Closed=true; read.TrySetCanceled(); base.Dispose(disposing); }
    }
    partial class Tests
    {
        static MirrorCatalog Catalog(byte[] bytes)
        {
            return new MirrorCatalog {Schema=1,Application="WoWS-Aslain-Classic-Mirror",GeneratedUtc=DateTimeOffset.UtcNow.ToString("o"),ExpiresUtc=DateTimeOffset.UtcNow.AddDays(2).ToString("o"),Packages=new[]{
                new MirrorPackage {Version="15.10.0_08",Sha256=Hash(bytes),Size=bytes.Length,OfficialUrl="https://filez.aslain.com/Aslains_WoWs_Modpack_Installer_v.15.10.0_08.exe",Urls=new[]{"https://gitee.com/test/mirror/releases/download/v15.10.0_08/installer.exe"},PublishedUtc=DateTimeOffset.UtcNow.AddDays(-1).ToString("o")},
                new MirrorPackage {Version="15.9.0_06",Sha256=Hash(bytes),Size=bytes.Length,OfficialUrl="https://filez.aslain.com/Aslains_WoWs_Modpack_Installer_v.15.9.0_06.exe",Urls=new[]{"https://gitee.com/test/mirror/releases/download/v15.9.0_06/installer.exe"},PublishedUtc=DateTimeOffset.UtcNow.AddDays(-30).ToString("o")}
            }};
        }
        static string SignMirror(MirrorCatalog catalog,RSACryptoServiceProvider key)
        {
            byte[] payload=Encoding.UTF8.GetBytes(Storage.Json().Serialize(catalog));
            return Storage.Json().Serialize(new SignedUpdate {Payload=Convert.ToBase64String(payload),Signature=Convert.ToBase64String(key.SignData(payload,CryptoConfig.MapNameToOID("SHA256")))});
        }
        static async Task RunMirrorTests()
        {
            using (var key=new RSACryptoServiceProvider(2048))
            {
                key.PersistKeyInCsp=false; string publicKey=key.ToXmlString(false); byte[] bytes=Exe();
                string signed=SignMirror(Catalog(bytes),key);
                Test("mirror signatures have a distinct purpose and bind versions, sizes and URLs",()=>{
                    var catalog=MirrorCatalog.Parse(signed,publicKey,DateTimeOffset.UtcNow);
                    Assert(catalog.Select("15.9.0","test").Version.FileTag=="15.9.0_06","old client received incompatible newest package");
                    Assert(catalog.Select("15.10.0","test").Version.FileTag=="15.10.0_08","latest matching package");
                    Assert(UpdatePolicy.Decide(Local("15.8.0"),catalog.Select("15.8.0","test"))==UpdateDecision.Incompatible,"unknown game release installed");
                    var envelope=Storage.Json().Deserialize<SignedUpdate>(signed); envelope.Payload=Convert.ToBase64String(Encoding.UTF8.GetBytes("{}"));
                    Throws(()=>MirrorCatalog.Parse(Storage.Json().Serialize(envelope),publicKey,DateTimeOffset.UtcNow),"tampered signature accepted");
                    var wrong=Catalog(bytes); wrong.Application="WoWS-Aslain-Assistant";
                    Throws(()=>MirrorCatalog.Parse(SignMirror(wrong,key),publicKey,DateTimeOffset.UtcNow),"self-update purpose accepted as Aslain");
                    wrong=Catalog(bytes); wrong.Packages[0].OfficialUrl=wrong.Packages[1].OfficialUrl;
                    Throws(()=>MirrorCatalog.Parse(SignMirror(wrong,key),publicKey,DateTimeOffset.UtcNow),"mismatched official version accepted");
                    string wrapped=Storage.Json().Serialize(new {encoding="base64",content=Convert.ToBase64String(Encoding.UTF8.GetBytes(signed))});
                    Assert(MirrorCatalog.Unwrap(wrapped)==signed,"Gitee contents wrapper not decoded");
                });
                Test("stale, future and duplicate mirror catalogs cannot claim a current release",()=>{
                    var c=Catalog(bytes); c.GeneratedUtc=DateTimeOffset.UtcNow.AddDays(-4).ToString("o"); c.ExpiresUtc=DateTimeOffset.UtcNow.AddDays(-1).ToString("o");
                    c.Packages[0].PublishedUtc=DateTimeOffset.UtcNow.AddDays(-5).ToString("o");
                    Throws(()=>MirrorCatalog.Parse(SignMirror(c,key),publicKey,DateTimeOffset.UtcNow),"expired accepted");
                    MirrorCatalog.Parse(SignMirror(c,key),publicKey,DateTimeOffset.UtcNow,false);
                    c=Catalog(bytes); c.GeneratedUtc=DateTimeOffset.UtcNow.AddDays(1).ToString("o");
                    Throws(()=>MirrorCatalog.Parse(SignMirror(c,key),publicKey,DateTimeOffset.UtcNow),"future accepted");
                    c=Catalog(bytes); c.Packages[1]=c.Packages[0];
                    Throws(()=>MirrorCatalog.Parse(SignMirror(c,key),publicKey,DateTimeOffset.UtcNow),"duplicate accepted");
                });
                Test("mirror sources reject local, credential-bearing and insecure URLs",()=>{
                    foreach(string url in new[]{"http://gitee.com/test", "https://localhost/test", "https://127.0.0.1/test", "https://a:b@gitee.com/test", "https://gitee.com:444/test", "file:///C:/a"})
                        Throws(()=>new MirrorSettings {Feeds=new[]{url}}.Validate(),"unsafe source accepted: "+url);
                });
                await TestAsync("domestic-only version check does not contact the official site",async()=>{
                    var requests=new List<string>();
                    var http=new FakeHttp {Respond=r=>{requests.Add(r.RequestUri.Host); return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(signed)};}};
                    using(var client=new OfficialClient(http)) {
                        client.MirrorPublicKey=publicKey; client.ReadMirrorSettings=()=>new MirrorSettings {Feeds=new[]{"https://gitee.com/test/catalog.json"}};
                        var result=await client.LatestForGame("15.9.0",CancellationToken.None);
                        Assert(result.Version.FileTag=="15.9.0_06" && requests.SequenceEqual(new[]{"gitee.com"}),"domestic lookup depended on external site");
                    }
                });
                await TestAsync("invalid primary feed falls back to the second domestic source then official",async()=>{
                    var visited=new List<string>();
                    var http=new FakeHttp {Respond=r=>{
                        visited.Add(r.RequestUri.Host);
                        return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(r.RequestUri.Host=="backup.example.com"?signed:r.RequestUri.Host=="aslain.com"?Html(Hash(bytes)):"<html>login</html>")};
                    }};
                    using(var client=new OfficialClient(http)) {
                        client.MirrorPublicKey=publicKey; client.ReadMirrorSettings=()=>new MirrorSettings {Feeds=new[]{"https://gitee.com/a", "https://backup.example.com/b"}};
                        await client.LatestForGame("15.10.0",CancellationToken.None);
                        Assert(visited.SequenceEqual(new[]{"gitee.com","backup.example.com"}),"secondary feed not used");
                        visited.Clear(); client.ReadMirrorSettings=()=>new MirrorSettings {Feeds=new[]{"https://gitee.com/a"}};
                        await client.LatestForGame("15.10.0",CancellationToken.None);
                        Assert(visited.SequenceEqual(new[]{"gitee.com","aslain.com"}),"official fallback not used");
                    }
                });
                await TestAsync("expired signed primary feed falls back rather than reporting up to date",async()=>{
                    var stale=Catalog(bytes); stale.GeneratedUtc=DateTimeOffset.UtcNow.AddDays(-4).ToString("o"); stale.ExpiresUtc=DateTimeOffset.UtcNow.AddDays(-2).ToString("o"); stale.Packages[0].PublishedUtc=DateTimeOffset.UtcNow.AddDays(-5).ToString("o");
                    var visited=new List<string>(); var http=new FakeHttp {Respond=r=>{visited.Add(r.RequestUri.Host);return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(r.RequestUri.Host=="gitee.com"?SignMirror(stale,key):Html(Hash(bytes)))};}};
                    using(var client=new OfficialClient(http)) {
                        client.MirrorPublicKey=publicKey; client.ReadMirrorSettings=()=>new MirrorSettings {Feeds=new[]{"https://gitee.com/a"}};
                        var result=await client.LatestForGame("15.10.0",CancellationToken.None);
                        Assert(visited.SequenceEqual(new[]{"gitee.com","aslain.com"}) && result.Source=="阿斯兰官网","stale feed was treated as current");
                    }
                });
                await TestAsync("mirror cancellation stops immediately without a fallback request",async()=>{
                    using(var client=new OfficialClient(new PendingHttp())) using(var stop=new CancellationTokenSource(60)) {
                        client.ReadMirrorSettings=()=>new MirrorSettings {Feeds=new[]{"https://gitee.com/a"}};
                        await Reject(async()=>await client.LatestForGame("15.10.0",stop.Token),"canceled query completed");
                    }
                });
                await TestAsync("cancellation also closes stalled Framework streams that ignore tokens",async()=>{
                    using(var input=new StalledMirrorStream()) using(var output=new MemoryStream()) using(var stop=new CancellationTokenSource(60)) {
                        var method=typeof(OfficialClient).GetMethod("CopyBounded",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static);
                        var work=(Task)method.Invoke(null,new object[]{input,output,2048L,0L,null,stop.Token});
                        Assert(await Task.WhenAny(work,Task.Delay(2000))==work,"stalled stream ignored cancellation");
                        await Reject(()=>work,"canceled stream completed successfully");
                        Assert(input.Closed,"stalled socket was not released");
                    }
                });
                await TestAsync("corrupt domestic installer switches sources and removes partial bytes",async()=>{
                    var visited=new List<string>();
                    var http=new FakeHttp {Respond=r=>{visited.Add(r.RequestUri.Host); return new HttpResponseMessage(HttpStatusCode.OK){Content=r.RequestUri.Host=="gitee.com"?(HttpContent)new StringContent("<html>not an exe</html>"):new ByteArrayContent(bytes)};}};
                    var release=Catalog(bytes).Select("15.10.0","test");
                    using(var client=new OfficialClient(http)) {
                        string dir=Path.Combine(root,"mirror-fallback");
                        string file=await client.Download(release,dir,null,CancellationToken.None);
                        OfficialClient.Verify(file,Hash(bytes));
                        Assert(visited.SequenceEqual(new[]{"gitee.com","filez.aslain.com"}) && !File.Exists(file+".part"),"download fallback did not verify/clean up");
                        visited.Clear(); await client.Download(release,dir,null,CancellationToken.None);
                        Assert(visited.Count==0,"valid cache unexpectedly redownloaded");
                    }
                });
                await TestAsync("mirror HTTPS downgrade and partial files are rejected",async()=>{
                    var release=Catalog(bytes).Select("15.10.0","test");
                    var http=new FakeHttp {Respond=r=> { var response=new HttpResponseMessage(HttpStatusCode.Found); response.Headers.Location=new Uri("http://gitee.com/file"); return response; }};
                    string dir=Path.Combine(root,"mirror-redirect");
                    using(var client=new OfficialClient(http)) await Reject(async()=>await client.Download(release,dir,null,CancellationToken.None),"downgrade accepted");
                    Assert(Directory.GetFiles(dir).Length==0,"failed download left an executable");
                });
                Test("manual backup import verifies bytes and cannot overwrite valid cache with corruption",()=>{
                    var release=Catalog(bytes).Select("15.10.0","test"); string dir=Path.Combine(root,"manual-import");
                    string source=Path.Combine(root,"download.exe"); File.WriteAllBytes(source,bytes);
                    string target=OfficialClient.ImportInstaller(source,release,dir);
                    File.WriteAllText(source,"<html>bad download</html>");
                    Throws(()=>OfficialClient.ImportInstaller(source,release,dir),"bad manual file accepted");
                    OfficialClient.Verify(target,release.Sha256);
                    Assert(Directory.GetFiles(dir).Length==1,"failed import left staging data");
                });
                await TestAsync("publisher probe actually downloads anonymously and verifies SHA-256",async()=>{
                    var http=new FakeHttp {Respond=r=>{Assert(r.Headers.Authorization==null,"credential leaked to public probe");return new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)};}};
                    using(var client=new OfficialClient(http)) await client.DownloadMirrorProbe("https://gitee.com/public.exe",Hash(bytes),bytes.Length,Path.Combine(root,"public-probe"));
                    Assert(http.Calls==1,"public probe skipped network verification");
                });
            }
        }
    }
}
