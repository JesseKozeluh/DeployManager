# Runs inside WinPE on the target machine after PXE boot.
# Downloads live config from the deploy server at startup - no hardcoded
# domain, sites, credentials, or timezone values except the bootstrap URL.

if (-not (Test-Path 'HKLM:\SYSTEM\CurrentControlSet\Control\MiniNT')) {
    Write-Warning "Not running in WinPE - exiting for safety."
    exit 1
}

# -- Bootstrap -----------------------------------------------------------------
# Both values below are replaced by Update-BootWim.ps1 / Settings > Boot Image.
# If the server URL or WinPE password changes, re-patch and rebuild boot.wim.
$bootstrapServer            = 'https://DEPLOYMGR_SERVER_IP:DEPLOYMGR_HTTPS_PORT'
$global:WinpeLocalPassword  = 'DEPLOYMGR_WINPE_PASSWORD'

# WinPE trusts the deploy server over HTTPS. The server uses a self-signed cert;
# we skip chain validation but still get wire encryption (protects against passive
# eavesdropping on the LAN). For full ISM-1139 compliance, import your internal CA
# cert into WinPE's trusted root store and remove this bypass.
[System.Net.ServicePointManager]::ServerCertificateValidationCallback = {$true}
[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12

# -- Network (must complete BEFORE any download) --------------------------------
# wpeinit starts DHCP but returns immediately - it does NOT wait for a lease.
# If we download config before the NIC has an address, the socket fails with
# "Unable to connect to the remote server" and we wrongly fall back to defaults.
# Wait for a routable IP first, THEN download config.
Write-Host "Waiting for network..." -ForegroundColor Gray
$netWait = 0
do {
    $ip = (Get-WmiObject Win32_NetworkAdapterConfiguration -Filter 'IPEnabled=True').IPAddress |
          Where-Object { $_ -match '^\d+\.\d+\.\d+\.\d+$' -and $_ -notmatch '^(127\.|169\.254\.)' } |
          Select-Object -First 1
    if ($ip) { break }
    Start-Sleep 2
    $netWait += 2
    Write-Host "`r  Still waiting... ($netWait s)" -NoNewline -ForegroundColor Gray
} while ($netWait -lt 60)
Write-Host ""
if ($ip) {
    Write-Host "  Network ready: $ip" -ForegroundColor Green
} else {
    Write-Warning "No IP obtained after 60 s - continuing without network may fail."
}
Write-Host ""

# Download live config from the ASP.NET API (HTTPS, port 8090).
# Provides: $global:DeployServer, $global:ApiServer, $global:Domain,
#           $global:OrgName, $global:ComputerPrefix, $global:DefaultTimezone,
#           $global:WinpeLocalAccount, $global:SiteConfig (array of {Site, Subnet, OU, Timezone})
# NOTE: WinpeLocalPassword is intentionally NOT served here - it is baked into boot.wim.
$configUrl    = "$bootstrapServer/api/deploy-config"
$configScript = 'X:\Deploy\deploy-config.ps1'

# Retry - DHCP/DNS can take a few extra seconds to settle after the first IP appears.
$configLoaded = $false
for ($attempt = 1; $attempt -le 5 -and -not $configLoaded; $attempt++) {
    try {
        (New-Object System.Net.WebClient).DownloadFile($configUrl, $configScript)
        . $configScript
        Write-Host "  Config loaded from server (attempt $attempt)." -ForegroundColor Green
        $configLoaded = $true
    } catch {
        Write-Warning "Config download attempt $attempt failed: $($_.Exception.Message)"
        if ($attempt -lt 5) { Start-Sleep 3 }
    }
}

if (-not $configLoaded) {
    Write-Host ""
    Write-Host ('=' * 60) -ForegroundColor Red
    Write-Host '  CANNOT REACH DEPLOYMANAGER SERVER' -ForegroundColor Red
    Write-Host ('=' * 60) -ForegroundColor Red
    Write-Host ""
    Write-Host "  URL attempted : $configUrl" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "  Verify:" -ForegroundColor White
    Write-Host "    1. This machine has a network connection (check DHCP lease)" -ForegroundColor Gray
    Write-Host "    2. DeployManager Windows Service is running on the server" -ForegroundColor Gray
    Write-Host "    3. DHCP options 066/067 point to the correct server IP" -ForegroundColor Gray
    Write-Host "    4. Firewall allows inbound TCP $($bootstrapServer -replace 'https?://[^:]+:','') on the server" -ForegroundColor Gray
    Write-Host ""
    Write-Host "  Reboot this machine once the server is reachable." -ForegroundColor Yellow
    Write-Host ""
    # Do not proceed without server config - there are no safe hardcoded defaults.
    pause
    exit 1
}

$deployServer = $global:DeployServer
$apiServer    = $global:ApiServer
$wimIndex     = 1

# -- Helper - resolve site from machine IP using downloaded site table ----------
function Resolve-Site {
    $machineIp = (Get-WmiObject Win32_NetworkAdapterConfiguration -Filter 'IPEnabled=True').IPAddress |
                 Where-Object { $_ -match '^\d+\.\d+\.\d+\.\d+$' -and $_ -notmatch '^(127\.|169\.254\.)' } |
                 Select-Object -First 1
    if ($machineIp -and $global:SiteConfig) {
        foreach ($s in $global:SiteConfig) {
            if (-not $s.Subnet) { continue }
            try {
                $parts = $s.Subnet -split '/'
                $subnetAddr  = $parts[0]
                $prefix      = [int]$parts[1]
                $mask        = [uint32](0xFFFFFFFF -shl (32 - $prefix))
                $subnetInt   = [uint32]([System.Net.IPAddress]::Parse($subnetAddr).Address -band 0xFFFFFFFF)
                $machineInt  = [uint32]([System.Net.IPAddress]::Parse($machineIp).Address -band 0xFFFFFFFF)
                # Convert to big-endian for comparison
                $subnetInt = [System.Net.IPAddress]::NetworkToHostOrder([int]$subnetInt)
                $machineInt = [System.Net.IPAddress]::NetworkToHostOrder([int]$machineInt)
                $mask = [System.Net.IPAddress]::NetworkToHostOrder([int]$mask)
                if (($machineInt -band $mask) -eq ($subnetInt -band $mask)) {
                    $tz = if ($s.Timezone) { $s.Timezone } else { $global:DefaultTimezone }
                    return [PSCustomObject]@{ Site = $s.Name; OU = $s.OU; Timezone = $tz }
                }
            } catch { }
        }
    }
    return [PSCustomObject]@{
        Site     = 'Unknown'
        OU       = ''
        Timezone = $global:DefaultTimezone
    }
}

# -- Abort helper --------------------------------------------------------------
# Sets the X-Deploy-Token header on a WebRequest if a token is known.
# $script:apiToken is populated after the job file is downloaded (line ~230).
function Set-DeployToken([System.Net.WebRequest]$req) {
    if ($script:apiToken) { $req.Headers.Add('X-Deploy-Token', $script:apiToken) }
}

function Send-JobError([string]$message) {
    if (-not $macKey -or -not $apiServer) { return }
    try {
        $safe  = $message -replace '"','\"'
        $req   = [System.Net.WebRequest]::Create("$apiServer/api/jobs/$macKey/imaging-error")
        $req.Method      = 'POST'
        $req.ContentType = 'application/json'
        Set-DeployToken $req
        $bytes = [System.Text.Encoding]::UTF8.GetBytes("{`"message`":`"$safe`"}")
        $req.ContentLength = $bytes.Length
        $s = $req.GetRequestStream(); $s.Write($bytes, 0, $bytes.Length); $s.Close()
        $req.GetResponse().Close()
    } catch { }
}

# -- Banner --------------------------------------------------------------------
if ($global:OrgName) { $banner = "$($global:OrgName) - Windows Deployment" } else { $banner = "Windows Deployment" }
$line   = '=' * ($banner.Length + 4)
Write-Host $line          -ForegroundColor Cyan
Write-Host "  $banner  "  -ForegroundColor Cyan
Write-Host $line          -ForegroundColor Cyan
Write-Host ""

# -- Runtime WinPE driver loading ----------------------------------------------
# ADK DISM /Add-Driver fails for boot.wim (PE image) on Server 2022 due to a
# PEProvider version mismatch. Drivers are instead bundled as files in X:\Drivers\WinPE\
# by Update-BootWim.ps1 and loaded here before disk detection so storage controllers
# (e.g. Intel VMD/RST for OptiPlex 5000 NVMe) are visible when Get-Disk runs.
$wpeDriverDir = 'X:\Drivers\WinPE'
if ((Test-Path $wpeDriverDir) -and (Get-ChildItem $wpeDriverDir -Recurse -Filter *.inf -ErrorAction SilentlyContinue)) {
    Write-Host "Loading WinPE drivers from $wpeDriverDir ..." -ForegroundColor Cyan
    $pnpOut = & pnputil.exe /add-driver $wpeDriverDir /subdirs /install 2>&1
    Write-Host ($pnpOut -join "`n") -ForegroundColor Gray
    Write-Host ""
}


# -- Machine identity + auto-discovery -----------------------------------------
# Gather hardware identity immediately after config loads so the operator can
# see MAC/IP/model/serial on screen and register the machine in Deploy Manager
# without needing a second PXE boot.
$job    = $null
$mac    = (Get-WmiObject Win32_NetworkAdapterConfiguration -Filter 'IPEnabled=True').MACAddress |
          Select-Object -First 1
$model  = (Get-WmiObject Win32_ComputerSystem  -ErrorAction SilentlyContinue).Model
$serial = (Get-WmiObject Win32_BIOS            -ErrorAction SilentlyContinue).SerialNumber

if ($mac) {
    $macKey = $mac -replace ':', ''

    # -- Display machine identity --------------------------------------------
    $boxW = 56
    Write-Host ('=' * $boxW) -ForegroundColor Yellow
    Write-Host '  MACHINE IDENTITY' -ForegroundColor Yellow
    Write-Host ('-' * $boxW) -ForegroundColor DarkYellow
    Write-Host "  MAC    : $mac"    -ForegroundColor White
    Write-Host "  IP     : $ip"     -ForegroundColor White
    Write-Host "  Model  : $model"  -ForegroundColor White
    Write-Host "  Serial : $serial" -ForegroundColor White
    Write-Host ('=' * $boxW) -ForegroundColor Yellow
    Write-Host ""

    # -- Register with Deploy Manager ----------------------------------------
    # Creates (or updates) the machine record so the operator can see this
    # machine in the Machines list with an "In WinPE" badge and assign a job.
    try {
        $safeModel  = ($model  -replace '"','\"')
        $safeSerial = ($serial -replace '"','\"')
        $discBody   = [System.Text.Encoding]::UTF8.GetBytes(
            "{`"mac`":`"$mac`",`"ip`":`"$ip`",`"model`":`"$safeModel`",`"serial`":`"$safeSerial`"}")
        $discReq    = [System.Net.WebRequest]::Create("$apiServer/api/discover")
        $discReq.Method        = 'POST'
        $discReq.ContentType   = 'application/json'
        $discReq.ContentLength = $discBody.Length
        $s = $discReq.GetRequestStream()
        $s.Write($discBody, 0, $discBody.Length); $s.Close()
        $discReq.GetResponse().Close()
        Write-Host "  Registered with Deploy Manager." -ForegroundColor DarkGray
    } catch {
        Write-Host "  Could not register: $($_.Exception.Message)" -ForegroundColor DarkGray
    }
    Write-Host ""

    # -- Poll for deployment job ---------------------------------------------
    # Checks immediately, then every 30 s. The operator creates the job in the
    # web UI; the next poll picks it up - no second PXE boot required.
    # Press Ctrl+C to abort and let the script fall through to manual prompts.
    $jobUrl    = "$deployServer/jobs/$macKey.json"
    $pollCount = 0
    Write-Host "Polling for deployment job (Ctrl+C to proceed manually)..." -ForegroundColor Cyan
    do {
        try {
            $jobJson = (New-Object System.Net.WebClient).DownloadString($jobUrl)
            $job     = $jobJson | ConvertFrom-Json
        } catch { $job = $null }

        if ($job) {
            Write-Host "  Job found!" -ForegroundColor Green
            Write-Host "    Device : $($job.DeviceName)" -ForegroundColor Gray
            Write-Host "    Site   : $($job.Site)"       -ForegroundColor Gray
            Write-Host "    Image  : $($job.WimName)"    -ForegroundColor Gray
            Write-Host ""
            # Store the per-job API token; Set-DeployToken uses it on all callbacks.
            $script:apiToken = if ($job.apiToken) { $job.apiToken } else { '' }
            break
        }

        $pollCount++
        if ($pollCount -eq 1) {
            Write-Host "  No job yet. Create one in Deploy Manager - this machine will pick it up automatically." -ForegroundColor Yellow
        }
        for ($i = 30; $i -gt 0; $i--) {
            Write-Host "`r  Next check in $i s...   " -NoNewline -ForegroundColor DarkGray
            Start-Sleep 1
        }
        Write-Host ""
    } while ($true)
}

# -- Site resolution -----------------------------------------------------------
if ($job -and $job.Site -and $job.OU) {
    # Resolve timezone for the job's site from the downloaded site config
    $siteTimezone = $global:DefaultTimezone
    if ($global:SiteConfig) {
        $matched = $global:SiteConfig | Where-Object { $_.Name -eq $job.Site } | Select-Object -First 1
        if ($matched -and $matched.Timezone) { $siteTimezone = $matched.Timezone }
    }
    $site = [PSCustomObject]@{ Site = $job.Site; OU = $job.OU; Timezone = $siteTimezone }
    Write-Host "Site     : $($site.Site)"     -ForegroundColor Green
    Write-Host "OU       : $($site.OU)"       -ForegroundColor Green
    Write-Host "Timezone : $($site.Timezone)" -ForegroundColor Green
} else {
    $site = Resolve-Site
    Write-Host "Site     : $($site.Site)"     -ForegroundColor Green
    Write-Host "OU       : $($site.OU)"       -ForegroundColor Green
    Write-Host "Timezone : $($site.Timezone)" -ForegroundColor Green
}
Write-Host ""

# -- Computer name -------------------------------------------------------------
if ($job -and $job.DeviceName) {
    $computerName = $job.DeviceName.Trim().ToUpper()
    Write-Host "Computer name : $computerName (from job)" -ForegroundColor Green
} else {
    $prefix        = if ($global:ComputerPrefix) { $global:ComputerPrefix } else { '' }
    $suggestedName = if ($serial) { "$prefix$($serial.Trim())" } else { "${prefix}UNKNOWN" }
    Write-Host "Computer name (press ENTER to accept default):" -ForegroundColor Cyan
    Write-Host "  Default: $suggestedName" -ForegroundColor Gray
    $inputName    = Read-Host "Name"
    $computerName = if ($inputName.Trim()) { $inputName.Trim().ToUpper() } else { $suggestedName }
    if ($computerName.Length -gt 15 -or $computerName -notmatch '^[A-Za-z0-9\-]+$') {
        Write-Warning "Name '$computerName' is invalid (max 15 chars, letters/numbers/hyphens only)."
        $computerName = (Read-Host "Enter a valid computer name").Trim().ToUpper()
    }
    Write-Host "Computer name : $computerName" -ForegroundColor Green
}
Write-Host ""

# -- WIM selection -------------------------------------------------------------
if ($job -and $job.WimName) {
    $wimRelPath = $job.WimName
    Write-Host "Image : $wimRelPath (from job)" -ForegroundColor Green
} else {
    $wimRelPath = Read-Host "WIM path (e.g. Win11Pro/install)"
}
$wimUrl = "$deployServer/images/$wimRelPath.wim"
Write-Host "WIM URL : $wimUrl" -ForegroundColor Gray
Write-Host ""

# -- Join mode -----------------------------------------------------------------
$joinMode   = if ($job -and $job.JoinMode)  { $job.JoinMode }  else { 'domain' }
$workgroup  = if ($job -and $job.Workgroup) { $job.Workgroup } else { 'WORKGROUP' }
$djoinBlob  = if ($job -and $job.DjoinBlob) { $job.DjoinBlob } else { $null }
$djUser     = $null; $djPassPlain = $null

if ($joinMode -eq 'workgroup') {
    Write-Host "Join mode  : Workgroup ($workgroup) - no domain join (Autopilot/Intune)" -ForegroundColor Cyan
}
elseif ($djoinBlob) {
    Write-Host "Join mode  : Domain join - offline blob present, no credentials needed." -ForegroundColor Green
}
elseif ($job) {
    # Automated/headless deploy with NO blob (server-side provisioning failed).
    # Do NOT prompt for credentials - that hangs a remote machine in WinPE forever.
    # Abort BEFORE the disk wipe and reboot back to the existing OS so the machine
    # recovers itself; the operator fixes provisioning and re-deploys.
    $msg = "No domain-join blob in the job for $computerName - server-side provisioning failed (check the deployment job error). Aborting before any disk changes; this machine is left unmodified. Fix provisioning and re-deploy."
    Write-Host $msg -ForegroundColor Red
    try { Send-JobError $msg } catch {}
    Write-Host "Rebooting back to the existing OS in 20 seconds..." -ForegroundColor Yellow
    Start-Sleep 20
    & wpeutil reboot
    exit 1
}
else {
    Write-Host "Join mode  : Domain join - no blob, entering credential prompt." -ForegroundColor Yellow
    Write-Host "Domain join credentials:" -ForegroundColor Cyan
    Write-Host "  (Use a service account with rights to join $global:Domain)" -ForegroundColor Gray
    $djUser       = Read-Host "  Username"
    $djPassSecure = Read-Host "  Password" -AsSecureString
    $djPassPlain  = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($djPassSecure))
}
Write-Host ""

