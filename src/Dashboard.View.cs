using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Ant = AntdUI;

namespace AslainAssistant
{
    public sealed partial class Dashboard
    {
        Color blue { get { return UiTheme.Primary; } }
        Color ink { get { return UiTheme.Ink; } }
        UiPreferences preferences;
        readonly List<UpdateEngine> engines = new List<UpdateEngine>();
        readonly Dictionary<UpdateEngine, Ant.Button> tiles = new Dictionary<UpdateEngine, Ant.Button>();
        readonly List<Control> mutable = new List<Control>();
        readonly List<Control> clientControls = new List<Control>();
        readonly Ant.StackPanel clientTiles = new Ant.StackPanel();
        readonly Ant.Input gamePath = new Ant.Input(), logs = new Ant.Input();
        readonly Ant.Label gameValue = UiTheme.Label("—",22,true), installedValue = UiTheme.Label("—",22,true), latestValue = UiTheme.Label("待检查",22,true);
        readonly Ant.Label presetLabel = UiTheme.Label(""), statusLabel = UiTheme.Label("准备就绪",19,true), detailLabel = UiTheme.Label(""), scheduleLabel = UiTheme.Label("",9), selectedLabel = UiTheme.Label("我的客户端",21,true);
        readonly Ant.Switch automatic = new Ant.Switch(), startup = new Ant.Switch();
        readonly Ant.InputNumber hours = new Ant.InputNumber();
        readonly Ant.Progress progress = new Ant.Progress();
        readonly Ant.Tabs pages = new Ant.Tabs();
        readonly Ant.Segmented themeMode = new Ant.Segmented();
        readonly Ant.Switch assistantAutomatic = new Ant.Switch();
        readonly Ant.Label assistantUpdateStatus = UiTheme.Label("");
        readonly SelfUpdateEngine selfUpdate;
        InstanceServer instanceServer;
        InstanceServer activationServer;
        bool shutdownForUpdate;
        DateTime shutdownLease;
        bool networkWasAvailable = System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly bool background;
        UpdateEngine selected;
        Ant.Button cancel, update, deleteClient, addClient;
        Ant.Panel statusCard;
        bool binding, quitting, booted;
        bool loadingClients;
        readonly CancellationTokenSource startupCancellation = new CancellationTokenSource();
        Func<List<Settings>,CancellationToken,Task<List<ClientSnapshot>>> loadClients = LoadStartupClients;
        bool confirmingDelete;
        Func<Settings,bool> confirmDelete;
        Action<string,string> warnGameRunning;
        DateTimeOffset lastLocalPoll = DateTimeOffset.MinValue;

