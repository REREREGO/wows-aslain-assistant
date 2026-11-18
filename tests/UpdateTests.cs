using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AslainAssistant
{
    class PendingHttp : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) { await Task.Delay(Timeout.Infinite,token); return null; }
    }
    static class UpdateTests
    {
        static int count;
        static void Assert(bool value,string text) { if(!value)throw new Exception(text); }
        static void Pass(string text) { count++; Console.WriteLine("PASS "+text); }
        static string Signed(AssistantRelease release,RSACryptoServiceProvider key)
        {
            byte[] data=Encoding.UTF8.GetBytes(Storage.Json().Serialize(release));
            return Storage.Json().Serialize(new SignedUpdate {Payload=Convert.ToBase64String(data),Signature=Convert.ToBase64String(key.SignData(data,CryptoConfig.MapNameToOID("SHA256")))});
        }
        static Settings Config(string id) { return new Settings {Id=id,GamePath="unused-test-path",AutoUpdate=true}; }
        public static async Task<int> Run(string root)
        {
            count=0;
            var offlineHttp=new FakeHttp {Respond=r=>{throw new Exception("Offline check must not send a request");}};
            var noInstaller=new FakeInstaller();
            using(var engine=new UpdateEngine(Config("offline"),new OfficialClient(offlineHttp),noInstaller))
            {
                engine.InspectGame=p=>new GameState {Path=p,GameVersion="15.10.0"}; engine.NetworkAvailable=()=>false;
                await engine.Run(true,false,false);
                Assert(engine.WaitingForNetwork && noInstaller.Calls==0 && offlineHttp.Calls==0,"Offline check launched network/installer");
                Assert(engine.NextCheck>DateTimeOffset.Now.AddMinutes(14) && string.IsNullOrEmpty(engine.Settings.FailedRelease),"Offline state damaged retry/install state");
            }
            Pass("offline check preserves installation and retries without starting downloads");
            var timeoutHttp=new FakeHttp {Respond=r=>{throw new OperationCanceledException();}};
            using(var engine=new UpdateEngine(Config("timeout"),new OfficialClient(timeoutHttp),new FakeInstaller()))
            {
                engine.NetworkAvailable=()=>true; engine.InspectGame=p=>new GameState {Path=p,GameVersion="15.10.0"};
                await engine.Run(true,false,false); Assert(engine.WaitingForNetwork,"Timeout was incorrectly treated as user cancellation");
            }
            using(var engine=new UpdateEngine(Config("cancel-network"),new OfficialClient(new PendingHttp()),new FakeInstaller()))
            {
                engine.NetworkAvailable=()=>true; engine.InspectGame=p=>new GameState {Path=p,GameVersion="15.10.0"};
                var work=engine.Run(true,false,false); engine.Cancel(); await work;
                Assert(engine.Status=="已取消" && !engine.WaitingForNetwork,"User cancellation was incorrectly treated as offline");
            }
            Pass("timeout and explicit cancellation have distinct recoverable states");
            using(var key=new RSACryptoServiceProvider(2048))
            {
                key.PersistKeyInCsp=false;
                string setup=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"AslainAssistant.Setup.exe");
                byte[] package=File.ReadAllBytes(setup); string hash;
                using(var sha=SHA256.Create()) hash=BitConverter.ToString(sha.ComputeHash(package)).Replace("-","").ToLowerInvariant();
                var release=new AssistantRelease {Schema=1,Application="WoWS-Aslain-Assistant",Version=FileVersionInfo.GetVersionInfo(setup).FileVersion,PublishedUtc=DateTimeOffset.UtcNow.ToString("o"),Size=package.Length,Sha256=hash,Urls=new[]{AssistantUpdateClient.Repository+"/releases/download/test/AslainAssistant.Setup.exe"}};
                string signed=Signed(release,key), publicKey=key.ToXmlString(false);
                Assert(AssistantRelease.Parse(signed,publicKey).Sha256==hash,"Valid signature rejected");
                using(var wrongKey=new RSACryptoServiceProvider(2048))
                {
                    wrongKey.PersistKeyInCsp=false; bool rejected=false;
                    try {AssistantRelease.Parse(signed,wrongKey.ToXmlString(false));} catch(InvalidDataException) {rejected=true;}
                    Assert(rejected,"Incorrect signature accepted");
                }
                Assert(!AssistantUpdateClient.AllowedUrl("https://github.com.evil.example/REREREGO/wows-aslain-assistant/a") && !AssistantUpdateClient.AllowedUrl("https://github.com/other/repo/a"),"Foreign repository accepted");
                Pass("assistant release signatures and repository boundaries are enforced");
                var fallback=new FakeHttp {Respond=r=>r.RequestUri.Host=="github.com" ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(signed)}};
                using(var client=new AssistantUpdateClient(fallback,publicKey)) Assert((await client.Latest(CancellationToken.None)).Sha256==hash && fallback.Calls==2,"Feed fallback failed");
                Pass("backup metadata endpoint works when the primary endpoint is unavailable");
                foreach(bool metadata in new[]{true,false})
                {
                    using(var input=new StalledMirrorStream())
                    using(var stop=new CancellationTokenSource(60))
                    {
                        var stalledHttp=new FakeHttp {Respond=r=>new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(input)}};
                        using(var client=new AssistantUpdateClient(stalledHttp,publicKey))
                        {
                            Task work=metadata ? (Task)client.Latest(stop.Token) : client.Download(release,Path.Combine(root,"self-stalled"),null,stop.Token);
                            Assert(await Task.WhenAny(work,Task.Delay(2000))==work,"Assistant stream ignored cancellation after receiving headers");
                            bool canceled=false;
                            try {await work;} catch(OperationCanceledException) {canceled=true;}
                            Assert(canceled && input.Closed && stalledHttp.Calls==1,"Canceled transfer leaked its stream or tried another source");
                            if(!metadata) Assert(Directory.GetFiles(Path.Combine(root,"self-stalled")).Length==0,"Canceled installer left partial files");
                        }
                    }
                    Pass(metadata ? "stalled assistant metadata responds to cancellation without fallback" : "stalled assistant installer cancels and removes partial files");
                }
                string checkRoot=Path.Combine(root,"version-check");
                new AssistantUpdateSettings {Enabled=false,AttemptedVersion="keep-attempt"}.Save(checkRoot);
                var checkedRelease=Storage.Json().Deserialize<AssistantRelease>(Storage.Json().Serialize(release));
                var checkHttp=new FakeHttp {Respond=r=>
                {
                    Assert(r.RequestUri.AbsolutePath.EndsWith(".json"),"Version check downloaded an installer");
                    return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(Signed(checkedRelease,key))};
                }};
                using(var engine=new SelfUpdateEngine(checkRoot,new AssistantUpdateClient(checkHttp,publicKey),true))
                {
                    engine.CurrentVersion=new Version(1,0,1,0); engine.NetworkAvailable=()=>true;
                    engine.Launch=file=>{throw new Exception("Version check launched setup");};
                    engine.CanInstall=()=>{throw new Exception("Version check touched installation state");};
                    checkedRelease.Version="1.0.2";
                    await engine.CheckVersion();
                    Assert(engine.UpdateAvailable && engine.PublishedVersion=="1.0.2" && !engine.Busy && !engine.Settings.Enabled,"New release was not detected independently of auto-install");
                    Assert(!Directory.Exists(Path.Combine(checkRoot,"assistant-updates")) && engine.Settings.AttemptedVersion=="keep-attempt","Check modified installation state");
                    Assert(!AssistantUpdateSettings.Load(checkRoot).Enabled && engine.NextVersionCheck>DateTimeOffset.Now.AddHours(5),"Check enabled automatic updates or lost its schedule");
                    checkedRelease.Version="1.0.1"; await engine.CheckVersion();
                    Assert(!engine.UpdateAvailable && engine.VersionStatus.Contains("一致"),"Equal versions not recognized");
                    checkedRelease.Version="1.0.0"; await engine.CheckVersion();
                    Assert(!engine.UpdateAvailable && engine.VersionStatus.Contains("高于"),"Local newer build misreported");
                    Pass("version self-check compares releases without downloading, installing or enabling auto-update");
                    checkedRelease.Version="1.4.4"; checkedRelease.PublishedUtc="2026-11-18T08:15:03Z";
                    await engine.CheckVersion();
                    Assert(!engine.UpdateAvailable && engine.VersionStatus.Contains("历史") && engine.PublishedVersion=="1.4.4","Historical larger number was promoted as an update");
                    await engine.Run(true);
                    engine.SetEnabled(true); await engine.Run(false); engine.SetEnabled(false);
                    Assert(!Directory.Exists(Path.Combine(checkRoot,"assistant-updates")),"Historical release was downloaded by manual or automatic install");
                    Pass("pre-reset releases cannot overwrite 1.0.1 through either install path");
                    string lastCheck=engine.Settings.LastCheck; int calls=checkHttp.Calls;
                    engine.NetworkAvailable=()=>false; await engine.CheckVersion();
                    Assert(checkHttp.Calls==calls && engine.PublishedRelease==null && !engine.UpdateAvailable && engine.VersionStatus.Contains("无法确认"),"Offline state retained a stale update or claimed latest");
                    Assert(engine.Settings.LastCheck==lastCheck && engine.NextVersionCheck>DateTimeOffset.Now.AddMinutes(14) && !engine.Busy,"Failed check changed success timestamp or did not retry");
                    engine.NetworkAvailable=()=>true; checkedRelease.Version="1.0.2"; checkedRelease.PublishedUtc=release.PublishedUtc;
                    await engine.CheckVersion(); Assert(engine.UpdateAvailable,"Online retry did not recover");
                    checkedRelease.PublishedUtc=null; await engine.CheckVersion();
                    Assert(!engine.UpdateAvailable && engine.PublishedRelease==null && engine.VersionStatus.Contains("发布时间"),"Missing release chronology accepted");
                    checkedRelease.PublishedUtc=release.PublishedUtc;
                    checkHttp.Respond=r=>new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"Payload\":\"invalid\",\"Signature\":\"invalid\"}")};
                    await engine.CheckVersion(); Assert(!engine.UpdateAvailable && engine.PublishedRelease==null && engine.VersionStatus.Contains("无法确认"),"Unverified metadata presented as a release");
                    Pass("offline and untrusted checks remain retryable and clear stale release results");
                }
                var pendingHttp=new PendingHttp();
                using(var engine=new SelfUpdateEngine(Path.Combine(root,"version-cancel"),new AssistantUpdateClient(pendingHttp,publicKey),true))
                {
                    engine.NetworkAvailable=()=>true;
                    var pendingCheck=engine.CheckVersion(); Assert(engine.Busy,"Pending check not marked busy");
                    await engine.CheckVersion(); engine.Cancel(); await pendingCheck;
                    Assert(!engine.Busy && !engine.UpdateAvailable && engine.VersionStatus.Contains("取消"),"Duplicate/cancelled check did not release busy state");
                }
                Pass("version checks reject overlapping work and support cancellation");
                var valid=new FakeHttp {Respond=r=>new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(package)}};
                using(var client=new AssistantUpdateClient(valid,publicKey))
                {
                    string dir=Path.Combine(root,"self-download");
                    string file=await client.Download(release,dir,null,CancellationToken.None);
                    AssistantUpdateClient.Verify(file,release); await client.Download(release,dir,null,CancellationToken.None);
                    Assert(valid.Calls==1,"Verified cached helper installer was downloaded again");
                }
                var damaged=new FakeHttp {Respond=r=>new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(new byte[package.Length])}};
                using(var client=new AssistantUpdateClient(damaged,publicKey))
                {
                    string dir=Path.Combine(root,"self-corrupt"); bool rejected=false;
                    try {await client.Download(release,dir,null,CancellationToken.None);} catch(InvalidDataException) {rejected=true;}
                    Assert(rejected && Directory.GetFiles(dir).Length==0,"Damaged installer or partial file was retained");
                }
                Pass("assistant packages require hash, size, PE and product version checks");
                var workflow=new FakeHttp {Respond=r=>new HttpResponseMessage(HttpStatusCode.OK){Content=r.RequestUri.AbsolutePath.EndsWith(".json") ? (HttpContent)new StringContent(signed) : new ByteArrayContent(package)}};
                using(var engine=new SelfUpdateEngine(Path.Combine(root,"self-workflow"),new AssistantUpdateClient(workflow,publicKey),true))
                {
                    int launches=0; engine.CurrentVersion=new Version(0,0,0,0); engine.NetworkAvailable=()=>true; engine.CanInstall=()=>false;
                    engine.Launch=file=>{launches++;AssistantUpdateClient.Verify(file,release);return Task.FromResult(0);};
                    await engine.Run(false); Assert(launches==0 && engine.Status.Contains("等待"),"Self-update interrupted a busy game");
                    engine.CanInstall=()=>true; engine.NetworkAvailable=()=>false;
                    await engine.Run(false); Assert(launches==1,"Verified staged package did not install after the game exited");
                    Assert(AssistantUpdateSettings.Load(Path.Combine(root,"self-workflow")).AttemptedVersion==release.Version,"Attempt marker missing");
                    engine.NetworkAvailable=()=>true; await engine.Run(false); Assert(launches==1,"Failed handoff retried indefinitely");
                }
                Pass("self-update stages while busy and hands off exactly one verified installer");
            }
            string transactionRoot=Path.Combine(root,"transaction"); Directory.CreateDirectory(transactionRoot); File.WriteAllText(Path.Combine(transactionRoot,"app.exe"),"old");
            using(var transaction=new FileInstallTransaction(transactionRoot))
            {
                using(var bytes=new MemoryStream(Encoding.UTF8.GetBytes("new"))) transaction.Stage("app.exe",bytes);
                using(var bytes=new MemoryStream(Encoding.UTF8.GetBytes("dependency"))) transaction.Stage("library.dll",bytes);
                try {transaction.Commit(()=>{throw new IOException("simulated publication failure");});} catch(IOException) { }
                Assert(File.ReadAllText(Path.Combine(transactionRoot,"app.exe"))=="old" && !File.Exists(Path.Combine(transactionRoot,"library.dll")),"Upgrade rollback failed");
            }
            Assert(Directory.GetFiles(transactionRoot,"*.new").Length==0,"Staged files were not cleaned");
            Pass("failed installer transaction restores prior executable and dependencies");
            bool busy=true; using(var ended=new ManualResetEvent(false))
            using(var server=new InstanceServer(command=>busy ? InstanceControl.Busy : InstanceControl.Ready,()=>ended.Set()))
            {
                byte response=0;
                for(int i=0;i<10 && response==0;i++) response=InstanceControl.Send(Process.GetCurrentProcess().Id,InstanceControl.Exit);
                Assert(response==InstanceControl.Busy && !ended.WaitOne(0),"Busy application was asked to exit");
                busy=false; Assert(InstanceControl.Send(Process.GetCurrentProcess().Id,InstanceControl.Exit)==InstanceControl.Ready && ended.WaitOne(2000),"Cooperative shutdown acknowledgement failed");
            }
            Pass("local shutdown protocol waits for busy work before acknowledging exit");
            return count;
        }
    }
}
