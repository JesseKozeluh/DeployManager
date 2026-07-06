<#
.SYNOPSIS
    Builds the Deploy Manager MSI installer.

.DESCRIPTION
    1. Publishes the WebApp (framework-dependent, win-x64).
    2. Publishes the TrayApp (self-contained, win-x64).
    3. Stages the WebApp publish output, replacing appsettings.json with the
       installer template so upgrades never overwrite user configuration.
    4. Verifies tftpd64 service edition vendor files in
       Installer\Vendor\tftpd64\ (tftpd64_svc.exe from GitHub releases).
    5. Installs WiX extensions if not already present.
    6. Runs wix build to produce DeployManager-<Version>-Setup.msi.

.PARAMETER Version
    Semver string embedded in the MSI (no pre-release suffix). Default: 1.0.0

.PARAMETER OutDir
    Output directory for the finished .msi.
    Default: .\Installer\Output\

.PARAMETER SkipPublish
    Skip dotnet publish steps (reuse existing publish output). Useful when
    only the WXS or build script has changed.

.NOTES
    Prerequisites (this machine):
      - .NET 8 SDK          (dotnet publish)
      - WiX 7 global tool   (dotnet tool install --global wix)

    Prerequisites (TARGET machine where the MSI will be installed):
      - ASP.NET Core 8 Runtime (x64)
        https://dotnet.microsoft.com/download/dotnet/8.0
      - Windows ADK (only required for the boot.wim patch feature)
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $Version     = '1.0.0',
    [string] $OutDir      = '',
    [switch] $SkipPublish
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot  = $PSScriptRoot
$WebApp    = Join-Path $RepoRoot 'WebApp'
$TrayApp   = Join-Path $RepoRoot 'Installer\TrayApp'
$Scripts   = Join-Path $RepoRoot 'Scripts'
$PkgDir    = Join-Path $RepoRoot 'Installer\Package'
$VendorDir = Join-Path $RepoRoot 'Installer\Vendor\tftpd64'
$IpxeDir   = Join-Path $RepoRoot 'Installer\Vendor\ipxe'
$StageDir  = Join-Path $RepoRoot 'Installer\.stage\webApp'
$OutputDir = if ($OutDir) { $OutDir } else { Join-Path $RepoRoot 'Installer\Output' }

$WebPublish  = Join-Path $WebApp  'bin\Release\net8.0\win-x64\publish'
$TrayPublish = Join-Path $TrayApp 'bin\Release\net8.0-windows\win-x64\publish'

$ChocolateyTftpDir = 'C:\ProgramData\chocolatey\lib\tftpd32\tools'

function Step([string]$msg) {
    Write-Host ""
    Write-Host "  $msg" -ForegroundColor Cyan
}

function OK([string]$msg) {
    Write-Host "  [OK] $msg" -ForegroundColor Green
}

function Fail([string]$msg) {
    Write-Host ""
    Write-Error "BUILD FAILED: $msg"
    exit 1
}

Write-Host ""
Write-Host "Deploy Manager -- Build Installer  v$Version" -ForegroundColor White
Write-Host "--------------------------------------------" -ForegroundColor DarkGray

# ---- Check prerequisites ----------------------------------------------------

Step "Checking prerequisites..."

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Fail ".NET SDK not found. Install from https://dot.net"
}
OK ".NET SDK: $(dotnet --version)"

if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    Fail "WiX tool not found. Run: dotnet tool install --global wix"
}
OK "WiX: $(wix --version)"

# ---- Publish WebApp ---------------------------------------------------------

if (-not $SkipPublish) {
    Step "Publishing WebApp (self-contained, win-x64)..."
    # Self-contained: the .NET runtime ships inside the app, so the MSI has no
    # prerequisites at all. Runtime patches require rebuilding this installer.
    & dotnet publish "$WebApp\DeployManager.csproj" `
        -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=false `
        -p:Version=$Version `
        -o "$WebPublish" `
        --nologo
    if ($LASTEXITCODE -ne 0) { Fail "dotnet publish failed for WebApp." }
    OK "WebApp published to: $WebPublish"
}
else {
    if (-not (Test-Path "$WebPublish\DeployManager.exe")) {
        Fail "SkipPublish set but $WebPublish\DeployManager.exe not found."
    }
    OK "SkipPublish -- using existing: $WebPublish"
}

# ---- Publish TrayApp --------------------------------------------------------

