<#
.SYNOPSIS
Install the bundled game-local OpenVR -> OpenXR adapter for Demeo or Battles.
Run once after extracting the mod ZIP into BepInEx/plugins, with the game closed.
.EXAMPLE
.\Install-OpenXR.ps1 -GamePath 'D:\SteamLibrary\steamapps\common\Demeo'
.EXAMPLE
.\Install-OpenXR.ps1 -RestoreOpenVR
#>
[CmdletBinding(SupportsShouldProcess=$true)]
param([string]$GamePath,[switch]$RestoreOpenVR)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'scripts/Install.Helpers.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'scripts/OpenXR.Install.Helpers.psm1') -Force
if (-not $GamePath) {
    $candidate = $PSScriptRoot
    for ($level=0; $level -lt 8 -and $candidate; $level++) {
        $found = @(Find-DemeoInstallations -GamePath $candidate)
        if ($found.Count -eq 1) { $GamePath=$candidate; break }
        $candidate = Split-Path -Parent $candidate
    }
    if (-not $GamePath) { throw 'Game folder not found above the extracted mod. Pass -GamePath with the game folder.' }
}
$installed = @(Find-DemeoInstallations -GamePath $GamePath)
if ($installed.Count -ne 1) { throw 'The path must identify one installed Demeo game.' }
if ($installed[0].Spec.Id -eq 'DEMEOBATTLEMARKED') {
    Write-Host 'Battlemarked already uses OpenXR; no OpenComposite adapter is needed.'
    return
}
$directory = Join-Path $PSScriptRoot 'assets/opencomposite'
if (-not (Test-Path -LiteralPath $directory -PathType Container)) { $directory=Join-Path $PSScriptRoot 'native/opencomposite' }
$payload = Get-DemeoOpenXRPayload $directory
$plan = Get-DemeoOpenXRPlan $installed[0] $payload
if ($RestoreOpenVR) {
    if ($PSCmdlet.ShouldProcess($plan.Target,'Restore original game OpenVR API')) { Restore-DemeoOpenVR $plan }
} elseif ($PSCmdlet.ShouldProcess($plan.Target,'Install game-local OpenXR adapter with original OpenVR backup')) {
    Install-DemeoOpenXRAdapter $plan
    Write-Host 'Use SteamVR as your OpenXR runtime. NativePassthrough=true and Backend=Auto enable native passthrough on Steam Frame. Restart the game after changing between Auto and LegacyCompanion.'
}
