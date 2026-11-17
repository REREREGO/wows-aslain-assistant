using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace AslainAssistant
{
    public static class InstallOperations
    {
        public static bool IsRunning(string target,int sourcePid)
        {
            foreach(var process in Process.GetProcessesByName("AslainAssistant")) using(process)
                try { if(IsAssistant(process) && (process.Id==sourcePid || string.Equals(process.MainModule.FileName,target,StringComparison.OrdinalIgnoreCase))) return true; }
                catch(InvalidOperationException) { }
            return false;
        }
        static bool IsAssistant(Process process)
        {
            try
            {
                return process.SessionId==Process.GetCurrentProcess().SessionId &&
                    Path.GetFileName(process.MainModule.FileName).Equals("AslainAssistant.exe",StringComparison.OrdinalIgnoreCase) &&
                    FileVersionInfo.GetVersionInfo(process.MainModule.FileName).ProductName=="窝窝斯坐牢小助手";
            }
            catch { return false; }
        }
        public static void CloseAssistants(string installedExe,int sourcePid,Action<string> report,CancellationToken token)
        {
            var deadline=DateTime.UtcNow.AddMinutes(10);
            try
            {
            while(true)
            {
                token.ThrowIfCancellationRequested();
                var targets=new List<Process>();
                foreach(var process in Process.GetProcessesByName("AslainAssistant"))
                {
                    bool target=false;
                    try { target=IsAssistant(process) && (process.Id==sourcePid || string.Equals(process.MainModule.FileName,installedExe,StringComparison.OrdinalIgnoreCase)); }
                    catch(InvalidOperationException) { }
                    if(target) targets.Add(process); else process.Dispose();
                }
                if(targets.Count==0) return;
                bool busy=false;
                try
                {
                    foreach(var process in targets)
                    {
                        if(process.HasExited) continue;
                        if(InstanceControl.ModInstallerRunning()) { busy=true; continue; }
                        byte response=InstanceControl.Send(process.Id,InstanceControl.Exit);
                        if(response==InstanceControl.Busy) { busy=true; continue; }
                        if(response==InstanceControl.Unavailable)
                        {
                            // Version 1.3 predates cooperative shutdown. Migrate only the
                            // exact installed executable, in this session, with no mod installer.
                            var info=FileVersionInfo.GetVersionInfo(process.MainModule.FileName);
                            Version version;
                            if(Version.TryParse(info.FileVersion,out version) && version < new Version(1,4,0,0) &&
                                string.Equals(process.MainModule.FileName,installedExe,StringComparison.OrdinalIgnoreCase) && !InstanceControl.ModInstallerRunning())
                                process.Kill();
                        }
                        process.WaitForExit(500);
                    }
                }
                finally { foreach(var process in targets) process.Dispose(); }
                report(busy ? "阿斯兰正在安装模组，等待完成后再退出旧助手…" : "正在自动退出旧助手，等待正在进行的下载取消…");
                if(DateTime.UtcNow>deadline) throw new IOException("未能安全退出旧助手，更新已停止，原版本保持不变。请等待模组安装结束后重试。");
                if(token.WaitHandle.WaitOne(500)) token.ThrowIfCancellationRequested();
            }
            }
            catch
            {
                foreach(var process in Process.GetProcessesByName("AslainAssistant")) using(process)
                    try
                    {
                        if(IsAssistant(process) && (process.Id==sourcePid || string.Equals(process.MainModule.FileName,installedExe,StringComparison.OrdinalIgnoreCase))) InstanceControl.Send(process.Id,InstanceControl.Resume);
                    }
                    catch(InvalidOperationException) { }
                throw;
            }
        }
    }
    public sealed class FileInstallTransaction : IDisposable
    {
        readonly string directory;
        readonly List<string> staged=new List<string>();
        readonly List<Tuple<string,bool>> replaced=new List<Tuple<string,bool>>();
        public FileInstallTransaction(string root) { directory=Path.GetFullPath(root); Directory.CreateDirectory(directory); }
        string Target(string name)
        {
            if(string.IsNullOrEmpty(name) || name!=Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars())>=0 || name=="." || name=="..") throw new InvalidDataException("安装文件名无效。");
            return Path.Combine(directory,name);
        }
        public void Stage(string name,Stream content)
        {
            if(content==null) throw new InvalidDataException("安装包缺少文件："+name);
            string target=Target(name);
            staged.Add(target);
            using(var output=File.Create(target+".new")) content.CopyTo(output);
        }
        public void Commit(Action afterFiles)
        {
            try
            {
                foreach(string target in staged)
                {
                    bool existed=File.Exists(target);
                    if(existed) File.Replace(target+".new",target,target+".bak",true); else File.Move(target+".new",target);
                    replaced.Add(Tuple.Create(target,existed));
                }
                afterFiles();
            }
            catch
            {
                foreach(var entry in replaced.AsEnumerable().Reverse())
                {
                    if(entry.Item2) File.Copy(entry.Item1+".bak",entry.Item1,true);
                    else if(File.Exists(entry.Item1)) File.Delete(entry.Item1);
                }
                throw;
            }
        }
        public void Dispose() { foreach(string target in staged) if(File.Exists(target+".new")) File.Delete(target+".new"); }
    }
}
