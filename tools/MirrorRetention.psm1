Set-StrictMode -Version Latest
function Get-MirrorRetentionPlan {
    param([object[]]$Packages, [long]$MaxBytes, [int]$Days = 730, [string[]]$ProtectedGames = @(), [DateTimeOffset]$Now = [DateTimeOffset]::UtcNow)
    if ($MaxBytes -le 0 -or $Days -lt 1) { throw 'Retention limits must be positive.' }
    $ordered = @($Packages | Sort-Object { [version]($_.Version.Replace('_','.')) } -Descending)
    $seen = @{}
    foreach ($item in $ordered) {
        if ($item.Version -notmatch '^\d{1,3}\.\d{1,3}\.\d{1,3}_\d{2,4}$' -or $item.Size -lt 1024 -or $seen.ContainsKey($item.Version)) { throw 'Invalid or duplicate package.' }
        $seen[$item.Version] = $true
    }
    # Keep the newest package for the two newest game versions, plus explicit CN/other clients.
    $games = @($ordered | ForEach-Object { $_.Version.Split('_')[0] } | Select-Object -Unique | Select-Object -First 2)
    $protected = @{}
    foreach ($game in @($games) + @($ProtectedGames)) {
        $package = $ordered | Where-Object { $_.Version.Split('_')[0] -eq $game } | Select-Object -First 1
        if ($null -ne $package) { $protected[$package.Version] = $true }
    }
    $keep = [Collections.Generic.List[object]]::new()
    $remove = [Collections.Generic.List[object]]::new()
    [long]$size = 0
    foreach ($item in $ordered) {
        if ($protected.ContainsKey($item.Version)) { $keep.Add($item); $size += $item.Size }
    }
    if ($size -gt $MaxBytes) { throw 'Protected game versions exceed capacity; increase quota or revise supported versions. Nothing was deleted.' }
    foreach ($item in $ordered) {
        if ($protected.ContainsKey($item.Version)) { continue }
        if ([DateTimeOffset]::Parse($item.PublishedUtc) -ge $Now.AddDays(-$Days) -and $size + $item.Size -le $MaxBytes) { $keep.Add($item); $size += $item.Size }
        else { $remove.Add($item) }
    }
    [pscustomobject]@{ Keep = @($keep.ToArray()); Remove = @($remove.ToArray()); Bytes = $size }
}
Export-ModuleMember -Function Get-MirrorRetentionPlan
