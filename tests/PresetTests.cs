using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace AslainAssistant
{
    partial class Tests
    {
        static FakeHttp PresetHttp()
        {
            byte[] bytes = Exe();
            return new FakeHttp { Respond = request => new HttpResponseMessage(HttpStatusCode.OK) {
                Content = request.RequestUri.Host == "filez.aslain.com" ? (HttpContent)new ByteArrayContent(bytes) : new StringContent(Html(Hash(bytes))) } };
        }
        static Settings Imported(string id, string extension = ".inf")
        {
            var target = Config(id); target.GamePath = Path.Combine(root,id); target.AutoUpdate = true;
            var donor = Config("friend"); donor.GamePath = @"Z:\FriendsGame";
            donor.Preset.Components = @"ship_icons,ship_icons\contouricons,ship_icons\contouricons\aslains,ship_icons\contouricons\aslains\v1";
            string file = Path.Combine(root,id + extension);
            string text = extension == ".json" ? Storage.Export(donor) : extension == ".inf" ? donor.Preset.ToInf(donor.GamePath) :
                "Windows Registry Editor Version 5.00\r\n[HKEY_LOCAL_MACHINE\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Aslains_WoWs_Modpack_Installer_is1]\r\n" +
                "\"Inno Setup: Selected Components\"=\"" + donor.Preset.Components.Replace("\\","\\\\") + "\"\r\n\"Inno Setup: Selected Tasks\"=\"install_clean\"\r\n\"Inno Setup: Language\"=\"cn\"\r\n";
            File.WriteAllText(file,text,extension == ".json" ? Encoding.UTF8 : Encoding.Unicode);
            var settings = Storage.Import(file,target); Storage.Save(settings);
            settings = Storage.LoadAll().Single(x=>x.Id == id);
            Assert(settings.PendingPreset && !settings.AutoUpdate && settings.GamePath == target.GamePath,"Import/restart lost the pending selection or replaced the target");
            return settings;
        }
        static GameState Current(Settings settings)
        {
            var game = Local("15.10.0","15.10.0_08"); game.Path = settings.GamePath; return game;
        }
        static UpdateEngine PresetEngine(Settings settings,FakeHttp http,FakeInstaller installer,GameState game)
        {
            var engine = new UpdateEngine(settings,new OfficialClient(http),installer);
            engine.InspectGame = path => game; engine.IsGameRunning = ()=>false; engine.NetworkAvailable = ()=>true;
            engine.IsInstallerRunning = ()=>false;
            return engine;
        }
        static async Task RunPresetTests()
        {
            string originalRoot=Storage.Root;
            Storage.Root=Path.Combine(root,"preset-data");
            try
            {
            foreach(string extension in new[]{".json",".inf",".reg"})
                await TestAsync("imported " + extension + " survives restart and installs on the current version exactly once",async ()=>
                {
                    var settings = Imported("share-" + extension.TrimStart('.'),extension);
                    string expected = settings.Preset.Components;
                    var installer = new FakeInstaller();
                    using(var engine = PresetEngine(settings,PresetHttp(),installer,Current(settings)))
                    {
                        await engine.Run(false,false,true);
                        Assert(engine.Status == "配置待应用" && settings.PendingPreset && installer.Calls == 0,"Check-only applied or forgot the pending configuration");
                        await engine.Run(true,false,true);
                        Assert(installer.Calls == 1 && !installer.WasInteractive && Preset.FromInf(installer.InputInf).Components == expected,"Latest-version install did not receive the imported choices");
                        Assert(installer.InputInf.Contains("Dir=" + settings.GamePath) && !installer.InputInf.Contains("FriendsGame"),"Installation targeted the sender's directory");
                        Assert(!settings.PendingPreset && !Storage.LoadAll().Single(x=>x.Id==settings.Id).PendingPreset,"Successful installation did not clear persisted pending state");
                        await engine.Run(true,false,true);
                        Assert(installer.Calls == 1 && engine.Status == "已是最新版本","Applied preset installed again");
                    }
                });
            await TestAsync("failed and partial imported installs retain pending choices and allow manual retry",async ()=>
            {
                var settings = Imported("share-retry"); string expected = settings.Preset.Components;
                var installer = new FakeInstaller {Code=2,LogText="Installation cancelled.\nLog closed."};
                using(var engine = PresetEngine(settings,PresetHttp(),installer,Current(settings)))
                {
                    await engine.Run(true,false,true);
                    Assert(settings.PendingPreset && settings.Preset.Components == expected && installer.Calls==1,"Failure discarded pending configuration");
                    await engine.Run(true,false,false);
                    Assert(installer.Calls==1,"Failed import retried unattended");
                    installer.Code=0; installer.LogText="[ExtractDL] Extracting flags_Fanhexie_15.10.0.7z -- FAILED: missing\nCurrent step: ssDone\nLog closed.";
                    await engine.Run(true,false,true);
                    Assert(settings.PendingPreset && settings.IncompleteRelease!="" && installer.Calls==2,"Partial installation marked the imported preset applied");
                    installer.LogText="Current step: ssDone\nLog closed.";
                    await engine.Run(true,false,true);
                    Assert(!settings.PendingPreset && installer.Calls==3 && settings.Preset.Components==expected,"Manual retry failed to apply original choices");
                }
            });
            Test("old helper and external installation logs cannot replace pending imports",()=>
            {
                var settings = Imported("share-old-log"); string expected = settings.Preset.Components;
                string run = Path.Combine(Storage.Root,"runs","20261118-050000-abcdef"); Directory.CreateDirectory(run);
                File.WriteAllText(Path.Combine(run,"selection.inf"),"[Setup]\nDir="+settings.GamePath+"\nComponents=camera\nLang=cn\nTasks=install_clean");
                string log="Original Setup EXE: Aslains_WoWs_Modpack_Installer_v.15.10.0_08.exe\nDefaultDirName = "+settings.GamePath+"\nCurrent step: ssDone\nLog closed.";
                File.WriteAllText(Path.Combine(run,"installer.log"),log);
                string external=Path.Combine(settings.GamePath,"Aslain_Modpack","Logs"); Directory.CreateDirectory(external);
                File.WriteAllText(Path.Combine(external,"Aslains_Installer.log"),log);
                using(var engine = PresetEngine(settings,PresetHttp(),new FakeInstaller(),Current(settings))) engine.RefreshLocal();
                Assert(settings.PendingPreset && settings.Preset.Components==expected,"Old logs replaced imported choices");
                settings.PendingRun=Path.GetFileName(run); settings.FailedRelease="15.10.0_08";
                File.WriteAllText(Path.Combine(run,"selection.inf"),settings.Preset.ToInf(settings.GamePath));
                using(var engine = PresetEngine(settings,PresetHttp(),new FakeInstaller(),Current(settings))) engine.RefreshLocal();
                Assert(!settings.PendingPreset && settings.PendingRun=="" && settings.Preset.Components==expected,"Verified interrupted run was not recovered");
            });
            await TestAsync("pending imports still respect incompatible and newer local versions",async ()=>
            {
                foreach(bool incompatible in new[]{true,false})
                {
                    var settings=Imported(incompatible ? "share-mismatch" : "share-newer"); var game=Current(settings);
                    if(incompatible) game.GameVersion="15.9.0"; else game.Installed=PackVersion.Parse("15.10.0_09");
                    var installer=new FakeInstaller();
                    using(var engine=PresetEngine(settings,PresetHttp(),installer,game))
                    {
                        await engine.Run(true,false,true);
                        Assert(installer.Calls==0 && settings.PendingPreset,"Pending choices bypassed version protection");
                    }
                }
            });
            await TestAsync("manual install and reinstall notify before any network or disk work while game runs",async ()=>
            {
                foreach(bool interactive in new[]{false,true})
                {
                    var settings=Imported(interactive ? "running-reinstall" : "running-install");
                    var http=PresetHttp(); var installer=new FakeInstaller(); int notices=0;
                    using(var engine=PresetEngine(settings,http,installer,Current(settings)))
                    {
                        engine.IsGameRunning=()=>true;
                        engine.InspectGame=path=>{throw new Exception("Should block before local inspection");};
                        engine.InstallationBlocked+=message=>{notices++; Assert(message.Contains("关闭游戏") && message.Contains("重试"),"Missing action in popup");};
                        await engine.Run(true,interactive,true);
                        Assert(notices==1 && http.Calls==0 && installer.Calls==0 && settings.PendingPreset && !engine.Busy,"Manual game guard failed");
                    }
                }
            });
            await TestAsync("game opened during download blocks launch and preserves pending choices",async ()=>
            {
                var settings=Imported("running-late"); var installer=new FakeInstaller(); int probes=0,notices=0;
                using(var engine=PresetEngine(settings,PresetHttp(),installer,Current(settings)))
                {
                    engine.IsGameRunning=()=>++probes>=3; engine.InstallationBlocked+=message=>notices++;
                    await engine.Run(true,false,true);
                    Assert(probes==3 && notices==1 && installer.Calls==0 && settings.PendingPreset,"Final game guard failed");
                    engine.IsGameRunning=()=>false;
                    await engine.Run(true,false,true);
                    Assert(installer.Calls==1 && !settings.PendingPreset,"Cannot retry after the game exits");
                }
            });
            await TestAsync("background installs wait without popups and check-only never requests game closure",async ()=>
            {
                var settings=Imported("running-background"); var installer=new FakeInstaller(); int notices=0;
                using(var engine=PresetEngine(settings,PresetHttp(),installer,Current(settings)))
                {
                    engine.IsGameRunning=()=>true; engine.InstallationBlocked+=message=>notices++;
                    await engine.Run(false,false,true);
                    Assert(notices==0 && installer.Calls==0 && engine.Status=="配置待应用","Version check requested game closure");
                    await engine.Run(true,false,false);
                    Assert(notices==0 && installer.Calls==0 && engine.Status=="等待游戏退出" && engine.NextCheck<DateTimeOffset.Now.AddMinutes(2),"Background update interrupted play or failed to defer");
                }
            });
            }
            finally { Storage.Root=originalRoot; }
        }
    }
}
