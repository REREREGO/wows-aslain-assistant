$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '..\tools\MirrorRetention.psm1') -Force
function Assert($Condition, $Message) { if (!$Condition) { throw $Message } }
$now=[DateTimeOffset]::UtcNow
$packages=@(
    [pscustomobject]@{Version='15.10.0_08';Size=50000000;PublishedUtc=$now.ToString('o')},
    [pscustomobject]@{Version='15.10.0_07';Size=50000000;PublishedUtc=$now.AddDays(-2).ToString('o')},
    [pscustomobject]@{Version='15.9.0_06';Size=50000000;PublishedUtc=$now.AddDays(-800).ToString('o')},
    [pscustomobject]@{Version='15.8.0_01';Size=50000000;PublishedUtc=$now.AddDays(-800).ToString('o')}
)
$plan=Get-MirrorRetentionPlan -Packages $packages -MaxBytes 150000000 -Now $now
Assert ($plan.Keep.Count -eq 3 -and $plan.Remove[0].Version -eq '15.8.0_01') 'Two-year policy or protected compatibility release failed.'
$plan=Get-MirrorRetentionPlan -Packages $packages -MaxBytes 100000000 -Now $now
Assert (($plan.Keep.Version -join ',') -eq '15.10.0_08,15.9.0_06') 'Capacity policy deleted supported release instead of old revision.'
$plan=Get-MirrorRetentionPlan -Packages $packages -MaxBytes 150000000 -ProtectedGames @('15.8.0') -Now $now
Assert ($plan.Keep.Version -contains '15.8.0_01' -and $plan.Keep.Version -notcontains '15.10.0_07') 'Explicit CN compatibility version not protected.'
$failed=$false
try { Get-MirrorRetentionPlan -Packages $packages -MaxBytes 99999999 | Out-Null } catch { $failed=$true }
Assert $failed 'Protected packages overflow should stop without deleting.'
$failed=$false
try { Get-MirrorRetentionPlan -Packages @($packages[0],$packages[0]) -MaxBytes 200000000 | Out-Null } catch { $failed=$true }
Assert $failed 'Duplicate version was accepted.'
Write-Host 'PASS retention age, capacity, protected clients, insufficient capacity, duplicate rejection (5 checks).'