if (-not $SkipPublish) {
    Step "Publishing TrayApp (self-contained, win-x64)..."
    & dotnet publish "$TrayApp\DeployManagerTray.csproj" `
        -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=false `
        -p:Version=$Version `
        -o "$TrayPublish" `
        --nologo
    if ($LASTEXITCODE -ne 0) { Fail "dotnet publish failed for TrayApp." }
    OK "TrayApp published to: $TrayPublish"
}
else {
    if (-not (Test-Path "$TrayPublish\DeployManagerTray.exe")) {
        Fail "SkipPublish set but $TrayPublish\DeployManagerTray.exe not found."
    }
    OK "SkipPublish -- using existing: $TrayPublish"
}

# ---- Validate PowerShell scripts --------------------------------------------
#
# These scripts run under Windows PowerShell 5.1 on the server and inside WinPE.
# Two failure modes have bitten us and are now guarded:
#   1. Non-ASCII bytes in a BOM-less file: PS 5.1 decodes as CP1252 and turns
#      box-drawing/em-dashes into curly quotes it treats as string delimiters,
#      producing cascading "missing terminator" parse errors on the target.
#   2. PS 7-only syntax (e.g. the ternary "? :") that parses fine in the editor
#      but fails on 5.1.
# Fail the build here rather than shipping a script that only breaks at runtime.

Step "Validating PowerShell scripts (ASCII + 5.1 parse)..."
$scriptFiles = Get-ChildItem -Path $Scripts -Recurse -Filter '*.ps1'
$scriptProblems = @()
foreach ($sf in $scriptFiles) {
    $bytes = [System.IO.File]::ReadAllBytes($sf.FullName)
    $nonAscii = @($bytes | Where-Object { $_ -gt 127 }).Count
    if ($nonAscii -gt 0) {
        $scriptProblems += "  $($sf.Name): $nonAscii non-ASCII byte(s) - keep scripts ASCII (see Update-BootWim.ps1 header)."
    }
    $parseErrors = $null
    $null = [System.Management.Automation.PSParser]::Tokenize(
        (Get-Content -Raw -LiteralPath $sf.FullName), [ref]$parseErrors)
    foreach ($pe in $parseErrors) {
        $scriptProblems += "  $($sf.Name) line $($pe.Token.StartLine): $($pe.Message)"
    }
}
if ($scriptProblems.Count -gt 0) {
    Fail ("PowerShell script validation failed:`n" + ($scriptProblems -join "`n"))
}
OK "$($scriptFiles.Count) script(s) validated."

# ---- Stage WebApp files -----------------------------------------------------
#
# The stage dir is what WiX harvests. Remove appsettings*.json so the installer
# installs our own template version instead of the dev/publish copy.

Step "Staging WebApp files..."
if (Test-Path $StageDir) { Remove-Item $StageDir -Recurse -Force }
New-Item -ItemType Directory -Path $StageDir -Force | Out-Null

Copy-Item -Path "$WebPublish\*" -Destination $StageDir -Recurse -Force

Get-ChildItem -Path $StageDir -Filter 'appsettings*.json' | Remove-Item -Force
OK "Staged to: $StageDir"

# ---- Vendor tftpd64 ---------------------------------------------------------

Step "Checking tftpd64 vendor files..."
$RequiredTftp = @('tftpd64_svc.exe', 'tftpd32.chm', 'LICENSE.txt')

$allPresent = $true
foreach ($f in $RequiredTftp) {
    if (-not (Test-Path (Join-Path $VendorDir $f))) { $allPresent = $false; break }
}

if (-not $allPresent) {
    if (Test-Path $ChocolateyTftpDir) {
        Write-Host "  Copying available files from Chocolatey tftpd32 package..." -ForegroundColor Yellow
        New-Item -ItemType Directory -Path $VendorDir -Force | Out-Null
        foreach ($f in $RequiredTftp) {
            $src = Join-Path $ChocolateyTftpDir $f
            if (Test-Path $src) {
                Copy-Item $src -Destination $VendorDir -Force
                Write-Host "    Copied: $f" -ForegroundColor Gray
            }
        }
    }
    if (-not (Test-Path (Join-Path $VendorDir 'tftpd64_svc.exe'))) {
        $msg = "tftpd64_svc.exe (service edition) not found at: $VendorDir`n`n" +
               "Download the service installer from:`n" +
               "  https://github.com/PJO2/tftpd64/releases`n`n" +
               "Install it, then copy tftpd64_svc.exe to:`n  $VendorDir"
        Fail $msg
    }
}

foreach ($f in $RequiredTftp) {
    if (-not (Test-Path (Join-Path $VendorDir $f))) {
        Fail "Missing tftpd64 vendor file: $f"
    }
}
OK "tftpd64 vendor files present at: $VendorDir"

