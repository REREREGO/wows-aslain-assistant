using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AslainAssistant
{
    // Exercise actual controls at the machine's DPI with isolated, inactive profiles.
    // No update request, installer, game write or startup preference change is made.
    class UiTests
    {
        static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        static int passed, exitCode;
        static object Field(Dashboard form,string name) { return typeof(Dashboard).GetField(name,Flags).GetValue(form); }
        static void Call(Dashboard form,string name) { typeof(Dashboard).GetMethod(name,Flags).Invoke(form,null); }
        static void Assert(bool condition,string message) { if (!condition) throw new Exception(message); }
        static void Test(string name,Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
        static void Fits(Control control)
        {
            Assert(control.Width > 0 && control.Height > 0,"Control has no usable size: " + control.Text);
            Assert(control.Parent.ClientRectangle.Contains(control.Bounds),"Control clipped inside its container: " + control.Text + " " + control.Bounds);
        }
        static void VersionFits(AntdUI.Label label)
        {
            Fits(label);
            using (var graphics = label.CreateGraphics())
            {
                var text = graphics.MeasureString(label.Text,label.Font,10000,StringFormat.GenericTypographic);
                Assert(text.Width <= label.Width && text.Height <= label.Height,"Version text would be truncated: " + label.Text + " text=" + text + " label=" + label.Size);
            }
        }
        static Settings Read(string id) { return Storage.Json().Deserialize<Settings>(File.ReadAllText(Path.Combine(Storage.Root,"profiles",id + ".json"))); }
        static void Run(Dashboard form)
        {
            var pages = (AntdUI.Tabs)Field(form,"pages");
            var engines = (List<UpdateEngine>)Field(form,"engines");
            var first = engines[0];
            first.Game = new GameState { GameVersion = "15.10.0", HasInstallation = true, Installed = PackVersion.Parse("15.10.0_08") };
            first.Release = new Release { Version = PackVersion.Parse("15.10.0_09") };
            Call(form,"RefreshUi");
            Test("version numbers fit at normal and minimum window size",()=>
            {
                foreach (Size size in new[] { form.Size,form.MinimumSize })
                {
                    form.Size = size; form.PerformLayout(); Application.DoEvents();
                    foreach (string field in new[] { "gameValue","installedValue","latestValue" }) VersionFits((AntdUI.Label)Field(form,field));
                }
            });
            Test("four pages remain reachable with usable content",()=>
            {
                Assert(pages.Pages.Count == 4,"Missing dashboard page");
                for (int i = 0; i < pages.Pages.Count; i++)
                {
                    pages.SelectedIndex = i; Application.DoEvents();
                    Assert(pages.Pages[i].Visible,"Selected page is hidden");
                    Assert(pages.Pages[i].Height > 200 * AntdUI.Config.Dpi,"Page is too short to use");
                }
            });
            Test("interval input and switches remain visible inside their rows",()=>
            {
                pages.SelectedIndex = 1; Application.DoEvents();
                var hours = (AntdUI.InputNumber)Field(form,"hours"); Fits(hours);
                Assert(hours.Width >= 120 * AntdUI.Config.Dpi,"Interval input too narrow");
                foreach(string name in new[] { "automatic","startup" })
                {
                    var toggle = (AntdUI.Switch)Field(form,name); Fits(toggle);
                    Assert(toggle.Width >= toggle.Height * 1.8,"Switch is distorted");
                }
                var remove = (AntdUI.Button)Field(form,"deleteClient"); Fits(remove);
                Assert(remove.Type == AntdUI.TTypeMini.Error,"Delete action should use the library's destructive style");
            });
            Test("changing interval saves only the selected client and survives reload",()=>
            {
                var hours = (AntdUI.InputNumber)Field(form,"hours");
                hours.Value = 24;
                Assert(Read("cn").CheckHours == 24 && Read("intl").CheckHours == 2,"First client's setting leaked");
                typeof(Dashboard).GetField("selected",Flags).SetValue(form,engines[1]); Call(form,"Bind");
                Assert(hours.Value == 2,"Client switching did not restore its interval");
                Assert(((AntdUI.Input)Field(form,"gamePath")).Text == engines[1].Settings.GamePath,"Client switching did not restore its path");
                hours.Value = 6;
                Assert(Read("cn").CheckHours == 24 && Read("intl").CheckHours == 6,"Second client's setting leaked");
                Assert(Storage.LoadAll().All(x=>!x.AutoUpdate),"Test unexpectedly enabled installation");
            });
            Test("dark and light themes update pages, log surface and dialogs and persist",()=>
            {
                var theme = (AntdUI.Segmented)Field(form,"themeMode");
                foreach (int mode in new[] { 1,0 })
                {
                    theme.SelectIndex = mode; Application.DoEvents();
                    bool dark = mode == 1;
                    Assert(AntdUI.Config.IsDark == dark && UiPreferences.Load(Storage.Root).Dark == dark,"Theme choice did not persist");
                    Assert(form.BackColor == UiTheme.Background,"Window theme not updated");
                    Assert(((AntdUI.Input)Field(form,"logs")).BackColor == UiTheme.Surface,"Logs theme not updated");
                    foreach (AntdUI.TabPage page in pages.Pages) Assert(page.BackColor == UiTheme.Background,"Page theme not updated");
                    using (var dialog = UiTheme.Dialog("UI test",new Size(400,300)))
                    using (var card = UiTheme.Card(100))
                    {
                        Assert(dialog.BackColor == UiTheme.Background && card.Back == UiTheme.Surface,"New dialog/card did not inherit theme");
                    }
                }
            });
            Test("keyboard navigation changes page and restores saved selection",()=>
            {
                foreach (int page in new[] { 0,1,2,3 })
                {
                    object[] args = { new Message(),Keys.Control | (Keys)((int)Keys.D1 + page) };
                    bool handled = (bool)typeof(Dashboard).GetMethod("ProcessCmdKey",Flags).Invoke(form,args);
                    Assert(handled && pages.SelectedIndex == page,"Keyboard navigation did not change page");
                    Assert(UiPreferences.Load(Storage.Root).PageIndex == page,"Page selection was not saved");
                }
                File.WriteAllText(Path.Combine(Storage.Root,"ui.json"),"broken json");
                Assert(UiPreferences.Load(Storage.Root).PageIndex == 0,"Damaged preference should fall back to overview");
            });
            Test("canceling deletion keeps the client and pauses background work during confirmation",()=>
            {
                var target = (UpdateEngine)Field(form,"selected");
                string file = Path.Combine(Storage.Root,"profiles",target.Settings.Id + ".json"), before = File.ReadAllText(file);
                typeof(Dashboard).GetField("confirmDelete",Flags).SetValue(form,new Func<Settings,bool>(settings=>
                {
                    Assert(settings == target.Settings && (bool)Field(form,"confirmingDelete"),"Confirmation targeted the wrong client");
                    Assert(!((AntdUI.Button)Field(form,"deleteClient")).Enabled,"Deletion remained enabled while confirming");
                    var tick = (Task)typeof(Dashboard).GetMethod("Tick",Flags).Invoke(form,null);
                    Assert(tick.IsCompleted && !engines.Any(x=>x.Busy),"A background update started while confirming deletion");
                    byte response=(byte)typeof(Dashboard).GetMethod("HandleInstanceCommand",Flags).Invoke(form,new object[]{InstanceControl.Exit});
                    Assert(response == InstanceControl.Busy,"Self update could interrupt the confirmation");
                    return false;
                }));
                Call(form,"DeleteClient");
                Assert(engines.Count == 2 && Field(form,"selected") == target && File.ReadAllText(file) == before,"Canceling deletion changed the profile");
            });
            Test("busy clients cannot be deleted and confirmed deletion selects the remaining client",()=>
            {
                var target = (UpdateEngine)Field(form,"selected");
                int confirmations = 0;
                typeof(Dashboard).GetField("confirmDelete",Flags).SetValue(form,new Func<Settings,bool>(s=>{confirmations++; return true;}));
                var setBusy = typeof(UpdateEngine).GetProperty("Busy").GetSetMethod(true);
                try
                {
                    setBusy.Invoke(target,new object[]{true}); Call(form,"RefreshUi"); Call(form,"DeleteClient");
                    Assert(!((AntdUI.Button)Field(form,"deleteClient")).Enabled && confirmations == 0 && engines.Count == 2,"Busy client could be deleted");
                }
                finally {setBusy.Invoke(target,new object[]{false}); Call(form,"RefreshUi");}
                Call(form,"DeleteClient");
                Assert(confirmations == 1 && engines.Count == 1 && Field(form,"selected") == engines[0],"Did not select the remaining client");
                Assert(!File.Exists(Path.Combine(Storage.Root,"profiles",target.Settings.Id + ".json")),"Removed profile persisted");
                Assert(((AntdUI.StackPanel)Field(form,"clientTiles")).Controls.Count == 1,"Removed tile is still visible");
                Assert(((AntdUI.Input)Field(form,"gamePath")).Text == engines[0].Settings.GamePath,"Remaining client's path was not restored");
            });
            Test("a locked profile leaves the client intact and permits a later retry",()=>
            {
                var target = engines[0];
                string file = Path.Combine(Storage.Root,"profiles",target.Settings.Id + ".json"); bool failed = false;
                using(var locked = new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.None))
                    try {Call(form,"DeleteClient");} catch(TargetInvocationException ex) {if(!(ex.InnerException is IOException)) throw; failed=true;}
                Assert(failed && engines.Count == 1 && Field(form,"selected") == target && File.Exists(file),"Failed deletion removed client state");
                Assert(!(bool)Field(form,"confirmingDelete") && ((AntdUI.Button)Field(form,"deleteClient")).Enabled,"Failed deletion left the UI locked");
            });
            Test("deleting the last client clears client actions while keeping global settings available",()=>
            {
                Call(form,"DeleteClient");
                Assert(engines.Count == 0 && Field(form,"selected") == null && Storage.LoadAll().Count == 0,"Last client was not removed");
                Assert(pages.SelectedIndex == 0 && ((AntdUI.Label)Field(form,"statusLabel")).Text == "还没有客户端","Missing empty-state guidance");
                Assert(((List<Control>)Field(form,"clientControls")).All(c=>!c.Enabled),"A client action remained active with no selection");
                Assert(((AntdUI.Button)Field(form,"addClient")).Enabled && ((AntdUI.Switch)Field(form,"startup")).Enabled,"Global controls were disabled");
                var theme = (AntdUI.Segmented)Field(form,"themeMode");
                foreach(int mode in new[]{1,0}) {theme.SelectIndex=mode; Application.DoEvents(); Assert(form.BackColor == UiTheme.Background,"Empty view theme failed");}
                var settings = new Settings {Id="added-again",GamePath=Path.Combine(Storage.Root,"absent-again")};
                Storage.Save(settings);
                typeof(Dashboard).GetMethod("AddEngine",Flags).Invoke(form,new object[]{settings});
                typeof(Dashboard).GetField("selected",Flags).SetValue(form,engines.Last()); Call(form,"Bind");
                Assert(((AntdUI.Button)Field(form,"deleteClient")).Enabled && ((AntdUI.Button)Field(form,"update")).Enabled,"Re-adding a client did not restore its actions");
                Call(form,"DeleteClient");
            });
        }
        static void RunWindow(Action<Dashboard> check)
        {
            using (var form = new Dashboard(false))
            using (var ready = new Timer { Interval = 100 })
            {
                form.Opacity = 0; form.ShowInTaskbar = false;
                int ticks = 0;
                ready.Tick += (s,e)=>
                {
                    bool booted = (bool)Field(form,"booted");
                    if (!booted && ++ticks < 100) return;
                    ready.Stop();
                    try { Assert(booted,"Dashboard initialization timed out"); check(form); }
                    catch (Exception ex) { Console.Error.WriteLine(ex); exitCode = 1; }
                    finally { typeof(Dashboard).GetField("quitting",Flags).SetValue(form,true); form.Close(); }
                };
                form.Shown += (s,e)=>ready.Start(); Application.Run(form);
            }
        }
        [STAThread] static int Main()
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); UiTheme.Initialize();
            Control.CheckForIllegalCrossThreadCalls = true;
            Storage.Root = Path.GetFullPath(Path.Combine("artifacts","ui-tests-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
            Storage.Save(new Settings { GamePath = Path.Combine(Storage.Root,"absent-cn"), Name = "UI 测试国服" });
            Storage.Save(new Settings { Id = "intl", Server = "INTL", Name = "UI 测试外服", GamePath = Path.Combine(Storage.Root,"absent-intl"), CheckHours = 2 });
            RunWindow(Run);
            if(exitCode == 0) RunWindow(form=>Test("restarting an empty dashboard does not recreate default clients",()=>
            {
                Assert(((List<UpdateEngine>)Field(form,"engines")).Count == 0 && Field(form,"selected") == null,"Default clients reappeared after restart");
                Assert(((AntdUI.Button)Field(form,"addClient")).Enabled,"Cannot add a client after restarting empty");
            }));
            if(exitCode == 0) Console.WriteLine("All " + passed + " UI tests passed at " + (AntdUI.Config.Dpi * 100) + "% DPI.");
            return exitCode;
        }
    }
}
