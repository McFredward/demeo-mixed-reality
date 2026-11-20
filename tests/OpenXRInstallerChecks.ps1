$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot '../scripts/OpenXR.Install.Helpers.psm1') -Force
$temporary=Join-Path ([IO.Path]::GetTempPath()) ('demeo-openxr-install-'+[Guid]::NewGuid().ToString('N'))
$script:checks=0
function Check([bool]$condition,[string]$name) { if (-not $condition) { throw $name }; $script:checks++ }
function Hash([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
function File([string]$path,[string]$text) { [void][IO.Directory]::CreateDirectory((Split-Path -Parent $path)); [IO.File]::WriteAllText($path,$text) }
function Reject([scriptblock]$action,[string]$name) { $failed=$false; try { & $action } catch { $failed=$true }; Check $failed $name }
function global:Get-Process { @() }
try {
    $payloadDirectory=Join-Path $temporary 'Payload'
    [void][IO.Directory]::CreateDirectory($payloadDirectory)
    $binary=Join-Path $payloadDirectory 'opencomposite.bin'
    # Non-executable PE fixture: exercise validation/file transactions only.
    $bytes=[byte[]]::new(512)
    $bytes[0]=77; $bytes[1]=90
    [BitConverter]::GetBytes([int]128).CopyTo($bytes,60)
    [BitConverter]::GetBytes([uint32]17744).CopyTo($bytes,128)
    [BitConverter]::GetBytes([uint16]34404).CopyTo($bytes,132)
    [BitConverter]::GetBytes([uint16]523).CopyTo($bytes,152)
    [IO.File]::WriteAllBytes($binary,$bytes)
    $manifest=Join-Path $payloadDirectory 'build.json'
    File $manifest (([ordered]@{schema='demeo-opencomposite-build-v1'; binary_sha256=(Hash $binary)}) | ConvertTo-Json)
    $payload=Get-DemeoOpenXRPayload $payloadDirectory
    Check ($payload.Hash -eq (Hash $binary)) 'Validate exact bundled payload hash and x64 PE header'
    $badManifest=([IO.File]::ReadAllText($manifest)).Replace($payload.Hash,('0'*64))
    File $manifest $badManifest
    Reject { Get-DemeoOpenXRPayload $payloadDirectory } 'Reject mismatched binary before changing game files'
    File $manifest (([ordered]@{schema='demeo-opencomposite-build-v1'; binary_sha256=(Hash $binary)}) | ConvertTo-Json)
    $game=Join-Path $temporary 'Game With Spaces'
    $data=Join-Path $game 'demeo_Data'
    $install=[pscustomobject]@{Spec=[pscustomobject]@{Id='DEMEO';Name='Demeo'}; Path=$game; Data=$data; Executable=(Join-Path $game 'demeo.exe')}
    $target=Join-Path $data 'Plugins/x86_64/openvr_api.dll'
    File $install.Executable 'fixture-exe'
    File $target 'game-original-v1'
    $original=Hash $target
    $plan=Get-DemeoOpenXRPlan $install $payload
    Check (-not $plan.Owned -and -not (Test-Path $plan.Backup)) 'Preflight is read-only'
    Install-DemeoOpenXRAdapter $plan
    Check ((Hash $target) -eq $payload.Hash -and (Hash $plan.Backup) -eq $original) 'Install with exact original DLL backup'
    $repeat=Get-DemeoOpenXRPlan $install $payload
    Check $repeat.Unchanged 'Identical reinstall is a no-op'
    Install-DemeoOpenXRAdapter $repeat
    $melon=Get-DemeoOpenXRPlan $install $payload -LoaderBackend MelonLoader
    Check (-not $melon.Unchanged) 'Changing selected loader updates startup config selection even with identical native bytes'
    Install-DemeoOpenXRAdapter $melon
    Check ((Get-Content $melon.Receipt -Raw | ConvertFrom-Json).loader_backend -eq 'MelonLoader') 'Record the explicitly selected loader for native startup'
    File $target 'steam-game-update-v2'
    $updated=Hash $target
    $update=Get-DemeoOpenXRPlan $install $payload
    Install-DemeoOpenXRAdapter $update
    Check ((Hash $target) -eq $payload.Hash -and (Hash $update.Backup) -eq $updated) 'Steam game update becomes the new legacy original'
    Check (Test-Path (Join-Path $update.Directory ('.DemeoMixedReality-backups/'+$updated+'/previous-original.dll'))) 'Previous original remains in versioned history'
    # New native payload keeps the current game original rather than backing up itself.
    $bytes[511]=1; [IO.File]::WriteAllBytes($binary,$bytes)
    File $manifest (([ordered]@{schema='demeo-opencomposite-build-v1'; binary_sha256=(Hash $binary)}) | ConvertTo-Json)
    $next=Get-DemeoOpenXRPayload $payloadDirectory
    $upgrade=Get-DemeoOpenXRPlan $install $next
    Check ($upgrade.Owned -and -not $upgrade.Unchanged) 'Recognize the previous installed adapter during upgrade'
    Install-DemeoOpenXRAdapter $upgrade
    Check ((Hash $target) -eq $next.Hash -and (Hash $upgrade.Backup) -eq $updated) 'Adapter upgrade never promotes the adapter to original'
    $restore=Get-DemeoOpenXRPlan $install $next
    Restore-DemeoOpenVR $restore
    Check ((Hash $target) -eq $updated) 'Restore exact original game API'
    Reject { Restore-DemeoOpenVR (Get-DemeoOpenXRPlan $install $next) } 'Do not restore over an unowned current DLL'
    Install-DemeoOpenXRAdapter (Get-DemeoOpenXRPlan $install $next)
    $backup=(Get-DemeoOpenXRPlan $install $next).Backup
    File $backup 'unexpected-backup-edit'
    Reject { Get-DemeoOpenXRPlan $install $next } 'Refuse a changed original backup'
    File $backup 'steam-game-update-v2'
    Copy-Item -LiteralPath $binary -Destination $backup -Force
    Reject { Get-DemeoOpenXRPlan $install $next } 'Never treat a native adapter as the original DLL'
    File $backup 'steam-game-update-v2'
    $race=Get-DemeoOpenXRPlan $install $next
    File $target 'concurrent-game-update'
    Reject { Install-DemeoOpenXRAdapter $race } 'Refuse target changes after preflight'
    Check ([IO.File]::ReadAllText($target) -eq 'concurrent-game-update') 'Concurrent target remains untouched'
    function global:Get-Process { [pscustomobject]@{Path=$install.Executable} }
    Reject { Get-DemeoOpenXRPlan $install $next } 'Refuse a running game without closing it'
    function global:Get-Process { @() }
    # Make receipt replacement fail after both DLL copies to exercise rollback.
    $failureGame=Join-Path $temporary 'Rollback Game'
    $failure=[pscustomobject]@{Spec=$install.Spec;Path=$failureGame;Data=(Join-Path $failureGame 'demeo_Data');Executable=(Join-Path $failureGame 'demeo.exe')}
    $failureTarget=Join-Path $failure.Data 'Plugins/x86_64/openvr_api.dll'
    File $failureTarget 'rollback-original'
    $failurePlan=Get-DemeoOpenXRPlan $failure $next
    [void][IO.Directory]::CreateDirectory($failurePlan.Receipt)
    Reject { Install-DemeoOpenXRAdapter $failurePlan } 'Receipt failure triggers DLL rollback'
    Check ([IO.File]::ReadAllText($failureTarget) -eq 'rollback-original' -and -not (Test-Path $failurePlan.Backup)) 'Rollback restores initial target and backup state'
    Write-Host "OpenXR installer checks passed: $script:checks"
} finally {
    Remove-Item Function:\global:Get-Process -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}