# -- Select target disk (largest FIXED disk; never removable/USB/virtual) -------
$allDisks = Get-Disk | Sort-Object Number
$diskInfo = ($allDisks | Select-Object Number, FriendlyName,
                @{N='SizeGB';E={[math]::Round($_.Size/1GB,0)}}, BusType, PartitionStyle, OperationalStatus |
            Format-Table -AutoSize | Out-String).Trim()
Write-Host "Disks detected:" -ForegroundColor Gray
Write-Host $diskInfo -ForegroundColor Gray

$target = $allDisks |
    Where-Object { $_.BusType -notin @('USB','SD','MMC','SDIO','Virtual','File Backed Virtual') } |
    Sort-Object Size -Descending | Select-Object -First 1
if (-not $target) {
    $msg = "No fixed disk found to image.`n--- Disks ---`n$diskInfo"
    Write-Error $msg; Send-JobError $msg; exit 1
}
$diskNum = $target.Number
Write-Host ("Target disk: {0} ({1}, {2} GB, {3})" -f $diskNum, $target.FriendlyName, [math]::Round($target.Size/1GB,0), $target.BusType) -ForegroundColor Green

if ($job) {
    Write-Host "Disk $diskNum will be wiped and Windows installed. Starting in 5 seconds (Ctrl+C to abort)..." -ForegroundColor Yellow
    Start-Sleep 5
} else {
    Write-Host "Disk $diskNum will be wiped and Windows installed. Press ENTER to continue (Ctrl+C to abort)..." -ForegroundColor Yellow
    Read-Host
}

