Set-StrictMode -Version Latest

function Get-DemeoOpenXRPayload {
    param([Parameter(Mandatory=$true)][string]$Directory)
    $manifest = Join-Path $Directory 'build.json'
    $binary = Join-Path $Directory 'opencomposite.bin'
    if (-not (Test-Path -LiteralPath $manifest -PathType Leaf) -or -not (Test-Path -LiteralPath $binary -PathType Leaf)) {
        throw 'The bundled OpenXR adapter is missing. Download the complete mod package or update the dev checkout.'
    }
    $info = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
    if ($info.schema -ne 'demeo-opencomposite-build-v1' -or $info.binary_sha256 -notmatch '^[0-9a-f]{64}$') {
        throw 'Invalid OpenXR adapter manifest.'
    }
    $hash = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $info.binary_sha256) { throw 'OpenXR adapter hash does not match its build manifest.' }
    $bytes = [IO.File]::ReadAllBytes($binary)
    if ($bytes.Length -lt 256 -or $bytes[0] -ne 77 -or $bytes[1] -ne 90) { throw 'Invalid OpenXR adapter PE header.' }
    $offset = [BitConverter]::ToInt32($bytes,60)
    if ($offset -lt 64 -or $offset -gt $bytes.Length-26 -or [BitConverter]::ToUInt32($bytes,$offset) -ne 17744 `
        -or [BitConverter]::ToUInt16($bytes,$offset+4) -ne 34404 -or [BitConverter]::ToUInt16($bytes,$offset+24) -ne 523) {
        throw 'OpenXR adapter must be a Windows x64 DLL.'
    }
    [pscustomobject]@{ Binary=$binary; Hash=$hash }
}

function Assert-DemeoGameStopped {
    param([Parameter(Mandatory=$true)][string]$Executable)
    $expected = [IO.Path]::GetFullPath($Executable)
    $running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and [string]::Equals([IO.Path]::GetFullPath($_.Path),$expected,[StringComparison]::OrdinalIgnoreCase) } catch { $false }
    })
    if ($running.Count -gt 0) { throw "Close the game before changing its XR adapter: $Executable" }
}

function Get-DemeoOpenXRPlan {
    param([Parameter(Mandatory=$true)]$Install,[Parameter(Mandatory=$true)]$Payload,
        [ValidateSet('BepInEx','MelonLoader')][string]$LoaderBackend='BepInEx')
    if ($Install.Spec.Id -eq 'DEMEOBATTLEMARKED') { return $null }
    Assert-DemeoGameStopped $Install.Executable
    $directory = Join-Path $Install.Data 'Plugins/x86_64'
    $target = Join-Path $directory 'openvr_api.dll'
    $backup = Join-Path $directory 'openvr_api.dmr-original.dll'
    $receipt = Join-Path $directory 'DemeoMixedReality.OpenXR.json'
    if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { throw "Game OpenVR API is missing: $target. Verify the game's files in Steam first." }
    $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    $previous = $null
    if (Test-Path -LiteralPath $receipt -PathType Leaf) {
        $previous = Get-Content -LiteralPath $receipt -Raw | ConvertFrom-Json
        if ($previous.schema -ne 'demeo-openxr-install-v1') { throw "Unknown XR install receipt: $receipt" }
    }
    $owned = $hash -eq $Payload.Hash -or ($previous -and $hash -eq $previous.installed_sha256)
    if ($owned) {
        if (-not (Test-Path -LiteralPath $backup -PathType Leaf)) { throw "Original OpenVR backup missing: $backup. Restore the game's files in Steam before reinstalling." }
        $originalHash = (Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($originalHash -eq $Payload.Hash -or (Test-DemeoOpenXRMarker $backup)) {
            throw 'Original backup contains an OpenXR adapter; restore the original game files in Steam before reinstalling.'
        }
        if ($previous -and $originalHash -ne $previous.original_sha256) { throw 'Original OpenVR backup changed; refusing to overwrite it.' }
    } else {
        if (Test-DemeoOpenXRMarker $target) { throw 'An unrecognized Demeo OpenXR adapter has no matching receipt. Restore the original game API in Steam before reinstalling.' }
        $originalHash = $hash
    }
    $previousLoader = 'BepInEx'
    if ($previous -and $previous.PSObject.Properties['loader_backend']) { $previousLoader = $previous.loader_backend }
    [pscustomobject]@{ Install=$Install; Payload=$Payload; Directory=$directory; Target=$target; Backup=$backup; Receipt=$receipt; LoaderBackend=$LoaderBackend;
        CurrentHash=$hash; OriginalHash=$originalHash; Owned=[bool]$owned; Unchanged=($hash -eq $Payload.Hash -and $null -ne $previous -and $previousLoader -eq $LoaderBackend) }
}

function Test-DemeoOpenXRMarker {
    param([string]$Path)
    $text = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($Path))
    $text.Contains('DMR_GetState') -and $text.Contains('DMR_SetBlendMode')
}

function Copy-DemeoAtomicFile {
    param([string]$Source,[string]$Destination)
    $stage = $Destination + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        Copy-Item -LiteralPath $Source -Destination $stage
        if (Test-Path -LiteralPath $Destination -PathType Leaf) { [IO.File]::Replace($stage,$Destination,[NullString]::Value) }
        else { [IO.File]::Move($stage,$Destination) }
    } finally { if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Force } }
}

function Install-DemeoOpenXRAdapter {
    param([Parameter(Mandatory=$true)]$Plan)
    Assert-DemeoGameStopped $Plan.Install.Executable
    if ((Get-FileHash -LiteralPath $Plan.Target -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Plan.CurrentHash) {
        throw 'Game OpenVR API changed after preflight; no XR files were replaced.'
    }
    if ($Plan.Unchanged) { Write-Host "$($Plan.Install.Spec.Name): game-local OpenXR adapter is current."; return }
    $history = Join-Path $Plan.Directory ('.DemeoMixedReality-backups/' + $Plan.CurrentHash)
    [void][IO.Directory]::CreateDirectory($history)
    $oldTarget = Join-Path $history 'openvr_api.dll'
    if (-not (Test-Path -LiteralPath $oldTarget)) { Copy-Item -LiteralPath $Plan.Target -Destination $oldTarget }
    if ((Get-FileHash -LiteralPath $oldTarget -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Plan.CurrentHash) { throw 'XR history backup hash mismatch.' }
    $oldBackup = $null
    if (Test-Path -LiteralPath $Plan.Backup -PathType Leaf) {
        $oldBackup = Join-Path $history 'previous-original.dll'
        Copy-Item -LiteralPath $Plan.Backup -Destination $oldBackup -Force
    }
    $replaced = $false; $backupChanged = $false
    $receiptStage = $Plan.Receipt + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        if (-not $Plan.Owned) { Copy-DemeoAtomicFile $Plan.Target $Plan.Backup; $backupChanged=$true }
        Copy-DemeoAtomicFile $Plan.Payload.Binary $Plan.Target; $replaced=$true
        $record = [ordered]@{ schema='demeo-openxr-install-v1'; installed_sha256=$Plan.Payload.Hash; original_sha256=$Plan.OriginalHash; loader_backend=$Plan.LoaderBackend }
        [IO.File]::WriteAllText($receiptStage,($record | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
        if (Test-Path -LiteralPath $Plan.Receipt) { [IO.File]::Replace($receiptStage,$Plan.Receipt,[NullString]::Value) }
        else { [IO.File]::Move($receiptStage,$Plan.Receipt) }
    } catch {
        if ($replaced) { Copy-DemeoAtomicFile $oldTarget $Plan.Target }
        if ($backupChanged) {
            if ($oldBackup) { Copy-DemeoAtomicFile $oldBackup $Plan.Backup }
            elseif (Test-Path -LiteralPath $Plan.Backup) { Remove-Item -LiteralPath $Plan.Backup }
        }
        throw
    } finally { if (Test-Path -LiteralPath $receiptStage) { Remove-Item -LiteralPath $receiptStage -Force } }
    Write-Host "$($Plan.Install.Spec.Name): installed game-local OpenXR adapter; original OpenVR API preserved for LegacyCompanion."
}

function Restore-DemeoOpenVR {
    param([Parameter(Mandatory=$true)]$Plan)
    Assert-DemeoGameStopped $Plan.Install.Executable
    if (-not $Plan.Owned) { throw 'Current OpenVR API is not owned by this installer; nothing was restored.' }
    if ((Get-FileHash -LiteralPath $Plan.Backup -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Plan.OriginalHash) { throw 'Original backup changed after preflight.' }
    if ((Get-FileHash -LiteralPath $Plan.Target -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Plan.CurrentHash) { throw 'Current API changed after preflight.' }
    Copy-DemeoAtomicFile $Plan.Backup $Plan.Target
    Write-Host "$($Plan.Install.Spec.Name): original OpenVR API restored; backup and mod configuration retained."
}

Export-ModuleMember -Function Get-DemeoOpenXRPayload,Get-DemeoOpenXRPlan,Install-DemeoOpenXRAdapter,Restore-DemeoOpenVR,Assert-DemeoGameStopped
