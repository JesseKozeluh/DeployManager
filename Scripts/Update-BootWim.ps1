<#
.SYNOPSIS
    Patches a WinPE boot.wim with the DeployManager server URL and WinPE local password.

.DESCRIPTION
    Mounts boot.wim using ADK DISM (inbox DISM fails on Server 2022 25398-era WIMs),
    replaces the DEPLOYMGR_* placeholders in Deploy\Start-OSDeploy.ps1 inside the
    mounted image, then unmounts and commits.

    Run this whenever the server URL or WinPE local password changes in Settings.

.PARAMETER BootWimPath
    Full path to boot.wim (inside the TFTP root).
    Default: C:\ProgramData\DeployManager\tftp\Boot\boot.wim

.PARAMETER MountPath
    Temporary directory used as the WIM mount point. Created if absent.
    Default: C:\WinPE_MOUNT

.PARAMETER ServerUrl
    Full HTTPS URL of the DeployManager API server (ApiServerUrl from Settings).
    Example: https://192.168.1.10:8090

.PARAMETER WinpePassword
    The local administrator password baked into boot.wim and applied to newly
    deployed machines via unattend.xml. Must match WinpeLocalPassword in Settings.

.PARAMETER WimIndex
    WIM image index to patch. Default: 1 (standard WinPE boot image).

.NOTES
    This file MUST be saved as ASCII (or UTF-8 with BOM). Windows PowerShell 5.1
    reads a BOM-less UTF-8 file as ANSI/CP1252, which mis-decodes any non-ASCII
    character (box drawing, arrows, em-dashes) into curly quotes that PowerShell
    treats as string delimiters -- causing cascading parser errors. Keep it ASCII.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]   $BootWimPath   = "$env:ProgramData\DeployManager\tftp\Boot\boot.wim",
    [string]   $MountPath     = 'C:\WinPE_MOUNT',
    [Parameter(Mandatory)] [string] $ServerUrl,
    [Parameter(Mandatory)] [string] $WinpePassword,
    [int]      $WimIndex      = 1,
    [string]   $DriverPathList = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# -- Locate ADK DISM ----------------------------------------------------------
# Inbox DISM (C:\Windows\System32\dism.exe) cannot service 25398-era WinPE images.
# ADK DISM must be used instead.
$adkDism = @(
    "$env:ProgramFiles\Windows Kits\10\Assessment and Deployment Kit\Deployment Tools\amd64\DISM\dism.exe",
    "${env:ProgramFiles(x86)}\Windows Kits\10\Assessment and Deployment Kit\Deployment Tools\amd64\DISM\dism.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $adkDism) {
    Write-Error @"
ADK DISM not found. Install the Windows Assessment and Deployment Kit (ADK):
  https://learn.microsoft.com/en-us/windows-hardware/get-started/adk-install
Then re-run this script.
"@
    exit 1
}
Write-Host "  ADK DISM : $adkDism" -ForegroundColor Gray

# -- Validate inputs ----------------------------------------------------------
if (-not (Test-Path $BootWimPath)) {
    Write-Error "boot.wim not found at: $BootWimPath`nCopy your WinPE boot.wim there first."
    exit 1
}

if ($ServerUrl -notmatch '^https?://') {
    Write-Error "ServerUrl must start with https:// or http:// - got: $ServerUrl"
    exit 1
}

# Parse host:port for the placeholder replacement
$uri  = [System.Uri]$ServerUrl
$svrHost = $uri.Host
$svrPort = $uri.Port
$placeholderValue = "${svrHost}:${svrPort}"

Write-Host ""
Write-Host "DeployManager - Update-BootWim" -ForegroundColor Cyan
Write-Host "  Boot.wim : $BootWimPath"
Write-Host "  Mount    : $MountPath"
Write-Host "  Index    : $WimIndex"
Write-Host "  Server   : $ServerUrl  (placeholder -> $placeholderValue)"
Write-Host ""

