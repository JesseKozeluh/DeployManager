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
# Per-installer timeout: kill a hung installer after this many minutes so one bad package
# cannot stall the whole deployment. Falls back to 60 if the config value is missing.
$softwareTimeoutMin = if ($config.SoftwareInstallTimeoutMinutes -and [int]$config.SoftwareInstallTimeoutMinutes -gt 0) {
    [int]$config.SoftwareInstallTimeoutMinutes
} else { 60 }

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
            # Run net use as a bounded process. An unreachable/slow share can make a plain
            # 'net use' hang for a long time, which would stall the whole deployment before
            # the install step is even reached (its timeout would not cover it). Give up
            # after 60s so the item fails cleanly and the deployment continues.
            $psi = New-Object System.Diagnostics.ProcessStartInfo
            $psi.FileName        = 'net.exe'
            $psi.Arguments       = "use ${letter}: `"$uncPath`""
            $psi.UseShellExecute = $false
            $psi.CreateNoWindow  = $true
            $np = [System.Diagnostics.Process]::Start($psi)
            if ($np.WaitForExit(60000)) {
                if ($np.ExitCode -eq 0) { return "${letter}:" }
            } else {
                try { & taskkill.exe /PID $np.Id /T /F 2>&1 | Out-Null } catch {}
                Write-Warning "  net use for '$uncPath' timed out after 60s - share unreachable?"
                return $null
            }
        }
    }
    return $null
}

# Best-effort check that BitLocker recovery information was backed up to AD DS - typically by
# the customer's own Group Policy, which owns AD backup and refuses the manual cmdlet. Reads the
# BitLocker Management operational log and returns $true only on a positive backup signal.
function Test-AdBackupSucceeded {
    try {
        $since = (Get-Date).AddMinutes(-30)
        $evts  = Get-WinEvent -FilterHashtable @{
            LogName   = 'Microsoft-Windows-BitLocker/BitLocker Management'
            StartTime = $since
        } -ErrorAction Stop
        foreach ($e in $evts) {
            if ($e.Id -eq 845) { return $true }
            if ($e.Message -and $e.Message -match 'Active Directory' -and $e.Message -match 'backed up' -and $e.Message -notmatch 'fail') { return $true }
        }
    } catch { }
    return $false
}

# True only when the device is actually joined to Entra ID. Used to skip the Entra recovery-key
# backup on domain-only machines, where it would otherwise throw a noisy 0x801C0450 error.
function Test-EntraJoined {
    try {
        $out = & dsregcmd /status 2>$null
        foreach ($line in $out) {
            if ($line -match 'AzureAdJoined\s*:\s*YES') { return $true }
        }
    } catch { }
    return $false
}

# Enable BitLocker on one volume. The OS drive uses a TPM protector; data drives use a
# recovery-password protector plus auto-unlock (which needs the OS drive already protected).
# Returns a status hashtable. The recovery password is NEVER written to the log - the enabling
# cmdlets are silenced on the warning stream (which echoes the password) and it is only ever
# written to the secured escrow share file.
function Enable-DriveBitLocker([string]$Mount, [bool]$IsOs, $blCfg) {
    $st = @{ mount = $Mount; state = 'unknown'; escrowed = $false; keyId = ''; error = '' }
    try {
        $vol = Get-BitLockerVolume -MountPoint $Mount -ErrorAction Stop
        if ($vol.VolumeStatus -ne 'FullyDecrypted') { $st.state = 'already'; return $st }

        $method   = if ($blCfg.EncryptionMethod -eq 'XtsAes128') { 'XtsAes128' } else { 'XtsAes256' }
        $usedOnly = [bool]$blCfg.UsedSpaceOnly

        # NOTE: the enabling cmdlets echo the plaintext recovery password on the WARNING stream,
        # which Start-Transcript would capture. -WarningAction SilentlyContinue plus a 3>$null
        # redirect keep the password out of the log.
        if ($IsOs) {
            Enable-BitLocker -MountPoint $Mount -EncryptionMethod $method -UsedSpaceOnly:$usedOnly -TpmProtector -SkipHardwareTest -WarningAction SilentlyContinue -ErrorAction Stop 3>$null | Out-Null
        } else {
            Enable-BitLocker -MountPoint $Mount -EncryptionMethod $method -UsedSpaceOnly:$usedOnly -RecoveryPasswordProtector -SkipHardwareTest -WarningAction SilentlyContinue -ErrorAction Stop 3>$null | Out-Null
        }

        # Ensure a recovery-password protector exists (add one on the OS drive).
        $rp = (Get-BitLockerVolume -MountPoint $Mount).KeyProtector | Where-Object { $_.KeyProtectorType -eq 'RecoveryPassword' } | Select-Object -First 1
        if (-not $rp) {
            Add-BitLockerKeyProtector -MountPoint $Mount -RecoveryPasswordProtector -WarningAction SilentlyContinue -ErrorAction Stop 3>$null | Out-Null
            $rp = (Get-BitLockerVolume -MountPoint $Mount).KeyProtector | Where-Object { $_.KeyProtectorType -eq 'RecoveryPassword' } | Select-Object -First 1
        }
        if ($rp) { $st.keyId = $rp.KeyProtectorId }

        if (-not $IsOs) {
            try { Enable-BitLockerAutoUnlock -MountPoint $Mount -ErrorAction Stop | Out-Null } catch {}
        }

        # Escrow the recovery key to each enabled target. Success on ANY target counts.
        # The recovery password itself is only ever written to the (secured) share file -
        # never to the deployment log.
        if ($rp) {
            $escErrs      = @()
            $anyRequested = $false

            if ($blCfg.BackupToAd) {
                $anyRequested = $true
                if ([bool]$blCfg.AdBackupViaGpo) {
                    # Operator has declared that their BitLocker Group Policy owns AD DS backup; it
                    # escrows the key at encryption time and refuses the manual cmdlet. Skip the
                    # manual backup, trust the policy, and confirm best-effort via the event log.
                    $st.escrowed = $true
                    if (Test-AdBackupSucceeded) { $escErrs += 'AD: stored by Group Policy (confirmed in event log)' }
                    else { $escErrs += 'AD: via Group Policy (verify recovery key present in AD)' }
                } else {
                    try { Backup-BitLockerKeyProtector -MountPoint $Mount -KeyProtectorId $rp.KeyProtectorId -ErrorAction Stop | Out-Null; $st.escrowed = $true }
                    catch {
                        $adMsg = $_.Exception.Message
                        if ($adMsg -like '*Group policy does not permit*Active Directory*') {
                            # Safety net: the customer's Group Policy owns AD backup even though the
                            # operator did not tick "AD backup via Group Policy". It refuses the
                            # manual cmdlet but stores the key at encryption time, so treat this as
                            # escrowed rather than false-failing (the OS drive also has a TPM protector).
                            $st.escrowed = $true
                            if (Test-AdBackupSucceeded) { $escErrs += 'AD: stored by Group Policy (confirmed in event log)' }
                            else { $escErrs += 'AD: managed by Group Policy (verify recovery key present in AD)' }
                        } else {
                            $escErrs += "AD: $adMsg"
                        }
                    }
                }
            }
            if ($blCfg.BackupToEntra) {
                $anyRequested = $true
                if (Test-EntraJoined) {
                    try { BackupToAAD-BitLockerKeyProtector -MountPoint $Mount -KeyProtectorId $rp.KeyProtectorId -ErrorAction Stop | Out-Null; $st.escrowed = $true }
                    catch { $escErrs += "Entra: $($_.Exception.Message)" }
                } else {
                    # Domain-only (or not-yet-Entra-joined Autopilot) device - attempting the Entra
                    # backup would throw 0x801C0450. Skip it cleanly; Intune policy should own
                    # BitLocker on devices that become Entra-joined later at OOBE.
                    $escErrs += 'Entra: skipped (device is not Entra joined)'
                }
            }
            if ($blCfg.SaveToShare -and $blCfg.SharePath) {
                $anyRequested = $true
                try {
                    $rpFull = (Get-BitLockerVolume -MountPoint $Mount).KeyProtector | Where-Object { $_.KeyProtectorType -eq 'RecoveryPassword' } | Select-Object -First 1
                    $file   = Join-Path ([string]$blCfg.SharePath) ("{0}_{1}.txt" -f $env:COMPUTERNAME, $Mount.TrimEnd(':'))
                    $lines  = @(
                        "Computer        : $env:COMPUTERNAME"
                        "Volume          : $Mount"
                        "KeyProtectorId  : $($rpFull.KeyProtectorId)"
                        "RecoveryPassword: $($rpFull.RecoveryPassword)"
                        "Date            : $(Get-Date -Format 'o')"
                    )
                    Set-Content -Path $file -Value $lines -Encoding UTF8 -ErrorAction Stop
                    $st.escrowed = $true
                } catch { $escErrs += "Share: $($_.Exception.Message)" }
            }

            if ($escErrs.Count -gt 0) { $st.error = ($escErrs -join '; ') }

            if ($anyRequested -and (-not $st.escrowed) -and [bool]$blCfg.RequireEscrow) {
                # Escrow was required but no target succeeded. Leave the drive encrypted and
                # functional (it has a TPM / auto-unlock protector) and flag the job for operator
                # attention. We deliberately do NOT auto-decrypt: decryption exposes data, is slow,
                # and proved unreliable - a surfaced failure the operator can remediate is safer.
                $st.state = 'failed-escrow'
                return $st
            }
        }
        $st.state = 'protected'
    } catch {
        $st.state = 'failed'
        $st.error = $_.Exception.Message
    }
    return $st
}

# ---------------------------------------------------------------
# Software installation
# ---------------------------------------------------------------
$items = @($config.SoftwareItems | Where-Object { $_ -and $_.InstallerPath })
if ($items.Count -eq 0) {
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

            # Launch via a directly-constructed .NET process. Start-Process -PassThru does
            # NOT reliably populate ExitCode after WaitForExit (it comes back blank), which
            # mislabels successful installs and sends a null exit code that breaks the result
            # callback. A direct Process gives a reliable exit code plus the timeout/kill.
            $psi = New-Object System.Diagnostics.ProcessStartInfo
            $psi.FileName         = $exe
            $psi.Arguments        = $args
            $psi.WorkingDirectory = $runDir
            $psi.UseShellExecute  = $false      # inherit the console so output lands in PostInstall.log
            $proc = [System.Diagnostics.Process]::Start($psi)

            # Poll for exit up to the configured timeout, sending a heartbeat every 60s so a
            # long-but-legitimate installer isn't flagged as an inactive/hung job by the
            # server watchdog. Never blocks forever - a still-running installer is killed at
            # the deadline.
            $deadline = (Get-Date).AddMinutes($softwareTimeoutMin)
            $lastBeat = Get-Date
            while (-not $proc.HasExited -and (Get-Date) -lt $deadline) {
                Start-Sleep -Seconds 5
                if (((Get-Date) - $lastBeat).TotalSeconds -ge 60) {
                    Send-Api "/api/jobs/$macKey/heartbeat" '{}'
                    $lastBeat = Get-Date
                }
            }

            if (-not $proc.HasExited) {
                Write-Warning "  $($sw.Name) exceeded the $softwareTimeoutMin-minute install timeout - terminating."
                # Kill the installer and any child processes it spawned, then move on.
                try { & taskkill.exe /PID $proc.Id /T /F 2>&1 | Out-Null } catch {}
                $result.exitCode = -1
                $result.error    = "$($sw.Name) timed out after $softwareTimeoutMin minute(s) and was terminated."
            } else {
                $code = $proc.ExitCode
                Write-Host "  Exit code: $code"
                $result.exitCode = $code
                if ($code -in 0, 3010) {
                    $result.success = $true
                } else {
                    $msg = "$($sw.Name) returned exit code $code - may have failed."
                    Write-Warning "  $msg"
                    $result.error = $msg
                }
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

# ---------------------------------------------------------------
# BitLocker drive encryption (runs after software/drivers)
# ---------------------------------------------------------------
$blCfg = $config.BitLocker
if ($blCfg -and $blCfg.Enable) {
    Write-Host ""
    Write-Host "--- BitLocker encryption ---"

    $tpmOk = $false
    try { $tpm = Get-Tpm -ErrorAction Stop; $tpmOk = ($tpm.TpmPresent -and $tpm.TpmReady) } catch {}

    if (-not $tpmOk) {
        Write-Warning "  TPM not present or not ready - skipping BitLocker."
        if ($macKey -and $apiServer) {
            Send-Api "/api/jobs/$macKey/bitlocker" (@{ status = 'skipped'; detail = 'TPM not present or not ready' } | ConvertTo-Json -Compress)
        }
    } else {
        $osLetter = ($env:SystemDrive).TrimEnd(':')
        $mode     = if ($blCfg.Volumes) { [string]$blCfg.Volumes } else { 'os' }
        $allVols  = @(Get-Volume | Where-Object { $_.DriveLetter })

        # OS drive is always first; auto-unlock on data drives needs it protected first.
        $targets = @(@{ Mount = "${osLetter}:"; IsOs = $true })

        if ($mode -eq 'osdata') {
            foreach ($v in $allVols) {
                if ($v.DriveType -eq 'Fixed' -and $v.DriveLetter -ne $osLetter) {
                    $targets += @{ Mount = "$($v.DriveLetter):"; IsOs = $false }
                }
            }
        } elseif ($mode -eq 'specific') {
            $tokens = @(([string]$blCfg.SpecificVolumes) -split ';' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
            foreach ($tok in $tokens) {
                $letter = $tok.TrimEnd(':')
                $match  = $allVols | Where-Object {
                    $_.DriveType -eq 'Fixed' -and $_.DriveLetter -ne $osLetter -and
                    ($_.FileSystemLabel -eq $tok -or [string]$_.DriveLetter -eq $letter)
                } | Select-Object -First 1
                if ($match) { $targets += @{ Mount = "$($match.DriveLetter):"; IsOs = $false } }
            }
        }

        $summary = @()
        foreach ($t in $targets) {
            Write-Host "  Encrypting $($t.Mount) ..."
            $r    = Enable-DriveBitLocker $t.Mount $t.IsOs $blCfg
            $line = "$($t.Mount) $($r.state)"
            if ($r.escrowed) { $line += ' (escrowed)' }
            if ($r.error)    { $line += " - $($r.error)" }
            $summary += $line
            Write-Host "    -> $line"
        }

        $overall = if ($summary -match 'failed') { 'failed' } elseif ($targets.Count -gt 0) { 'protected' } else { 'none' }
        Write-Host "  BitLocker: $overall"
        if ($macKey -and $apiServer) {
            Send-Api "/api/jobs/$macKey/bitlocker" (@{ status = $overall; detail = ($summary -join '; ') } | ConvertTo-Json -Compress)
        }
    }
}

# -- Collect hardware hash for Autopilot ----------------------------------------
# MDM_DevDetail_Ext01 is only available in a full Windows installation, not WinPE.
if ($macKey -and $apiServer) {
    try {
        $mdmInfo = Get-WmiObject -Namespace 'root/cimv2/mdm/dmmap' `
            -Class 'MDM_DevDetail_Ext01' -Filter "InstanceID='Ext' AND ParentID='./DevDetail'" `
            -ErrorAction Stop
        $hwHash = $mdmInfo.DeviceHardwareData
        if ($hwHash) {
            Write-Host "Hardware hash collected ($($hwHash.Length) chars)"
            $prodId = ''
            try {
                $ntVer = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
                $prodId = (Get-ItemProperty -Path $ntVer -Name 'ProductId' -ErrorAction Stop).ProductId
                Write-Host "Windows Product ID: $prodId"
            } catch { Write-Warning "Could not read Product ID: $_" }
            $serial = ''
            try {
                $serial = (Get-WmiObject Win32_BIOS -ErrorAction Stop).SerialNumber
                Write-Host "Serial number: $serial"
            } catch { Write-Warning "Could not read serial number: $_" }
            $safeHash   = ($hwHash -replace '"','\"' -replace '\\','\\')
            $safeProd   = ($prodId -replace '"','\"')
            $safeSerial = ($serial -replace '"','\"')
            Send-Api "/api/jobs/$macKey/hardware-hash" "{`"hash`":`"$safeHash`",`"productId`":`"$safeProd`",`"serialNumber`":`"$safeSerial`"}"
            Write-Host "Hardware hash submitted to Deploy Manager."
        }
    } catch {
        Write-Warning "Could not collect hardware hash: $_"
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
