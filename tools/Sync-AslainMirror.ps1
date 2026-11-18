param(
    [string]$Configuration = (Join-Path $PSScriptRoot '..\mirrors\config.json'),
    [string]$Application = (Join-Path $PSScriptRoot '..\build\AslainAssistant.exe'),
    [string]$WorkDirectory = (Join-Path $PSScriptRoot '..\artifacts\mirror-sync'),
    [switch]$Publish
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'MirrorRetention.psm1') -Force
Add-Type -AssemblyName System.Net.Http
Add-Type -AssemblyName System.Web.Extensions
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
[void][Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Application))
$config = Get-Content -LiteralPath $Configuration -Raw | ConvertFrom-Json
$work = [IO.Path]::GetFullPath($WorkDirectory)
[IO.Directory]::CreateDirectory($work) | Out-Null
# One local process per working directory; Actions also uses a concurrency group.
$lock = [IO.File]::Open((Join-Path $work 'sync.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
$client = [AslainAssistant.OfficialClient]::new()
$handler = [Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect = $false
$api = [Net.Http.HttpClient]::new($handler)
$api.Timeout = [TimeSpan]::FromMinutes(5)
$api.DefaultRequestHeaders.UserAgent.ParseAdd('WoWS-Aslain-Mirror/1.0.1')
$api.DefaultRequestHeaders.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer',$(if($env:GITEE_TOKEN){$env:GITEE_TOKEN}else{'not-configured'}))
$gitcodeHandler = [Net.Http.HttpClientHandler]::new()
$gitcodeHandler.AllowAutoRedirect = $false
$gitcodeHandler.UseCookies = $false
$gitcode = [Net.Http.HttpClient]::new($gitcodeHandler)
$gitcode.Timeout = [TimeSpan]::FromMinutes(5)
$gitcode.DefaultRequestHeaders.UserAgent.ParseAdd('WoWS-Aslain-Mirror/1.0.1')
if ($env:GITCODE_TOKEN) { $gitcode.DefaultRequestHeaders.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer',$env:GITCODE_TOKEN) }
$key = $null
$base = 'https://gitee.com/api/v5/repos/' + $config.Repository
function Invoke-Gitee([string]$Method, [string]$Path, $Body = $null, [string]$File = '') {
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($Method), $base + $Path)
    try {
        if ($File) {
            $request.Content = [Net.Http.MultipartFormDataContent]::new()
            $request.Content.Add([Net.Http.StreamContent]::new([IO.File]::OpenRead($File)), 'file', [IO.Path]::GetFileName($File))
        } elseif ($null -ne $Body) {
            $request.Content = [Net.Http.StringContent]::new(($Body | ConvertTo-Json -Depth 20 -Compress), [Text.Encoding]::UTF8, 'application/json')
        }
        $response = $api.SendAsync($request).GetAwaiter().GetResult()
        try {
            if (!$response.IsSuccessStatusCode) {
                $detail=$response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                if ($env:GITEE_TOKEN) { $detail=$detail.Replace($env:GITEE_TOKEN,'[redacted]') }
                $detail=$detail.Substring(0,[Math]::Min(800,$detail.Length))
                throw "Gitee API failed: $Method $Path HTTP $([int]$response.StatusCode): $detail"
            }
            $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            if ($text.Trim()) { $decoded=$text | ConvertFrom-Json; return $decoded }
        } finally { $response.Dispose() }
    } finally { $request.Dispose() }
}
function Write-GiteeFile([string]$Path, [string]$Text, [string]$Sha) {
    $body = @{ content = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Text)); message = 'Sync verified Aslain mirror catalog'; branch = $config.Branch }
    $method = 'POST'
    if ($Sha) { $body.sha = $Sha; $method = 'PUT' }
    $null = Invoke-Gitee $method ('/contents/' + $Path) $body
}
function Invoke-GitCode([string]$Method, [string]$Path, $Body = $null) {
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($Method), 'https://api.gitcode.com/api/v5/repos/' + $config.InstallerRepository + $Path)
    try {
        if ($null -ne $Body) { $request.Content = [Net.Http.StringContent]::new(($Body | ConvertTo-Json -Depth 20 -Compress), [Text.Encoding]::UTF8, 'application/json') }
        $response = $gitcode.SendAsync($request).GetAwaiter().GetResult()
        try {
            $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            if (!$response.IsSuccessStatusCode) {
                if ($env:GITCODE_TOKEN) { $text = $text.Replace($env:GITCODE_TOKEN,'[redacted]') }
                throw "GitCode API failed: $Method $Path HTTP $([int]$response.StatusCode): $($text.Substring(0,[Math]::Min(800,$text.Length)))"
            }
            if ($text.Trim()) { $decoded = $text | ConvertFrom-Json; return $decoded }
        } finally { $response.Dispose() }
    } finally { $request.Dispose() }
}
function Send-GitCodeAsset([string]$Tag, [string]$File) {
    if (!$env:GITCODE_TOKEN) { throw 'A new installer requires GITCODE_TOKEN. Existing catalog is unchanged.' }
    $upload = Invoke-GitCode 'GET' ('/releases/' + [Uri]::EscapeDataString($Tag) + '/upload_url?file_name=' + [Uri]::EscapeDataString([IO.Path]::GetFileName($File)))
    $uri = [Uri]$upload.url
    if ($uri.Scheme -ne 'https' -or $uri.UserInfo -or !$uri.IsDefaultPort -or !($uri.DnsSafeHost.EndsWith('.myhuaweicloud.com') -or $uri.DnsSafeHost.EndsWith('.gitcode.com'))) { throw 'GitCode returned an unexpected upload host.' }
    # Use a separate client: the repository token must NEVER accompany the storage PUT.
    $storageHandler = [Net.Http.HttpClientHandler]::new()
    $storageHandler.AllowAutoRedirect = $false
    $storageHandler.UseCookies = $false
    $storage = [Net.Http.HttpClient]::new($storageHandler)
    $storage.Timeout = [TimeSpan]::FromMinutes(10)
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Put, $uri)
    try {
        $request.Content = [Net.Http.StreamContent]::new([IO.File]::OpenRead($File))
        foreach ($header in $upload.headers.PSObject.Properties) {
            if ($header.Name -match '^(?i:authorization|cookie|proxy-authorization)$') { throw 'Unexpected credential header in upload response.' }
            if (!$request.Headers.TryAddWithoutValidation($header.Name,[string]$header.Value)) {
                if (!$request.Content.Headers.TryAddWithoutValidation($header.Name,[string]$header.Value)) { throw 'Unsupported storage upload header.' }
            }
        }
        $response = $storage.SendAsync($request).GetAwaiter().GetResult()
        try {
            if (!$response.IsSuccessStatusCode) { throw "GitCode storage upload failed (HTTP $([int]$response.StatusCode)). No catalog was published." }
        } finally { $response.Dispose() }
    } catch {
        # Presigned URLs and callback headers are credentials too; never print them.
        throw 'GitCode storage upload failed. Check access, quota and platform file inspection; the previous catalog is unchanged.'
    } finally { $request.Dispose(); $storage.Dispose(); $storageHandler.Dispose() }
}
try {
    if ($config.Repository -and $config.Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Invalid Gitee owner/repository.' }
    if ($config.InstallerRepository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Invalid GitCode installer repository.' }
    if ($config.MaxAttachmentBytes -gt 1073741824 -or $config.MaxAttachmentBytes -lt 1024 -or $config.MaxRetainedBytes -le 0) { throw 'The assistant supports installers up to 1 GiB; retention budget must be positive.' }
    if ($Publish) {
        if (!$config.Repository -or !$env:GITEE_TOKEN -or !$env:MIRROR_SIGNING_KEY_XML) { throw 'Publishing requires Repository, GITEE_TOKEN and MIRROR_SIGNING_KEY_XML. Dry-run does not.' }
        $key = [Security.Cryptography.RSACryptoServiceProvider]::new()
        $key.PersistKeyInCsp = $false
        $key.FromXmlString($env:MIRROR_SIGNING_KEY_XML)
        if ($key.ToXmlString($false) -ne [AslainAssistant.MirrorTrust]::PublicKey) { throw 'Signing key does not match the public key bundled in the assistant.' }
    }
    $release = $client.Latest([Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $installer = $client.Download($release, $work, $null, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $size = (Get-Item -LiteralPath $installer).Length
    if ($size -gt $config.MaxAttachmentBytes) { throw 'Installer exceeds the configured download limit. Previous catalog remains in place.' }
    $candidate = [pscustomobject]@{ Version=$release.Version.FileTag; Sha256=$release.Sha256; Size=$size; OfficialUrl=$release.Download.AbsoluteUri; Urls=@(); PublishedUtc=[DateTimeOffset]::UtcNow.ToString('o') }
    if (!$Publish) {
        $candidate | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $work 'candidate.json') -Encoding UTF8
        Write-Host "Verified official package $($candidate.Version), $size bytes. Dry-run: no remote writes."
        return
    }
    # The dedicated mirror repository must already exist. Never write installer bytes to Git history.
    $repository = Invoke-Gitee 'GET' ''
    if (!$repository.default_branch) {
        $readme=[IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\mirrors\README.md'))
        Write-GiteeFile 'README.md' $readme ''
    }
    $existing = $null; $catalogSha = ''; $packages = @()
    $listing = @(Invoke-Gitee 'GET' ('/contents?ref=' + [Uri]::EscapeDataString($config.Branch)))
    if (@($listing | Where-Object { $_.name -eq 'catalog.json' }).Count) {
        $existing = Invoke-Gitee 'GET' ('/contents/catalog.json?ref=' + [Uri]::EscapeDataString($config.Branch))
        $signedText = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($existing.content))
        $verified = [AslainAssistant.MirrorCatalog]::Parse($signedText,[AslainAssistant.MirrorTrust]::PublicKey,[DateTimeOffset]::UtcNow,$false)
        $packages = @($verified.Packages); $catalogSha = $existing.sha
    }
    $same = $packages | Where-Object { $_.Version -eq $candidate.Version } | Select-Object -First 1
    if ($same -and ($same.Sha256 -ne $candidate.Sha256 -or $same.Size -ne $candidate.Size)) { throw 'Official bytes changed without a version change. Manual review required; catalog unchanged.' }
    if ($same) { $candidate.PublishedUtc = $same.PublishedUtc }
    $plan = Get-MirrorRetentionPlan -Packages (@($packages | Where-Object { $_.Version -ne $candidate.Version }) + @($candidate)) -MaxBytes $config.MaxRetainedBytes -Days $config.RetentionDays -ProtectedGames $config.ProtectedGameVersions
    # Idempotent recovery: reuse an existing release/asset after an interrupted run.
    $releases = @()
    for ($page=1; $page -le 20; $page++) {
        $batch = @(Invoke-GitCode 'GET' "/releases?per_page=100&page=$page")
        $releases += $batch
        if ($batch.Count -lt 100) { break }
        if ($page -eq 20) { throw 'Too many releases to inspect safely.' }
    }
    $tag = 'aslain-' + $candidate.Version
    $remote = $releases | Where-Object { $_.tag_name -eq $tag } | Select-Object -First 1
    if (!$remote) {
        if (!$env:GITCODE_TOKEN) { throw 'Creating a new installer release requires GITCODE_TOKEN. Existing catalog is unchanged.' }
        $remote = Invoke-GitCode 'POST' '/releases' @{tag_name=$tag;target_commitish=$config.InstallerBranch;name=('Aslain '+$candidate.Version);body=("Managed-By: WoWS-Aslain-Mirror`nOfficial original installer; SHA-256: "+$candidate.Sha256+"`nSource: "+[AslainAssistant.Release]::Page);release_status='pre'}
    }
    if ($remote.body -notlike '*Managed-By: WoWS-Aslain-Mirror*') { throw 'Release tag belongs to an unmanaged release. No changes made to it.' }
    $filename = [IO.Path]::GetFileName($installer)
    $asset = $remote.assets | Where-Object { $_.type -eq 'attach' -and $_.name -ceq $filename } | Select-Object -First 1
    if (!$asset) { Send-GitCodeAsset $tag $installer }
    $remote = Invoke-GitCode 'GET' ('/releases/tags/' + [Uri]::EscapeDataString($tag))
    $asset = $remote.assets | Where-Object { $_.type -eq 'attach' -and $_.name -ceq $filename } | Select-Object -First 1
    if (!$asset) { throw 'Uploaded attachment was not returned by GitCode. Catalog unchanged.' }
    if ($asset.size -ne $size) { throw 'GitCode attachment size differs from the official package. Catalog unchanged.' }
    # Test the public, token-free URL through the same downloader used by players, including SHA-256.
    $publicUrl = $asset.browser_download_url
    if (![AslainAssistant.MirrorSettings]::Https($publicUrl)) { throw 'GitCode did not return a valid public attachment download URL.' }
    $candidate.Urls = @($publicUrl)
    # A separate directory prevents a cache hit from hiding a broken anonymous download.
    $probeDirectory=Join-Path $work ('probe-' + [Guid]::NewGuid().ToString('N'))
    $check = $client.DownloadMirrorProbe($publicUrl,$release.Sha256,$size,$probeDirectory).GetAwaiter().GetResult()
    if ($remote.prerelease) {
        if (!$env:GITCODE_TOKEN) { throw 'Publishing a verified pre-release requires GITCODE_TOKEN.' }
        $null=Invoke-GitCode 'PATCH' ('/releases/' + [Uri]::EscapeDataString($tag)) @{name=$remote.name;body=$remote.body;release_status='latest'}
    }
    $all = @($plan.Keep | ForEach-Object { if ($_.Version -eq $candidate.Version) { $candidate } else { $_ } })
    $now=[DateTimeOffset]::UtcNow
    $catalog = @{Schema=1;Application='WoWS-Aslain-Classic-Mirror';GeneratedUtc=$now.ToString('o');ExpiresUtc=$now.AddDays(3).ToString('o');Packages=$all}
    $payload=[Text.Encoding]::UTF8.GetBytes(($catalog | ConvertTo-Json -Depth 20 -Compress))
    $signed=@{Payload=[Convert]::ToBase64String($payload);Signature=[Convert]::ToBase64String($key.SignData($payload,[Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256')))} | ConvertTo-Json -Compress
    $null=[AslainAssistant.MirrorCatalog]::Parse($signed,[AslainAssistant.MirrorTrust]::PublicKey,$now)
    Write-GiteeFile 'catalog.json' $signed $catalogSha
    # Commit the catalog BEFORE deleting retired assets. Only managed assets are ever removed.
    foreach ($oldRelease in $releases) {
        if ($oldRelease.tag_name -notmatch '^aslain-(\d{1,3}\.\d{1,3}\.\d{1,3}_\d{2,4})$' -or $oldRelease.body -notlike '*Managed-By: WoWS-Aslain-Mirror*') { continue }
        $oldVersion=$Matches[1]
        if (@($all | Where-Object { $_.Version -eq $oldVersion }).Count) { continue }
        $oldName='Aslains_WoWs_Modpack_Installer_v.'+$oldVersion+'.exe'
        $oldAssets=@($oldRelease.assets | Where-Object { $_.type -eq 'attach' -and $_.name -ceq $oldName })
        foreach ($oldAsset in $oldAssets) {
            if (!$env:GITCODE_TOKEN) { throw 'Cleaning expired installer attachments requires GITCODE_TOKEN.' }
            $null=Invoke-GitCode 'DELETE' ('/releases/' + [Uri]::EscapeDataString($oldRelease.tag_name) + '/attach_files/' + $oldAsset.id)
        }
    }
    $summary="Synced $($candidate.Version). Retained $($all.Count) installers / $($plan.Bytes) bytes. Lanzou remains a manual backup."
    Write-Host $summary
    if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $summary }
} catch {
    # Never include HTTP credentials or signing keys in logs.
    $message=$_.Exception.Message
    if ($env:GITEE_TOKEN) { $message=$message.Replace($env:GITEE_TOKEN,'[redacted]') }
    if ($env:GITCODE_TOKEN) { $message=$message.Replace($env:GITCODE_TOKEN,'[redacted]') }
    if ($env:MIRROR_SIGNING_KEY_XML) { $message=$message.Replace($env:MIRROR_SIGNING_KEY_XML,'[redacted]') }
    Write-Error $message
    exit 1
} finally {
    if ($key) { $key.Dispose() }; $api.Dispose(); $handler.Dispose(); $gitcode.Dispose(); $gitcodeHandler.Dispose(); $client.Dispose(); $lock.Dispose()
}