# -- Ensure mount point is clean ----------------------------------------------
if (-not (Test-Path $MountPath)) {
    New-Item -ItemType Directory -Path $MountPath -Force | Out-Null
    Write-Host "  Created mount directory: $MountPath" -ForegroundColor Gray
} else {
    # Attempt to clean up any previous stuck mount
    & $adkDism /Unmount-Image /MountDir:$MountPath /Discard 2>$null | Out-Null
}

# -- Mount --------------------------------------------------------------------
Write-Host "Mounting boot.wim (index $WimIndex)..." -ForegroundColor Yellow
& $adkDism /Mount-Image /ImageFile:$BootWimPath /Index:$WimIndex /MountDir:$MountPath
if ($LASTEXITCODE -ne 0) {
    Write-Error "DISM mount failed (exit $LASTEXITCODE). Check the DISM log at C:\Windows\Logs\DISM\dism.log"
    exit 1
}
Write-Host "  Mounted." -ForegroundColor Green

# -- Copy latest Start-OSDeploy.ps1 into the WIM ------------------------------
$scriptPath = Join-Path $MountPath 'Deploy\Start-OSDeploy.ps1'
$deployDir  = Join-Path $MountPath 'Deploy'
if (-not (Test-Path $deployDir)) {
    New-Item -ItemType Directory -Path $deployDir -Force | Out-Null
}

