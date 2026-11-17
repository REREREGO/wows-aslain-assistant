using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
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
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            #if UI_PREVIEW
            Storage.Root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "preview-profile");
            #endif
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "--profile") Storage.Root = Path.GetFullPath(args[i + 1]);
            UiTheme.Initialize(UiPreferences.Load(Storage.Root).Dark);
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
                if (!created) { InstanceControl.ActivateExisting(reveal); return; }
                // Only older releases lack this mutex. New launchers must not mistake
                // one another for the running instance during simultaneous startup.
                if (InstanceControl.ActivateExisting(reveal, true)) return;
                Application.Run(new Dashboard(Array.IndexOf(args, "--background") >= 0));
            }
        }
    }
}
