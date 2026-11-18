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
            Assert(settings.PendingPreset && settings.ImportedPreset != null && !settings.AutoUpdate && settings.GamePath == target.GamePath,"Import/restart lost the pending selection or replaced the target");
            Assert(settings.Preset.Components == target.Preset.Components,"Import replaced installation history");
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
            await TestAsync("manual and automatic updates use history while imports wait for explicit application",async ()=>
            {
                foreach(bool manual in new[]{true,false})
                {
                    var settings=Imported(manual ? "separate-manual" : "separate-auto");
                    string history=settings.Preset.Components, imported=settings.ImportedPreset.Components;
                    var game=Current(settings); game.Installed=PackVersion.Parse("15.10.0_01");
                    var installer=new FakeInstaller {Installed=()=>game.Installed=PackVersion.Parse("15.10.0_08")};
                    using(var engine=PresetEngine(settings,PresetHttp(),installer,game))
                    {
                        await engine.Run(true,false,manual);
                        Assert(installer.Calls==1 && Preset.FromInf(installer.InputInf).Components==history,"Normal update consumed the imported selection");
                        Assert(settings.PendingPreset && settings.ImportedPreset.Components==imported && settings.Preset.Components==history,"Normal install discarded the pending import");
                        await engine.ApplyImportedPreset();
                        Assert(installer.Calls==2 && Preset.FromInf(installer.InputInf).Components==imported && settings.Preset.Components==imported && !settings.PendingPreset,"Explicit import application failed");
                        await engine.ApplyImportedPreset();
                        Assert(installer.Calls==2,"Missing import caused another installation");
                    }
                }
            });
            Test("export shares the displayed import without mutating saved installation history",()=>
            {
                var settings=Imported("share-export"); string history=settings.Preset.Components, expected=settings.ImportedPreset.Components;
                string file=Path.Combine(root,"displayed-preset.json"); File.WriteAllText(file,Storage.Export(settings));
                var exported=Storage.Json().Deserialize<Backup>(File.ReadAllText(file));
                Assert(exported.Settings.Preset.Components==expected && !exported.Settings.PendingPreset && exported.Settings.ImportedPreset==null,"Export cannot be read by older importers");
                var restored=Storage.Import(file,Config("share-recipient"));
                Assert(restored.ImportedPreset.Components==expected && settings.Preset.Components==history && settings.PendingPreset,"Sharing changed the sender or imported the wrong choices");
            });
            Test("existing pending imports migrate without being mistaken for installed history",()=>
            {
                var legacy=Config("legacy-import"); legacy.PendingPreset=true; legacy.Preset.Components="camera";
                Storage.AtomicWrite(Path.Combine(Storage.Root,"profiles",legacy.Id+".json"),Storage.Json().Serialize(legacy));
                var migrated=Storage.LoadAll().Single(x=>x.Id==legacy.Id);
                Assert(migrated.ImportedPreset.Components=="camera" && migrated.Preset==null && migrated.PendingPreset,"Legacy import was lost or promoted to history");
                Storage.Save(migrated);
                var again=Storage.LoadAll().Single(x=>x.Id==legacy.Id);
                Assert(again.ImportedPreset.Components=="camera" && again.Preset==null,"Migration was not stable across restarts");
                again.GamePath=Path.Combine(root,"legacy-game"); Directory.CreateDirectory(Path.Combine(again.GamePath,"bin"));
                File.WriteAllText(Path.Combine(again.GamePath,"WorldOfWarships.exe"),"");
                var history=Config("history").Preset;
                File.WriteAllText(Path.Combine(again.GamePath,"_Aslains_Installer_Options.inf"),history.ToInf(again.GamePath));
                using(var engine=PresetEngine(again,PresetHttp(),new FakeInstaller(),Current(again))) engine.RefreshLocal();
                Assert(again.Preset.Components==history.Components && again.ImportedPreset.Components=="camera","Migration did not recover the real installation history separately");
            });
            await TestAsync("without installation history only the explicit import action installs",async ()=>
            {
                var settings=Imported("no-history"); settings.Preset=null; Storage.Save(settings);
                var installer=new FakeInstaller();
                using(var engine=PresetEngine(settings,PresetHttp(),installer,Current(settings)))
                {
                    await engine.Run(true,false,true);
                    Assert(installer.Calls==0 && engine.Status=="尚无历史安装配置" && settings.PendingPreset,"Normal update silently used the import without history");
                    await engine.ApplyImportedPreset();
                    Assert(installer.Calls==1 && !settings.PendingPreset,"Explicit import could not install without history");
                }
            });
            Test("recovering a historical update preserves a separate pending import",()=>
            {
                var settings=Imported("history-recovery"); string imported=settings.ImportedPreset.Components, history=settings.Preset.Components;
                settings.PendingRun="20261118-050001-abcdef"; settings.PendingRunUsesImportedPreset=false; settings.FailedRelease="15.10.0_08";
                string run=Path.Combine(Storage.Root,"runs",settings.PendingRun); Directory.CreateDirectory(run);
                File.WriteAllText(Path.Combine(run,"selection.inf"),settings.Preset.ToInf(settings.GamePath));
                File.WriteAllText(Path.Combine(run,"installer.log"),"Original Setup EXE: Aslains_WoWs_Modpack_Installer_v.15.10.0_08.exe\nCurrent step: ssDone\nLog closed.");
                using(var engine=PresetEngine(settings,PresetHttp(),new FakeInstaller(),Current(settings))) engine.RefreshLocal();
                Assert(settings.PendingPreset && settings.ImportedPreset.Components==imported && settings.Preset.Components==history && settings.PendingRun=="","Recovery consumed the unrelated imported preset");
            });
            foreach(string extension in new[]{".json",".inf",".reg"})
                await TestAsync("imported " + extension + " survives restart and installs on the current version exactly once",async ()=>
                {
                    var settings = Imported("share-" + extension.TrimStart('.'),extension);
                    string expected = settings.ImportedPreset.Components;
                    var installer = new FakeInstaller();
                    using(var engine = PresetEngine(settings,PresetHttp(),installer,Current(settings)))
                    {
                        await engine.Run(false,false,true);
                        Assert(engine.Status == "已是最新版本" && settings.PendingPreset && installer.Calls == 0,"Check-only applied or forgot the pending configuration");
                        await engine.Run(true,false,true);
                        Assert(installer.Calls == 0 && settings.PendingPreset,"Normal update applied an unconfirmed import");
                        await engine.ApplyImportedPreset();
                        Assert(installer.Calls == 1 && !installer.WasInteractive && Preset.FromInf(installer.InputInf).Components == expected,"Latest-version install did not receive the imported choices");
                        Assert(installer.InputInf.Contains("Dir=" + settings.GamePath) && !installer.InputInf.Contains("FriendsGame"),"Installation targeted the sender's directory");
                        Assert(!settings.PendingPreset && settings.ImportedPreset == null && settings.Preset.Components == expected && !Storage.LoadAll().Single(x=>x.Id==settings.Id).PendingPreset,"Successful installation did not promote the import and clear persisted pending state");
                        await engine.Run(true,false,true);
                        Assert(installer.Calls == 1 && engine.Status == "已是最新版本","Applied preset installed again");
                    }
                });
            await TestAsync("failed and partial imported installs retain pending choices and allow manual retry",async ()=>
            {
                var settings = Imported("share-retry"); string expected = settings.ImportedPreset.Components, history = settings.Preset.Components;
                var installer = new FakeInstaller {Code=2,LogText="Installation cancelled.\nLog closed."};
                using(var engine = PresetEngine(settings,PresetHttp(),installer,Current(settings)))
                {
                    await engine.ApplyImportedPreset();
                    Assert(settings.PendingPreset && settings.ImportedPreset.Components == expected && settings.Preset.Components == history && installer.Calls==1,"Failure discarded pending configuration or replaced history");
                    await engine.Run(true,false,false);
                    Assert(installer.Calls==1,"Failed import retried unattended");
                    installer.Code=0; installer.LogText="[ExtractDL] Extracting flags_Fanhexie_15.10.0.7z -- FAILED: missing\nCurrent step: ssDone\nLog closed.";
                    await engine.ApplyImportedPreset();
                    Assert(settings.PendingPreset && settings.Preset.Components == history && settings.ImportedPreset.Components == expected && settings.IncompleteRelease!="" && installer.Calls==2,"Partial installation marked the imported preset applied");
                    installer.LogText="Current step: ssDone\nLog closed.";
                    await engine.ApplyImportedPreset();
                    Assert(!settings.PendingPreset && installer.Calls==3 && settings.Preset.Components==expected,"Manual retry failed to apply original choices");
                }
            });
            Test("old helper and external installation logs cannot replace pending imports",()=>
            {
                var settings = Imported("share-old-log"); string expected = settings.ImportedPreset.Components;
                string run = Path.Combine(Storage.Root,"runs","20261118-050000-abcdef"); Directory.CreateDirectory(run);
                File.WriteAllText(Path.Combine(run,"selection.inf"),"[Setup]\nDir="+settings.GamePath+"\nComponents=camera\nLang=cn\nTasks=install_clean");
                string log="Original Setup EXE: Aslains_WoWs_Modpack_Installer_v.15.10.0_08.exe\nDefaultDirName = "+settings.GamePath+"\nCurrent step: ssDone\nLog closed.";
                File.WriteAllText(Path.Combine(run,"installer.log"),log);
                string external=Path.Combine(settings.GamePath,"Aslain_Modpack","Logs"); Directory.CreateDirectory(external);
                File.WriteAllText(Path.Combine(external,"Aslains_Installer.log"),log);
                using(var engine = PresetEngine(settings,PresetHttp(),new FakeInstaller(),Current(settings))) engine.RefreshLocal();
                Assert(settings.PendingPreset && settings.ImportedPreset.Components==expected,"Old logs replaced imported choices");
                settings.PendingRun=Path.GetFileName(run); settings.FailedRelease="15.10.0_08";
                settings.PendingRunUsesImportedPreset=true;
                File.WriteAllText(Path.Combine(run,"selection.inf"),settings.ImportedPreset.ToInf(settings.GamePath));
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
                        await engine.ApplyImportedPreset();
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
                    await engine.ApplyImportedPreset();
                    Assert(probes==3 && notices==1 && installer.Calls==0 && settings.PendingPreset,"Final game guard failed");
                    engine.IsGameRunning=()=>false;
                    await engine.ApplyImportedPreset();
                    Assert(installer.Calls==1 && !settings.PendingPreset,"Cannot retry after the game exits");
                }
            });
            await TestAsync("background installs wait without popups and check-only never requests game closure",async ()=>
            {
                var settings=Imported("running-background"); var installer=new FakeInstaller(); int notices=0;
                var game=Current(settings); game.Installed=PackVersion.Parse("15.10.0_01");
                using(var engine=PresetEngine(settings,PresetHttp(),installer,game))
                {
                    engine.IsGameRunning=()=>true; engine.InstallationBlocked+=message=>notices++;
                    await engine.Run(false,false,true);
                    Assert(notices==0 && installer.Calls==0 && engine.Status=="可以更新","Version check requested game closure");
                    await engine.Run(true,false,false);
                    Assert(notices==0 && installer.Calls==0 && engine.Status=="等待游戏退出" && engine.NextCheck<DateTimeOffset.Now.AddMinutes(2),"Background update interrupted play or failed to defer");
                }
            });
            }
            finally { Storage.Root=originalRoot; }
        }
    }
}
