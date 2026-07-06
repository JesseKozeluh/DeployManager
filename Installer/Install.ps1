#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Pre-flight installer for Deploy Manager.
    Ensures .NET 8 ASP.NET Core Runtime (x64) is present, then runs the MSI.
.DESCRIPTION
    Run this script from an elevated PowerShell prompt instead of double-clicking
    the MSI when the target machine may not have .NET 8 installed.  The script
    uses winget to install the runtime silently if it is missing, then launches
    the MSI with a basic progress UI (/qb).
.EXAMPLE
    PowerShell -ExecutionPolicy Bypass -File .\Install.ps1
#>

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# ── 1. Check for .NET 8+ Runtime AND ASP.NET Core Runtime ────────────────────
# These are SEPARATE installs: the standalone "ASP.NET Core Runtime" package
# provides only the Microsoft.AspNetCore.App shared framework, and the base
# ".NET Runtime" package provides only Microsoft.NETCore.App / hostfxr.
# The app needs both. The simplest single manual download that satisfies both
# is the "ASP.NET Core Hosting Bundle" from the .NET 8 download page.

$manualHelp = "`nInstall the 'ASP.NET Core 8 Hosting Bundle' manually from" +
              "`nhttps://dotnet.microsoft.com/download/dotnet/8.0 (it includes both required" +
              "`nruntimes), then re-run this script."

$dotnetCmd   = Get-Command dotnet -ErrorAction SilentlyContinue
$runtimeList = if ($dotnetCmd) { & dotnet --list-runtimes 2>$null } else { @() }

$haveBase = $runtimeList | Where-Object { $_ -match '^Microsoft\.NETCore\.App [89]\.' }
$haveAsp  = $runtimeList | Where-Object { $_ -match '^Microsoft\.AspNetCore\.App [89]\.' }

if ($haveBase) { Write-Host "OK: $($haveBase | Select-Object -First 1)" -ForegroundColor Green }
if ($haveAsp)  { Write-Host "OK: $($haveAsp  | Select-Object -First 1)" -ForegroundColor Green }

if (-not ($haveBase -and $haveAsp)) {
    $missing = @()
    if (-not $haveBase) { $missing += '.NET Runtime 8' }
    if (-not $haveAsp)  { $missing += 'ASP.NET Core Runtime 8' }
    Write-Host "Missing: $($missing -join ', ') — installing via winget..." -ForegroundColor Yellow

    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        Write-Error ('winget is not available on this machine.' + $manualHelp)
        exit 1
    }

    $wingetIds = @()
    if (-not $haveBase) { $wingetIds += 'Microsoft.DotNet.Runtime.8' }
    if (-not $haveAsp)  { $wingetIds += 'Microsoft.DotNet.AspNetCore.8' }

    foreach ($id in $wingetIds) {
        & winget install $id `
            --silent `
            --accept-source-agreements `
            --accept-package-agreements

        # winget exit codes: 0 = success, -1978335189 (0x8A150003) = already installed
        if ($LASTEXITCODE -notin 0, -1978335189) {
            Write-Error ("winget install $id exited with code $LASTEXITCODE." + $manualHelp)
            exit 1
        }
    }

    Write-Host '.NET 8 runtimes installed.' -ForegroundColor Green
}

# ── 2. Locate the MSI ────────────────────────────────────────────────────────

$msi = Get-Item (Join-Path $PSScriptRoot 'DeployManager-*.msi') -ErrorAction SilentlyContinue |
       Sort-Object Name -Descending |
       Select-Object -First 1

if (-not $msi) {
    Write-Error "No DeployManager-*.msi found in $PSScriptRoot"
    exit 1
}

Write-Host "Installing $($msi.Name) ..." -ForegroundColor Cyan

# ── 3. Run the MSI ───────────────────────────────────────────────────────────

$proc = Start-Process msiexec.exe `
    -ArgumentList "/i `"$($msi.FullName)`" /qb" `
    -Wait `
    -PassThru

if ($proc.ExitCode -eq 0) {
    Write-Host 'Deploy Manager installed successfully.' -ForegroundColor Green
} elseif ($proc.ExitCode -eq 3010) {
    Write-Host 'Deploy Manager installed — a reboot is required to complete setup.' -ForegroundColor Yellow
} else {
    Write-Error "msiexec exited with code $($proc.ExitCode). Check the Windows Event Log for details."
}

exit $proc.ExitCode
