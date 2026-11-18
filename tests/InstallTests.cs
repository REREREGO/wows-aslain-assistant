using System;
using System.IO;
using System.Text;

namespace AslainAssistant
{
    partial class Tests
    {
        static void RunInstallTests()
        {
            foreach(string name in new[]{"../outside.exe","sub\\outside.exe","C:\\outside.exe","..","app.exe:stream"})
                Test("installer refuses a non-leaf target name: "+name,()=>{
                    string folder=Path.Combine(root,"install-path-"+Guid.NewGuid().ToString("N"));
                    using(var transaction=new FileInstallTransaction(folder)) using(var bytes=new MemoryStream(new byte[2])) {
                        bool rejected=false;try {transaction.Stage(name,bytes);}catch(InvalidDataException){rejected=true;}
                        Assert(rejected && Directory.GetFiles(folder).Length==0,"Invalid path created an installation file");
                    }
                });
            Test("abandoning staged installation preserves old files and removes staging files",()=>{
                string folder=Path.Combine(root,"install-abandon");Directory.CreateDirectory(folder);
                string target=Path.Combine(folder,"app.exe");File.WriteAllText(target,"old");
                using(var transaction=new FileInstallTransaction(folder)) using(var bytes=new MemoryStream(Encoding.UTF8.GetBytes("new"))) transaction.Stage("app.exe",bytes);
                Assert(File.ReadAllText(target)=="old" && Directory.GetFiles(folder).Length==1,"Uncommitted bytes reached the install");
            });
            Test("locked dependency during commit rolls earlier replacements back",()=>{
                string folder=Path.Combine(root,"install-locked");Directory.CreateDirectory(folder);
                string app=Path.Combine(folder,"app.exe"),library=Path.Combine(folder,"library.dll");
                File.WriteAllText(app,"old-app");File.WriteAllText(library,"old-library");
                bool callback=false,rejected=false;
                using(var transaction=new FileInstallTransaction(folder)) {
                    using(var input=new MemoryStream(Encoding.UTF8.GetBytes("new-app"))) transaction.Stage("app.exe",input);
                    using(var input=new MemoryStream(Encoding.UTF8.GetBytes("new-library"))) transaction.Stage("library.dll",input);
                    using(var locked=new FileStream(library,FileMode.Open,FileAccess.Read,FileShare.Read))
                        try {transaction.Commit(()=>callback=true);}catch(IOException){rejected=true;}
                }
                Assert(rejected && !callback && File.ReadAllText(app)=="old-app" && File.ReadAllText(library)=="old-library" && Directory.GetFiles(folder,"*.new").Length==0,"Partially committed update was not rolled back");
            });
            Test("successful installation replaces the complete file set and retains a prior backup",()=>{
                string folder=Path.Combine(root,"install-success");Directory.CreateDirectory(folder);
                string app=Path.Combine(folder,"app.exe"),library=Path.Combine(folder,"library.dll");
                File.WriteAllText(app,"old-app");int callbacks=0;
                using(var transaction=new FileInstallTransaction(folder)) {
                    using(var input=new MemoryStream(Encoding.UTF8.GetBytes("new-app"))) transaction.Stage("app.exe",input);
                    using(var input=new MemoryStream(Encoding.UTF8.GetBytes("new-library"))) transaction.Stage("library.dll",input);
                    transaction.Commit(()=>{callbacks++;Assert(File.ReadAllText(app)=="new-app" && File.ReadAllText(library)=="new-library","Registration ran before files were committed");});
                }
                Assert(callbacks==1 && File.ReadAllText(app+".bak")=="old-app" && Directory.GetFiles(folder,"*.new").Length==0,"Successful transaction left incomplete state");
            });
        }
    }
}
