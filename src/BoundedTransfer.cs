using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AslainAssistant
{
    internal static class BoundedTransfer
    {
        internal static async Task Copy(Stream source,Stream destination,long maximum,long expected,IProgress<Transfer> progress,CancellationToken token,int idleMilliseconds=30000)
        {
            byte[] buffer=new byte[81920]; long total=0;
            var watch=Stopwatch.StartNew();
            while(true)
            {
                token.ThrowIfCancellationRequested();
                int read;
                using(var idle=CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    // Framework HTTP streams can ignore cancellation while a read is pending.
                    var pending=source.ReadAsync(buffer,0,buffer.Length,token);
                    if(await Task.WhenAny(pending,Task.Delay(idleMilliseconds,idle.Token))!=pending)
                    {
                        source.Dispose();
                        ObserveFailure(pending);
                        token.ThrowIfCancellationRequested();
                        throw new OperationCanceledException("下载长时间没有收到数据，请重试。");
                    }
                    idle.Cancel(); read=await pending;
                }
                token.ThrowIfCancellationRequested();
                if(read==0) break;
                total+=read;
                if(total>maximum) throw new InvalidDataException("服务器返回文件超过大小限制。");
                await destination.WriteAsync(buffer,0,read,token);
                if(progress!=null && watch.ElapsedMilliseconds>200) {progress.Report(new Transfer {Received=total,Total=expected});watch.Restart();}
            }
            if(expected>0 && total!=expected) throw new InvalidDataException("下载不完整，请重试。");
            if(progress!=null) progress.Report(new Transfer {Received=total,Total=expected});
        }
        static void ObserveFailure(Task pending)
        {
            pending.ContinueWith(t=>{var ignored=t.Exception;},TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}
