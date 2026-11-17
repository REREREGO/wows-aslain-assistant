using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using AslainAssistant;

// Render this application's own controls with isolated demo data, without desktop
// capture or input injection. Do not run alongside an existing assistant instance.
class RenderReadme
{
    static readonly BindingFlags Flags=BindingFlags.Instance | BindingFlags.NonPublic;
    static object Field(Dashboard form,string name) { return typeof(Dashboard).GetField(name,Flags).GetValue(form); }
    static void Save(Dashboard form,string name)
    {
        form.PerformLayout(); Application.DoEvents(); form.Refresh();
        // Render client controls directly: WM_PRINT adds a legacy Windows caption
        // over the AntdUI header when printing the whole custom-chrome form.
        using(var image=new Bitmap(form.ClientSize.Width,form.ClientSize.Height))
        {
            using(var graphics=Graphics.FromImage(image)) graphics.Clear(form.BackColor);
            for(int i=form.Controls.Count-1;i>=0;i--)
            {
                var control=form.Controls[i];
                if(control.Visible) control.DrawToBitmap(image,control.Bounds);
            }
            image.Save(Path.Combine("docs","images",name),ImageFormat.Png);
        }
    }
    [STAThread] static int Main()
    {
        using(var mutex=new Mutex(false,InstanceControl.ApplicationMutex))
        {
            if(!mutex.WaitOne(0)) { Console.Error.WriteLine("已有助手正在运行，请先退出，再生成文档图片。"); return 1; }
            try
            {
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); UiTheme.Initialize(); AntdUI.Config.Animation=false;
                Storage.Root=Path.GetFullPath("artifacts/docs-profile");
                Directory.CreateDirectory("docs/images");
                using(var form=new Dashboard(false))
                using(var ready=new System.Windows.Forms.Timer {Interval=100})
                {
                    form.Opacity=0; form.ShowInTaskbar=false;
                    int ticks=0; Exception failure=null;
                    ready.Tick+=(s,e)=>
                    {
                        if(!(bool)Field(form,"booted") && ++ticks<100) return;
                        ready.Stop();
                        try
                        {
                            if(!(bool)Field(form,"booted")) throw new Exception("演示窗口初始化超时。");
                            var selected=(UpdateEngine)Field(form,"selected");
                            selected.Release=new Release {Version=PackVersion.Parse("15.10.0_08")};
                            selected.Status="已是最新版本"; selected.Detail="演示：选择好插件后，让助手按设定间隔检查更新。";
                            var pages=(AntdUI.Tabs)Field(form,"pages"); var theme=(AntdUI.Segmented)Field(form,"themeMode");
                            theme.SelectIndex=0; pages.SelectedIndex=0;
                            typeof(Dashboard).GetMethod("RefreshUi",Flags).Invoke(form,null);
                            Save(form,"dashboard-light.png");
                            theme.SelectIndex=1; Save(form,"dashboard-dark.png");
                            theme.SelectIndex=0; pages.SelectedIndex=2; Save(form,"presets.png");
                        }
                        catch(Exception ex) { failure=ex; }
                        finally { typeof(Dashboard).GetField("quitting",Flags).SetValue(form,true); form.Close(); }
                    };
                    form.Shown+=(s,e)=>ready.Start(); Application.Run(form);
                    if(failure!=null) throw failure;
                }
                Console.WriteLine("Rendered three README images from isolated demo controls."); return 0;
            }
            finally { mutex.ReleaseMutex(); }
        }
    }
}
