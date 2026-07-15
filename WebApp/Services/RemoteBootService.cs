using System.Diagnostics;

namespace DeployManager.Services;

public class RemoteBootService
{
    private readonly ISettingsService _settings;
    private readonly ILogger<RemoteBootService> _logger;

    public RemoteBootService(ISettingsService settings, ILogger<RemoteBootService> logger)
    {
        _settings = settings;
        _logger   = logger;
    }

    // Credentials are passed via environment variables (not CLI args) so they don't
    // appear in the process list or get embedded in the temp script file.
    private static readonly string OrchestratorScript = @"
param([string]$Hostname, [string]$Id)

$User = $env:DM_SA_USER
$Pass = $env:DM_SA_PASS

$remoteScriptUNC = ""\\$Hostname\admin$\Temp\dm_pxe_$Id.ps1""
$remoteResultUNC = ""\\$Hostname\admin$\Temp\dm_pxe_$Id.txt""
$localScriptPath = ""C:\Windows\Temp\dm_pxe_$Id.ps1""
$localResultPath = ""C:\Windows\Temp\dm_pxe_$Id.txt""

# Build the inner script that will run on the TARGET machine.
$inner = @'
$result = 'NO_PXE_ENTRY'
try {
    $fwLines = & bcdedit /enum firmware 2>&1
    $pxeGuid = $null; $inEntry = $false
    foreach ($line in $fwLines) {
        if ($line -match '^\s*identifier\s+(\{[^}]+\})') { $pxeGuid = $Matches[1]; $inEntry = $true }
        if ($inEntry -and $line -match 'IPV4|PXE|Network|NIC') { break }
        if ($line -match '^\s*$') { $inEntry = $false; $pxeGuid = $null }
    }
    if ($pxeGuid) {
        bcdedit /set '{fwbootmgr}' bootsequence $pxeGuid | Out-Null
        $result = 'REBOOT_OK:' + $pxeGuid
    }
} catch {
    $result = 'ERROR:' + $_.Exception.Message
}
'@
$inner += [System.Environment]::NewLine + ""[System.IO.File]::WriteAllText('$localResultPath', `$result)""
$inner += [System.Environment]::NewLine + ""if (`$result -like 'REBOOT_OK*') { Start-Sleep 1; Restart-Computer -Force }""

# Step 1: Authenticate to admin share using service account credentials
$secPass = ConvertTo-SecureString $Pass -AsPlainText -Force
$cred    = New-Object System.Management.Automation.PSCredential($User, $secPass)

$netOut = & net use ""\\$Hostname\admin$"" /user:$User $Pass 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Output ""SMB_FAIL:Could not authenticate to \\$Hostname\admin$ as $User (exit $LASTEXITCODE): $netOut""
    exit
}

# Step 2: Write inner script to remote machine via authenticated admin share
try {
    [System.IO.File]::WriteAllText($remoteScriptUNC, $inner)
} catch {
    & net use ""\\$Hostname\admin$"" /delete /yes | Out-Null
    Write-Output ""SMB_FAIL:$($_.Exception.Message)""
    exit
}

# Step 3: Launch inner script via WMI Win32_Process with explicit credentials
try {
    $cmdLine = ""powershell.exe -NonInteractive -ExecutionPolicy Bypass -File $localScriptPath""
    $r = Invoke-WmiMethod -ComputerName $Hostname -Credential $cred -Class Win32_Process -Name Create -ArgumentList $cmdLine
    if ($r.ReturnValue -ne 0) {
        Write-Output ""WMI_FAIL:$($r.ReturnValue)""
        Remove-Item $remoteScriptUNC -Force -ErrorAction SilentlyContinue
        & net use ""\\$Hostname\admin$"" /delete /yes | Out-Null
        exit
    }
} catch {
    Write-Output ""WMI_FAIL:$($_.Exception.Message)""
    Remove-Item $remoteScriptUNC -Force -ErrorAction SilentlyContinue
    & net use ""\\$Hostname\admin$"" /delete /yes | Out-Null
    exit
}