# ---- Vendor iPXE Secure Boot chain ------------------------------------------
# These get bundled into the MSI and pre-staged in the TFTP root so PXE boot
# works with no manual download. If a fresh checkout is missing them, fetch the
# signed release now (same logic as scripts\Get-IpxeBinaries.ps1).

Step "Checking iPXE Secure Boot vendor files..."
$RequiredIpxe    = @('ipxe-shim.efi','ipxe.efi','snponly-shim.efi','snponly.efi','undionly.kpxe','wimboot','VERSION.txt')
$IpxeReleaseTag  = 'v2.0.0'

$ipxeAllPresent = $true
foreach ($f in $RequiredIpxe) {
    if (-not (Test-Path (Join-Path $IpxeDir $f))) { $ipxeAllPresent = $false; break }
}

if (-not $ipxeAllPresent) {
    Write-Host "  Vendor iPXE files missing - downloading signed release $IpxeReleaseTag..." -ForegroundColor Yellow
    New-Item -ItemType Directory -Path $IpxeDir -Force | Out-Null
    [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
    $ipxeTmp = Join-Path $env:TEMP ("ipxe-build-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $ipxeTmp -Force | Out-Null
    try {
        $tarball = Join-Path $ipxeTmp 'ipxeboot.tar.gz'
        Invoke-WebRequest -Uri "https://github.com/ipxe/ipxe/releases/download/$IpxeReleaseTag/ipxeboot.tar.gz" `
            -OutFile $tarball -UseBasicParsing -TimeoutSec 180
        $tarExe = Join-Path $env:SystemRoot 'System32\tar.exe'
        & $tarExe -xzf $tarball -C $ipxeTmp `
            'ipxeboot/x86_64-sb/shimx64.efi' 'ipxeboot/x86_64-sb/ipxe.efi' `
            'ipxeboot/x86_64-sb/snponly.efi' 'ipxeboot/x86_64/undionly.kpxe'
        if ($LASTEXITCODE -ne 0) { Fail "tar extraction of iPXE release failed (exit $LASTEXITCODE)." }
        $sb  = Join-Path $ipxeTmp 'ipxeboot/x86_64-sb'
        $bin = Join-Path $ipxeTmp 'ipxeboot/x86_64'
        Copy-Item (Join-Path $sb  'shimx64.efi') (Join-Path $IpxeDir 'ipxe-shim.efi')    -Force
        Copy-Item (Join-Path $sb  'ipxe.efi')    (Join-Path $IpxeDir 'ipxe.efi')         -Force
        Copy-Item (Join-Path $sb  'shimx64.efi') (Join-Path $IpxeDir 'snponly-shim.efi') -Force
        Copy-Item (Join-Path $sb  'snponly.efi') (Join-Path $IpxeDir 'snponly.efi')      -Force
        Copy-Item (Join-Path $bin 'undionly.kpxe') (Join-Path $IpxeDir 'undionly.kpxe')  -Force
        Invoke-WebRequest -Uri 'https://github.com/ipxe/wimboot/releases/latest/download/wimboot' `
            -OutFile (Join-Path $IpxeDir 'wimboot') -UseBasicParsing -TimeoutSec 120
        "iPXE Secure Boot chain $IpxeReleaseTag + wimboot (auto-fetched by Build-Installer.ps1)." |
            Set-Content (Join-Path $IpxeDir 'VERSION.txt') -Encoding ASCII
    }
    finally { Remove-Item $ipxeTmp -Recurse -Force -ErrorAction SilentlyContinue }
}

foreach ($f in $RequiredIpxe) {
    if (-not (Test-Path (Join-Path $IpxeDir $f))) { Fail "Missing iPXE vendor file: $f" }
}
OK "iPXE Secure Boot vendor files present at: $IpxeDir"

# ---- Install WiX extensions (idempotent) ------------------------------------

Step "Installing WiX extensions..."
& wix extension add WixToolset.Util.wixext     --global 2>&1 | Write-Host
& wix extension add WixToolset.Firewall.wixext --global 2>&1 | Write-Host
& wix extension add WixToolset.UI.wixext       --global 2>&1 | Write-Host
OK "WiX extensions ready."

# ---- Build MSI --------------------------------------------------------------

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$OutMsi  = Join-Path $OutputDir "DeployManager-$Version-Setup.msi"
$WxsFile = Join-Path $PkgDir 'Package.wxs'

Step "Building MSI..."
Write-Host "  Output: $OutMsi" -ForegroundColor Gray

$WixArgs = @(
    'build',
    $WxsFile,
    '-ext', 'WixToolset.Util.wixext',
    '-ext', 'WixToolset.Firewall.wixext',
    '-ext', 'WixToolset.UI.wixext',
    '-d', "WebAppStageDir=$StageDir\",
    '-d', "TrayAppDir=$TrayPublish\",
    '-d', "ScriptsDir=$Scripts\",
    '-d', "VendorTftpDir=$VendorDir\",
    '-d', "IpxeVendorDir=$IpxeDir\",
    '-d', "PackageDir=$PkgDir\",
    '-d', "DocsDir=$RepoRoot\Docs\",
    '-d', "ProductVersion=$Version",
    '-o', $OutMsi,
    '-arch', 'x64'
)

& wix @WixArgs
if ($LASTEXITCODE -ne 0) { Fail "wix build failed." }

# ---- Stamp MSI summary info ------------------------------------------------
# WiX sets Subject from Package.Name but does not expose Title or Category.
# Use the Windows Installer COM API to set them directly on the built MSI.

Step "Stamping MSI summary information..."
$stampScript = Join-Path $env:TEMP "dm_stamp_$([guid]::NewGuid().ToString('N')).ps1"
@"
`$msiPath = '$($OutMsi -replace "'","''")'
`$type = [Type]::GetTypeFromProgID('WindowsInstaller.Installer')
`$inst = [Activator]::CreateInstance(`$type)
`$db = `$inst.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', `$null, `$inst, @(`$msiPath, [int]1))
`$si = `$db.GetType().InvokeMember('SummaryInformation', 'GetProperty', `$null, `$db, @([int]6))
`$si.GetType().InvokeMember('Property', 'SetProperty', `$null, `$si, @([int]2, [string]'Deploy Manager'))
`$si.GetType().InvokeMember('Property', 'SetProperty', `$null, `$si, @([int]3, [string]'Windows Deployment Manager Tool'))
`$si.GetType().InvokeMember('Property', 'SetProperty', `$null, `$si, @([int]4, [string]'Jesse Kozeluh'))
`$si.GetType().InvokeMember('Property', 'SetProperty', `$null, `$si, @([int]5, [string]'Deploy Manager;PXE;Windows Imaging;WinPE;OSD;Deployment'))
`$si.GetType().InvokeMember('Property', 'SetProperty', `$null, `$si, @([int]6, [string]'PXE-based Windows deployment server with web dashboard, TFTP service, iPXE Secure Boot chain, and automated AD join. v$Version'))
`$si.GetType().InvokeMember('Persist', 'InvokeMethod', `$null, `$si, `$null)
`$db.GetType().InvokeMember('Commit', 'InvokeMethod', `$null, `$db, `$null)
[System.Runtime.InteropServices.Marshal]::ReleaseComObject(`$si) | Out-Null
[System.Runtime.InteropServices.Marshal]::ReleaseComObject(`$db) | Out-Null
[System.Runtime.InteropServices.Marshal]::ReleaseComObject(`$inst) | Out-Null
"@ | Set-Content -Path $stampScript -Encoding UTF8
try {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $stampScript
    if ($LASTEXITCODE -ne 0) { throw "stamp script exited with code $LASTEXITCODE" }
    OK "MSI summary info stamped."
}
catch {
    Write-Host "  [WARN] Could not stamp MSI summary info: $($_.Exception.Message)" -ForegroundColor Yellow
}
finally {
    Remove-Item $stampScript -Force -ErrorAction SilentlyContinue
}

# ---- Summary ----------------------------------------------------------------

$size = [math]::Round((Get-Item $OutMsi).Length / 1MB, 1)
Write-Host ""
Write-Host "--------------------------------------------" -ForegroundColor DarkGray
Write-Host "  Build succeeded!" -ForegroundColor Green
Write-Host "  MSI  : $OutMsi"
Write-Host "  Size : $size MB  (self-contained - no .NET prerequisites)"
Write-Host ""
Write-Host "  TARGET machine prerequisites:" -ForegroundColor Yellow
Write-Host "    - None (the .NET runtime ships inside the app)" -ForegroundColor Yellow
Write-Host "    - Windows ADK (optional, for boot.wim patching)" -ForegroundColor Yellow
Write-Host ""
Write-Host "  Post-install steps:" -ForegroundColor Yellow
Write-Host "    1. Set DHCP option 66 = this server's IP" -ForegroundColor Yellow
Write-Host "    2. Set DHCP option 67 = ipxe-shim.efi (UEFI, Secure Boot) / undionly.kpxe (BIOS)" -ForegroundColor Yellow
Write-Host "    3. Open the web UI and follow the Setup Guide (docs\index.html)." -ForegroundColor Yellow
Write-Host ""
