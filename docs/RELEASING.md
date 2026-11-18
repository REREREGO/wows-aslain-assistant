# 发布新版助手

只有用户明确要求“发布”时才执行本文的发布步骤。日常开发仅在本地构建、测试和安装，不递增版本号、不推送仓库，也不生成或上传发布更新清单。当前国内镜像迭代版本按用户指定为 1.0.1，此次仅推送源码。

用户单独要求推送到 GitHub 时，只同步当前源码，保持版本号，不自动发布安装包。

本机助手自动更新保持关闭，避免远程已发布版本覆盖本地开发版；阿斯兰插件的自动更新不受影响。

## 需要保留的文件

- `src/UpdateTrust.cs` 是客户端内置的更新验证公钥，可提交仓库。
- 私钥默认位于 `%LOCALAPPDATA%\WoWS-Aslain-Assistant-Release\signing-key.xml`。不要提交、上传或打进安装包，另行做好维护者自己的备份。
- 更新信息的签名用于应用内校验，不等同于 Windows Authenticode 代码签名。

## 发布顺序

1. 修改 `src/AssemblyInfo.cs` 的版本号，并更新文档中的版本说明。
2. 构建正式文件，运行业务测试和界面测试。
3. 在 GitHub 创建对应标签的草稿 Release，上传 `AslainAssistant.Setup.exe`。
4. 用 `tools/Sign-Release.ps1` 生成 `updates/latest.json`，下载地址必须指向这次 Release 的安装器。可同时填入该资产的 GitHub API 下载地址作为备用。
5. 将 `latest.json` 作为 Release 资产上传，并提交到仓库的 `updates/latest.json`。
6. 确认安装器与签名对应后，将草稿 Release 发布。
7. 使用 `AslainAssistant.Tests.exe --self-live` 从实际发布源下载并校验；该命令不会执行安装器。

示例：

```powershell
.\build.ps1 -OutputDirectory release
.\release\AslainAssistant.Tests.exe
.\release\AslainAssistant.UiTests.exe
.\tools\Sign-Release.ps1 -Version 1.4.0
```

正式发布时，不要重用已发布的版本号替换不同的安装包。本地开发可以反复构建并覆盖安装同一版本，无需签发更新清单。

新增国内镜像时，需要先核实来源和同步方式，再更新客户端允许的地址范围及发布流程。镜像提供的文件仍然必须通过同一签名和哈希校验；不能把普通文件分享地址直接当作可信更新源。
