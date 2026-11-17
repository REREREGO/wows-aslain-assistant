using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
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
        }
        [STAThread] static int Main()
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); UiTheme.Initialize();
            Control.CheckForIllegalCrossThreadCalls = true;
            Storage.Root = Path.GetFullPath(Path.Combine("artifacts","ui-tests-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
            Storage.Save(new Settings { GamePath = Path.Combine(Storage.Root,"absent-cn"), Name = "UI 测试国服" });
            Storage.Save(new Settings { Id = "intl", Server = "INTL", Name = "UI 测试外服", GamePath = Path.Combine(Storage.Root,"absent-intl"), CheckHours = 2 });
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
                    try { Assert(booted,"Dashboard initialization timed out"); Run(form); Console.WriteLine("All " + passed + " UI tests passed at " + (AntdUI.Config.Dpi * 100) + "% DPI."); }
                    catch (Exception ex) { Console.Error.WriteLine(ex); exitCode = 1; }
                    finally { typeof(Dashboard).GetField("quitting",Flags).SetValue(form,true); form.Close(); }
                };
                form.Shown += (s,e)=>ready.Start(); Application.Run(form);
            }
            return exitCode;
        }
    }
}
