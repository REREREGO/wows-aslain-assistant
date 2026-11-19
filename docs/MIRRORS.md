# 国内阿斯兰镜像

玩家电脑不必先访问阿斯兰官网才能查版本：助手优先读取国内托管的 `catalog.json`，挑选与当前游戏版本匹配的最新安装包。默认两个国内查询入口，最多可配置三个，按顺序尝试，每个最多等待 8 秒；全部不可用时回退官网。过期、签名错误的清单不会用于安装。查询失败时保留现有插件和配置，按原有网络重试机制恢复。

版本清单和下载地址分开维护。清单可以放在 Gitee 仓库及另一个国内 HTTPS 站点；其中的安装包可以有多个下载地址。安装包必须同时通过文件大小、Windows PE 格式和官网 SHA-256 校验，失败自动换源。助手自身更新仍使用原来的 GitHub 发布渠道。

## 蓝奏云备用下载

[阿斯兰备用目录](https://wwbcm.lanzouq.com/b01i60xlde)，提取码 `6fpw`。

自动下载不可用时，在 **客户端设置 → 国内镜像 → 蓝奏云下载** 打开目录，下载与助手显示版本相同的 `.exe`，再点 **导入已下载的安装包**。助手校验后放入自己的缓存。随后点 **使用历史配置更新**，或者在插件配置页点 **使用该配置更新**。

蓝奏云分享页不是安装包直链。本版本提供手工上传和下载的备用入口，没有使用未经确认的网盘上传接口，也不会把网盘网页当作安装包。维护者上传官网原始 `.exe`，不要重新打包。分享目录里是否已有文件，以实际目录为准。

安装器中的部分插件仍会从 Aslain 服务器下载 `.7z` 资源；镜像 `.exe` 不代表这些资源也已镜像。缺失资源可通过助手的 **查看处理方案** 查看原始地址和缓存目录。

## 镜像仓库

版本清单放在 [Gitee](https://gitee.com/rererergo/aslain_versions)，官网原始安装包放在 [GitCode 发行版](https://gitcode.com/REREREGO/aslain_versions/releases)。Gitee 曾拒绝这份原始 EXE 的附件上传，因此只用于托管清单，不再尝试上传安装包。不要把安装包提交到 Git 历史。

`mirrors/config.json` 的 `Repository` 和 `Branch` 指向 Gitee；`InstallerRepository` 和 `InstallerBranch` 指向 GitCode。同步脚本在公布版本前，以不带令牌的请求重新下载安装包并核对大小、SHA-256 和 PE 格式。2026-11-19 已实测 `15.10.0_08` 原始 EXE：GitCode 上传成功，匿名完整下载的 45,622,090 字节与官网一致。

内置两个国内版本查询入口：

```
https://gitee.com/rererergo/aslain_versions/raw/master/catalog.json
https://gitee.com/api/v5/repos/rererergo/aslain_versions/contents/catalog.json?ref=master
```

两者同属 Gitee，一个走原始文件，一个走公开 Contents API，不是两个独立平台的版本服务。GitCode 提供安装包下载；蓝奏云提供手工下载后备。若增加第二个清单托管平台，可在 **客户端设置 → 国内镜像 → 版本源设置** 添加其清单地址。

## 定时同步

同步顺序：检查官网经典版 → 下载原始安装包 → 验证官网哈希 → 上传 GitCode Release → 匿名下载校验 → 签名并更新 Gitee 清单 → 清理过期附件。官网解析变化、下载失败、超出单文件限制或签名错误时停止，保留之前的清单。清单有效期三天；过期后助手回退官网，不能继续声称镜像版本是最新。

本地先构建，再在 **Windows PowerShell 5.1** 运行只读演练：

```powershell
.\build.ps1 -OutputDirectory build
.\tools\Sync-AslainMirror.ps1
```

演练只下载并验证官网文件，结果在 `artifacts/mirror-sync/candidate.json`，不修改远程仓库。

实际自动同步需要环境变量 `GITEE_TOKEN`、`GITCODE_TOKEN` 和 `MIRROR_SIGNING_KEY_XML`，然后运行：

```powershell
.\tools\Sync-AslainMirror.ps1 -Publish
```

Gitee 令牌需要清单仓库文件读写权限；GitCode 令牌需要安装包仓库的发行版和附件读写权限。只验证并登记已经公开的 GitCode 附件时可以不提供 GitCode 令牌，创建新版本、上传和清理都需要它。上传使用平台签发的地址，仓库令牌不会转发给文件存储服务。

镜像专用签名密钥必须与助手的 `MirrorTrust.PublicKey` 对应；它与助手安装包更新使用不同密钥和应用标识，不能相互替代。本机凭据目录为 `%LOCALAPPDATA%\WoWS-Aslain-Assistant-Release`，对应文件是 `gitee-token.txt`、`gitcode-token.txt` 和 `aslain-mirror-key.xml`。令牌和私钥只在维护机器或 Actions Secrets 中保存，不提交到仓库，也不发给玩家。

自动任务使用 `.github/workflows/aslain-mirror.yml`，每小时第 17 分钟运行一次。配置 GitHub Secrets：`GITEE_MIRROR_TOKEN`、`GITCODE_MIRROR_TOKEN`、`ASLAIN_MIRROR_SIGNING_KEY_XML`，确认手工同步成功，再将仓库变量 `ASLAIN_MIRROR_ENABLED` 设为 `true`。默认不启用，避免尚未配置的任务反复失败。任务失败记录在 Actions，成功摘要包括版本、份数和容量。

GitHub 定时任务可能延迟，公开仓库长期没有活动时还可能被停用。长期维护可在一台能访问官网、Gitee 和 GitCode 的 Windows 机器上通过任务计划程序执行同一个命令，并监测任务失败、清单是否过期。网页适合展示清单与下载入口，单独一个静态网页不能负责定时上传。

## 保留期限和容量

默认保留两年内的安装包，项目自行设置的容量预算为 800 MiB（这不是 GitCode 的平台额度）。达到预算时从旧版本开始清理，也可按账号实际额度调整 `MaxRetainedBytes`，并留出下一份安装包的上传空间。无论时间和容量如何，都保留最近两个游戏版本各自的最新安装包；还可通过 `ProtectedGameVersions` 指定国服等仍需支持的版本。

GitCode 当前上传页面标注单个附件不能超过 2 GB；助手为限制异常下载，最多接受 1 GiB，实际以配置中的更小限制为准。平台配额用尽或拒绝上传时停止同步，保留已公布清单，不通过改扩展名或重新打包绕过检查。

若这些受保护的包已经超过容量，任务报错停止，不会为了腾空间删掉它们。先扩容或人工调整支持范围。两年是保留上限，不是免费仓库能保证的全部历史：本机 `15.10.0_08` 安装包为 45,622,090 字节，800 MiB 约容纳 18 个同等大小的包。

新清单发布后再删被淘汰的附件；清理失败，下次仍会识别工具创建的旧附件并重试。只清理带有 `Managed-By: WoWS-Aslain-Mirror` 标记、且标签和文件名匹配的发行版附件，不删其他项目或整个仓库。蓝奏云目录目前由维护者按相同策略手动清理。

接口依据：[GitCode 附件上传](https://docs.gitcode.com/docs/apis/get-api-v-5-repos-owner-repo-releases-tag-upload-url/)、[GitCode 附件下载](https://docs.gitcode.com/docs/apis/get-api-v-5-repos-owner-repo-releases-attach-files-file-name-download/)、[Gitee 官方 SDK](https://gitee.com/sdk/gitee5j/blob/main/docs/RepositoriesApi.md)、[GitHub 定时任务](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#schedule)、[蓝奏云分享说明](https://www.lanzou.com/q/zh/wt.htm)。
