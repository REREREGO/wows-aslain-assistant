using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AslainAssistant
{
    public sealed partial class Dashboard
    {
        void ConfigureMirrors()
        {
            using (var dialog=UiTheme.Dialog("国内版本源",new Size(740,460)))
            {
                var input=new AntdUI.Input {Multiline=true,Height=140,Text=string.Join(Environment.NewLine,MirrorSettings.Load().Feeds),AccessibleName="国内版本清单地址，每行一个"};
                UiTheme.Bind(input,()=>input.BackColor=UiTheme.Surface);
                var explanation=TextLabel("填写维护者提供的版本清单地址，每行一个，最多三个。留空时只查询官网。所有清单均须通过助手内置公钥验证。",68);
                var actions=UiTheme.Actions(UiTheme.Button("保存",()=>Guard(()=>{
                    new MirrorSettings {Feeds=input.Text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries).Select(x=>x.Trim()).ToArray()}.Save();
                    AddLog("已保存国内版本源，下一次检查生效。"); dialog.Close();
                }),true),UiTheme.Button("关闭",()=>dialog.Close()));
                var body=UiTheme.Column(explanation,input,actions); body.Padding=new Padding(20); dialog.Controls.Add(body); body.BringToFront(); dialog.ResumeLayout(true); dialog.ShowDialog(this);
            }
        }
        async Task ImportMirrorInstaller()
        {
            try
            {
                var engine=selected;
                if (engine == null || engines.Any(x=>x.Busy)) return;
                if (engine.Release == null) throw new InvalidDataException("请先点「仅检查版本」，取得安装包的可信校验信息。");
                var release=engine.Release;
                using (var dialog=new OpenFileDialog {Title="选择阿斯兰 "+release.Version+" 安装包",Filter="Windows 安装包 (*.exe)|*.exe",Multiselect=false})
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    string source=dialog.FileName;
                    await Task.Run(()=>OfficialClient.ImportInstaller(source,release,Path.Combine(Storage.Root,"downloads")));
                    AddLog("已校验并缓存阿斯兰 "+release.Version+"。");
                    UiTheme.Notice(this,"安装包已就绪","回到更新概览，点击「使用历史配置更新」；若要应用导入预设，请在插件配置页点击「使用该配置更新」。",AntdUI.TType.Success);
                }
            }
            catch (Exception ex) { if (!IsDisposed) UiTheme.Notice(this,"安装包未导入",ex.Message,AntdUI.TType.Warn); }
        }
    }
}
