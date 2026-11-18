using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;

namespace AslainAssistant
{
    public static class InstanceControl
    {
        public const byte Exit = 1, Activate = 2, Resume = 3, Ready = 1, Busy = 2, Waiting = 3, Unavailable = 0;
        public static string PipeName(int pid) { return "WoWSAslainAssistant-Control-" + pid; }
        public static string SetupMutex { get { return "Local\\WoWSAslainAssistant-Setup-" + WindowsIdentity.GetCurrent().User.Value; } }
        public static string ApplicationMutex { get { return "Local\\WoWSAslainAssistant-App-" + WindowsIdentity.GetCurrent().User.Value; } }
        public static string ActivationPipeName { get { return "WoWSAslainAssistant-User-" + WindowsIdentity.GetCurrent().User.Value; } }
        delegate bool WindowCallback(IntPtr window,IntPtr state);
        [DllImport("user32.dll")] static extern bool EnumWindows(WindowCallback callback,IntPtr state);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint processId);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window,StringBuilder text,int maximum);
        [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr window,int command);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        public static bool ActivateExisting(bool reveal, bool legacyOnly = false)
        {
            // The current release can be reached without enumerating processes or
            // reading executable version resources. Older releases use the PID fallback.
            if(!legacyOnly && reveal && Send(ActivationPipeName,Activate,100)==Ready) return true;
            int ownId=Process.GetCurrentProcess().Id;
            foreach(var process in Process.GetProcessesByName("AslainAssistant")) using(process)
            {
                try
                {
                    if(process.Id==ownId || process.SessionId!=Process.GetCurrentProcess().SessionId) continue;
                    var info=FileVersionInfo.GetVersionInfo(process.MainModule.FileName);
                    if(info.ProductName!="窝窝斯坐牢小助手") continue;
                    bool legacy=info.FileMajorPart<1 || (info.FileMajorPart==1 && info.FileMinorPart<4);
                    if(legacyOnly && !legacy) continue;
                    if(!legacyOnly && !reveal) return true;
                    if(reveal && Send(process.Id,Activate)==Ready) return true;
                    // Another simultaneous launcher may not own the mutex or a window.
                    if(!legacy) continue;
                    // Version numbering may restart for local builds. A process
                    // without a window can be another launcher exiting at the mutex,
                    // so only an actual legacy window counts as a running instance.
                    bool foundWindow=false;
                    EnumWindows((window,state)=>
                    {
                        uint pid; GetWindowThreadProcessId(window,out pid); if(pid!=process.Id) return true;
                        var title=new StringBuilder(256); GetWindowText(window,title,title.Capacity);
                        if(!title.ToString().StartsWith("窝窝斯坐牢小助手")) return true;
                        foundWindow=true;
                        if(reveal) {ShowWindowAsync(window,9); ShowWindowAsync(window,5); SetForegroundWindow(window);}
                        return false;
                    },IntPtr.Zero);
                    if(foundWindow) return true;
                }
                catch(InvalidOperationException) { }
                catch(System.ComponentModel.Win32Exception) { }
            }
            return false;
        }
        public static string ProfileMutex(string root)
        {
            using (var sha = SHA256.Create()) return "Local\\WoWSAslainAssistant-" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant()))).Replace("-", "").Substring(0,24);
        }
        public static bool ModInstallerRunning()
        {
            foreach (var process in Process.GetProcesses()) using (process)
                try { if (process.ProcessName.StartsWith("Aslains_WoWs_Modpack_Installer",StringComparison.OrdinalIgnoreCase)) return true; }
                catch (InvalidOperationException) { }
            return false;
        }
        public static byte Send(int pid,byte command)
        {
            return Send(PipeName(pid),command,500);
        }
        public static byte Send(string name,byte command,int connectTimeout=500)
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous))
                {
                    pipe.Connect(connectTimeout); pipe.WriteByte(command); pipe.Flush();
                    var result = new byte[1]; var read = pipe.ReadAsync(result,0,1);
                    return read.Wait(2500) && read.Result == 1 ? result[0] : Unavailable;
                }
            }
            catch (IOException) { return Unavailable; }
            catch (TimeoutException) { return Unavailable; }
            catch (UnauthorizedAccessException) { return Unavailable; }
            catch (AggregateException) { return Unavailable; }
        }
    }
    public sealed class InstanceServer : IDisposable
    {
        readonly Func<byte,byte> command;
        readonly Action exit;
        readonly string name;
        volatile bool stopped;
        NamedPipeServerStream active;
        public InstanceServer(Func<byte,byte> onCommand,Action onExit,string pipeName=null)
        {
            command = onCommand; exit = onExit;
            name = pipeName ?? InstanceControl.PipeName(Process.GetCurrentProcess().Id);
            Task.Run((Func<Task>)Listen);
        }
        async Task Listen()
        {
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true,false);
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User,PipeAccessRights.FullControl,AccessControlType.Allow));
            while (!stopped)
            {
                try
                {
                    using (var pipe = new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,512,512,security))
                    {
                        active = pipe; if (stopped) return;
                        await pipe.WaitForConnectionAsync().ConfigureAwait(false);
                        var request = new byte[1];
                        var read = pipe.ReadAsync(request,0,1);
                        if (await Task.WhenAny(read,Task.Delay(3000)).ConfigureAwait(false) != read || await read.ConfigureAwait(false) != 1) continue;
                        byte response = command(request[0]);
                        pipe.WriteByte(response); pipe.Flush();
                        if (request[0] == InstanceControl.Exit && response == InstanceControl.Ready) { exit(); return; }
                    }
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
                catch (UnauthorizedAccessException) { return; }
                finally { active = null; }
            }
        }
        public void Dispose() { stopped = true; var pipe = active; if (pipe != null) pipe.Dispose(); }
    }
}