# Step 4: Poll for result file (up to 15 s)
for ($i = 0; $i -lt 15; $i++) {
    Start-Sleep 1
    if (Test-Path $remoteResultUNC) {
        $text = (Get-Content $remoteResultUNC -Raw).Trim()
        Write-Output $text
        Remove-Item $remoteResultUNC,$remoteScriptUNC -Force -ErrorAction SilentlyContinue
        & net use ""\\$Hostname\admin$"" /delete /yes | Out-Null
        exit
    }
}
Write-Output 'TIMEOUT'
Remove-Item $remoteScriptUNC -Force -ErrorAction SilentlyContinue
& net use ""\\$Hostname\admin$"" /delete /yes | Out-Null
";

    public async Task<(bool Success, string Message)> SetPxeBootAndRestart(string hostname)
    {
        var s = _settings.Get();

        if (string.IsNullOrWhiteSpace(s.ServiceAccountUpn) || string.IsNullOrWhiteSpace(s.ServiceAccountPassword))
            return (false, "Service account credentials are not configured. Set them in Settings → Active Directory.");

        var id               = Guid.NewGuid().ToString("N");
        var orchestratorPath = Path.Combine(Path.GetTempPath(), $"dm_orch_{id}.ps1");

        try
        {
            await File.WriteAllTextAsync(orchestratorPath, OrchestratorScript);

            var psi = new ProcessStartInfo(SystemPaths.PowerShell,
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{orchestratorPath}\" -Hostname \"{hostname}\" -Id \"{id}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            // Pass credentials via environment variables — not visible in process list
            psi.Environment["DM_SA_USER"] = s.ServiceAccountUpn;
            psi.Environment["DM_SA_PASS"] = s.ServiceAccountPassword;

            using var proc = Process.Start(psi)!;

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            await Task.WhenAll(stdoutTask, stderrTask, proc.WaitForExitAsync());

            var stdout = stdoutTask.Result.Trim();
            var stderr = stderrTask.Result.Trim();

            _logger.LogDebug("PXE boot stdout: {Out}", stdout);
            if (!string.IsNullOrEmpty(stderr))
                _logger.LogDebug("PXE boot stderr: {Err}", stderr);

            if (stdout.StartsWith("REBOOT_OK"))
            {
                var bootGuid = stdout.Replace("REBOOT_OK:", "").Trim();
                _logger.LogInformation("PXE reboot initiated on {Host}, boot entry {Guid}", hostname, bootGuid);
                return (true, $"Reboot initiated on {hostname}. PXE entry: {bootGuid}");
            }

            if (stdout.Contains("NO_PXE_ENTRY"))
                return (false, "No PXE/NIC firmware boot entry found on the target. Ensure a 'LAN with PXE Boot' option appears in the BIOS boot order.");

            if (stdout.StartsWith("SMB_FAIL"))
                return (false, $"Could not write script to {hostname} via admin share — check the machine is online and {s.ServiceAccountUpn} has local admin rights. Detail: {stdout["SMB_FAIL:".Length..]}");

            if (stdout.StartsWith("WMI_FAIL"))
                return (false, $"WMI process creation failed on {hostname} — check DCOM/RPC is not blocked by firewall. Detail: {stdout["WMI_FAIL:".Length..]}");

            if (stdout == "TIMEOUT")
                return (false, $"Script launched on {hostname} but no result within 15 s — the machine may have rebooted successfully. Check if it PXE-booted.");

            var errDetail = string.IsNullOrEmpty(stdout) ? stderr : stdout;
            var firstLine = errDetail.Split('\n')
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.Length > 0 && !l.StartsWith("+") && !l.StartsWith("At "))
                ?? errDetail;

            _logger.LogWarning("Unexpected remote boot output for {Host}: {Out}", hostname, errDetail);
            return (false, $"Unexpected response from {hostname}: {firstLine}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Remote boot failed for {Host}", hostname);
            return (false, $"Error: {ex.Message}");
        }
        finally
        {
            try { File.Delete(orchestratorPath); } catch { }
        }
    }

    // ── Local-disk WinPE staging (Secure Boot compatible, no PXE) ──────────────
    // Same proven transport as the PXE reboot: authenticate to admin$, drop a
    // launcher, start it via WMI. The launcher writes a 'started' marker, then pulls
    // + runs Stage-LocalWinPE.ps1, which BITS-downloads boot.wim, adds a one-time BCD
    // ramdisk entry and reboots into WinPE booted by the machine's own Microsoft-signed
    // boot manager (so Secure Boot is satisfied; works on PXE-only models like the 3070).
    private static readonly string StagingOrchestratorScript = @"
param([string]$Hostname, [string]$Id, [string]$StageUrl)

$User = $env:DM_SA_USER
$Pass = $env:DM_SA_PASS

$remoteScriptUNC = ""\\$Hostname\admin$\Temp\dm_stage_$Id.ps1""
$remoteResultUNC = ""\\$Hostname\admin$\Temp\dm_stage_$Id.txt""
$localScriptPath = ""C:\Windows\Temp\dm_stage_$Id.ps1""
$localResultPath = ""C:\Windows\Temp\dm_stage_$Id.txt""

