using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AslainAssistant
{
    public sealed partial class Dashboard : AntdUI.Window
    {
        // Login/update launches must not steal focus from a running game. An
        // explicit user activation still calls Activate() in Reveal().
        protected override bool ShowWithoutActivation { get { return background; } }
        async Task Initialize()
        {
            if (background) Hide();
            loadingClients = true;
            var token = startupCancellation.Token;
            try
            {
                foreach (Settings settings in Storage.LoadAll()) AddEngine(settings);
                selected = engines.FirstOrDefault();
                foreach(var engine in engines) { engine.Status = "正在读取客户端"; engine.Detail = "正在读取游戏版本和插件安装记录…"; }
                Bind();
                // Only detached settings are passed to the worker. No control, live
                // engine or selected client is modified off the UI thread.
                var settingsCopies = engines.Select(x=>Storage.Json().Deserialize<Settings>(Storage.Json().Serialize(x.Settings))).ToList();
                var snapshots = await loadClients(settingsCopies,token);
                token.ThrowIfCancellationRequested();
                if (IsDisposed) return;
                foreach(var snapshot in snapshots) engines.Single(x=>x.Settings.Id == snapshot.Settings.Id).ApplySnapshot(snapshot);
                booted = true; Bind(); timer.Start();
                AddLog("已就绪。每个客户端独立保存路径和配置；首次安装可手动选择插件。关闭窗口后继续在托盘运行。");
                AddLog("版本匹配时发现更新即安装；跨游戏版本只预下载。安装可能需要 Windows 管理员授权。");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if(IsDisposed || token.IsCancellationRequested) return; UiTheme.Notice(this, "配置需要处理", "读取配置失败，原文件已保留：\n" + ex.Message + "\n配置目录：" + Storage.Root, AntdUI.TType.Error); quitting = true; Close(); }
            finally { loadingClients = false; }
        }
        static Task<List<ClientSnapshot>> LoadStartupClients(List<Settings> settings,CancellationToken token)
        {
            return Task.Run(()=>
            {
                token.ThrowIfCancellationRequested();
                var found = settings.Any(x=>string.IsNullOrEmpty(x.GamePath)) ? GameDetector.Discover() : new List<string>();
                var result = new List<ClientSnapshot>();
                foreach(var item in settings)
                {
                    token.ThrowIfCancellationRequested();
                    if(string.IsNullOrEmpty(item.GamePath))
                    {
                        var matching = found.Where(p=>GameDetector.ClientKind(p)==item.Server && !settings.Any(x=>string.Equals(x.GamePath,p,StringComparison.OrdinalIgnoreCase))).ToList();
                        if(matching.Count == 1)
                        {
                            item.GamePath = matching[0];
                            if(!item.PendingPreset)
                            {
                                item.Preset = GameDetector.ReadPreset(matching[0]);
                                if(item.Preset != null) item.Preset.Source = "从已安装阿斯兰读取";
                            }
                        }
                    }
                    result.Add(UpdateEngine.ReadSnapshot(item,token));
                }
                return result;
            },token);
        }
        void AddEngine(Settings settings)
        {
            var engine = new UpdateEngine(settings, new OfficialClient(), new Installer()); engines.Add(engine);
            engine.Changed += () => { if (!IsDisposed) RefreshUi(); };
            engine.Log += message => AddLog("[" + engine.Settings.Name + "] " + message);
            engine.Progress += transfer =>
            {
                if (selected != engine) return;
                progress.Loading = transfer.Total <= 0;
                if (transfer.Total > 0) progress.Value = (float)Math.Min(1, (double)transfer.Received / transfer.Total);
                detailLabel.Text = "已下载 " + (transfer.Received / 1048576.0).ToString("F1") + " MB" + (transfer.Total > 0 ? " / " + (transfer.Total / 1048576.0).ToString("F1") + " MB" : "") + " · 下载完成后校验 SHA-256";
            };
            engine.Notify += message => { tray.BalloonTipTitle = settings.Name; tray.BalloonTipText = message; tray.ShowBalloonTip(5000); };
            engine.InstallationBlocked += message => warnGameRunning(engine.Settings.Name,message);
            var tile = UiTheme.Button("",null,false,"DesktopOutlined"); tile.Height = (int)(72 * AntdUI.Config.Dpi); tile.TextAlign = ContentAlignment.MiddleLeft; tile.TextMultiLine = true; tile.AutoEllipsis = true;
            tile.Click += (s, e) => { selected = engine; Bind(); };
            tiles.Add(engine, tile); clientTiles.Controls.Add(tile); clientTiles.Controls.SetChildIndex(tile,0);
        }
        void Bind()
        {
            binding = true;
            gamePath.Text = selected == null ? "" : selected.Settings.GamePath;
            automatic.Checked = selected != null && selected.Settings.AutoUpdate; hours.Value = selected == null ? 1 : selected.Settings.CheckHours;
            startup.Checked = Startup.Enabled;
            binding = false; progress.Value = 0; progress.Loading = false; RefreshUi();
        }
        void ShowGameRunningNotice(string client,string message)
        {
            Reveal();
            UiTheme.Notice(this,"请先关闭游戏",client + "\n\n" + message,AntdUI.TType.Warn);
        }
        void RefreshUi()
        {
            bool busy = engines.Any(x => x.Busy) || selfUpdate.Busy || shutdownForUpdate || confirmingDelete;
            foreach (Control control in mutable) control.Enabled = !busy && booted;
            foreach (Control control in clientControls) control.Enabled = selected != null && !busy && booted;
            cancel.Enabled = engines.Any(x => x.Busy) && !engines.Any(x => x.Installing);
            if(selfUpdate.Busy || shutdownForUpdate || confirmingDelete) cancel.Enabled=false;
            assistantAutomatic.Enabled=selfUpdate.Allowed && !busy;
            assistantUpdateStatus.Text=selfUpdate.Status;
            foreach (var pair in tiles)
            {
                var s = pair.Key.Settings;
                pair.Value.Text = s.Name + "\n" + (string.IsNullOrEmpty(s.GamePath) ? "待选择游戏目录" : pair.Key.Busy ? pair.Key.Status : s.AutoUpdate ? "自动守护中 · " + s.CheckHours + " 小时 / 次" : "手动模式");
                pair.Value.Type = pair.Key == selected ? AntdUI.TTypeMini.Primary : AntdUI.TTypeMini.Default;
                pair.Value.Ghost = pair.Key == selected; pair.Value.BorderWidth = pair.Key == selected ? 1 : 0;
            }
            if (selected == null)
            {
                gameValue.Text = installedValue.Text = latestValue.Text = "—";
                selectedLabel.Text = "未添加客户端";
                presetLabel.Text = "添加客户端后，可以读取或导入插件配置。";
                statusLabel.Text = "还没有客户端"; statusLabel.ForeColor = blue;
                detailLabel.Text = "点击左下角「添加客户端」，选择游戏目录后开始使用。";
                update.Text = "立即更新"; progress.Visible = false; progress.Loading = false; progress.Value = 0;
                scheduleLabel.Text = "暂无客户端 · 可随时添加国服或外服客户端";
                return;
            }
            var game = selected.Game;
            gameValue.Text = game == null ? "未读取" : string.IsNullOrEmpty(game.GameVersion) ? "待确认" : game.GameVersion;
            installedValue.Text = game == null ? "—" : !game.HasInstallation ? "未安装" : game.Installed == null ? "版本未知" : game.Installed.ToString();
            latestValue.Text = selected.Release == null ? "待检查" : selected.Release.Version.ToString();
            selectedLabel.Text = selected.Settings.Name;
            presetLabel.Text = selected.Settings.Preset == null ? "插件配置 · 首次安装时手动选择，完成后自动保存" : "插件配置 · " + selected.Settings.Preset.Count + " 个选择项（含分类） · " + selected.Settings.Preset.Source;
            if (selected.Settings.PendingPreset) presetLabel.Text += " · 待应用，点击立即更新安装";
            statusLabel.Text = selected.Status; detailLabel.Text = selected.Detail;
            update.Text = !string.IsNullOrEmpty(selected.Settings.IncompleteRelease) ? "重试安装" : "立即更新";
            statusLabel.ForeColor = string.IsNullOrEmpty(selected.Settings.InstallIssue) ? blue : UiTheme.Warning;
            progress.Visible = selected.Busy && !selected.Installing;
            if (!busy) { progress.Loading = false; progress.Value = 0; }
            scheduleLabel.Text = selected.Settings.AutoUpdate ? "下次检查 " + selected.NextCheck.ToLocalTime().ToString("MM-dd HH:mm:ss") + "  ·  客户端版本变化每分钟复查  ·  关闭窗口后在托盘继续运行" : "当前客户端未开启自动更新  ·  首次手动选插件，以后可自动沿用  ·  关闭窗口后在托盘继续运行";
        }
        void SaveSwitches()
        {
            if (binding || selected == null) return;
            if (automatic.Checked && !GameDetector.IsGameFolder(selected.Settings.GamePath)) { binding = true; automatic.Checked = false; binding = false; throw new InvalidDataException("请先选择此客户端的游戏目录。"); }
            bool changed = selected.Settings.AutoUpdate != automatic.Checked;
            selected.Settings.AutoUpdate = automatic.Checked; selected.Settings.CheckHours = (int)hours.Value;
            selected.NextCheck = changed && automatic.Checked ? DateTimeOffset.Now.AddSeconds(10) : DateTimeOffset.Now.AddHours((int)hours.Value);
            Storage.Save(selected.Settings); RefreshUi();
            AddLog(selected.Settings.Name + (automatic.Checked ? "：已开启自动更新，每 " + hours.Value + " 小时检查。" : "：自动更新已暂停。"));
        }
        void ChangeTheme()
        {
            bool dark = themeMode.SelectIndex == 1;
            if (preferences.Dark == dark) return;
            var next = new UiPreferences { Dark = dark, PageIndex = pages.SelectedIndex };
            try { next.Save(Storage.Root); }
            catch { themeMode.SelectIndex = preferences.Dark ? 1 : 0; throw; }
            preferences = next; UiTheme.SetDark(dark); RefreshUi();
            AddLog(dark ? "已切换为深色主题，下次启动自动沿用。" : "已切换为浅色主题，下次启动自动沿用。");
        }
        protected override bool ProcessCmdKey(ref Message message,Keys keyData)
        {
            if ((keyData & Keys.Modifiers) == Keys.Control)
            {
                int number = (int)(keyData & Keys.KeyCode) - (int)Keys.D1;
                if (number >= 0 && number < pages.Pages.Count) { pages.SelectedIndex = number; return true; }
            }
            return base.ProcessCmdKey(ref message,keyData);
        }
        void SetPath(string folder)
        {
            folder = GameDetector.Normalize(folder);
            if (engines.Any(x => x != selected && string.Equals(x.Settings.GamePath, folder, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("此目录已由另一个客户端配置管理，请切换到对应客户端。");
            selected.Settings.GamePath = folder; selected.Settings.AutoUpdate = false; selected.Settings.FailedRelease = ""; selected.Settings.PendingRun = "";
            selected.Settings.IncompleteRelease = ""; selected.Settings.InstallIssue = "";
            if (!selected.Settings.PendingPreset)
            {
                selected.Settings.Preset = GameDetector.ReadPreset(folder);
                if (selected.Settings.Preset != null) selected.Settings.Preset.Source = "从该游戏目录读取";
            }
            selected.RefreshLocal(); selected.Status = "目录已就绪"; selected.Detail = "可以仅检查版本，或开启自动更新。";
            Storage.Save(selected.Settings); Bind();
        }
        void BrowsePath()
        {
            using (var dialog = new FolderBrowserDialog { Description = "选择包含 WorldOfWarships.exe 的游戏根目录", SelectedPath = selected.Settings.GamePath, ShowNewFolderButton = false }) if (dialog.ShowDialog(this) == DialogResult.OK) SetPath(dialog.SelectedPath);
        }
        void DiscoverPaths()
        {
            var paths = GameDetector.Discover();
            if (paths.Count == 0) throw new InvalidDataException("常见位置未找到游戏，请使用「选择游戏目录」。");
            var list = new AntdUI.Select { List = true, ListAutoWidth = true, Height = 46, MaxCount = 8, AccessibleName = "发现的游戏客户端" };
            foreach (string path in paths) list.Items.Add(path);
            list.SelectedIndex = 0;
            var content = UiTheme.Column(TextLabel("选择要由这个客户端配置管理的游戏目录。",38),list);
            content.Size = new Size(640,110); content.Dock = DockStyle.None;
            using (content)
                if (AntdUI.Modal.open(new AntdUI.Modal.Config(this,"发现的游戏客户端",content) { Width = 700, OkText = "使用所选目录", CancelText = "取消" }) == DialogResult.OK && list.SelectedIndex >= 0) SetPath(paths[list.SelectedIndex]);
        }
        void ReadInstalledPreset()
        {
            var preset = GameDetector.ReadPreset(selected.Settings.GamePath);
            if (preset == null) throw new InvalidDataException("游戏目录中没有可读取的配置，请首次手动选择插件或导入配置。");
            preset.Source = "从已安装阿斯兰读取"; selected.Settings.Preset = preset; selected.Settings.PendingPreset = false; selected.Settings.PendingRun = ""; Storage.Save(selected.Settings); RefreshUi(); AddLog("已读取 " + preset.Count + " 个插件选择项。");
        }
        void ShowRepairAdvice()
        {
            selected.RefreshLocal();
            var report = selected.LastReport ?? new InstallReport();
            string cache = RepairAdvice.CacheDirectory(selected.Settings.GamePath);
            string advice = RepairAdvice.Build(selected.Settings.GamePath,report);
            using (var dialog = UiTheme.Dialog(selected.Settings.Name + " · 安装问题处理方案",new Size(850,640)))
            {
                dialog.Resizable = true; dialog.MinimumSize = new Size(750,530);
                var body = new AntdUI.Panel { Dock = DockStyle.Fill, Back = UiTheme.Background, Padding = new Padding(22), Radius = 0 };
                UiTheme.Bind(body,()=>body.Back = UiTheme.Background);
                var explanation = new AntdUI.Input { Text = advice, Multiline = true, ReadOnly = true, AutoScroll = true, Dock = DockStyle.Fill, AccessibleName = "完整处理步骤" };
                UiTheme.Bind(explanation,()=>explanation.BackColor = UiTheme.Surface);
                var links = new AntdUI.StackPanel { Vertical = true, Gap = 6, AutoScroll = true, Height = report.Incomplete ? 150 : 44, Dock = DockStyle.Bottom };
                foreach (string name in report.FailedArchives)
                {
                    string url = RepairAdvice.DownloadFor(report,name);
                    var link = UiTheme.Button(url == "" ? name + "：无可验证的官方下载链接" : "官方下载 · " + name,null,false,"DownloadOutlined");
                    link.Height = 40; link.TextAlign = ContentAlignment.MiddleLeft; link.Type = AntdUI.TTypeMini.Primary; link.Ghost = true; link.AutoEllipsis = true; link.Enabled = url != "";
                    link.Click += (s,e)=>Guard(()=>Process.Start(new ProcessStartInfo(url){UseShellExecute=true})); links.Controls.Add(link); links.Controls.SetChildIndex(link,0);
                }
                if (!report.Incomplete) links.Controls.Add(TextLabel("当前没有需要补下载的模组包。",36,10));
                var destination = new AntdUI.Input { Text = cache, ReadOnly = true, PrefixSvg = "FolderOutlined", Height = 46, Dock = DockStyle.Bottom, AccessibleName = "文件放置目录" };
                var actions = UiTheme.Actions(
                    UiTheme.Button("打开缓存目录",()=>Guard(()=>{Directory.CreateDirectory(cache);Process.Start(new ProcessStartInfo(cache){UseShellExecute=true});})),
                    UiTheme.Button("复制完整方案",()=>Clipboard.SetText(advice),true),
                    UiTheme.Button("阿斯兰官网",()=>Process.Start(new ProcessStartInfo(Release.Page){UseShellExecute=true})),
                    UiTheme.Button("关闭",()=>dialog.Close()));
                actions.Dock = DockStyle.Bottom;
                body.Controls.Add(explanation); body.Controls.Add(links); body.Controls.Add(destination); body.Controls.Add(actions);
                dialog.Controls.Add(body); body.BringToFront(); dialog.ResumeLayout(true); dialog.ShowDialog(this);
            }
        }
        void Import()
        {
            using (var dialog = new OpenFileDialog { Title = "导入到「" + selected.Settings.Name + "」", Filter = "阿斯兰配置 (*.json;*.inf;*.reg)|*.json;*.inf;*.reg", Multiselect = false })
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    var settings = Storage.Import(dialog.FileName, selected.Settings);
                    if (engines.Any(x => x != selected && !string.IsNullOrEmpty(settings.GamePath) && string.Equals(x.Settings.GamePath, settings.GamePath, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("备份中的目录已由另一个客户端管理。");
                    Storage.Save(settings); selected.Settings = settings;
                    if (GameDetector.IsGameFolder(settings.GamePath)) selected.RefreshLocal();
                    selected.Status = settings.PendingPreset ? "配置待应用" : "配置导入完成"; selected.Detail = "已保留本机客户端目录。点击「立即更新」应用插件选择，即使阿斯兰已是最新版。"; Bind(); AddLog("配置已导入；自动更新暂停，点击「立即更新」应用。原配置已保存为 .bak。");
                }
        }
        void Export()
        {
            using (var dialog = new SaveFileDialog { Title = "导出客户端配置", FileName = selected.Settings.Name + "-Aslain.json", Filter = "助手完整配置 (*.json)|*.json|阿斯兰插件选择 (*.inf)|*.inf", AddExtension = true })
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    if (dialog.FilterIndex == 2 && selected.Settings.Preset == null) throw new InvalidDataException("尚未保存插件选择，请先完成首次安装或读取已装配置。");
                    string text = dialog.FilterIndex == 2 ? selected.Settings.Preset.ToInf("") : Storage.Export(selected.Settings);
                    File.WriteAllText(dialog.FileName, text, dialog.FilterIndex == 2 ? System.Text.Encoding.Unicode : new System.Text.UTF8Encoding(true)); AddLog("已导出配置：" + dialog.FileName);
                }
        }
        void AddClient() { EditClientDialog(true); }
        void EditClient() { EditClientDialog(false); }
        bool ConfirmClientDeletion(Settings settings)
        {
            return AntdUI.Modal.open(new AntdUI.Modal.Config(this,"删除客户端？",
                "将从助手中移除「" + settings.Name + "」及其插件预设。\n游戏文件和已安装的模组会保留。",AntdUI.TType.Warn)
                { OkText = "删除", CancelText = "取消", MaskClosable = false, Width = 540 }) == DialogResult.OK;
        }
        void DeleteClient()
        {
            var target = selected;
            if (target == null || confirmingDelete || engines.Any(x=>x.Busy) || selfUpdate.Busy || shutdownForUpdate) return;
            confirmingDelete = true; RefreshUi();
            try
            {
                if (!confirmDelete(target.Settings)) return;
                // Persist first: if the file is locked or inaccessible, keep the UI and engine.
                Storage.DeleteClient(target.Settings.Id);
                int index = engines.IndexOf(target);
                engines.Remove(target);
                var tile = tiles[target]; tiles.Remove(target); clientTiles.Controls.Remove(tile); tile.Dispose();
                target.Dispose();
                selected = engines.Count == 0 ? null : engines[Math.Min(index,engines.Count-1)];
                Bind();
                if (selected == null) pages.SelectedIndex = 0;
                AddLog("已删除客户端「" + target.Settings.Name + "」，游戏和模组文件已保留。");
            }
            finally { confirmingDelete = false; RefreshUi(); }
        }
        void EditClientDialog(bool add)
        {
            if (add && engines.Count >= 20) throw new InvalidDataException("最多管理 20 个客户端。");
            var name = new AntdUI.Input { Height = 46, Text = add ? "我的外服客户端" : selected.Settings.Name, MaxLength = 40, PlaceholderText = "给这个客户端起个名字", AccessibleName = "客户端名称" };
            var kind = new AntdUI.Select { List = true, Height = 46, AccessibleName = "客户端类型" };
            kind.Items.Add("国服客户端（360）"); kind.Items.Add("外服客户端（Wargaming / Steam）");
            kind.SelectedIndex = !add && selected.Settings.Server == "CN" ? 0 : 1;
            var content = UiTheme.Column(TextLabel("客户端名称",28),name,TextLabel("客户端类型",28),kind);
            content.Size = new Size(460,210); content.Dock = DockStyle.None;
            using (content)
            {
                var modal = new AntdUI.Modal.Config(this,add ? "新增客户端" : "编辑客户端",content) { Width = 520, OkText = "保存", CancelText = "取消", LoadingDisable = true };
                modal.OnOk = config =>
                {
                    try
                    {
                        var settings = add ? new Settings { Id = Guid.NewGuid().ToString("N") } : Storage.Json().Deserialize<Settings>(Storage.Json().Serialize(selected.Settings));
                        settings.Name = name.Text.Trim(); settings.Server = kind.SelectedIndex == 0 ? "CN" : "INTL"; settings.Validate(); Storage.Save(settings);
                        if (add) { AddEngine(settings); selected = engines.Last(); } else selected.Settings = settings;
                        Bind(); return true;
                    }
                    catch (Exception ex) { name.Status = AntdUI.TType.Error; name.PlaceholderText = ex.Message; AddLog(ex.Message); return false; }
                };
                AntdUI.Modal.open(modal);
            }
        }
        async Task RunSelected(bool install, bool interactive)
        {
            if (!booted || selected == null || engines.Any(x => x.Busy) || selfUpdate.Busy || shutdownForUpdate || confirmingDelete) return;
            await selected.Run(install, interactive, true);
        }
        async Task CheckAll(bool update)
        {
            if (!booted || engines.Any(x => x.Busy) || selfUpdate.Busy || shutdownForUpdate || confirmingDelete) return;
            foreach (var engine in engines.Where(x => !string.IsNullOrEmpty(x.Settings.GamePath) && (!update || x.Settings.AutoUpdate)).ToArray()) await engine.Run(update, false, true);
        }
        async Task Tick()
        {
            if(shutdownForUpdate && DateTime.UtcNow>shutdownLease) { shutdownForUpdate=false; RefreshUi(); }
            if (!booted || shutdownForUpdate || confirmingDelete) return;
            bool available=System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();
            if(available && !networkWasAvailable)
            {
                foreach(var engine in engines.Where(x=>x.Settings.AutoUpdate && x.WaitingForNetwork)) engine.NextCheck=DateTimeOffset.Now;
                selfUpdate.NextCheck=DateTimeOffset.Now.AddSeconds(5);
            }
            networkWasAvailable=available;
            if (engines.Any(x => x.Busy) || selfUpdate.Busy) return;
            if ((DateTimeOffset.Now - lastLocalPoll).TotalMinutes >= 1)
            {
                lastLocalPoll = DateTimeOffset.Now;
                foreach (var engine in engines.Where(x => x.Settings.AutoUpdate))
                    try
                    {
                        var local = GameDetector.Inspect(engine.Settings.GamePath);
                        if (engine.Game != null && local.GameVersion != engine.Game.GameVersion) engine.NextCheck = DateTimeOffset.Now;
                        engine.Game = local;
                    }
                    catch { /* The scheduled check reports inaccessible / disconnected targets without losing configuration. */ }
            }
            var due = engines.Where(x => x.Settings.AutoUpdate && x.NextCheck <= DateTimeOffset.Now).OrderBy(x => x.NextCheck).FirstOrDefault();
            if (due != null) await due.Run(true, false, false);
            else if(selfUpdate.Settings.Enabled && selfUpdate.NextCheck<=DateTimeOffset.Now) await CheckAssistant(false);
            RefreshUi();
        }
        async Task CheckAssistant(bool manual)
        {
            if(!booted || shutdownForUpdate || confirmingDelete || engines.Any(x=>x.Busy) || selfUpdate.Busy) return;
            await selfUpdate.Run(manual);
        }
        byte HandleInstanceCommand(byte command)
        {
            if(command==InstanceControl.Activate) { Reveal(); return InstanceControl.Ready; }
            if(command==InstanceControl.Resume) { shutdownForUpdate=false; RefreshUi(); return InstanceControl.Ready; }
            if(command!=InstanceControl.Exit) return InstanceControl.Unavailable;
            if(confirmingDelete || engines.Any(x=>x.Installing) || GameDetector.InstallerRunning()) return InstanceControl.Busy;
            shutdownForUpdate=true; shutdownLease=DateTime.UtcNow.AddSeconds(30);
            foreach(var engine in engines) engine.Cancel();
            selfUpdate.Cancel();
            if(loadingClients || engines.Any(x=>x.Busy) || (selfUpdate.Busy && !selfUpdate.Handoff)) return InstanceControl.Waiting;
            return InstanceControl.Ready;
        }
        void AddLog(string message)
        {
            if (IsDisposed) return;
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine;
            if (logs.Text.Length > 70000) logs.Text = logs.Text.Substring(logs.Text.Length - 40000);
            logs.Text += line; logs.SelectionStart = logs.Text.Length;
            try
            {
                string dir = Path.Combine(Storage.Root, "logs"); Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, DateTime.Now.ToString("yyyy-MM-dd") + ".log"), line);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        void Reveal() { Show(); WindowState = FormWindowState.Normal; UiTheme.LayoutVisible(this); Invalidate(true); Activate(); }
        void Quit()
        {
            if (engines.Any(x => x.Installing)) { Reveal(); UiTheme.Notice(this, "正在安装", "阿斯兰正在安装，请等待安装完成后再退出助手。", AntdUI.TType.Warn); return; }
            foreach (var engine in engines) engine.Cancel();
            quitting = true; Close();
        }
        void OnClosing(object sender, FormClosingEventArgs args)
        {
            if (!quitting && args.CloseReason == CloseReason.UserClosing) { args.Cancel = true; Hide(); return; }
            timer.Stop(); tray.Visible = false; tray.Dispose();
            startupCancellation.Cancel(); startupCancellation.Dispose();
            if(instanceServer!=null) instanceServer.Dispose();
            if(activationServer!=null) activationServer.Dispose();
            selfUpdate.Dispose();
            foreach (var engine in engines) engine.Dispose();
        }
    }
}
