# 发布新版助手

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

不要重用同一版本号替换不同的安装包。安装包内容变化后，应重新构建新版本并重新签名。

新增国内镜像时，需要先核实来源和同步方式，再更新客户端允许的地址范围及发布流程。镜像提供的文件仍然必须通过同一签名和哈希校验；不能把普通文件分享地址直接当作可信更新源。