$installDir = "$env:ProgramFiles\DeployManager\scripts"
$sourceScript = @(
    (Join-Path $installDir 'Start-OSDeploy.ps1'),
    (Join-Path $PSScriptRoot 'TaskSequences\Start-OSDeploy.ps1')
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($sourceScript) {
    Copy-Item $sourceScript -Destination $scriptPath -Force
    Write-Host "  Copied latest Start-OSDeploy.ps1 from: $sourceScript" -ForegroundColor Green
} elseif (-not (Test-Path $scriptPath)) {
    Write-Warning "Start-OSDeploy.ps1 not found in WIM or installed scripts."
    & $adkDism /Unmount-Image /MountDir:$MountPath /Discard
    exit 1
}

$postInstall = @(
    (Join-Path $installDir 'Start-PostInstall.ps1'),
    (Join-Path $PSScriptRoot 'TaskSequences\Start-PostInstall.ps1')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($postInstall) {
    Copy-Item $postInstall -Destination (Join-Path $deployDir 'Start-PostInstall.ps1') -Force
    Write-Host "  Copied latest Start-PostInstall.ps1" -ForegroundColor Green
}

# -- Patch Start-OSDeploy.ps1 -------------------------------------------------
$content = Get-Content $scriptPath -Raw -Encoding UTF8

# Replace server placeholder
$oldServer = 'DEPLOYMGR_SERVER_IP:DEPLOYMGR_HTTPS_PORT'
if ($content -notmatch [regex]::Escape($oldServer)) {
    Write-Warning "Server placeholder '$oldServer' not found in script - may already be patched."
} else {
    $content = $content -replace [regex]::Escape($oldServer), $placeholderValue
    Write-Host "  Replaced server placeholder -> $placeholderValue" -ForegroundColor Green
}

# Replace WinPE password placeholder
$oldPass = 'DEPLOYMGR_WINPE_PASSWORD'
if ($content -notmatch [regex]::Escape($oldPass)) {
    Write-Warning "Password placeholder '$oldPass' not found - may already be patched."
} else {
    $content = $content -replace [regex]::Escape($oldPass), $WinpePassword
    Write-Host "  Replaced WinPE password placeholder." -ForegroundColor Green
}

Set-Content -Path $scriptPath -Value $content -Encoding UTF8 -NoNewline
Write-Host "  Script written." -ForegroundColor Green

# -- Stage WinPE drivers -------------------------------------------------------
# DISM /Add-Driver fails on boot.wim when running from Server 2022 (Error 87).
# Workaround: copy driver files into X:\Drivers\WinPE\ inside the WIM, then
# Start-OSDeploy.ps1 loads them at runtime via pnputil before disk detection.
$DriverPaths = @(if ($DriverPathList) {
    $DriverPathList.Split(';', [System.StringSplitOptions]::RemoveEmptyEntries)
} else { @() })

$winpeDriverDir = Join-Path $MountPath 'Drivers\WinPE'
if ($DriverPaths.Count -gt 0) {
    Write-Host ""
    Write-Host "Staging WinPE drivers (file copy into WIM)..." -ForegroundColor Yellow
    if (Test-Path $winpeDriverDir) {
        Remove-Item $winpeDriverDir -Recurse -Force
        Write-Host "  Cleared previous WinPE drivers." -ForegroundColor Gray
    }
    New-Item -ItemType Directory -Path $winpeDriverDir -Force | Out-Null
    $drvTotal = 0; $drvFail = 0
    foreach ($drvPath in $DriverPaths) {
        $drvPath = $drvPath.Trim()
        if (-not $drvPath) { continue }
        if (-not (Test-Path $drvPath)) {
            Write-Warning "  Driver path not found, skipping: $drvPath"
            $drvFail++
            continue
        }
        $folderName = Split-Path $drvPath -Leaf
        $destDir    = Join-Path $winpeDriverDir $folderName
        Write-Host "  Copying: $drvPath -> Drivers\WinPE\$folderName" -ForegroundColor Gray
        Copy-Item -Path $drvPath -Destination $destDir -Recurse -Force
        $drvTotal++
    }
    if ($drvTotal -gt 0) {
        Write-Host "  $drvTotal driver folder(s) staged into boot.wim." -ForegroundColor Green
    }
    if ($drvFail -gt 0) {
        Write-Warning "  $drvFail driver path(s) failed or were skipped."
    }
} else {
    Write-Host ""
    Write-Host "No WinPE drivers configured - skipping driver staging." -ForegroundColor DarkGray
    if (Test-Path $winpeDriverDir) {
        Remove-Item $winpeDriverDir -Recurse -Force
        Write-Host "  Cleared stale WinPE drivers from WIM." -ForegroundColor Gray
    }
}

# -- Unmount and commit -------------------------------------------------------
Write-Host ""
Write-Host "Committing changes..." -ForegroundColor Yellow
& $adkDism /Unmount-Image /MountDir:$MountPath /Commit
if ($LASTEXITCODE -ne 0) {
    Write-Error "DISM unmount/commit failed (exit $LASTEXITCODE). Check C:\Windows\Logs\DISM\dism.log"
    exit 1
}

Write-Host ""
Write-Host "boot.wim patched successfully." -ForegroundColor Green

# -- Ensure BCD and boot.sdi exist alongside boot.wim -------------------------
# wimboot (iPXE HTTP boot) requires BCD and boot.sdi in the same Boot\ folder.
# These are standard WinPE files generated by the ADK and do not change between
# deployments. If they are missing, look for them in the standard ADK WinPE
# working directories and copy them automatically.
$bootDir = Split-Path $BootWimPath -Parent
foreach ($bootFile in 'BCD', 'boot.sdi') {
    $dest = Join-Path $bootDir $bootFile
    if (Test-Path $dest) {
        Write-Host "  $bootFile already present." -ForegroundColor Gray
        continue
    }
    # Search standard ADK WinPE locations
    $candidates = @(
        "$env:ProgramFiles\Windows Kits\10\Assessment and Deployment Kit\Windows Preinstallation Environment\amd64\Media\Boot\$bootFile",
        "C:\WinPE_amd64\media\Boot\$bootFile",
        "C:\WinPE_amd64\Boot\$bootFile"
    )
    $src = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($src) {
        Copy-Item $src -Destination $dest -Force
        Write-Host "  Copied $bootFile from: $src" -ForegroundColor Green
    } else {
        Write-Warning "$bootFile not found alongside boot.wim and not found in standard ADK locations."
        Write-Warning "Copy $bootFile manually to: $bootDir"
        Write-Warning "  Source: your WinPE working directory (e.g. C:\WinPE_amd64\media\Boot\$bootFile)"
    }
}

Write-Host ""
Write-Host "DHCP reminder: option 66 = this server IP, option 67 = ipxe-shim.efi" -ForegroundColor Gray
