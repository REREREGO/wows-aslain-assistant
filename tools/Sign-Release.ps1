param(
    [Parameter(Mandatory=$true)][string]$Version,
    [string]$Package,
    [string[]]$DownloadUrls,
    [string]$KeyPath = (Join-Path $env:LOCALAPPDATA 'WoWS-Aslain-Assistant-Release\signing-key.xml'),
    [string]$Output = (Join-Path $PSScriptRoot '..\updates\latest.json')
)
$ErrorActionPreference = 'Stop'
$packageName = 'AslainAssistant.Setup.' + $Version + '.exe'
if (!$Package) { $Package = Join-Path $PSScriptRoot ('..\release\' + $packageName) }
if (!$DownloadUrls) { $DownloadUrls = @('https://github.com/REREREGO/wows-aslain-assistant/releases/download/v' + $Version + '/' + $packageName) }
$payload = [ordered]@{ Schema=1; Application='WoWS-Aslain-Assistant'; Version=$Version; Urls=$DownloadUrls; Sha256=(Get-FileHash -LiteralPath $Package -Algorithm SHA256).Hash.ToLowerInvariant(); Size=(Get-Item -LiteralPath $Package).Length; PublishedUtc=[DateTimeOffset]::UtcNow.ToString('o') }
$bytes = [Text.Encoding]::UTF8.GetBytes(($payload | ConvertTo-Json -Depth 4 -Compress))
$rsa = [Security.Cryptography.RSACryptoServiceProvider]::new()
$rsa.PersistKeyInCsp=$false
try {
    $rsa.FromXmlString([IO.File]::ReadAllText($KeyPath))
    $publicKeySource = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\src\UpdateTrust.cs'))
    if (!$publicKeySource.Contains($rsa.ToXmlString($false))) { throw '签名私钥与客户端内置公钥不一致，不能发布。' }
    $signature = $rsa.SignData($bytes,[Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'))
    $envelope = [ordered]@{ Payload=[Convert]::ToBase64String($bytes); Signature=[Convert]::ToBase64String($signature) }
    New-Item -ItemType Directory -Path (Split-Path -Parent $Output) -Force | Out-Null
    [IO.File]::WriteAllText($Output,($envelope | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    Write-Output ('Signed update manifest: ' + [IO.Path]::GetFullPath($Output))
} finally { $rsa.Dispose() }
