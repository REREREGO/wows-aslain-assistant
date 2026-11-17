using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using AslainAssistant;

// Explicit integration test: requires no running assistant; all child processes
// use inactive, isolated profiles and start hidden. No game installer is run.
class InstanceTests
{
    static int Main()
    {
        if(Process.GetProcessesByName("AslainAssistant").Length!=0)
        {
            Console.Error.WriteLine("Please exit the assistant before running this isolated integration test.");
            return 2;
        }
        var children=new List<Process>();
        string root=Path.GetFullPath(Path.Combine("artifacts","instance-tests-"+Guid.NewGuid().ToString("N")));
        string executable=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"AslainAssistant.exe");
        try
        {
            for(int i=0;i<8;i++)
            {
                Storage.Root=Path.Combine(root,i.ToString());
                Storage.Save(new Settings {GamePath=Path.Combine(Storage.Root,"absent-game"),AutoUpdate=false});
            }
            for(int i=0;i<8;i++)
                children.Add(Process.Start(new ProcessStartInfo(executable,"--background --profile \""+Path.Combine(root,i.ToString())+"\"") {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden}));
            Process survivor=null;
            for(int i=0;i<40;i++)
            {
                Thread.Sleep(250);
                var living=children.Where(p=>!p.HasExited).ToArray();
                if(living.Length==1 && InstanceControl.Send(living[0].Id,InstanceControl.Resume)==InstanceControl.Ready) {survivor=living[0];break;}
            }
            if(survivor==null) throw new Exception("Simultaneous startup did not settle on exactly one responsive assistant.");
            Thread.Sleep(1500);
            if(children.Count(p=>!p.HasExited)!=1) throw new Exception("The sole assistant exited after a startup race.");
            Console.WriteLine("PASS eight simultaneous launches with different profiles retain one responsive assistant");
            using(var duplicate=Process.Start(new ProcessStartInfo(executable,"--background --profile \""+Path.Combine(root,"unused")+"\"") {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden}))
            {
                children.Add(duplicate);
                if(!duplicate.WaitForExit(5000) || duplicate.ExitCode!=0 || survivor.HasExited) throw new Exception("Repeated launch did not exit cleanly.");
                children.Remove(duplicate);
            }
            if(Directory.Exists(Path.Combine(root,"unused"))) throw new Exception("Duplicate launch changed the unused profile.");
            Console.WriteLine("PASS repeated background launch leaves the existing instance and alternate profile unchanged");
            if(InstanceControl.Send(survivor.Id,InstanceControl.Exit)!=InstanceControl.Ready || !survivor.WaitForExit(5000)) throw new Exception("Cooperative shutdown did not terminate the assistant.");
            Console.WriteLine("PASS the real assistant acknowledges and completes cooperative shutdown");
            return 0;
        }
        catch(Exception ex) {Console.Error.WriteLine(ex.Message);return 1;}
        finally
        {
            foreach(var child in children) using(child)
                try {if(!child.HasExited) {InstanceControl.Send(child.Id,InstanceControl.Exit);if(!child.WaitForExit(5000)) child.Kill();}} catch(InvalidOperationException) { }
        }
    }
}
