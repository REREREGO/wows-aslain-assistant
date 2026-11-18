using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AslainAssistant
{
    sealed class DirectProgress : IProgress<Transfer>
    {
        public Transfer Last;
        public void Report(Transfer value) { Last=value; }
    }
    sealed class ChunkedTestStream : MemoryStream
    {
        readonly int chunk,delay;
        public int Reads;
        public ChunkedTestStream(byte[] bytes,int chunkSize,int delayMilliseconds=0) : base(bytes) {chunk=chunkSize;delay=delayMilliseconds;}
        public override async Task<int> ReadAsync(byte[] bytes,int offset,int count,CancellationToken token)
        {
            Reads++;
            if(delay>0) await Task.Delay(delay,token);
            return await base.ReadAsync(bytes,offset,Math.Min(count,chunk),token);
        }
    }
    partial class Tests
    {
        static async Task RunTransferTests()
        {
            await TestAsync("idle timeout releases a stalled stream without user cancellation",async()=>{
                using(var input=new StalledMirrorStream()) using(var output=new MemoryStream()) {
                    var work=BoundedTransfer.Copy(input,output,1024,0,null,CancellationToken.None,60);
                    Assert(await Task.WhenAny(work,Task.Delay(2000))==work,"Idle stream remained stuck");
                    bool timedOut=false;try {await work;}catch(OperationCanceledException){timedOut=true;}
                    Assert(timedOut && input.Closed,"Idle socket not closed");
                }
            });
            await TestAsync("cancellation before transfer does not read or write bytes",async()=>{
                using(var input=new ChunkedTestStream(new byte[10],3)) using(var output=new MemoryStream()) using(var stop=new CancellationTokenSource()) {
                    stop.Cancel();bool canceled=false;
                    try {await BoundedTransfer.Copy(input,output,10,10,null,stop.Token);}catch(OperationCanceledException){canceled=true;}
                    Assert(canceled && input.Reads==0 && output.Length==0,"Canceled operation performed I/O");
                }
            });
            foreach(int declared in new[]{0,32})
                await TestAsync("chunked transfer completes and reports final progress with length "+declared,async()=>{
                    byte[] data=Enumerable.Range(0,32).Select(x=>(byte)x).ToArray();var progress=new DirectProgress();
                    using(var input=new ChunkedTestStream(data,3)) using(var output=new MemoryStream()) {
                        await BoundedTransfer.Copy(input,output,32,declared,progress,CancellationToken.None);
                        Assert(output.ToArray().SequenceEqual(data) && progress.Last.Received==32 && progress.Last.Total==declared,"Bytes or final progress incorrect");
                    }
                });
            await TestAsync("continued progress resets the idle deadline",async()=>{
                using(var input=new ChunkedTestStream(new byte[6],1,120)) using(var output=new MemoryStream()) {
                    await BoundedTransfer.Copy(input,output,6,6,null,CancellationToken.None,500);
                    Assert(output.Length==6,"Healthy slow download was interrupted");
                }
            });
            foreach(int declared in new[]{0,4,100})
                await TestAsync("oversized response cannot bypass transfer limit with declared length "+declared,async()=>{
                    using(var input=new MemoryStream(new byte[33])) using(var output=new MemoryStream()) {
                        bool rejected=false;try {await BoundedTransfer.Copy(input,output,32,declared,null,CancellationToken.None);}catch(InvalidDataException){rejected=true;}
                        Assert(rejected && output.Length<=32,"Oversized bytes were written");
                    }
                });
            await TestAsync("early EOF with a declared size is rejected",async()=>{
                using(var input=new MemoryStream(new byte[3])) using(var output=new MemoryStream()) {
                    bool rejected=false;try {await BoundedTransfer.Copy(input,output,100,10,null,CancellationToken.None);}catch(InvalidDataException){rejected=true;}
                    Assert(rejected,"Truncated download accepted");
                }
            });
            byte[] package=File.ReadAllBytes(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"AslainAssistant.Setup.exe"));
            var release=new AssistantRelease {Version=System.Diagnostics.FileVersionInfo.GetVersionInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"AslainAssistant.Setup.exe")).FileVersion,Size=package.Length,Sha256=Hash(package),Urls=new[]{AssistantUpdateClient.Repository+"/releases/download/test/primary.exe",AssistantUpdateClient.Repository+"/releases/download/test/backup.exe"}};
            foreach(string failure in new[]{"http","corrupt","truncated"})
                await TestAsync("assistant download recovers from "+failure+" at its first source",async()=>{
                    var visited=new List<string>();
                    var http=new FakeHttp {Respond=r=>{
                        visited.Add(r.RequestUri.AbsolutePath);
                        if(visited.Count==2) return new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(package)};
                        if(failure=="http") return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                        var content=new ByteArrayContent(failure=="corrupt" ? new byte[package.Length] : package.Take(2000).ToArray());
                        if(failure=="truncated") content.Headers.ContentLength=package.Length;
                        return new HttpResponseMessage(HttpStatusCode.OK){Content=content};
                    }};
                    using(var client=new AssistantUpdateClient(http,UpdateTrust.PublicKey)) {
                        var folder=Path.Combine(root,"transfer-fallback-"+failure);
                        string file=await client.Download(release,folder,null,CancellationToken.None);
                        AssistantUpdateClient.Verify(file,release);
                        Assert(visited.Count==2 && visited[1].EndsWith("backup.exe") && Directory.GetFiles(folder).Length==1,"Fallback or cleanup failed");
                    }
                });
            foreach(string target in new[]{"http://github.com/REREREGO/wows-aslain-assistant/file","https://github.com/foreign/repo/file","https://github.com.evil.example/file","missing","loop"})
                await TestAsync("assistant redirect rejects "+target,async()=>{
                    var http=new FakeHttp {Respond=r=>{
                        var response=new HttpResponseMessage(HttpStatusCode.Redirect);
                        if(target!="missing") response.Headers.Location=target=="loop" ? r.RequestUri : new Uri(target);
                        return response;
                    }};
                    using(var client=new AssistantUpdateClient(http,UpdateTrust.PublicKey)) {
                        var folder=Path.Combine(root,"redirect-"+Guid.NewGuid().ToString("N"));
                        bool rejected=false;try {await client.Download(release,folder,null,CancellationToken.None);}catch(InvalidDataException){rejected=true;}
                        Assert(rejected && Directory.GetFiles(folder).Length==0 && http.Calls==(target=="loop"?12:2),"Unsafe or endless redirect was followed");
                    }
                });
            await TestAsync("relative assistant download redirects stay in the allowed repository",async()=>{
                var http=new FakeHttp();http.Respond=r=>{
                    if(http.Calls==1){var redirect=new HttpResponseMessage(HttpStatusCode.Redirect);redirect.Headers.Location=new Uri("verified.exe",UriKind.Relative);return redirect;}
                    Assert(r.RequestUri.AbsolutePath=="/REREREGO/wows-aslain-assistant/releases/download/test/verified.exe","Relative redirect resolved incorrectly");
                    return new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(package)};
                };
                using(var client=new AssistantUpdateClient(http,UpdateTrust.PublicKey)) {
                    string file=await client.Download(release,Path.Combine(root,"relative-redirect"),null,CancellationToken.None);
                    AssistantUpdateClient.Verify(file,release);Assert(http.Calls==2,"Unexpected redirect requests");
                }
            });
        }
    }
}
