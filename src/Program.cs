using System;
using System.IO;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Forms;

namespace AslainAssistant
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            #if UI_PREVIEW
            Storage.Root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "preview-profile");
            #endif
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "--profile") Storage.Root = Path.GetFullPath(args[i + 1]);
            using(var setup=new Mutex(false,InstanceControl.SetupMutex))
            {
                bool ready=false;
                try { ready=setup.WaitOne(0); }
                catch(AbandonedMutexException) { ready=true; }
                // Do not keep a second executable mapped while the installer replaces it.
                // The installer restores the assistant after releasing this mutex.
                if(!ready) return;
                setup.ReleaseMutex();
            }
            bool created;
            using (var mutex = new Mutex(true, InstanceControl.ApplicationMutex, out created))
            {
                bool reveal=Array.IndexOf(args,"--background")<0;
                if (!created) { if(reveal) InstanceControl.ActivateExisting(true); return; }
                // Only older releases lack this mutex. New launchers must not mistake
                // one another for the running instance during simultaneous startup.
                if (InstanceControl.ActivateExisting(reveal, true)) return;
                RunDashboard(Array.IndexOf(args, "--background") >= 0);
            }
        }
        // A duplicate launch must not load/JIT the component library before sending
        // its activation request to the already running UI.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void RunDashboard(bool background)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            UiTheme.Initialize(UiPreferences.Load(Storage.Root).Dark);
            Application.Run(new Dashboard(background,true));
        }
    }
}