        public Dashboard(bool startHidden,bool ownsInstance=false)
        {
            SuspendLayout(); background = startHidden;
            preferences = UiPreferences.Load(Storage.Root); UiTheme.SetDark(preferences.Dark);
            bool normalProfile = string.Equals(System.IO.Path.GetFullPath(Storage.Root).TrimEnd('\\'),System.IO.Path.GetFullPath(UiPreferences.DefaultRoot).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase);
            selfUpdate = new SelfUpdateEngine(Storage.Root,new AssistantUpdateClient(),normalProfile);
            selfUpdate.CanInstall = ()=>!engines.Any(x=>x.Busy) && !GameDetector.GameRunning() && !GameDetector.InstallerRunning();
            selfUpdate.Changed += ()=>{if(!IsDisposed)RefreshUi();};
            selfUpdate.Log += text=>AddLog("[助手更新] "+text);
            Text = "窝窝斯坐牢小助手 · 阿斯兰更新";
            Font = new Font("Microsoft YaHei UI",10); UiTheme.Bind(this,()=>{ForeColor = ink; BackColor = UiTheme.Background;});
            ClientSize = new Size(1160,820); MinimumSize = new Size(980,640);
            StartPosition = FormStartPosition.CenterScreen; Icon = Brand.LoadIcon();
            AutoScaleDimensions = new SizeF(96,96); AutoScaleMode = AutoScaleMode.Dpi;
            confirmDelete = ConfirmClientDeletion;
            warnGameRunning = ShowGameRunningNotice;
            BuildUi();
            pages.SelectedIndex = preferences.PageIndex;
            pages.SelectedIndexChanged += (s,e)=>Guard(()=>{UiTheme.LayoutVisible(pages); if (!booted) return; preferences.PageIndex = pages.SelectedIndex; preferences.Save(Storage.Root);});
            var menu = new ContextMenuStrip();
            menu.Items.Add("打开仪表盘",null,(s,e)=>Reveal());
            menu.Items.Add("立即检查所有客户端",null,async (s,e)=>await CheckAll(false));
            menu.Items.Add("立即更新已开启的客户端",null,async (s,e)=>await CheckAll(true));
            menu.Items.Add(new ToolStripSeparator()); menu.Items.Add("退出助手",null,(s,e)=>Quit());
            tray.Icon = Icon; tray.Text = "窝窝斯坐牢小助手"; tray.ContextMenuStrip = menu; tray.Visible = true;
            tray.MouseClick += (s,e)=>{if(e.Button == MouseButtons.Left) Reveal();}; tray.BalloonTipClicked += (s,e)=>Reveal();
            FormClosing += OnClosing;
            Shown += async (s,e) =>
            {
                var area = Screen.FromControl(this).WorkingArea;
                MinimumSize = new Size(Math.Min(MinimumSize.Width,area.Width-40),Math.Min(MinimumSize.Height,area.Height-40));
                Size = new Size(Math.Min(Width,area.Width-40),Math.Min(Height,area.Height-40));
                Location = new Point(area.Left+(area.Width-Width)/2,area.Top+(area.Height-Height)/2);
                if (!IsDisposed) instanceServer = new InstanceServer(command=>(byte)Invoke(new Func<byte>(()=>HandleInstanceCommand(command))),()=>{if(!IsDisposed)BeginInvoke(new Action(()=>{quitting=true;Close();}));});
                if (!IsDisposed && ownsInstance) activationServer = new InstanceServer(command=>(byte)Invoke(new Func<byte>(()=>HandleInstanceCommand(command))),()=>{if(!IsDisposed)BeginInvoke(new Action(()=>{quitting=true;Close();}));},InstanceControl.ActivationPipeName);
                await Initialize();
            };
            timer.Interval = 10000; timer.Tick += async (s,e)=>await Tick();
            ResumeLayout(true);
        }
        Ant.Button Button(string text, Action action, bool primary = false, string icon = null)
        {
            var button = UiTheme.Button(text,()=>Guard(action),primary,icon); mutable.Add(button); return button;
        }
        Ant.Button ClientButton(string text, Action action, bool primary = false, string icon = null)
        {
            var button = Button(text,action,primary,icon); clientControls.Add(button); return button;
        }
        void Guard(Action action)
        {
            try { action(); }
            catch(Exception ex) { AddLog("操作未完成 · "+ex.Message); UiTheme.Notice(this,"操作未完成",ex.Message,Ant.TType.Warn); RefreshUi(); }
        }
        Ant.Label TextLabel(string text, int height, float size = 10, bool bold = false)
        {
            var label = UiTheme.Label(text,size,bold); label.Height = height; return label;
        }
        Ant.Panel Card(string title, int height, params Control[] content)
        {
            var card = UiTheme.Card(height);
            var heading = TextLabel(title,30,12,true); heading.Dock = DockStyle.Top;
            card.Controls.Add(UiTheme.Column(content)); card.Controls.Add(heading); return card;
        }
        Control ToggleRow(Ant.Switch toggle, string text, string description)
        {
            var title = TextLabel(text,27,11,true);
            var hint = TextLabel(description,28,9); UiTheme.Secondary(hint);
            var labels = UiTheme.Column(title,hint); labels.Gap = 0; labels.AutoScroll = false;
            toggle.Size = new Size(56,28); toggle.CheckedText = "开"; toggle.UnCheckedText = "关"; toggle.AccessibleName = text;
            var toggleHolder = UiTheme.Actions(toggle); toggleHolder.Align = Ant.TAlignFlow.RightCenter; toggleHolder.Dock = DockStyle.Right; toggleHolder.Width = 72;
            var holder = new Ant.Panel { Back = Color.Transparent, Height = 60, Padding = Padding.Empty, Margin = Padding.Empty };
            holder.Controls.Add(labels); holder.Controls.Add(toggleHolder); mutable.Add(toggle); return holder;
        }
        Control Metric(string caption, Ant.Label value, string hint)
        {
            var card = UiTheme.Card(112); card.Padding = new Padding(18,12,18,12);
            var label = TextLabel(caption,24,9); UiTheme.Secondary(label); label.Dock = DockStyle.Top;
            var note = TextLabel(hint,22,8.5f); UiTheme.Secondary(note); note.Dock = DockStyle.Bottom;
            value.Dock = DockStyle.Fill; value.TextMultiLine = false;
            card.Controls.Add(value); card.Controls.Add(note); card.Controls.Add(label); return card;
        }
        Ant.TabPage Page(string name, string icon, Control content)
        {
            var page = new Ant.TabPage { Text = name, IconSvg = icon, Padding = new Padding(0,16,0,0), BackColor = UiTheme.Background };
            UiTheme.Bind(page,()=>page.BackColor = UiTheme.Background);
            var column = content as Ant.StackPanel; if (column != null) column.AutoScroll = true;
            page.Controls.Add(content); pages.Pages.Add(page); return page;
        }
        void BuildUi()
        {
            var header = new Ant.PageHeader { Text = "窝窝斯坐牢小助手", SubText = "阿斯兰自动更新", ShowIcon = true, Icon = Brand.LoadLogo(), ShowButton = true, DividerShow = true, Height = 64, Dock = DockStyle.Top, Font = new Font(Font.FontFamily,14,FontStyle.Bold), SubFont = new Font(Font.FontFamily,9) };
            UiTheme.Bind(header,()=>header.BackColor = UiTheme.Surface);
            var sidebar = new Ant.Panel { Width = 240, Dock = DockStyle.Left, Radius = 0, Padding = new Padding(16,22,16,16), BorderWidth = 1 };
            UiTheme.Bind(sidebar,()=>{sidebar.Back = UiTheme.Surface; sidebar.BorderColor = UiTheme.Border;});
            var sidebarTitle = TextLabel("游戏客户端",38,11,true); sidebarTitle.Dock = DockStyle.Top;
            clientTiles.Dock = DockStyle.Fill; clientTiles.Vertical = true; clientTiles.Gap = 10; clientTiles.AutoScroll = true; clientTiles.BackColor = Color.Transparent;
            addClient = Button("添加客户端",AddClient,false,"PlusOutlined"); addClient.Dock = DockStyle.Bottom;
            var about = TextLabel("Ctrl + 1–4 切换功能页\n关闭窗口后继续在托盘守护",62,8.5f); UiTheme.Secondary(about); about.Dock = DockStyle.Bottom;
            themeMode.Items.Add(new Ant.SegmentedItem { Text = "浅色", IconSvg = "SunOutlined" });
            themeMode.Items.Add(new Ant.SegmentedItem { Text = "深色", IconSvg = "MoonOutlined" });
            themeMode.Full = true; themeMode.Height = 42; themeMode.IconAlign = Ant.TAlignMini.Left; themeMode.IconRatio = 1.1f; themeMode.SelectIndex = preferences.Dark ? 1 : 0; themeMode.AccessibleName = "界面主题：浅色或深色";
            themeMode.SelectIndexChanged += (s,e)=>Guard(ChangeTheme);
            var themeSection = UiTheme.Column(TextLabel("外观",26,9),themeMode); themeSection.Dock = DockStyle.Bottom; themeSection.Height = 82; themeSection.Gap = 6;
            sidebar.Controls.Add(clientTiles); sidebar.Controls.Add(themeSection); sidebar.Controls.Add(about); sidebar.Controls.Add(addClient); sidebar.Controls.Add(sidebarTitle);
            var main = new Ant.Panel { Dock = DockStyle.Fill, Back = UiTheme.Background, Radius = 0, Padding = new Padding(24,14,24,14) };
            UiTheme.Bind(main,()=>main.Back = UiTheme.Background);
            var selection = new Ant.Panel { Height = 60, Dock = DockStyle.Top, Back = Color.Transparent };
            selectedLabel.Dock = DockStyle.Fill;
            selection.Controls.Add(selectedLabel);
            var metrics = UiTheme.Grid("33.33% 33.33% 33.34%",Metric("游戏客户端",gameValue,"以当前客户端实际版本为准"),Metric("已安装阿斯兰",installedValue,"插件安装版本"),Metric("官网经典版",latestValue,"检查后显示最新发布"));
            metrics.Dock = DockStyle.Top; metrics.Height = 132;
            pages.Dock = DockStyle.Fill; pages.Gap = 22;
            pages.EnablePageCloseByMouseMiddle = false; pages.EnablePageCloseByMouseDoubleClick = false; pages.EnablePageScrolling = false;
            scheduleLabel.Height = 32; scheduleLabel.Dock = DockStyle.Bottom; UiTheme.Secondary(scheduleLabel);

            statusCard = UiTheme.Card(174); statusCard.Padding = new Padding(16);
            statusLabel.Height = 34; statusLabel.ForeColor = blue; detailLabel.Height = 68; detailLabel.AutoEllipsis = true;
            progress.Height = 12; progress.Value = 0; progress.Radius = 4; progress.Visible = false; UiTheme.Bind(progress,()=>progress.Fill = blue);
            var statusContent = UiTheme.Column(statusLabel,detailLabel,progress); statusContent.Gap = 8; statusContent.AutoScroll = false; statusCard.Controls.Add(statusContent);
            update = ClientButton("立即更新",()=>{var task=RunSelected(true,false);},true,"CloudDownloadOutlined"); update.Width = 148;
            cancel = Button("取消下载",()=>{var active=engines.FirstOrDefault(x=>x.Busy);if(active!=null)active.Cancel();},false,"CloseOutlined"); mutable.Remove(cancel);
            var primaryActions = UiTheme.Actions(update,ClientButton("仅检查版本",()=>{var task=RunSelected(false,false);},false,"SyncOutlined"),ClientButton("查看处理方案",ShowRepairAdvice,false,"QuestionCircleOutlined"),cancel);
            var ruleText = TextLabel("同一游戏版本：沿用插件选择自动安装更新。\n游戏版本不匹配：先缓存新版，等待客户端更新后再安装。\n游戏运行中：等待游戏退出。",90);
            var rules = Card("更新规则",162,ruleText); rules.Padding = new Padding(16);
            Page("更新概览","DashboardOutlined",UiTheme.Column(statusCard,primaryActions,rules));

            gamePath.ReadOnly = true; gamePath.Height = 44; gamePath.PrefixSvg = "FolderOutlined"; gamePath.PlaceholderText = "先选择这个客户端的游戏根目录"; gamePath.AccessibleName = "游戏根目录";
            deleteClient = ClientButton("删除客户端",DeleteClient,false,"DeleteOutlined"); deleteClient.Type = Ant.TTypeMini.Error; deleteClient.Ghost = true; deleteClient.AccessibleName = "删除当前客户端配置";
            var path = Card("游戏安装目录",192,gamePath,UiTheme.Actions(Button("选择游戏目录",BrowsePath,false,"FolderOpenOutlined"),Button("自动发现",DiscoverPaths,false,"SearchOutlined"),Button("编辑客户端",EditClient,false,"EditOutlined"),deleteClient));
            clientControls.Add(path);
            automatic.CheckedChanged += (s,e)=>Guard(SaveSwitches);
            hours.Minimum = 1; hours.Maximum = 24; hours.Value = 1; hours.DecimalPlaces = 0; hours.AlwaysShowControl = true; hours.WheelModifyEnabled = false; hours.Height = 44; hours.Width = 140; hours.SuffixText = "小时"; hours.AccessibleName = "自动检查间隔（小时）";
            hours.ValueChanged += (s,e)=>Guard(SaveSwitches); mutable.Add(hours);
            var intervalLabel = TextLabel("检查频率",44); intervalLabel.Width = 100;
            var autoCard = Card("自动更新",200,ToggleRow(automatic,"自动下载并更新此客户端","仅应用于左侧选中的客户端"),UiTheme.Actions(intervalLabel,hours));
            clientControls.Add(autoCard);
            startup.CheckedChanged += (s,e)=>Guard(()=>{if(binding)return;Startup.Set(startup.Checked,Application.ExecutablePath);AddLog(startup.Checked?"已开启开机登录自启。":"已关闭开机自启。");});
            var startupCard = Card("助手设置",146,ToggleRow(startup,"开机登录后启动助手","所有客户端共用此设置"));
            assistantAutomatic.Checked = selfUpdate.Allowed && selfUpdate.Settings.Enabled;
            assistantAutomatic.CheckedChanged += (s,e)=>Guard(()=>{if(binding || !selfUpdate.Allowed)return;selfUpdate.SetEnabled(assistantAutomatic.Checked);});
            assistantUpdateStatus.Height = 64;
            var assistantActions = UiTheme.Actions(Button("检查助手更新",()=>{var task=CheckAssistant(true);},false,"SyncOutlined"),Button("项目与使用说明",()=>Process.Start(new ProcessStartInfo(AssistantUpdateClient.Repository){UseShellExecute=true}),false,"GithubOutlined"));
            var assistantCard = Card("助手更新 · 当前 "+selfUpdate.CurrentVersion.ToString(3),292,ToggleRow(assistantAutomatic,"自动接收助手新版","每 6 小时检查，安装时自动退出旧助手"),assistantUpdateStatus,assistantActions);
            Page("客户端设置","SettingOutlined",UiTheme.Column(path,autoCard,startupCard,assistantCard));

            presetLabel.Height = 64;
            var presets = Card("当前插件预设",208,presetLabel,UiTheme.Actions(Button("读取已装配置",ReadInstalledPreset,false,"ReloadOutlined"),Button("导入配置",Import,false,"ImportOutlined"),Button("导出配置",Export,false,"ExportOutlined")));
            var chooseButton = Button("选择插件 / 重装",()=>{var task=RunSelected(true,true);},true,"AppstoreOutlined"); chooseButton.Width = 174;
            var choose = Card("管理插件选择",196,TextLabel("首次在阿斯兰安装器中选择插件；之后更新会沿用此客户端的预设。",48),UiTheme.Actions(chooseButton));
            clientControls.Add(presets); clientControls.Add(choose);
            var format = Card("配置文件",132,TextLabel("支持 JSON、INF 和阿斯兰 REG。每个客户端分别保存目录、插件和检查频率。",48));
            Page("插件配置","AppstoreOutlined",UiTheme.Column(presets,choose,format));

            logs.Dock = DockStyle.Fill; logs.Multiline = true; logs.ReadOnly = true; logs.AutoScroll = true; logs.WordWrap = true; UiTheme.Bind(logs,()=>logs.BackColor = UiTheme.Surface); logs.Font = new Font("Microsoft YaHei UI",9); logs.AccessibleName = "助手运行日志";
            var logContainer = new Ant.Panel { Dock = DockStyle.Fill, Back = Color.Transparent };
            var logActions = UiTheme.Actions(Button("打开日志目录",()=>{System.IO.Directory.CreateDirectory(Storage.Root);Process.Start(new ProcessStartInfo(Storage.Root){UseShellExecute=true});},false,"FolderOpenOutlined"),ClientButton("查看处理方案",ShowRepairAdvice,false,"QuestionCircleOutlined")); logActions.Dock = DockStyle.Bottom;
            logContainer.Controls.Add(logs); logContainer.Controls.Add(logActions);
            Page("运行日志","FileTextOutlined",logContainer);
            main.Controls.Add(pages); main.Controls.Add(scheduleLabel); main.Controls.Add(metrics); main.Controls.Add(selection);
            Controls.Add(main); Controls.Add(sidebar); Controls.Add(header);
        }
    }
}