# -- Partition disk -------------------------------------------------------------
Write-Host "Partitioning disk $diskNum..." -ForegroundColor Green
$dpScript = @"
select disk $diskNum
clean
convert gpt
create partition efi size=260
format quick fs=fat32 label=EFI
assign letter=S
create partition msr size=16
create partition primary
format quick fs=ntfs label=Windows
assign letter=W
exit
"@
$dpFile = 'X:\Deploy\diskpart.txt'
Set-Content -Path $dpFile -Value $dpScript -Encoding ASCII
$dpOut = (& diskpart /s $dpFile 2>&1 | Out-String).Trim()
Remove-Item $dpFile -Force -ErrorAction SilentlyContinue
Write-Host $dpOut -ForegroundColor Gray

$efiDrive = 'S:'
$winDrive = 'W:'

if (-not (Test-Path "$winDrive\")) {
    # Report the disk list AND the full diskpart output back to the job so failures
    # on a remote machine are diagnosable in the web UI (no more flying blind).
    $msg = "Diskpart failed - W: not found on disk $diskNum.`n--- Disks ---`n$diskInfo`n--- Diskpart output ---`n$dpOut"
    Write-Error $msg; Send-JobError $msg; exit 1
}
Write-Host "Windows : $winDrive   EFI : $efiDrive" -ForegroundColor Green

# -- Download and apply WIM ----------------------------------------------------
$localWim   = "$winDrive\install.wim"
$totalBytes = 0
try {
    $req = [System.Net.HttpWebRequest]::Create($wimUrl)
    $req.Method  = 'HEAD'
    $req.Timeout = 10000
    $resp = $req.GetResponse()
    $totalBytes = $resp.ContentLength
    $resp.Close()
} catch {}
$totalMB = [math]::Round($totalBytes / 1MB)
Write-Host "Downloading image ($totalMB MB)..." -ForegroundColor Green

# Resumable, retrying download (HTTP Range) - survives WAN drops without restarting.
# WinPE has no BITS, so this uses .NET HttpWebRequest with byte-range resume.
if (Test-Path $localWim) { Remove-Item $localWim -Force -ErrorAction SilentlyContinue }
$maxAttempts = 20
$attempt     = 0
$complete    = $false
$spin        = @('|', '/', '-', '\')
$colors      = @('Cyan', 'Green', 'Yellow', 'Magenta', 'Cyan', 'Green')
$frame       = 0
Write-Host ""
while (-not $complete -and $attempt -lt $maxAttempts) {
    $attempt++
    $have = 0
    if (Test-Path $localWim) { $have = (Get-Item $localWim).Length }
    if ($totalBytes -gt 0 -and $have -ge $totalBytes) { $complete = $true; break }
    if ($attempt -gt 1) {
        Write-Host ("  Resuming download (attempt {0}) from {1} MB..." -f $attempt, [math]::Round($have / 1MB)) -ForegroundColor Yellow
    }
    $fs = $null; $stream = $null; $resp = $null
    try {
        $req = [System.Net.HttpWebRequest]::Create($wimUrl)
        $req.Timeout          = 30000
        $req.ReadWriteTimeout = 120000
        if ($have -gt 0) { $req.AddRange([long]$have) }
        $resp   = $req.GetResponse()
        $stream = $resp.GetResponseStream()
        $fs     = [System.IO.File]::Open($localWim, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write)
        $buf    = New-Object byte[] 1048576
        $last   = [DateTime]::Now
        while (($read = $stream.Read($buf, 0, $buf.Length)) -gt 0) {
            $fs.Write($buf, 0, $read)
            $have += $read
            if (([DateTime]::Now - $last).TotalMilliseconds -ge 300) {
                $dlMB = [math]::Round($have / 1MB)
                if ($totalBytes -gt 0) {
                    $filled = [int]($have * 36 / $totalBytes)
                    $bar    = '[' + ('#' * $filled) + ('.' * (36 - $filled)) + ']'
                    $pct    = [int]($have * 100 / $totalBytes)
                    $line   = "  $($spin[$frame % 4])  $bar  $pct%  $dlMB / $totalMB MB"
                } else {
                    $line   = "  $($spin[$frame % 4])  $dlMB MB downloaded..."
                }
                Write-Host "`r$line     " -NoNewline -ForegroundColor $colors[$frame % 6]
                $frame++; $last = [DateTime]::Now
            }
        }
        $fs.Close(); $stream.Close(); $resp.Close(); $fs = $null
        if ($totalBytes -le 0 -or $have -ge $totalBytes) { $complete = $true }
    } catch {
        Write-Host ""
        Write-Host ("  Download interrupted: {0}" -f $_.Exception.Message) -ForegroundColor Yellow
        if ($fs)     { try { $fs.Close() }     catch {} }
        if ($stream) { try { $stream.Close() } catch {} }
        if ($resp)   { try { $resp.Close() }   catch {} }
        Start-Sleep -Seconds 8
    }
}
Write-Host ""
if (-not $complete -or -not (Test-Path $localWim) -or (Get-Item $localWim).Length -lt 1073741824) {
    $msg = "WIM download failed or incomplete after $attempt attempt(s) (URL: $wimUrl)."
    Write-Error $msg; Send-JobError $msg; exit 1
}
Write-Host "  Download complete ($attempt attempt(s))." -ForegroundColor Green

Write-Host "Applying image..." -ForegroundColor Green
try {
    Expand-WindowsImage -ImagePath $localWim -Index $wimIndex -ApplyPath "$winDrive\" -ErrorAction Stop
    Write-Host "  Image applied." -ForegroundColor Green
} catch {
    $msg = "Image apply failed: $_"
    Write-Error $msg; Send-JobError $msg; exit 1
}
Remove-Item $localWim -Force -ErrorAction SilentlyContinue

# -- Join configuration --------------------------------------------------------
if ($joinMode -eq 'workgroup') {
    Write-Host "Join : workgroup '$workgroup' - OOBE will run for Autopilot enrollment." -ForegroundColor Cyan
} elseif ($djoinBlob) {
    Write-Host "Join : offline domain-join blob applied via unattend.xml." -ForegroundColor Green
}

# -- Unattend.xml --------------------------------------------------------------
function ConvertTo-XmlSafe([string]$s) {
    [System.Security.SecurityElement]::Escape($s)
}

$xmlComputer  = ConvertTo-XmlSafe $computerName
$xmlDomain    = ConvertTo-XmlSafe $global:Domain
$xmlTimezone  = ConvertTo-XmlSafe $site.Timezone
$xmlLocalAcct = ConvertTo-XmlSafe $global:WinpeLocalAccount
$xmlLocalPass = ConvertTo-XmlSafe $global:WinpeLocalPassword

# Locale: prefer site-level override, then global default, then en-US
$resolvedLocale = if ($site.Locale)              { $site.Locale }
                  elseif ($global:DefaultLocale) { $global:DefaultLocale }
                  else                           { 'en-US' }
$xmlLocale = ConvertTo-XmlSafe $resolvedLocale

$joinXml = if ($joinMode -eq 'workgroup') {
    $xmlWorkgroup = ConvertTo-XmlSafe $workgroup
    @"
    <component name="Microsoft-Windows-UnattendedJoin" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
      <Identification>
        <JoinWorkgroup>$xmlWorkgroup</JoinWorkgroup>
      </Identification>
    </component>
"@
} elseif ($djoinBlob) {
    $xmlBlob = ConvertTo-XmlSafe ($djoinBlob.Trim())
    @"
    <component name="Microsoft-Windows-UnattendedJoin" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
      <Identification>
        <Provisioning>
          <AccountData>$xmlBlob</AccountData>
        </Provisioning>
      </Identification>
    </component>
"@
} elseif ($djUser) {
    $xmlUser = ConvertTo-XmlSafe $djUser
    $xmlPass = ConvertTo-XmlSafe $djPassPlain
    $xmlOU   = ConvertTo-XmlSafe $site.OU
    @"
    <component name="Microsoft-Windows-UnattendedJoin" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
      <Identification>
        <Credentials>
          <Domain>$xmlDomain</Domain>
          <Password>$xmlPass</Password>
          <Username>$xmlUser</Username>
        </Credentials>
        <JoinDomain>$xmlDomain</JoinDomain>
        <MachineObjectOU>$xmlOU</MachineObjectOU>
      </Identification>
    </component>
"@
} else { '' }

$unattendXml = @"
<?xml version="1.0" encoding="utf-8"?>
<unattend xmlns="urn:schemas-microsoft-com:unattend"
          xmlns:wcm="urn:schemas-microsoft-com:unattend"
          xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <settings pass="specialize">
    <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
      <ComputerName>$xmlComputer</ComputerName>
      <TimeZone>$xmlTimezone</TimeZone>
    </component>
$joinXml
    <component name="Microsoft-Windows-Deployment" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
      <RunSynchronous>
        <RunSynchronousCommand wcm:action="add">
          <Order>1</Order>
          <Path>reg add HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\OOBE /v BypassNRO /t REG_DWORD /d 1 /f</Path>
        </RunSynchronousCommand>
      </RunSynchronous>
    </component>
    <component name="Microsoft-Windows-International-Core" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
      <InputLocale>$xmlLocale</InputLocale>
      <SystemLocale>$xmlLocale</SystemLocale>
      <UILanguage>$xmlLocale</UILanguage>
      <UILanguageFallback>en-US</UILanguageFallback>
      <UserLocale>$xmlLocale</UserLocale>
    </component>
  </settings>
  <settings pass="oobeSystem">
    <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
$(if ($joinMode -eq 'workgroup') {
    # Autopilot/Intune: OOBE must run so the Autopilot profile can enroll the device.
    # Do NOT skip OOBE or hide online account screens.
    @"
      <OOBE>
        <HideEULAPage>true</HideEULAPage>
        <HideOEMRegistrationScreen>true</HideOEMRegistrationScreen>
        <HideWirelessSetupInOOBE>false</HideWirelessSetupInOOBE>
        <ProtectYourPC>3</ProtectYourPC>
      </OOBE>
"@
} else {
    @"
      <OOBE>
        <HideEULAPage>true</HideEULAPage>
        <HideOEMRegistrationScreen>true</HideOEMRegistrationScreen>
        <HideOnlineAccountScreens>true</HideOnlineAccountScreens>
        <HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE>
        <NetworkLocation>Work</NetworkLocation>
        <ProtectYourPC>3</ProtectYourPC>
        <SkipMachineOOBE>true</SkipMachineOOBE>
        <SkipUserOOBE>true</SkipUserOOBE>
      </OOBE>
      <UserAccounts>
        <LocalAccounts>
          <LocalAccount wcm:action="add">
            <Password>
              <Value>$xmlLocalPass</Value>
              <PlainText>true</PlainText>
            </Password>
            <Group>Administrators</Group>
            <Name>$xmlLocalAcct</Name>
            <DisplayName>$xmlLocalAcct</DisplayName>
          </LocalAccount>
        </LocalAccounts>
      </UserAccounts>
"@
})
    </component>
    <component name="Microsoft-Windows-International-Core" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
      <InputLocale>$xmlLocale</InputLocale>
      <SystemLocale>$xmlLocale</SystemLocale>
      <UILanguage>$xmlLocale</UILanguage>
      <UILanguageFallback>en-US</UILanguageFallback>
      <UserLocale>$xmlLocale</UserLocale>
    </component>
  </settings>
</unattend>
"@

$pantherDir = "$winDrive\Windows\Panther"
New-Item -ItemType Directory -Path $pantherDir -Force | Out-Null
Set-Content -Path "$pantherDir\unattend.xml" -Value $unattendXml -Encoding UTF8
Write-Host "  Unattend written." -ForegroundColor Green
Write-Host "    Computer : $computerName"    -ForegroundColor Gray
Write-Host "    Timezone : $($site.Timezone)" -ForegroundColor Gray
if ($joinMode -eq 'workgroup') {
    Write-Host "    Join     : workgroup '$workgroup' (Autopilot/Intune)" -ForegroundColor Gray
} else {
    Write-Host "    Domain   : $global:Domain"   -ForegroundColor Gray
    Write-Host "    Join     : $(if ($djoinBlob) { 'offline (blob)' } else { 'credentials via unattend' })" -ForegroundColor Gray
}

# -- Boot files -----------------------------------------------------------------
bcdboot "$winDrive\Windows" /s $efiDrive /f UEFI
if ($LASTEXITCODE -ne 0) {
    $msg = "bcdboot failed (exit $LASTEXITCODE)."
    Write-Error $msg; Send-JobError $msg; exit 1
}

$fwLines = & bcdedit /enum firmware 2>&1
$inEntry = $false; $guid = $null
foreach ($line in $fwLines) {
    if ($line -match '^\s*identifier\s+(\{[^}]+\})') { $guid = $Matches[1]; $inEntry = $true }
    if ($inEntry -and $line -match 'Windows Boot Manager') { break }
    if ($line -match '^\s*$') { $inEntry = $false; $guid = $null }
}
if ($guid) {
    bcdedit /set '{fwbootmgr}' bootsequence $guid | Out-Null
    Write-Host "  Next boot: local Windows Boot Manager ($guid)" -ForegroundColor Green
} else {
    Write-Warning "Could not find firmware boot entry - set BIOS boot order to disk first."
}

# -- Setup scripts --------------------------------------------------------------
$setupScripts = "$winDrive\Windows\Setup\Scripts"
New-Item -ItemType Directory -Path $setupScripts -Force | Out-Null

@{
    Site               = $site.Site
    OU                 = $site.OU
    Timezone           = $site.Timezone
    Domain             = $global:Domain
    ComputerName       = $computerName
    PackageId          = if ($job -and $job.PackageId) { $job.PackageId } else { '' }
    DriverPackage      = if ($job -and $job.DriverPackage) { $job.DriverPackage } else { $null }
    DeployServer       = $deployServer
    ApiServer          = $apiServer
    ApiToken           = if ($script:apiToken) { $script:apiToken } else { '' }
    SoftwareItems      = if ($job -and $job.SoftwareItems) { $job.SoftwareItems } else { @() }
    EnableBranchCache  = if ($global:EnableBranchCache) { $global:EnableBranchCache } else { $false }
} | ConvertTo-Json -Depth 5 | Set-Content "$setupScripts\DeployConfig.json" -Encoding UTF8

Copy-Item 'X:\Deploy\Start-PostInstall.ps1' "$setupScripts\Start-PostInstall.ps1" -Force

@"
@ECHO OFF
PowerShell -NoLogo -ExecutionPolicy Bypass -File "%~dp0Start-PostInstall.ps1" >> "%~dp0PostInstall.log" 2>&1
"@ | Set-Content "$setupScripts\SetupComplete.cmd" -Encoding ASCII

# -- Notify server -------------------------------------------------------------
if ($macKey) {
    try {
        $req = [System.Net.WebRequest]::Create("$apiServer/api/jobs/$macKey/imaging-started")
        $req.Method        = 'POST'
        $req.ContentType   = 'application/json'
        Set-DeployToken $req
        $req.ContentLength = 0
        $req.GetRequestStream().Close()
        $req.GetResponse().Close()
        Write-Host "  Job marked as imaging on server." -ForegroundColor Green
    } catch {
        Write-Warning "  Could not notify server of imaging start: $_"
    }
}

Write-Host ""
Write-Host "Image applied. Rebooting in 10 seconds..." -ForegroundColor Green
Start-Sleep 10
& wpeutil reboot