# Inner script (runs on the TARGET): write a 'started' marker immediately, then
# pull + run the staging script. The long download + reboot happen asynchronously.
$inner  = ""`$result = 'STAGING_STARTED'""
$inner += [System.Environment]::NewLine + ""[System.IO.File]::WriteAllText('$localResultPath', `$result)""
$inner += [System.Environment]::NewLine + ""`$StageUrl = '$StageUrl'""
$inner += [System.Environment]::NewLine + ""try { Invoke-Expression ((New-Object System.Net.WebClient).DownloadString(`$StageUrl)) } catch { [System.IO.File]::WriteAllText('$localResultPath', ('STAGE_ERROR:' + `$_.Exception.Message)) }""

$secPass = ConvertTo-SecureString $Pass -AsPlainText -Force
$cred    = New-Object System.Management.Automation.PSCredential($User, $secPass)

$netOut = & net use ""\\$Hostname\admin$"" /user:$User $Pass 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Output ""SMB_FAIL:Could not authenticate to \\$Hostname\admin$ as $User (exit $LASTEXITCODE): $netOut""
    exit
}

try {
    [System.IO.File]::WriteAllText($remoteScriptUNC, $inner)
} catch {
    & net use ""\\$Hostname\admin$"" /delete /yes | Out-Null
    Write-Output ""SMB_FAIL:$($_.Exception.Message)""
    exit
}

try {
    $cmdLine = ""powershell.exe -NonInteractive -ExecutionPolicy Bypass -File $localScriptPath""
    $r = Invoke-WmiMethod -ComputerName $Hostname -Credential $cred -Class Win32_Process -Name Create -ArgumentList $cmdLine
    if ($r.ReturnValue -ne 0) {
        Write-Output ""WMI_FAIL:$($r.ReturnValue)""
        & net use ""\\$Hostname\admin$"" /delete /yes | Out-Null
        exit
    }
} catch {
    Write-Output ""WMI_FAIL:$($_.Exception.Message)""
    & net use ""\\$Hostname\admin$"" /delete /yes | Out-Null
    exit
}

# Poll briefly for the 'started' marker (the download itself runs on for minutes).
for ($i = 0; $i -lt 20; $i++) {
    Start-Sleep 1
    if (Test-Path $remoteResultUNC) {
        $text = (Get-Content $remoteResultUNC -Raw).Trim()
        Write-Output $text
        Remove-Item $remoteResultUNC -Force -ErrorAction SilentlyContinue
        & net use ""\\$Hostname\admin$"" /delete /yes | Out-Null
        exit
    }
}
Write-Output 'TIMEOUT'
& net use ""\\$Hostname\admin$"" /delete /yes | Out-Null
";

    public async Task<(bool Success, string Message)> StageLocalWinPE(string hostname)
    {
        var s = _settings.Get();

        if (string.IsNullOrWhiteSpace(s.ServiceAccountUpn) || string.IsNullOrWhiteSpace(s.ServiceAccountPassword))
            return (false, "Service account credentials are not configured. Set them in Settings → Active Directory.");

        var id                = Guid.NewGuid().ToString("N");
        var orchestratorPath  = Path.Combine(Path.GetTempPath(), $"dm_stageorch_{id}.ps1");
        var stageUrl          = $"{s.DeployServerUrl.TrimEnd('/')}/boot/Stage-LocalWinPE.ps1";

        try
        {
            await File.WriteAllTextAsync(orchestratorPath, StagingOrchestratorScript);

            var psi = new ProcessStartInfo(SystemPaths.PowerShell,
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{orchestratorPath}\" -Hostname \"{hostname}\" -Id \"{id}\" -StageUrl \"{stageUrl}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            psi.Environment["DM_SA_USER"] = s.ServiceAccountUpn;
            psi.Environment["DM_SA_PASS"] = s.ServiceAccountPassword;

            using var proc = Process.Start(psi)!;

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            await Task.WhenAll(stdoutTask, stderrTask, proc.WaitForExitAsync());

            var stdout = stdoutTask.Result.Trim();
            var stderr = stderrTask.Result.Trim();

            _logger.LogDebug("Stage stdout: {Out}", stdout);
            if (!string.IsNullOrEmpty(stderr))
                _logger.LogDebug("Stage stderr: {Err}", stderr);

            if (stdout.StartsWith("STAGING_STARTED"))
            {
                _logger.LogInformation("Local-WinPE staging started on {Host}", hostname);
                return (true, $"Staging started on {hostname}. It is downloading WinPE (resumable) and will reboot into the imaging environment shortly — watch the job status for progress.");
            }

            if (stdout.StartsWith("STAGE_ERROR"))
                return (false, $"Staging script error on {hostname}: {stdout["STAGE_ERROR:".Length..]}");

            if (stdout.StartsWith("SMB_FAIL"))
                return (false, $"Could not reach {hostname} via admin share — check it is online and {s.ServiceAccountUpn} has local admin rights. Detail: {stdout["SMB_FAIL:".Length..]}");

            if (stdout.StartsWith("WMI_FAIL"))
                return (false, $"Could not launch the staging script on {hostname} (WMI/DCOM). Detail: {stdout["WMI_FAIL:".Length..]}");

            if (stdout == "TIMEOUT")
                return (false, $"Launched the staging script on {hostname} but received no confirmation within 20 s. It may still be running — check the job status.");

            var errDetail = string.IsNullOrEmpty(stdout) ? stderr : stdout;
            _logger.LogWarning("Unexpected staging output for {Host}: {Out}", hostname, errDetail);
            return (false, $"Unexpected response from {hostname}: {errDetail}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Staging failed for {Host}", hostname);
            return (false, $"Error: {ex.Message}");
        }
        finally
        {
            try { File.Delete(orchestratorPath); } catch { }
        }
    }
}
