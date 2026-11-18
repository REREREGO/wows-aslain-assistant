# 窝窝斯坐牢小助手

<img src="assets/logo/lan-blue-shield.png" width="96" alt="懒字蓝盾图标">

因为懒得手动更新阿斯兰，写了这个助手。

第一次选好插件，以后让它定期检查、下载并安装更新。国服、外服都能用，也可以添加多个游戏目录，每套客户端的插件配置分别保存。

**[下载最新版](https://github.com/REREREGO/wows-aslain-assistant/releases/latest/download/AslainAssistant.Setup.exe)** · [反馈问题](https://github.com/REREREGO/wows-aslain-assistant/issues)

适用于 Windows，需要 .NET Framework 4.8。更新的是阿斯兰经典模组包。

![更新概览](docs/images/dashboard-light.png)

<details>
<summary>看看深色界面</summary>

![深色界面](docs/images/dashboard-dark.png)

</details>

## 开始使用

1. 下载并运行 `AslainAssistant.Setup.exe`，安装后从桌面快捷方式打开。
2. 左侧选国服或外服，在 **客户端设置** 里选择游戏目录，也可以点 **自动发现**。目录选到包含 `WorldOfWarships.exe` 的那一层。
3. 已经装过阿斯兰的，助手会尝试读取原来的插件选择；没装过的，点 **立即更新**，在阿斯兰安装器里手动选一次插件。
4. 在 **客户端设置** 中开启 **自动下载并更新此客户端**，设好检查间隔。想开机就运行，再打开 **开机登录后启动助手**。

设置完就可以关掉窗口，助手会留在托盘里。双击托盘图标重新打开，右键选择 **退出助手** 才会完全退出。

游戏正在运行时会等你退出再安装。如果模组包对应的游戏版本更高，会先下载，等游戏更新后再装。安装过程中可能需要确认 Windows 的管理员授权。

## 插件和客户端配置

- **有多套游戏**：点左下角 **添加客户端**，分别设置路径、插件和检查间隔。
- **不需要的客户端**：选中后到 **客户端设置 → 删除客户端**。只移除助手里的配置，游戏和已安装的模组会保留。
- **想换插件**：进入 **插件配置 → 选择插件 / 重装**，重新打开阿斯兰安装器选择。
- **已经手动装好了**：点 **插件配置 → 读取已装配置**，让助手沿用这次的选择。
- **备份或换电脑**：在 **插件配置** 里导入、导出。导入支持 JSON、INF、REG，导出支持 JSON、INF。导入后保留本机游戏目录，确认插件选择后需要重新开启自动更新。

左下角的 **外观** 可以切换深色、浅色，重启后会记住。

## 更新和报错

助手自身也会自动更新，开关在 **客户端设置 → 助手更新**。手动下载新版安装包覆盖安装也可以，安装器会自动退出旧助手，保留原来的配置。

阿斯兰从官网下载，助手新版从 GitHub 下载，目前没有内置国内镜像。断网时会保留现有模组和配置，开启自动更新后约 15 分钟重试一次。

如果提示模组没有装完整，先点 **查看处理方案**。能从日志中找到下载地址的资源包，会列出链接和该放的缓存目录。按提示下载，保持原文件名放进去，不要解压；重装时也别勾选清空下载缓存。

仍然装不上，可以到 [Issues](https://github.com/REREREGO/wows-aslain-assistant/issues) 附上报错截图和 **运行日志** 中的相关记录，注明游戏及阿斯兰版本。

## 从源码构建

C# / .NET Framework 4.8 / WinForms，界面使用 AntdUI。在 Windows PowerShell 中运行：

```powershell
.\build.ps1 -OutputDirectory build
```

发布流程见 [发布说明](docs/RELEASING.md)，组件库许可见 [AntdUI LICENSE](vendor/AntdUI/2.4.19/LICENSE.txt)。

这是个人做的非官方工具，阿斯兰模组包来自 [Aslain](https://aslain.com/)。
