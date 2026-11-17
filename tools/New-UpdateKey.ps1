param([string]$KeyPath = (Join-Path $env:LOCALAPPDATA 'WoWS-Aslain-Assistant-Release\signing-key.xml'))
$ErrorActionPreference = 'Stop'
$rsa = [Security.Cryptography.RSACryptoServiceProvider]::new(3072)
$rsa.PersistKeyInCsp = $false
try {
    if (Test-Path -LiteralPath $KeyPath) { $rsa.FromXmlString([IO.File]::ReadAllText($KeyPath)) }
    else {
        $keyDirectory = Split-Path -Parent $KeyPath
        New-Item -ItemType Directory -Path $keyDirectory -Force | Out-Null
        $acl = Get-Acl -LiteralPath $keyDirectory
        $acl.SetAccessRuleProtection($true,$false)
        $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
        $rule = [Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow')
        $acl.SetAccessRule($rule)
        Set-Acl -LiteralPath $keyDirectory -AclObject $acl
        [IO.File]::WriteAllText($KeyPath,$rsa.ToXmlString($true),[Text.UTF8Encoding]::new($false))
    }
    $publicKey = $rsa.ToXmlString($false)
    $source = 'namespace AslainAssistant { public static class UpdateTrust { public const string PublicKey = "' + $publicKey + '"; } }'
    [IO.File]::WriteAllText((Join-Path $PSScriptRoot '..\src\UpdateTrust.cs'),$source,[Text.UTF8Encoding]::new($false))
    Write-Output 'Update verification public key generated. The signing private key remains outside the repository.'
} finally { $rsa.Dispose() }
