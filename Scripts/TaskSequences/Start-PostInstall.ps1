# Runs on first boot via SetupComplete.cmd, after Windows Setup completes.
# Computer rename and domain join are handled by unattend.xml during setup.

$log        = 'C:\Windows\Setup\Scripts\PostInstall.log'
$configPath = 'C:\Windows\Setup\Scripts\DeployConfig.json'

Start-Transcript -Path $log -Append
Write-Host "=== Post-install starting: $(Get-Date) ==="

# Trust the deploy server's self-signed cert and force TLS 1.2 - same as the WinPE
# bootstrap. Without this, every HTTPS callback to the API on 8090 fails with
# "Could not establish trust relationship for the SSL/TLS secure channel", so software
# progress never shows and the job stays stuck in 'imaging' (the /complete call fails).
[System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }
[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12

if (-not (Test-Path $configPath)) {
    Write-Warning "DeployConfig.json not found - skipping software installs."
    Write-Host "=== Post-install complete (no config): $(Get-Date) ==="
    Stop-Transcript
    exit 0
}

$config          = Get-Content $configPath -Raw | ConvertFrom-Json
$server          = $config.DeployServer
$apiServer       = if ($config.ApiServer)       { $config.ApiServer }       else { $config.DeployServer }
$apiToken        = if ($config.ApiToken)        { $config.ApiToken }        else { '' }
$enableBranchCache = if ($null -ne $config.EnableBranchCache) { [bool]$config.EnableBranchCache } else { $false }

Write-Host "Site         : $($config.Site)"
Write-Host "Domain       : $($config.Domain)"
Write-Host "Deploy server: $server"
Write-Host "API server   : $apiServer"

# Detect MAC address to identify the job on the server
$mac    = (Get-WmiObject Win32_NetworkAdapterConfiguration -Filter 'IPEnabled=True' | Select-Object -First 1).MACAddress
$macKey = if ($mac) { $mac -replace ':', '' } else { '' }

# Helper - fire-and-forget POST to the API; swallows errors so installs are never blocked.
function Send-Api([string]$path, [string]$body) {
    if (-not $macKey -or -not $apiServer) { return }
    try {
        $headers = @{}
        if ($script:apiToken) { $headers['X-Deploy-Token'] = $script:apiToken }
        Invoke-WebRequest -Uri "$apiServer$path" -Method Post -Body $body `
            -ContentType 'application/json' -Headers $headers `
            -UseBasicParsing -TimeoutSec 30 | Out-Null
    } catch {
        Write-Warning "Send-Api failed for $path : $_"
    }
}

# Download a file via BITS (preferred when BranchCache is enabled).
# BITS handles retry/resume and participates in BranchCache peer distribution when
# the client has Distributed Cache mode active and the HTTP source supports BC headers.
function Invoke-BitsDownload([string]$Url, [string]$Dest) {
    $job = Start-BitsTransfer -Source $Url -Destination $Dest -Asynchronous `
               -RetryTimeout 1800 -RetryInterval 30 -Priority Foreground
    try {
        while ($job.JobState -notin @('Transferred', 'Error', 'TransientError')) {
            $pct = if ($job.BytesTotal -gt 0) { [int]($job.BytesTransferred * 100 / $job.BytesTotal) } else { 0 }
            Write-Host "  BITS: $pct% ($([math]::Round($job.BytesTransferred/1MB)) MB)..." -ForegroundColor DarkGray
            Start-Sleep 5
        }
        if ($job.JobState -eq 'Transferred') {
            Complete-BitsTransfer $job
        } else {
            $err = $job.ErrorDescription
            Remove-BitsTransfer $job -ErrorAction SilentlyContinue
            throw "BITS transfer failed: $err"
        }
    } catch {
        try { Remove-BitsTransfer $job -ErrorAction SilentlyContinue } catch {}
        throw
    }
}

# Map a UNC folder to a temporary drive letter. Start-Process / external installers
# cannot use a UNC path as a working directory (DirectoryNotFoundException), and the
# install commands reference items relative to their folder, so we map a drive and run
# from there. Returns the drive (e.g. 'X:') or $null if it couldn't be mapped.
function Mount-UncDrive([string]$uncPath) {
    foreach ($code in 90..68) {                       # Z .. D
        $letter = [char]$code
        if (-not (Test-Path "${letter}:")) {
            & net use "${letter}:" "$uncPath" 2>&1 | Out-Null
            if ($LASTEXITCODE -eq 0) { return "${letter}:" }
        }
    }
    return $null
}

# ---------------------------------------------------------------
# Software installation
# ---------------------------------------------------------------
$items = $config.SoftwareItems
if (-not $items -or $items.Count -eq 0) {
    Write-Host "No software items in config - nothing to install."
} else {
    $total = $items.Count
    Write-Host "Installing $total software item(s)..."

    foreach ($sw in $items) {
        Write-Host ""
        Write-Host "--- Installing: $($sw.Name) ---"

        # Notify server this item is starting
        Send-Api "/api/jobs/$macKey/software/start" `
            "{`"name`":`"$($sw.Name -replace '"','\"')`",`"total`":$total}"

        $result = @{ name = $sw.Name; success = $false; exitCode = -1; error = '' }

        $isUNC = $sw.InstallerPath -match '^\\\\' -or $sw.InstallerPath -match '^//'

        if ($isUNC) {
            $installerResolved = $sw.InstallerPath
            Write-Host "  Source : UNC ($installerResolved)"
        } else {
            $fileName = Split-Path $sw.InstallerPath -Leaf
            $tempFile = Join-Path $env:TEMP $fileName
            $url      = "$server$($sw.InstallerPath)"
            Write-Host "  Source : HTTP ($url)"
            Write-Host "  Downloading to $tempFile $(if ($enableBranchCache) { '(BITS/BranchCache)' } else { '' }) ..."
            try {
                if ($enableBranchCache) {
                    Invoke-BitsDownload -Url $url -Dest $tempFile
                } else {
                    Invoke-WebRequest -Uri $url -OutFile $tempFile -UseBasicParsing -TimeoutSec 300
                }
            } catch {
                $msg = "FAILED to download $($sw.Name): $_"
                Write-Warning "  $msg"
                $result.error = $msg
                Send-Api "/api/jobs/$macKey/software/result" `
                    ($result | ConvertTo-Json -Compress)
                continue
            }
            $installerResolved = $tempFile
        }

        # Resolve the intended working directory
        if ($sw.WorkingDirectory -and $sw.WorkingDirectory.Trim() -ne '') {
            $workDir = $sw.WorkingDirectory.Trim()
        } elseif ($isUNC) {
            $workDir = Split-Path $installerResolved -Parent
        } else {
            $workDir = $env:TEMP
        }

        # A UNC working directory can't be used by Start-Process - map it to a drive.
        $mappedDrive        = $null
        $localInstallerCopy = $null
        $runDir             = $workDir
        if ($workDir -match '^(\\\\|//)') {
            $mappedDrive = Mount-UncDrive $workDir
            if ($mappedDrive) {
                $runDir = "$mappedDrive\"
                # Rebase the installer path onto the mapped drive too
                if ($installerResolved -like "$workDir*") {
                    $installerResolved = $installerResolved -replace [regex]::Escape($workDir), $mappedDrive
                }
                Write-Host "  Mapped : $workDir -> $mappedDrive"
            } else {
                $msg = "Could not map UNC working directory $workDir (machine joined to domain? share permissions?)."
                Write-Warning "  $msg"
                $result.error = $msg
                Send-Api "/api/jobs/$macKey/software/result" ($result | ConvertTo-Json -Compress)
                continue
            }
        }
        Write-Host "  WorkDir: $runDir"

        # Copy the installer to local temp before running. UNC installers run directly
        # off the mapped share, which causes ERROR_SWAPERROR on large files over WAN.
        # Copying first means the EXE/MSI runs from local disk regardless of link quality.
        if ($mappedDrive -and (Test-Path $installerResolved -PathType Leaf)) {
            $localInstallerCopy = Join-Path $env:TEMP (Split-Path $installerResolved -Leaf)
            Write-Host "  Copying installer to local temp ..." -ForegroundColor DarkGray
            Copy-Item $installerResolved $localInstallerCopy -Force
            $installerResolved = $localInstallerCopy
        }

        # Build final command - substitute {installer}
        $cmd = $sw.InstallCommand -replace '\{installer\}', $installerResolved
        Write-Host "  Command: $cmd"

        try {
            if ($cmd -match '^"([^"]+)"\s*(.*)$') {
                $exe  = $Matches[1]
                $args = $Matches[2]
            } elseif ($cmd -match '^(\S+)\s*(.*)$') {
                $exe  = $Matches[1]
                $args = $Matches[2]
            } else {
                $exe  = $cmd
                $args = ''
            }

            # Start-Process doesn't search the working directory for executables - only PATH.
            # If the exe has no path separator and isn't a PATH command, resolve it against runDir.
            if ($exe -notmatch '[/\\]' -and -not (Get-Command $exe -ErrorAction SilentlyContinue)) {
                $exe = Join-Path $runDir $exe
            }

            $proc = Start-Process -FilePath $exe -ArgumentList $args `
                                  -WorkingDirectory $runDir `
                                  -Wait -PassThru -NoNewWindow
            Write-Host "  Exit code: $($proc.ExitCode)"
            $result.exitCode = $proc.ExitCode

            if ($proc.ExitCode -in 0, 3010) {
                $result.success = $true
            } else {
                $msg = "$($sw.Name) returned exit code $($proc.ExitCode) - may have failed."
                Write-Warning "  $msg"
                $result.error = $msg
            }
        } catch {
            $msg = "FAILED to run $($sw.Name): $_"
            Write-Warning "  $msg"
            $result.error = $msg
        } finally {
            # Release any temporary drive mapping
            if ($mappedDrive) {
                $netOut = & net use $mappedDrive /delete /yes 2>&1
                if ($LASTEXITCODE -ne 0) {
                    Write-Warning "  Could not release drive $mappedDrive : $($netOut -join ' ')"
                }
            }
        }

        # Report this item's result to the server immediately
        Send-Api "/api/jobs/$macKey/software/result" ($result | ConvertTo-Json -Compress)

        # Clean up any local temp file - either an HTTP download or a UNC local copy
        $cleanupPath = if ($localInstallerCopy) { $localInstallerCopy } `
                       elseif (-not $isUNC)      { $installerResolved  } `
                       else                       { $null               }
        if ($cleanupPath -and (Test-Path $cleanupPath)) {
            Remove-Item $cleanupPath -Force -ErrorAction SilentlyContinue
        }
    }
}

# ---------------------------------------------------------------
# Driver installation
# ---------------------------------------------------------------
$drvPkg = $config.DriverPackage
if ($drvPkg -and $drvPkg.UncPath) {
    Write-Host ""
    Write-Host "--- Installing driver package: $($drvPkg.Name) ---"
    Write-Host "  UNC path: $($drvPkg.UncPath)"

    if (Test-Path $drvPkg.UncPath) {
        Write-Host "  Running pnputil /add-driver /subdirs /install ..."
        $pnpOut = & pnputil.exe /add-driver "$($drvPkg.UncPath)\*.inf" /subdirs /install 2>&1
        Write-Host ($pnpOut -join "`n")
        if ($LASTEXITCODE -in 0, 259) {
            Write-Host "  Drivers installed successfully."
        } else {
            Write-Warning "  pnputil exited $LASTEXITCODE - some drivers may not have installed."
        }
    } else {
        Write-Warning "  Driver package path not reachable: $($drvPkg.UncPath)"
        Write-Warning "  Ensure the DeployManagerDrivers share exists on the deployment server."
    }
} else {
    Write-Host "No driver package in config - skipping driver installation."
}

Write-Host ""
Write-Host "=== Post-install complete: $(Get-Date) ==="

# ---------------------------------------------------------------
# BranchCache - enable Distributed Cache mode so this machine can
# serve cached deployment content to peers at the same site.
# Runs after software installs so it does not delay packaging.
# ---------------------------------------------------------------
if ($enableBranchCache) {
    Write-Host ""
    Write-Host "--- Enabling BranchCache (Distributed Cache mode) ---"
    try {
        # netsh is available without the BranchCache PowerShell module
        $bcOut = & netsh branchcache set service mode=DISTRIBUTED 2>&1
        Write-Host ($bcOut -join "`n")

        # Also set the cache size (5% of disk by default is fine; adjust if needed)
        & netsh branchcache set localcache directory=default size=5percent 2>&1 | Out-Null

        # Enable the BranchCache firewall group so peers can reach this machine
        & netsh advfirewall firewall set rule group="BranchCache - Content Retrieval (Uses HTTP)" new enable=yes 2>&1 | Out-Null
        & netsh advfirewall firewall set rule group="BranchCache - Peer Discovery (Uses WSD)"    new enable=yes 2>&1 | Out-Null

        Write-Host "  BranchCache enabled in Distributed Cache mode."
    } catch {
        Write-Warning "Could not enable BranchCache: $_"
    }
}

# Finalise job status on the server (body is empty - server uses accumulated per-item results)
if ($macKey -and $apiServer) {
    try {
        $url     = "$apiServer/api/jobs/$macKey/complete"
        $headers = @{}
        if ($apiToken) { $headers['X-Deploy-Token'] = $apiToken }
        Write-Host "Finalising job at $url ..."
        Invoke-WebRequest -Uri $url -Method Post -Body '{}' `
            -ContentType 'application/json' -Headers $headers -UseBasicParsing | Out-Null
        Write-Host "Job finalised."
    } catch {
        Write-Warning "Could not finalise job on server: $_"
    }
}

Stop-Transcript
