using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AslainAssistant
{
    static class Setup
    {
        const string Title = "窝窝斯坐牢小助手", Exe = "AslainAssistant.exe";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WoWSAslainAssistant";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        static string InstallDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "WoWS-Aslain-Assistant"); } }
        static string LinkPath(Environment.SpecialFolder folder) { return Path.Combine(Environment.GetFolderPath(folder), Title + ".lnk"); }
        public static int Run(string[] args)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); UiTheme.Initialize(UiPreferences.Load(UiPreferences.DefaultRoot).Dark);
            try
            {
                if (args.Contains("--uninstall")) { Uninstall(); return 0; }
                int sourcePid=0;
                foreach(string argument in args) if(argument.StartsWith("--parent-pid=")) int.TryParse(argument.Substring(13),out sourcePid);
                if (args.Contains("--silent")) { Install(!args.Contains("--no-desktop"), !args.Contains("--no-launch"),args.Contains("--restart-background"),sourcePid,Log,CancellationToken.None); return 0; }
                using (var form = UiTheme.Dialog(Title + " · 安装",new Size(640,490)))
                {
                    form.StartPosition = FormStartPosition.CenterScreen;
                    var body = new AntdUI.Panel { Dock = DockStyle.Fill, Back = UiTheme.Background, Padding = new Padding(24), Radius = 0 };
                    UiTheme.Bind(body,()=>body.Back = UiTheme.Background);
                    var heading = new AntdUI.PageHeader { Text = Title, SubText = "阿斯兰更新助手 "+Assembly.GetExecutingAssembly().GetName().Version.ToString(3), ShowIcon = true, Icon = Brand.LoadLogo(), Height = 76, Font = new Font("Microsoft YaHei UI",18,FontStyle.Bold) };
                    var pathLabel = UiTheme.Label("安装到当前 Windows 用户"); pathLabel.Height = 28;
                    var path = new AntdUI.Input { Text = InstallDir, ReadOnly = true, PrefixSvg = "FolderOutlined", Height = 46, AccessibleName = "安装位置" };
                    var desktop = new AntdUI.Checkbox { Text = "创建桌面快捷方式", Checked = true, Height = 36 };
                    var launch = new AntdUI.Checkbox { Text = "安装完成后启动助手", Checked = true, Height = 36 };
                    var note = UiTheme.Label("安装时自动退出旧助手，保留游戏目录、插件预设和自动更新设置。",9); note.Height = 36; UiTheme.Secondary(note);
                    var install = UiTheme.Button("安装 / 更新",null,true,"DownloadOutlined"); install.Width = 160;
                    var cancellation=new CancellationTokenSource(); bool installing=false;
                    var cancel=UiTheme.Button("取消",()=>{if(installing)cancellation.Cancel();else form.Close();});
                    var actions = UiTheme.Actions(install,cancel);
                    body.Controls.Add(UiTheme.Column(heading,pathLabel,path,desktop,launch,note,actions));
                    form.Controls.Add(body); body.BringToFront();
                    form.FormClosing += (s,e)=>{if(installing){e.Cancel=true;cancellation.Cancel();}};
                    install.Click += async (s,e) =>
                    {
                        installing=true; install.Enabled=false; desktop.Enabled=false; launch.Enabled=false;
                        cancellation.Dispose(); cancellation=new CancellationTokenSource();
                        bool createDesktop=desktop.Checked, runAfter=launch.Checked;
                        var progress=new Progress<string>(text=>{note.Text=text;Log(text);});
                        try { await Task.Run(()=>Install(createDesktop,runAfter,false,sourcePid,text=>((IProgress<string>)progress).Report(text),cancellation.Token)); installing=false; form.Close(); }
                        catch (Exception ex) { Log(ex.ToString()); UiTheme.Notice(form,"安装未完成",ex is OperationCanceledException ? "安装已取消。" : ex.Message,AntdUI.TType.Warn); }
                        finally { installing=false; if(!form.IsDisposed) {install.Enabled=true; desktop.Enabled=true; launch.Enabled=true;} }
                    };
                    form.ResumeLayout(true); Application.Run(form);
                    cancellation.Dispose();
                }
                return 0;
            }
            catch (Exception ex) { Log(ex.ToString()); if (!args.Contains("--silent")) UiTheme.Notice(null,"安装未完成",ex.Message,AntdUI.TType.Error); return 1; }
        }
        static void Log(string text)
        {
            try { Directory.CreateDirectory(UiPreferences.DefaultRoot); File.AppendAllText(Path.Combine(UiPreferences.DefaultRoot,"setup.log"),DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")+" "+text+Environment.NewLine); } catch { }
        }
        static void Install(bool desktop,bool launch,bool background,int sourcePid,Action<string> report,CancellationToken token)
        {
            string target = Path.Combine(InstallDir, Exe);
            bool hadRunning=InstallOperations.IsRunning(target,sourcePid), owned=false;
            using(var mutex=new Mutex(false,InstanceControl.SetupMutex))
            {
                try
                {
                    try { owned=mutex.WaitOne(0); } catch(AbandonedMutexException) { owned=true; }
                    if(!owned) throw new IOException("另一份助手安装程序正在更新，请等待它完成。");
                    using(var files=new FileInstallTransaction(InstallDir))
                    {
                        foreach(var entry in new[] { Tuple.Create("AntdUIPayload","AntdUI.dll"),Tuple.Create("AntdUILicense","AntdUI.LICENSE.txt"),Tuple.Create("AppPayload",Exe) })
                            using(var input=Assembly.GetExecutingAssembly().GetManifestResourceStream(entry.Item1)) files.Stage(entry.Item2,input);
                        using(var input=File.OpenRead(Application.ExecutablePath)) files.Stage("Uninstall.exe",input);
                        report("正在准备升级并自动退出旧助手…");
                        InstallOperations.CloseAssistants(target,sourcePid,report,token);
                        report("正在更新文件，失败时会恢复原版本…");
                        files.Commit(()=>RegisterInstall(desktop));
                    }
                    Log("安装完成 · "+Assembly.GetExecutingAssembly().GetName().Version);
                }
                catch
                {
                    if(owned) { mutex.ReleaseMutex(); owned=false; }
                    if(hadRunning && File.Exists(target) && !InstallOperations.IsRunning(target,sourcePid)) try { Process.Start(new ProcessStartInfo(target,"--background"){UseShellExecute=true}); } catch { }
                    throw;
                }
                finally { if(owned) mutex.ReleaseMutex(); }
            }
            if (launch) Process.Start(new ProcessStartInfo(target,background ? "--background" : "") { UseShellExecute = true });
        }
        static void RegisterInstall(bool desktop)
        {
            string target=Path.Combine(InstallDir,Exe);
            string uninstaller = Path.Combine(InstallDir,"Uninstall.exe");
            CreateShortcut(LinkPath(Environment.SpecialFolder.Programs),target);
            if (desktop) CreateShortcut(LinkPath(Environment.SpecialFolder.DesktopDirectory),target);
            using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName",Title); key.SetValue("DisplayVersion",Assembly.GetExecutingAssembly().GetName().Version.ToString(3)); key.SetValue("DisplayIcon",target + ",0");
                key.SetValue("InstallLocation",InstallDir); key.SetValue("UninstallString","\"" + uninstaller + "\" --uninstall");
                key.SetValue("NoModify",1); key.SetValue("NoRepair",1);
            }
            // Preserve the existing preference and migrate its target when updating a portable copy.
            using (var run = Registry.CurrentUser.OpenSubKey(RunKey,true))
                if (run != null && run.GetValue("WoWSAslainAssistant") != null) run.SetValue("WoWSAslainAssistant","\"" + target + "\" --background");
        }
        static void CreateShortcut(string path, string target)
        {
            object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            object link = null;
            try
            {
                link = shell.GetType().InvokeMember("CreateShortcut",BindingFlags.InvokeMethod,null,shell,new object[] { path });
                Action<string,object> set = (name,value) => link.GetType().InvokeMember(name,BindingFlags.SetProperty,null,link,new[] { value });
                set("TargetPath",target); set("WorkingDirectory",Path.GetDirectoryName(target)); set("IconLocation",target + ",0"); set("Description",Title + " · 阿斯兰自动更新");
                link.GetType().InvokeMember("Save",BindingFlags.InvokeMethod,null,link,null);
            }
            finally { if (link != null) Marshal.FinalReleaseComObject(link); Marshal.FinalReleaseComObject(shell); }
        }
        static void Uninstall()
        {
            if (AntdUI.Modal.open(new AntdUI.Modal.Config((Form)null,"卸载助手","游戏、模组和已保存的客户端配置会保留。",AntdUI.TType.Warn) { OkText = "卸载", CancelText = "取消" }) != DialogResult.OK) return;
            InstallOperations.CloseAssistants(Path.Combine(InstallDir,Exe),0,Log,CancellationToken.None);
            string target = Path.Combine(InstallDir,Exe);
            using (var run = Registry.CurrentUser.OpenSubKey(RunKey,true))
                if (run != null && string.Equals(run.GetValue("WoWSAslainAssistant") as string,"\"" + target + "\" --background",StringComparison.OrdinalIgnoreCase)) run.DeleteValue("WoWSAslainAssistant",false);
            foreach (var folder in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.DesktopDirectory })
            {
                string path = LinkPath(folder);
                if (File.Exists(path)) File.Delete(path);
            }
            foreach (string name in new[] { Exe,"AntdUI.dll","AntdUI.LICENSE.txt" })
                foreach (string suffix in new[] { "",".bak",".new" })
                {
                    string path = Path.Combine(InstallDir,name + suffix);
                    if (File.Exists(path)) File.Delete(path);
                }
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey,false);
            // The running uninstaller cannot delete itself; retain it rather than invoking a shell cleanup.
            UiTheme.Notice(null,"助手已卸载","配置已保留，卸载程序本身可在退出后手动删除。");
        }
    }
}
