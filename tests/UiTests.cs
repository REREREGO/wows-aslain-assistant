using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
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
        static Control FindControl(Control parent,string text)
        {
            foreach(Control child in parent.Controls)
            {
                if(child.Text==text) return child;
                var found=FindControl(child,text); if(found!=null) return found;
            }
            return null;
        }
        static void Run(Dashboard form)
        {
            var pages = (AntdUI.Tabs)Field(form,"pages");
            var engines = (List<UpdateEngine>)Field(form,"engines");
            var first = engines[0];
            first.Game = new GameState { GameVersion = "15.10.0", HasInstallation = true, Installed = PackVersion.Parse("15.10.0_08") };
            first.Release = new Release { Version = PackVersion.Parse("15.10.0_09") };
            Call(form,"RefreshUi");
            Test("manual install and reinstall route running-game warnings to the modal in both themes",()=>
            {
                var warning=Field(form,"warnGameRunning"); var originalProbe=first.IsGameRunning;
                int shown=0;
                try
                {
                    first.IsGameRunning=()=>true;
                    typeof(Dashboard).GetField("warnGameRunning",Flags).SetValue(form,new Action<string,string>((client,message)=>
                    {
                        Assert(client==first.Settings.Name && message.Contains("关闭游戏") && message.Contains("重试"),"Wrong game warning or client");
                        Assert(first.Busy && !((AntdUI.Button)Field(form,"update")).Enabled,"An update can overlap the warning");
                        shown++;
                    }));
                    foreach(int mode in new[]{1,0})
                    {
                        ((AntdUI.Segmented)Field(form,"themeMode")).SelectIndex=mode;
                        pages.SelectedIndex=0; Application.DoEvents();
                        typeof(Control).GetMethod("OnClick",Flags).Invoke(Field(form,"update"),new object[]{EventArgs.Empty});
                        pages.SelectedIndex=2; Application.DoEvents();
                        var reinstall=FindControl(form,"选择插件 / 重装"); Assert(reinstall!=null,"Missing reinstall action");
                        typeof(Control).GetMethod("OnClick",Flags).Invoke(reinstall,new object[]{EventArgs.Empty});
                    }
                    Assert(shown==4 && !first.Busy,"Actions did not show one warning each or remained locked");
                }
                finally
                {
                    first.IsGameRunning=originalProbe;
                    typeof(Dashboard).GetField("warnGameRunning",Flags).SetValue(form,warning);
                }
            });
            Test("import action appears on the current client only and uses the game guard in both themes",()=>
            {
                var original=first.Settings;
                var warning=Field(form,"warnGameRunning"); var originalProbe=first.IsGameRunning;
                var theme=(AntdUI.Segmented)Field(form,"themeMode"); int originalTheme=theme.SelectIndex;
                Size originalSize=form.Size; int notices=0;
                try
                {
                    pages.SelectedIndex=2; Application.DoEvents();
                    var apply=(AntdUI.Button)Field(form,"applyPreset");
                    Assert(!apply.Visible,"Import action appears before an import");
                    first.Settings=Storage.Json().Deserialize<Settings>(Storage.Json().Serialize(original));
                    first.Settings.Preset=Preset.FromInf("[Setup]\nComponents=markers\nLang=cn");
                    string file=Path.Combine(Storage.Root,"friend.inf"); File.WriteAllText(file,"[Setup]\nComponents=camera\nLang=cn");
                    typeof(Dashboard).GetMethod("ImportPreset",Flags).Invoke(form,new object[]{file}); Application.DoEvents();
                    Assert(apply.Visible && apply.Enabled && first.Settings.Preset.Components=="markers" && first.Settings.ImportedPreset.Components=="camera","Import did not expose a separate apply action");
                    Assert(Read(first.Settings.Id).ImportedPreset.Components=="camera","Import did not persist");
                    typeof(Dashboard).GetField("selected",Flags).SetValue(form,engines[1]); Call(form,"Bind");
                    Assert(!apply.Visible,"Import action leaked into another client");
                    typeof(Dashboard).GetField("selected",Flags).SetValue(form,first); Call(form,"Bind");
                    Assert(apply.Visible,"Returning to the importing client lost the action");
                    first.IsGameRunning=()=>true;
                    typeof(Dashboard).GetField("warnGameRunning",Flags).SetValue(form,new Action<string,string>((client,message)=>
                    {
                        Assert(client==first.Settings.Name && message.Contains("使用该配置更新") && !apply.Enabled,"Imported action used the wrong warning or remained clickable"); notices++;
                    }));
                    foreach(int mode in new[]{1,0})
                    {
                        theme.SelectIndex=mode;
                        foreach(Size size in new[]{originalSize,form.MinimumSize})
                        {
                            form.Size=size; Application.DoEvents(); Fits(apply);
                            foreach(Control peer in apply.Parent.Controls) if(peer.Visible) {Fits(peer); if(peer!=apply) Assert(!apply.Bounds.IntersectsWith(peer.Bounds),"Import actions overlap");}
                        }
                        Capture(form,mode==1 ? "import-action-dark.png" : "import-action-light.png");
                        typeof(Control).GetMethod("OnClick",Flags).Invoke(apply,new object[]{EventArgs.Empty});
                        Assert(first.Settings.ImportedPreset!=null && apply.Visible && apply.Enabled,"Game guard discarded the preset or left the action disabled");
                    }
                    Assert(notices==2,"Apply action did not reach the running-game warning");
                    first.Settings.Preset=first.Settings.ImportedPreset; first.Settings.ImportedPreset=null; first.Settings.PendingPreset=false;
                    Call(form,"RefreshUi"); Assert(!apply.Visible,"Apply action remained after completion");
                }
                finally
                {
                    first.Settings=original; Storage.Save(original); first.IsGameRunning=originalProbe;
                    typeof(Dashboard).GetField("warnGameRunning",Flags).SetValue(form,warning);
                    typeof(Dashboard).GetField("selected",Flags).SetValue(form,first);
                    form.Size=originalSize; theme.SelectIndex=originalTheme; Call(form,"Bind");
                }
            });
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
        static void Capture(Dashboard form,string name)
        {
            using(var bitmap=new Bitmap(form.ClientSize.Width,form.ClientSize.Height))
            {
                using(var graphics=Graphics.FromImage(bitmap)) graphics.Clear(form.BackColor);
                for(int i=form.Controls.Count-1;i>=0;i--)
                    if(form.Controls[i].Visible) form.Controls[i].DrawToBitmap(bitmap,form.Controls[i].Bounds);
                bitmap.Save(Path.Combine(Storage.Root,name),System.Drawing.Imaging.ImageFormat.Png);
            }
        }
        static void BackgroundLayout(Dashboard form)
        {
            Assert((bool)typeof(Dashboard).GetProperty("ShowWithoutActivation",Flags).GetValue(form,null),"Background startup must not activate over the game");
            var tray=(NotifyIcon)Field(form,"tray");
            var click=typeof(NotifyIcon).GetMethod("OnMouseClick",Flags);
            form.Hide();
            click.Invoke(tray,new object[]{new MouseEventArgs(MouseButtons.Right,1,0,0,0)});
            Assert(!form.Visible && tray.ContextMenuStrip!=null,"Right click must retain the tray menu without opening the dashboard");
            click.Invoke(tray,new object[]{new MouseEventArgs(MouseButtons.Left,1,0,0,0)}); Application.DoEvents();
            Assert(form.Visible,"One left click did not restore the dashboard");
            click.Invoke(tray,new object[]{new MouseEventArgs(MouseButtons.Left,1,0,0,0)});
            Assert(form.Visible,"Repeated tray click hid the dashboard");
            Capture(form,"tray-layout.png");
            var tiles=(Dictionary<UpdateEngine,AntdUI.Button>)Field(form,"tiles");
            foreach(var pair in tiles) Console.WriteLine("TILE "+pair.Key.Settings.Name+" "+pair.Value.Bounds);
            Test("background startup restores separate full-width client tiles",()=>
            {
                var controls=tiles.Values.ToArray();
                for(int i=0;i<controls.Length;i++)
                {
                    Fits(controls[i]);
                    Assert(controls[i].Width>controls[i].Parent.ClientSize.Width*0.85,"Client tile kept its unlaid-out width");
                    for(int j=i+1;j<controls.Length;j++) Assert(!controls[i].Bounds.IntersectsWith(controls[j].Bounds),"Client tiles overlap after tray startup");
                }
            });
            Test("client selection and page layouts survive repeated tray restores",()=>
            {
                var pages=(AntdUI.Tabs)Field(form,"pages");
                foreach(var pair in tiles)
                {
                    typeof(Control).GetMethod("OnClick",Flags).Invoke(pair.Value,new object[]{EventArgs.Empty});
                    Assert(Field(form,"selected")==pair.Key && ((AntdUI.Label)Field(form,"selectedLabel")).Text==pair.Key.Settings.Name,"Sidebar selection does not match the displayed client");
                    form.Hide(); Call(form,"Reveal"); Application.DoEvents();
                    Assert(Field(form,"selected")==pair.Key && pair.Value.Type==AntdUI.TTypeMini.Primary,"Restoring changed the selected client");
                }
                form.Size=form.MinimumSize;
                foreach(int theme in new[]{1,0})
                {
                    ((AntdUI.Segmented)Field(form,"themeMode")).SelectIndex=theme;
                    for(int page=0;page<pages.Pages.Count;page++)
                    {
                        pages.SelectedIndex=page; form.Hide(); Call(form,"Reveal"); Application.DoEvents();
                        Assert(pages.Pages[page].Visible,"Restored page is hidden");
                        if(page==1)
                        {
                            var remove=(Control)Field(form,"deleteClient"); Fits(remove);
                            foreach(Control peer in remove.Parent.Controls) if(peer!=remove) Assert(!remove.Bounds.IntersectsWith(peer.Bounds),"Settings actions overlap after restore");
                        }
                    }
                }
            });
        }
        static void SlowStartup()
        {
            var source=new TaskCompletionSource<List<ClientSnapshot>>();
            List<Settings> copies=null; Exception failure=null; UpdateEngine chosen=null; long activation=0;
            RunWindow(form=>Test("activation responds during slow startup and stays visible after loading",()=>
            {
                if(failure!=null) throw failure;
                Assert(activation<1000,"Activation waited for startup work");
                Assert(form.Visible && Field(form,"selected")==chosen,"Startup completion hid the window or reset the selected client");
                Assert(((AntdUI.Label)Field(form,"selectedLabel")).Text==chosen.Settings.Name,"Loaded data replaced the wrong client selection");
                Console.WriteLine("Activation during pending disk work: "+activation+" ms");
            }),true,form=>
            {
                typeof(Dashboard).GetField("loadClients",Flags).SetValue(form,new Func<List<Settings>,System.Threading.CancellationToken,Task<List<ClientSnapshot>>>((settings,token)=>{copies=settings;return source.Task;}));
                form.Shown+=async (s,e)=>
                {
                    try
                    {
                        var watch=Stopwatch.StartNew();
                        byte reply=await Task.Run(()=>InstanceControl.Send(Process.GetCurrentProcess().Id,InstanceControl.Activate));
                        activation=watch.ElapsedMilliseconds;
                        Assert(reply==InstanceControl.Ready && form.Visible && !(bool)Field(form,"booted"),"Activation endpoint was unavailable until startup completed");
                        var tiles=(Dictionary<UpdateEngine,AntdUI.Button>)Field(form,"tiles");
                        var choice=tiles.Last(); chosen=choice.Key;
                        typeof(Control).GetMethod("OnClick",Flags).Invoke(choice.Value,new object[]{EventArgs.Empty});
                        await Task.Delay(800);
                        Assert(form.Visible && !(bool)Field(form,"booted"),"Slow worker blocked or hid the dashboard");
                    }
                    catch(Exception ex) {failure=ex;}
                    finally {source.TrySetResult(copies.Select(x=>new ClientSnapshot {Settings=x,Status="读取完成",Detail="测试加载结果"}).ToList());}
                };
            });
        }
        static void RunWindow(Action<Dashboard> check,bool background=false,Action<Dashboard> configure=null)
        {
            var elapsed=Stopwatch.StartNew();
            using (var form = new Dashboard(background))
            using (var ready = new Timer { Interval = 100 })
            {
                form.Opacity = 0; form.ShowInTaskbar = false;
                if(configure!=null) configure(form);
                int ticks = 0;
                ready.Tick += (s,e)=>
                {
                    bool booted = (bool)Field(form,"booted");
                    if (!booted && ++ticks < 100) return;
                    ready.Stop();
                    try { Assert(booted,"Dashboard initialization timed out"); Console.WriteLine("Dashboard ready in "+elapsed.ElapsedMilliseconds+" ms"); check(form); }
                    catch (Exception ex) { Console.Error.WriteLine(ex); exitCode = 1; }
                    finally { typeof(Dashboard).GetField("quitting",Flags).SetValue(form,true); form.Close(); }
                };
                form.Shown += (s,e)=>ready.Start(); Application.Run(form);
            }
        }
        [STAThread] static int Main(string[] args)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); UiTheme.Initialize();
            Control.CheckForIllegalCrossThreadCalls = true;
            Storage.Root = Path.GetFullPath(Path.Combine("artifacts","ui-tests-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
            Storage.Save(new Settings { GamePath = Path.Combine(Storage.Root,"absent-cn"), Name = "UI 测试国服" });
            Storage.Save(new Settings { Id = "intl", Server = "INTL", Name = "UI 测试外服", GamePath = Path.Combine(Storage.Root,"absent-intl"), CheckHours = 2 });
            if(Array.IndexOf(args,"--background-layout")>=0)
            {
                Storage.Save(new Settings {Id="custom-cn",Name="国服",GamePath=Path.Combine(Storage.Root,"absent-custom")});
                Console.WriteLine("UI evidence: "+Storage.Root);
                RunWindow(BackgroundLayout,true); return exitCode;
            }
            RunWindow(Run);
            if(exitCode == 0) RunWindow(form=>Test("restarting an empty dashboard does not recreate default clients",()=>
            {
                Assert(((List<UpdateEngine>)Field(form,"engines")).Count == 0 && Field(form,"selected") == null,"Default clients reappeared after restart");
                Assert(((AntdUI.Button)Field(form,"addClient")).Enabled,"Cannot add a client after restarting empty");
            }));
            if(exitCode==0)
            {
                Storage.Save(new Settings {GamePath=Path.Combine(Storage.Root,"absent-cn")});
                Storage.Save(new Settings {Id="intl",Server="INTL",Name="外服",GamePath=Path.Combine(Storage.Root,"absent-intl")});
                Storage.Save(new Settings {Id="custom",Name="国服",GamePath=Path.Combine(Storage.Root,"absent-custom")});
                RunWindow(BackgroundLayout,true);
                if(exitCode==0) SlowStartup();
            }
            if(exitCode == 0) Console.WriteLine("All " + passed + " UI tests passed at " + (AntdUI.Config.Dpi * 100) + "% DPI.");
            return exitCode;
        }
    }
}
