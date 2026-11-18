# 发布新版助手

只有用户明确要求“发布”时才执行本文的发布步骤。日常开发仅在本地构建、测试和安装，不递增版本号、不推送仓库，也不生成或上传发布更新清单。用户已于 2026-11-19 授权正式发布 1.0.1，包括源码、README、安装包与更新清单；海猴雷达继续留在本地，不进入本次发布。此授权不延续到后续迭代。

用户单独要求推送到 GitHub 时，只同步当前源码，保持版本号，不自动发布安装包。

本机助手自动更新保持关闭，避免远程已发布版本覆盖本地开发版；阿斯兰插件的自动更新不受影响。

## 需要保留的文件

- `src/UpdateTrust.cs` 是客户端内置的更新验证公钥，可提交仓库。
- 私钥默认位于 `%LOCALAPPDATA%\WoWS-Aslain-Assistant-Release\signing-key.xml`。不要提交、上传或打进安装包，另行做好维护者自己的备份。
- 更新信息的签名用于应用内校验，不等同于 Windows Authenticode 代码签名。

## 发布顺序

1. 确认 `src/AssemblyInfo.cs` 使用用户指定的版本号，不自行递增；更新 README、实际界面截图及文末的全部版本下载列表。
2. 构建正式文件，运行业务测试和界面测试。
3. 在 GitHub 创建对应标签的草稿 Release，上传 `AslainAssistant.Setup.exe`。
4. 用 `tools/Sign-Release.ps1` 生成 `updates/latest.json`，下载地址必须指向这次 Release 的安装器。可同时填入该资产的 GitHub API 下载地址作为备用。
5. 将 `latest.json` 作为 Release 资产上传，并提交到仓库的 `updates/latest.json`。
6. 确认安装器与签名对应后，将草稿 Release 发布并明确设为 Latest（API `make_latest: "true"`）。1.0.1 的编号低于历史 1.4.x，不能依赖按版本号推断最新版。
7. 使用 `AslainAssistant.Tests.exe --self-live` 从实际发布源下载并校验；该命令不会执行安装器。

示例：

```powershell
.\build.ps1 -OutputDirectory release
.\release\AslainAssistant.Tests.exe
.\release\AslainAssistant.UiTests.exe
.\tools\Sign-Release.ps1 -Version 1.0.1
```

正式发布时，不要重用已发布的版本号替换不同的安装包。本地开发可以反复构建并覆盖安装同一版本，无需签发更新清单。

助手历史版本暂不限制数量或保留时间。按用户后续要求，README 下载列表从 1.0.1 开始，包含最新版本及此后的历史版本，不再列出 1.4.x。未要求删除 GitHub 旧 Release、标签或安装包。阿斯兰镜像的独立保留规则不受此要求影响。

1.0.1 起根据签名内的发布时间识别编号重置前的旧发行版，避免回装 1.4.x。用户明确无需单独说明旧版迁移。

新增国内镜像时，需要先核实来源和同步方式，再更新客户端允许的地址范围及发布流程。镜像提供的文件仍然必须通过同一签名和哈希校验；不能把普通文件分享地址直接当作可信更新源。
