<#
  Stage-LocalWinPE.ps1
  Boots THIS machine into Deploy Manager WinPE without PXE.

  - Downloads boot.wim + boot.sdi over HTTP with a resumable .NET downloader
    (HTTP Range). NOTE: deliberately NOT BITS -- BITS needs an interactive logon
    and fails with 0x800704DD when launched non-interactively (WMI / Session 0).
  - Adds a one-time BCD ramdisk boot entry pointing at the local boot.wim.
  - Uses the machine's own Microsoft-signed Windows Boot Manager + signed winload.efi,
    so it is fully Secure Boot compatible (no unsigned bootloaders, no key enrolment).
  - Suspends BitLocker so WinPE can access and wipe the encrypted disk.

  Run as Administrator. Launched by the Deploy Manager orchestrator over WMI as the
  service account. Only the ONE-TIME boot is changed; the normal default boot entry
  is untouched, so a failed attempt falls back to Windows.

  $StageUrl is set by the orchestrator before Invoke-Expression runs this script.
#>
$ErrorActionPreference = 'Stop'

$baseUrl = $StageUrl -replace '/boot/Stage-LocalWinPE\.ps1$', ''
$WorkDir = 'C:\osdeploy-stage'

# Resumable HTTP download (no BITS) -- works in non-interactive/Session 0 contexts.
function Get-FileResumable {
    param([string]$Url, [string]$Dest, [int]$MaxAttempts = 20)
    $total = 0
    try {
        $h = [System.Net.HttpWebRequest]::Create($Url); $h.Method = 'HEAD'; $h.Timeout = 15000
        $r = $h.GetResponse(); $total = $r.ContentLength; $r.Close()
    } catch {}
    $attempt = 0; $done = $false
    while (-not $done -and $attempt -lt $MaxAttempts) {
        $attempt++
        $have = 0; if (Test-Path $Dest) { $have = (Get-Item $Dest).Length }
        if ($total -gt 0 -and $have -ge $total) { $done = $true; break }
        if ($attempt -gt 1) { Write-Host ("  Resuming {0} from {1} MB (attempt {2})..." -f (Split-Path $Url -Leaf), [math]::Round($have/1MB), $attempt) }
        $fs = $null; $st = $null; $rp = $null
        try {
            $req = [System.Net.HttpWebRequest]::Create($Url)
            $req.Timeout = 30000; $req.ReadWriteTimeout = 120000
            if ($have -gt 0) { $req.AddRange([long]$have) }
            $rp = $req.GetResponse(); $st = $rp.GetResponseStream()
            $fs = [System.IO.File]::Open($Dest, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write)
            $buf = New-Object byte[] 1048576
            while (($n = $st.Read($buf, 0, $buf.Length)) -gt 0) { $fs.Write($buf, 0, $n); $have += $n }
            $fs.Close(); $st.Close(); $rp.Close(); $fs = $null
            if ($total -le 0 -or $have -ge $total) { $done = $true }
        } catch {
            Write-Host ("  Download interrupted: {0}" -f $_.Exception.Message)
            if ($fs) { try { $fs.Close() } catch {} }
            if ($st) { try { $st.Close() } catch {} }
            if ($rp) { try { $rp.Close() } catch {} }
            Start-Sleep 8
        }
    }
    if (-not $done) { throw "Download failed after $attempt attempt(s): $Url" }
}

New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null
Start-Transcript -Path "$WorkDir\stage.log" -Append | Out-Null
try {
    function Log($m){ Write-Host ("[{0}] {1}" -f (Get-Date -Format HH:mm:ss), $m) }

    Log "Downloading WinPE boot files over HTTP (resumable, no BITS)..."
    Remove-Item "$WorkDir\boot.wim", "$WorkDir\boot.sdi" -Force -ErrorAction SilentlyContinue
    Get-FileResumable -Url "$baseUrl/winpe/Boot/boot.sdi" -Dest "$WorkDir\boot.sdi"
    Get-FileResumable -Url "$baseUrl/winpe/Boot/boot.wim" -Dest "$WorkDir\boot.wim"

    $wim = Get-Item "$WorkDir\boot.wim"
    Log ("boot.wim downloaded: {0:N0} bytes" -f $wim.Length)
    if ($wim.Length -lt 100MB) { throw "boot.wim too small ($($wim.Length) bytes) - download failed" }

    Log "Creating BCD ramdisk boot entry..."
    cmd /c 'bcdedit /create {ramdiskoptions} /d "Deploy Manager Ramdisk"' | Out-Null
    & bcdedit /set '{ramdiskoptions}' ramdisksdidevice partition=C: | Out-Null
    & bcdedit /set '{ramdiskoptions}' ramdisksdipath '\osdeploy-stage\boot.sdi' | Out-Null

    $out  = & bcdedit /create /d "Deploy Manager WinPE" /application osloader
    $guid = ([regex]'\{[0-9a-fA-F-]+\}').Match($out).Value
    if (-not $guid) { throw "Failed to create BCD loader entry. Output: $out" }
    Log "BCD entry GUID: $guid"

    & bcdedit /set $guid device     "ramdisk=[C:]\osdeploy-stage\boot.wim,{ramdiskoptions}" | Out-Null
    & bcdedit /set $guid osdevice   "ramdisk=[C:]\osdeploy-stage\boot.wim,{ramdiskoptions}" | Out-Null
    & bcdedit /set $guid path       '\windows\system32\boot\winload.efi' | Out-Null
    & bcdedit /set $guid systemroot '\windows' | Out-Null
    & bcdedit /set $guid detecthal  yes | Out-Null
    & bcdedit /set $guid winpe      yes | Out-Null

    & bcdedit /bootsequence $guid | Out-Null

    # Suspend BitLocker so the boot manager can read boot.wim from C: after reboot,
    # and so WinPE can wipe the (otherwise write-protected) encrypted disk.
    # Instant -- no decryption. Auto-resumes after 3 reboots if imaging doesn't proceed.
    try {
        $bv = Get-BitLockerVolume -MountPoint $env:SystemDrive -ErrorAction Stop
        if ($bv.ProtectionStatus -eq 'On') {
            Suspend-BitLocker -MountPoint $env:SystemDrive -RebootCount 3 -ErrorAction Stop | Out-Null
            Log "BitLocker suspended on $env:SystemDrive so boot manager can read boot.wim."
        } else { Log "BitLocker not active on $env:SystemDrive - no suspend needed." }
    } catch { Log "BitLocker suspend skipped: $($_.Exception.Message)" }

    Log "Staged successfully - one-time boot into WinPE is set."
    Log "Rebooting in 15 seconds..."
    Stop-Transcript | Out-Null
    & shutdown /r /t 15 /c "Deploy Manager: booting into WinPE for imaging"
}
catch {
    Write-Host "ERROR: $_"
    try { Stop-Transcript | Out-Null } catch {}
    throw
}
