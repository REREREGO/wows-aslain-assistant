using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AslainAssistant
{
    class FakeHttp : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond;
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Calls++; return Task.FromResult(Respond(request)); }
    }
    class FakeInstaller : Installer
    {
        public int Calls;
        public int Code;
        public bool WasInteractive;
        public string InputInf;
        public string LogText = "Installation process succeeded.\nCurrent step: ssDone\nLog closed.";
        public Action Installed;
        public override Task<int> Run(string file, string args)
        {
            Calls++; WasInteractive = !args.Contains("/VERYSILENT");
            string log = Regex.Match(args, "/LOG=\"([^\"]+)\"").Groups[1].Value;
            string output = Regex.Match(args, "/SAVEINF=\"([^\"]+)\"").Groups[1].Value;
            string target = Regex.Match(args, "/DIR=\"([^\"]+)\"").Groups[1].Value;
            string input = Regex.Match(args, "/LOADINF=\"([^\"]+)\"").Groups[1].Value;
            InputInf = File.Exists(input) ? File.ReadAllText(input) : "";
            File.WriteAllText(log, "Original Setup EXE: " + file + "\n" + LogText);
            File.WriteAllText(output, InputInf.Length > 0 ? InputInf : "[Setup]\nDir=" + target + "\nComponents=markers,markers\\speed_indicator\nTasks=install_clean\nLang=cn\n");
            if (Code == 0 && Installed != null) Installed();
            return Task.FromResult(Code);
        }
    }
    partial class Tests
    {
        static int passed;
        static string root;
        static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
        static void Throws(Action action, string message) { try { action(); } catch (Exception) { return; } throw new Exception(message); }
        static async Task Reject(Func<Task> action, string message) { try { await action(); } catch (Exception) { return; } throw new Exception(message); }
        static void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
        static async Task TestAsync(string name, Func<Task> action) { await action(); passed++; Console.WriteLine("PASS " + name); }
        static byte[] Exe()
        {
            var bytes = new byte[2048]; bytes[0] = (byte)'M'; bytes[1] = (byte)'Z'; bytes[0x3c] = 128; bytes[128] = (byte)'P'; bytes[129] = (byte)'E'; return bytes;
        }
        static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        static string Html(string hash)
        {
            return "<meta content=\"old SHA-256: " + new string('f', 64) + "\"><span>THE CLASSIC MODPACK 15.10.0 #08</span><a href=\"https://filez.aslain.com/Aslains_WoWs_Modpack_Installer_v.15.10.0_08.exe\">Download</a><a>SHA-256</a>: " + hash + "<div>Changelog v99.9.0 #99</div>";
        }
        static GameState Local(string version = "15.10.0", string pack = "15.10.0_01")
        {
            return new GameState { Path = Path.Combine(root, "game"), GameVersion = version, HasInstallation = pack != null, HasActiveMods = true, Installed = pack == null ? null : PackVersion.Parse(pack) };
        }
        static Settings Config(string id)
        {
            return new Settings { Id = id, Name = "测试 " + id, GamePath = Path.Combine(root, "game"), Preset = Preset.FromInf("[Setup]\nComponents=markers,markers\\speed_indicator\nTasks=install_clean\nLang=cn") };
        }
        static async Task Workflow(string id, bool first, bool running, int exit, bool compatible, bool broken = false)
        {
            byte[] bytes = Exe();
            var http = new FakeHttp { Respond = r => new HttpResponseMessage(HttpStatusCode.OK) { Content = r.RequestUri.Host == "filez.aslain.com" ? (HttpContent)new ByteArrayContent(bytes) : new StringContent(Html(Hash(bytes))) } };
            var installer = new FakeInstaller { Code = exit };
            if (broken) installer.LogText = "[ExtractDL] Extracting flags_Fanhexie_15.10.0.7z -- FAILED: CreateFile error 2\nCurrent step: ssDone\nLog closed.";
            var settings = Config(id); if (first) settings.Preset = null;
            var local = Local(compatible ? "15.10.0" : "15.9.0", first ? null : "15.10.0_01");
            installer.Installed = () => { local.Installed = PackVersion.Parse("15.10.0_08"); local.HasInstallation = true; };
            using (var engine = new UpdateEngine(settings, new OfficialClient(http), installer))
            {
                engine.InspectGame = p => local; engine.IsGameRunning = () => running;
                engine.NetworkAvailable = () => true;
                await engine.Run(true, false, false);
                if (!compatible || running) Assert(installer.Calls == 0, "must not install incompatible or running game");
                else
                {
                    Assert(installer.Calls == 1, "installer called once");
                    Assert(installer.WasInteractive == first, "first install wizard / subsequent silent");
                    if (broken)
                    {
                        Assert(engine.Status == "部分模组未安装完整" && settings.IncompleteRelease == "15.10.0_08", "exit 0 must not hide failed extraction");
                        Assert(settings.Preset != null && settings.InstallIssue.Contains("flags_Fanhexie"), "selection and diagnosis retained");
                        await engine.Run(true,false,false);
                        Assert(installer.Calls == 1 && engine.Status == "部分模组未安装完整", "same installed version must not hide partial failure or loop");
                        await engine.Run(false,false,true);
                        Assert(installer.Calls == 1 && settings.IncompleteRelease != "", "check-only preserves failure");
                        installer.LogText = "Current step: ssDone\nLog closed.";
                        await engine.Run(true,false,true);
                        Assert(installer.Calls == 2 && engine.Status == "更新完成" && settings.IncompleteRelease == "" && settings.FailedRelease == "", "manual repair reinstalls current version and clears warning after success");
                    }
                    else if (exit == 0) { Assert(engine.Status == "更新完成", engine.Detail); Assert(settings.Preset != null, "successful choices saved"); Assert(settings.FailedRelease == "", "failure guard cleared"); }
                    else { Assert(engine.Status == "检查或更新未完成", "failure reported"); await engine.Run(true, false, false); Assert(installer.Calls == 1, "failed installer is not repeated unattended"); }
                }
                Assert(!engine.Busy, "busy flag cleared");
            }
        }
        static async Task Run()
        {
            Test("numeric version comparison, game patch and revision", () => { Assert(PackVersion.Parse("15.10.0 #08").CompareTo(PackVersion.Parse("15.9.0_99")) > 0, "numeric compare"); Assert(PackVersion.Parse("15.10.0.10").CompareTo(PackVersion.Parse("15.10.0 #09")) > 0, "revision compare"); Throws(() => PackVersion.Parse("latest"), "reject unknown"); });
            Test("official release ignores stale meta and unrelated changelog", () => { var r = Release.Parse(Html(Hash(Exe()))); Assert(r.Version.ToString() == "15.10.0 #08" && r.Sha256 == Hash(Exe()), "current release"); Throws(() => Release.Parse(Html(Hash(Exe())).Replace("v.15.10.0_08.exe", "v.15.9.0_08.exe")), "mismatch blocked"); Throws(() => Release.Parse(Html(Hash(Exe())).Replace("filez.aslain.com", "evil.example")), "unexpected host blocked"); Throws(() => Release.Parse(Html(Hash(Exe())).Replace("SHA-256</a>", "hash</a>")), "missing hash blocked"); });
            Test("INF round-trip never imports foreign install paths or commands", () => { var p = Preset.FromInf("[Setup]\nDir=C:\\other\nComponents=markers,markers\\speed_indicator\nTasks=install_clean\nLang=cn\nRun=calc.exe"); string inf = p.ToInf(@"D:\游戏\World of Warships"); Assert(!inf.Contains("C:\\other") && !inf.Contains("calc.exe"), "whitelisted fields"); Assert(Preset.FromInf(inf).Components == p.Components, "round trip"); Throws(() => Preset.FromInf("[Setup]\nComponents=a\" /DIR=bad"), "reject parameter injection"); Throws(() => Preset.FromInf("[Setup]\nComponents="), "empty preset rejected"); Throws(() => Preset.FromInf("[Setup]\nComponents=a\nComponents=b"), "duplicate fields rejected"); });
            Test("REG importer reads only WoWs settings and never executes registry content", () => { string reg = "Windows Registry Editor Version 5.00\n[HKEY_LOCAL_MACHINE\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Aslains_WoWs_Modpack_Installer_is1]\n\"Inno Setup: Selected Components\"=\"markers,markers\\\\speed_indicator\"\n\"Inno Setup: Selected Tasks\"=\"install_clean\"\n\"Inno Setup: Language\"=\"cn\"\n[HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Run]\n\"evil\"=\"calc.exe\""; var p = Preset.FromReg(reg); Assert(p.Components == @"markers,markers\speed_indicator", "unescape"); Assert(!p.ToInf("").Contains("calc"), "unrelated reg values discarded"); Throws(() => Preset.FromReg(reg.Replace("WoWs", "WoT")), "WoT preset rejected"); });
            Test("client profiles isolate paths, preset and auto-update", () => { var a = Config("client-a"); var b = Config("client-b"); b.Server = "INTL"; b.Preset.Components = "camera"; Storage.Save(a); Storage.Save(b); var all = Storage.LoadAll(); Assert(all.Single(x => x.Id == a.Id).Preset.Components != all.Single(x => x.Id == b.Id).Preset.Components, "separate state"); string file = Path.Combine(root, "backup.json"); File.WriteAllText(file, Storage.Export(b)); a.AutoUpdate = true; var imported = Storage.Import(file, a); Assert(imported.Id == a.Id && imported.Server == "CN" && imported.GamePath == a.GamePath, "import preserves selected target"); Assert(!imported.AutoUpdate && imported.Preset.Components == "camera", "import pauses and applies choices"); Storage.Save(a); Assert(File.Exists(Path.Combine(Storage.Root, "profiles", "client-a.json.bak")), "atomic backup"); });
            Test("removed clients stay removed without changing games, other profiles or preferences", () =>
            {
                string previous = Storage.Root;
                Storage.Root = Path.Combine(root,"client-removal");
                try
                {
                    var defaults = Storage.LoadAll(); Assert(defaults.Count == 2,"First run should offer both clients");
                    string game = Path.Combine(root,"preserved-game-file"); File.WriteAllText(game,"game and mods stay here");
                    foreach (var settings in defaults) { settings.GamePath = game; Storage.Save(settings); }
                    string other = File.ReadAllText(Path.Combine(Storage.Root,"profiles","intl.json"));
                    string preferences = Path.Combine(Storage.Root,"ui.json"); File.WriteAllText(preferences,"preserve preferences");
                    Throws(()=>Storage.DeleteClient("../ui"),"Deletion must reject paths outside the profiles directory");
                    Storage.DeleteClient("cn");
                    Assert(Storage.LoadAll().Single().Id == "intl","Default CN client was recreated");
                    Assert(File.ReadAllText(Path.Combine(Storage.Root,"profiles","intl.json")) == other,"Other profile was changed");
                    Storage.DeleteClient("intl");
                    Assert(Storage.LoadAll().Count == 0 && Storage.LoadAll().Count == 0,"Last deletion must remain empty on reload");
                    Assert(File.ReadAllText(game) == "game and mods stay here" && File.ReadAllText(preferences) == "preserve preferences","Deletion touched a game file or UI preference");
                    Storage.Save(new Settings {Id="new-client",Name="重新添加的客户端"});
                    Assert(Storage.LoadAll().Single().Id == "new-client","New client could not be added after deleting all clients");
                }
                finally { Storage.Root = previous; }
            });
            Test("version mismatch, absent install, newer local and missing active mods", () => { var r = Release.Parse(Html(Hash(Exe()))); Assert(UpdatePolicy.Decide(Local("15.9.0"), r) == UpdateDecision.Incompatible, "mismatch"); Assert(UpdatePolicy.Decide(Local("", null), r) == UpdateDecision.UnknownGame, "unknown game"); Assert(UpdatePolicy.Decide(Local("15.10.0", null), r) == UpdateDecision.Install, "missing"); Assert(UpdatePolicy.Decide(Local("15.10.0", "15.10.0_09"), r) == UpdateDecision.NewerInstalled, "never downgrade"); Assert(UpdatePolicy.Decide(Local("15.10.0", "15.10.0_08"), r) == UpdateDecision.Current, "latest"); var empty = Local("15.10.0", "15.10.0_08"); empty.HasActiveMods = false; Assert(UpdatePolicy.Decide(empty, r) == UpdateDecision.Install, "missing mods repair"); });
            Test("legacy and modern Aslain layouts and active build ignore predownloaded client", () => {
                string game = Path.Combine(root, "layout-game"); Directory.CreateDirectory(Path.Combine(game, "bin", "100", "res_mods")); Directory.CreateDirectory(Path.Combine(game, "bin", "999")); File.WriteAllText(Path.Combine(game, "WorldOfWarships.exe"), "fixture");
                File.WriteAllText(Path.Combine(game, "game_info.xml"), "<protocol><game><id>WOWS.CN.PRODUCTION</id><part_versions><version name='client' installed='15.10.0.0.100' available='15.11.0.0.999'/></part_versions></game></protocol>");
                string mod = Path.Combine(game, "Aslain_Modpack"); Directory.CreateDirectory(mod); File.WriteAllText(Path.Combine(mod, "unins000.exe"), "fixture"); File.WriteAllText(Path.Combine(mod, "_Aslains_Installer.log"), "Original Setup EXE: C:\\Aslains_WoWs_Modpack_Installer_v.15.10.0_08.exe");
                var old = GameDetector.Inspect(game); Assert(old.Installed.FileTag == "15.10.0_08" && old.GameVersion == "15.10.0", "legacy detected, not future available version");
                Directory.CreateDirectory(Path.Combine(mod, "Uninstall")); File.Move(Path.Combine(mod, "unins000.exe"), Path.Combine(mod, "Uninstall", "unins000.exe")); Directory.CreateDirectory(Path.Combine(mod, "Logs")); File.Move(Path.Combine(mod, "_Aslains_Installer.log"), Path.Combine(mod, "Logs", "Aslains_Installer.log"));
                var modern = GameDetector.Inspect(game); Assert(modern.Installed.FileTag == "15.10.0_08" && modern.HasInstallation && !modern.HasActiveMods, "modern layout"); Assert(GameDetector.ClientKind(game) == "CN", "client kind");
            });
            Test("completed install recovers after interruption without reinstalling", () => {
                var settings = Config("recover"); settings.FailedRelease = "15.10.0_08"; settings.PendingRun = "20261118-010000-abcdef";
                string dir = Path.Combine(Storage.Root, "runs", settings.PendingRun); Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "selection.inf"), "[Setup]\nDir=" + settings.GamePath + "\nComponents=camera\nTasks=install_clean\nLang=cn");
                File.WriteAllText(Path.Combine(dir, "installer.log"), "Original Setup EXE: Aslains_WoWs_Modpack_Installer_v.15.10.0_08.exe\nCurrent step: ssDone\nLog closed.");
                using (var engine = new UpdateEngine(settings, new OfficialClient(), new FakeInstaller())) { engine.Game = Local("15.10.0", "15.10.0_08"); engine.RecoverCompletedRun(); Assert(settings.FailedRelease == "" && settings.Preset.Components == "camera", "recovered completed operation"); }
            });
            Test("installer paths with spaces and Chinese are quoted without a shell", () => { string args = Installer.Arguments(@"D:\战舰 世界", @"C:\配置\input.inf", @"C:\配置\output.inf", @"C:\日志\setup.log", false); Assert(args.Contains("/DIR=\"D:\\战舰 世界\"") && args.Contains("/VERYSILENT") && args.Contains("/NORESTART ") && args.Contains("/LOADINF="), "silent args"); Assert(!Installer.Arguments(@"D:\game", "", @"C:\o.inf", @"C:\l.log", true).Contains("/VERYSILENT"), "manual first run"); Throws(() => Installer.Arguments("bad\"path", "", "o", "l", false), "quote injection"); });
            Test("SHA-256 and PE validation reject tampering and HTML", () => { string file = Path.Combine(root, "verify.exe"); File.WriteAllBytes(file, Exe()); OfficialClient.Verify(file, Hash(Exe())); Throws(() => OfficialClient.Verify(file, new string('0', 64)), "hash mismatch"); File.WriteAllText(file, "<html>error</html>"); Throws(() => OfficialClient.Verify(file, Hash(Exe())), "HTML blocked"); Assert(!OfficialClient.Allowed(new Uri("https://filez.aslain.com.evil.example/a")) && !OfficialClient.Allowed(new Uri("http://aslain.com")), "host and TLS boundaries"); });
            await TestAsync("redirect to external host blocked", async () => { var h = new FakeHttp { Respond = r => { var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new Uri("https://evil.example/a"); return response; } }; using (var c = new OfficialClient(h)) await Reject(async () => { await c.Latest(CancellationToken.None); }, "untrusted redirect"); Assert(h.Calls == 1, "never requests external URL"); });
            await TestAsync("corrupt download and cancellation leave no executable", async () => { string dir = Path.Combine(root, "bad-download"); var h = new FakeHttp { Respond = r => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Exe()) } }; using (var c = new OfficialClient(h)) await Reject(async () => { await c.Download(Release.Parse(Html(new string('a', 64))), dir, null, CancellationToken.None); }, "corrupt body"); Assert(Directory.GetFiles(dir).Length == 0, "partial cleaned"); });
            await TestAsync("first installation uses wizard and saves selections after verification", () => Workflow("first", true, false, 0, true));
            await TestAsync("existing configuration updates silently", () => Workflow("update", false, false, 0, true));
            await TestAsync("running game defers without installation", () => Workflow("running", false, true, 0, true));
            await TestAsync("incompatible client predownloads without installation", () => Workflow("incompatible", false, false, 0, false));
            await TestAsync("canceled or failed install keeps configuration and blocks automatic loop", () => Workflow("failure", false, false, 2, true));
            await TestAsync("partial extraction failure persists and manual repair retries the same version", () => Workflow("partial", false, false, 0, true, true));
            Test("full log inspection catches middle extraction errors without treating preview failures as missing mods", () =>
            {
                string log = Path.Combine(root,"long-installer.log");
                File.WriteAllText(log,"Original Setup EXE: Aslains_WoWs_Modpack_Installer_v.15.10.0_08.exe\n" + new string('x',240000) + "\n[ExtractDL] Extracting flags_Fanhexie_15.10.0.7z -- FAILED: CreateFile error 2\n[ExtractDL] Archive is not in DL_cache, the download must have failed: flags_Fanhexie_15.10.0.7z\n" + new string('x',240000) + "\nFilename: D:\\game\\engine_config_EnableReplays.bat\nProcess exit code: 1\nCurrent step: ssDone\nLog closed.");
                var report = InstallReport.Read(log);
                Assert(report.Incomplete && report.FailedArchives.Count == 1 && report.FailedScripts.Single() == "engine_config_EnableReplays.bat" && report.Completed && report.Closed,"complete report");
                File.WriteAllText(log,"[PreviewCache] Download failed: 404 Not Found\nUpdateChecker: download failed or timed out\nCurrent step: ssDone\nLog closed.");
                Assert(!InstallReport.Read(log).Incomplete,"preview/update-check failures are not extraction failures");
            });
            Test("restart detects partial installation previously marked successful by an older assistant", () =>
            {
                var settings = Config("recover-partial"); settings.GamePath = Path.Combine(root,"legacy-partial-game");
                string dir = Path.Combine(Storage.Root,"runs","20261118-030000-abcdef"); Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir,"selection.inf"),"[Setup]\nDir=" + settings.GamePath + "\nComponents=camera\nTasks=install_clean\nLang=cn");
                File.WriteAllText(Path.Combine(dir,"installer.log"),"Original Setup EXE: Aslains_WoWs_Modpack_Installer_v.15.10.0_08.exe\n[ExtractDL] Extracting flags_Fanhexie_15.10.0.7z -- FAILED: missing\nCurrent step: ssDone\nLog closed.");
                using (var engine = new UpdateEngine(settings,new OfficialClient(),new FakeInstaller()))
                {
                    engine.Game = Local("15.10.0","15.10.0_08"); engine.Game.Path = settings.GamePath;
                    engine.IsInstallerRunning = ()=>false;
                    engine.RecoverCompletedRun();
                    Assert(settings.IncompleteRelease == "15.10.0_08" && settings.FailedRelease == "15.10.0_08" && engine.Status == "部分模组未安装完整","migration must restore warning without installing");
                }
            });
            Test("repair advice uses verified official links and the selected client path", () =>
            {
                string log = Path.Combine(root,"repair-links.log");
                File.WriteAllText(log,"Downloading temporary file from https://repo.aslain.com/Warships/flags_Fanhexie_15.10.0.7z: C:\\Temp\\flags.7z\n[ExtractDL] Extracting flags_Fanhexie_15.10.0.7z -- FAILED: missing\n[ExtractDL] Extracting unknown.7z -- FAILED: missing\nDownloading temporary file from https://repo.aslain.com.evil.example/Warships/unknown.7z: C:\\Temp\\unknown.7z\n");
                var report = InstallReport.Read(log);
                string target = Path.Combine(root,"a different client");
                string advice = RepairAdvice.Build(target,report);
                Assert(advice.Contains(Path.Combine(target,"Aslain_Modpack","DL_cache")) && advice.Contains("https://repo.aslain.com/Warships/flags_Fanhexie_15.10.0.7z"),"correct link and per-client cache destination");
                Assert(RepairAdvice.DownloadFor(report,"unknown.7z") == "" && !advice.Contains("evil.example"),"unverified URLs must never become a download instruction");
                Assert(!RepairAdvice.IsOfficialArchive("../bad.7z",new Uri("https://repo.aslain.com/Warships/../bad.7z")),"path traversal rejected");
            });
            Test("automated replay does not repeat the one-time download cache cleanup", () =>
            {
                var preset = Preset.FromInf("[Setup]\nComponents=flags\\custom\\flags_fanhexie\nTasks=install_clean,dlc_folder\nLang=cn");
                Assert(preset.ToInf(@"D:\game",true).Contains("Tasks=install_clean\r\n") && !preset.ToInf(@"D:\game",true).Contains("dlc_folder"),"cached repair is not erased by replay");
                Assert(preset.Tasks == "install_clean,dlc_folder" && preset.ToInf("").Contains("dlc_folder"),"export and saved selection are not silently rewritten");
            });
            Test("a later completed manual installation clears the stale missing-mod warning", () =>
            {
                var settings = Config("external-repair"); settings.GamePath = Path.Combine(root,"external-game"); settings.FailedRelease = settings.IncompleteRelease = "15.10.0_08";
                string dir = Path.Combine(settings.GamePath,"Aslain_Modpack","Logs"); Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir,"Aslains_Installer.log"),"Original Setup EXE: D:\\Aslains_WoWs_Modpack_Installer_v.15.10.0_08.exe\nDefaultDirName = " + settings.GamePath + "\n[ExtractDL] Extracting flags_Fanhexie_15.10.0.7z -- OK\nCurrent step: ssDone");
                using (var engine = new UpdateEngine(settings,new OfficialClient(),new FakeInstaller()))
                {
                    engine.Game = Local("15.10.0","15.10.0_08"); engine.Game.Path = settings.GamePath;
                    engine.IsInstallerRunning = ()=>false;
                    engine.RecoverCompletedRun();
                    Assert(settings.IncompleteRelease == "" && settings.FailedRelease == "" && engine.Status == "手动安装已核对","completed manual repair supersedes old failure");
                }
            });
            Test("replay script warnings are resolved only when actual active-build settings match", () =>
            {
                string build = Path.Combine(root,"replay-active"); Directory.CreateDirectory(Path.Combine(build,"res_mods"));
                string xml = Path.Combine(build,"res_mods","engine_config.xml");
                File.WriteAllText(xml,"<engine_config><replays><isEnabled>true</isEnabled><maxReplaysToSave>0</maxReplaysToSave><versioned>true</versioned></replays></engine_config>");
                var report = new InstallReport(); report.FailedScripts.AddRange(new[] { "engine_config_EnableReplays.bat", "engine_config_ReplaysLimitOFF.bat", "engine_config_VersionFolderON.bat", "unrelated.bat" });
                report.CheckReplaySettings(build);
                Assert(report.VerifiedScripts.Count == 3 && report.FailedScripts.Single() == "unrelated.bat", "only verified known settings may suppress their warning");
                File.WriteAllText(xml,"<engine_config><replays><isEnabled>false</isEnabled><maxReplaysToSave>30</maxReplaysToSave><versioned>false</versioned></replays></engine_config>");
                report = new InstallReport(); report.FailedScripts.Add("engine_config_EnableReplays.bat"); report.CheckReplaySettings(build);
                Assert(report.FailedScripts.Count == 1 && report.VerifiedScripts.Count == 0,"mismatched settings remain warnings");
                File.WriteAllText(xml,"<broken>"); report.CheckReplaySettings(build);
                Assert(report.FailedScripts.Count == 1,"malformed config cannot be called success");
                File.Delete(xml); report.CheckReplaySettings(build);
                Assert(report.FailedScripts.Count == 1,"missing config cannot be called success");
            });
        }
        static int Main(string[] args)
        {
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                if(args.Contains("--self-live"))
                {
                    using(var client=new AssistantUpdateClient())
                    {
                        var release=client.Latest(CancellationToken.None).GetAwaiter().GetResult();
                        Console.WriteLine("LIVE signed assistant release: "+release.Version);
                        Console.WriteLine("LIVE verified assistant installer: "+client.Download(release,Path.GetFullPath("artifacts/self-update-live"),null,CancellationToken.None).GetAwaiter().GetResult());
                    }
                    return 0;
                }
                root = Path.GetFullPath(Path.Combine("artifacts", "test-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"))); Directory.CreateDirectory(root); Storage.Root = Path.Combine(root, "data");
                Run().GetAwaiter().GetResult();
                RunPresetTests().GetAwaiter().GetResult();
                passed += UpdateTests.Run(root).GetAwaiter().GetResult();
                if (args.Contains("--live"))
                {
                    using (var client = new OfficialClient())
                    {
                        var release = client.Latest(CancellationToken.None).GetAwaiter().GetResult(); Console.WriteLine("LIVE latest: " + release.Version + " SHA256: " + release.Sha256);
                        if (args.Contains("--download")) Console.WriteLine("LIVE verified download: " + client.Download(release, Path.GetFullPath("artifacts/downloads"), null, CancellationToken.None).GetAwaiter().GetResult());
                    }
                    foreach (string game in GameDetector.Discover()) { var state = GameDetector.Inspect(game); var preset = GameDetector.ReadPreset(game); Console.WriteLine("LIVE local: " + game + " game=" + state.GameVersion + " installed=" + state.Installed + " activeMods=" + state.HasActiveMods + " choices=" + (preset == null ? 0 : preset.Count)); }
                }
                Console.WriteLine("All " + passed + " tests passed. No real installer was executed."); return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
    }
}
