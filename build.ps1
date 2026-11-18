param([string]$OutputDirectory = 'build')
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.8 编译器不存在。' }
New-Item -ItemType Directory -Path $OutputDirectory,artifacts -Force | Out-Null
$logo = Join-Path $PSScriptRoot 'assets\logo\lan-blue-shield.png'
$icon = Join-Path $PSScriptRoot 'assets\logo\lan-blue-shield.ico'
& (Join-Path $PSScriptRoot 'tools\Build-Icon.ps1') -Source $logo -Destination $icon
$sources = @(Get-ChildItem -LiteralPath src -Filter *.cs | ForEach-Object FullName)
$library = Join-Path $PSScriptRoot 'vendor\AntdUI\2.4.19\lib\net48\AntdUI.dll'
$license = Join-Path $PSScriptRoot 'vendor\AntdUI\2.4.19\LICENSE.txt'
if (!(Test-Path -LiteralPath $library)) { throw '缺少固定版本的 AntdUI 2.4.19 组件库。' }
Copy-Item -LiteralPath $library -Destination (Join-Path $OutputDirectory 'AntdUI.dll') -Force
Copy-Item -LiteralPath $license -Destination (Join-Path $OutputDirectory 'AntdUI.LICENSE.txt') -Force
$references = @('/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Design.dll','/r:System.Net.Http.dll','/r:System.Web.Extensions.dll','/r:System.Xml.Linq.dll',('/r:' + $library))
$appOutput = '/out:' + (Join-Path $OutputDirectory 'AslainAssistant.exe')
$branding = @(('/win32icon:' + $icon), ('/resource:' + $icon + ',AppIcon'), ('/resource:' + $logo + ',AppLogo'))
& $compiler /nologo /utf8output /target:winexe /platform:anycpu /optimize+ /win32manifest:app.manifest $appOutput @branding @references @sources
if ($LASTEXITCODE -ne 0) { throw '应用编译失败。' }
$testOutput = '/out:' + (Join-Path $OutputDirectory 'AslainAssistant.Tests.exe')
& $compiler /nologo /utf8output /target:exe /platform:anycpu $testOutput /main:AslainAssistant.Tests @branding @references @sources tests\Tests.cs tests\PresetTests.cs tests\UpdateTests.cs tests\MirrorTests.cs packaging\InstallOperations.cs
if ($LASTEXITCODE -ne 0) { throw '测试编译失败。' }
$uiTestOutput = '/out:' + (Join-Path $OutputDirectory 'AslainAssistant.UiTests.exe')
& $compiler /nologo /utf8output /target:exe /platform:anycpu /win32manifest:app.manifest $uiTestOutput /main:AslainAssistant.UiTests @branding @references @sources tests\UiTests.cs
if ($LASTEXITCODE -ne 0) { throw '界面测试编译失败。' }
$instanceTestOutput = '/out:' + (Join-Path $OutputDirectory 'AslainAssistant.InstanceTests.exe')
$appReference = '/r:' + (Join-Path $OutputDirectory 'AslainAssistant.exe')
& $compiler /nologo /utf8output /target:exe /platform:anycpu $instanceTestOutput $appReference tests\InstanceTests.cs
if ($LASTEXITCODE -ne 0) { throw '单实例测试编译失败。' }
$setupOutput = '/out:' + (Join-Path $OutputDirectory 'AslainAssistant.Setup.exe')
$payload = '/resource:' + (Join-Path $OutputDirectory 'AslainAssistant.exe') + ',AppPayload'
$uiPayload = '/resource:' + $library + ',AntdUIPayload'
$licensePayload = '/resource:' + $license + ',AntdUILicense'
& $compiler /nologo /utf8output /target:winexe /platform:anycpu /optimize+ /win32manifest:app.manifest /main:AslainAssistant.SetupBootstrap $setupOutput $payload $uiPayload $licensePayload @branding @references packaging\Setup.cs packaging\SetupBootstrap.cs packaging\InstallOperations.cs src\InstanceControl.cs src\Brand.cs src\UiTheme.cs src\UiPreferences.cs src\AssemblyInfo.cs
if ($LASTEXITCODE -ne 0) { throw '安装程序编译失败。' }
Write-Host "已生成应用、安装程序和测试程序：$OutputDirectory"
