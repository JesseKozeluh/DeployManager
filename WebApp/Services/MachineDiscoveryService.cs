using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using DeployManager.Models;

namespace DeployManager.Services;

public record DiscoveredMachine(string Hostname, string IpAddress, string MacAddress);

public class MachineDiscoveryService
{
    private static readonly Regex SafeHost = new(@"^[A-Za-z0-9.\-:]+$", RegexOptions.Compiled);

    private readonly IConfiguration   _config;
    private readonly ISettingsService _settings;
    private readonly IEncryptionService _enc;

    public MachineDiscoveryService(IConfiguration config, ISettingsService settings, IEncryptionService enc)
    {
        _config   = config;
        _settings = settings;
        _enc      = enc;
    }

    // Returns the discovered machine (Hostname/IP always populated on a successful DNS resolve;
    // MAC populated when WinRM succeeds), an Error (only when nothing could be resolved), and a
    // Warning (when name+IP resolved but the MAC couldn't be auto-retrieved — fill it manually).
    public async Task<(DiscoveredMachine? Machine, string? Error, string? Warning)> LookupAsync(string host)
    {
        host = host.Trim();

        if (!SafeHost.IsMatch(host))
            return (null, "Invalid hostname or IP address.", null);

        var entry = await ResolveWithSuffixAsync(host);
        if (entry == null)
        {
            var dom = _settings.Get().DomainFqdn;
            var hint = !string.IsNullOrWhiteSpace(dom) && !host.Contains('.')
                ? $" Tried '{host}' and '{host}.{dom}'."
                : "";
            return (null, $"Could not resolve '{host}': No such host is known.{hint}", null);
        }

        var shortName = entry.HostName.Split('.')[0].ToUpper();
        var ipv4 = entry.AddressList
            .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.ToString() ?? "";

        // Prefer the FQDN for the WinRM connection; fall back to the resolved IP.
        var target = entry.HostName.Contains('.') ? entry.HostName
                   : (!string.IsNullOrEmpty(ipv4) ? ipv4 : entry.HostName);

        var (mac, macErr) = await GetMacViaWinRmAsync(target);
        if (mac != null)
            return (new DiscoveredMachine(shortName, ipv4, mac), null, null);

        // Graceful degradation — keep the tool useful where WinRM isn't reachable/enabled.
        return (new DiscoveredMachine(shortName, ipv4, ""), null,
            $"Resolved {shortName} ({ipv4}), but couldn't auto-retrieve the MAC: {macErr} Enter the MAC manually below.");
    }

    // Returns the site whose configured subnet (CIDR) contains the given IPv4 address, or null.
    public static SiteConfig? MatchSite(string ip, IEnumerable<SiteConfig> sites)
    {
        if (!IPAddress.TryParse(ip, out var addr) || addr.AddressFamily != AddressFamily.InterNetwork)
            return null;

        var ib = addr.GetAddressBytes();
        uint ipInt = (uint)((ib[0] << 24) | (ib[1] << 16) | (ib[2] << 8) | ib[3]);

        foreach (var s in sites)
        {
            if (string.IsNullOrWhiteSpace(s.Subnet) || !s.Subnet.Contains('/')) continue;
            var parts = s.Subnet.Split('/');
            if (!IPAddress.TryParse(parts[0], out var net) || net.AddressFamily != AddressFamily.InterNetwork) continue;
            if (!int.TryParse(parts[1], out var prefix) || prefix < 0 || prefix > 32) continue;

            var nb = net.GetAddressBytes();
            uint netInt = (uint)((nb[0] << 24) | (nb[1] << 16) | (nb[2] << 8) | nb[3]);
            uint mask = prefix == 0 ? 0u : 0xFFFFFFFFu << (32 - prefix);

            if ((ipInt & mask) == (netInt & mask)) return s;
        }
        return null;
    }

    // Resolve a host, retrying with the configured domain suffix if a short name fails
    // (Windows Services may not inherit the domain DNS search suffix from the machine).
    private async Task<IPHostEntry?> ResolveWithSuffixAsync(string host)
    {
        var candidates = new List<string> { host };
        if (!host.Contains('.') && !IPAddress.TryParse(host, out _))
        {
            var domain = _settings.Get().DomainFqdn?.Trim().TrimStart('.');
            if (!string.IsNullOrWhiteSpace(domain))
                candidates.Add($"{host}.{domain}");
        }

        foreach (var c in candidates)
        {
            try { return await Dns.GetHostEntryAsync(c); } catch { /* try next candidate */ }
        }
        return null;
    }

    // Service account used to authenticate to the remote endpoint over WinRM. The service account
    // credentials are required even if the host is domain-joined, because the Windows Service may
    // run as SYSTEM or a local account. Prefer an appsettings override
    // (DeployManager:WmiUsername/WmiPassword); otherwise use the configured service account.
    private (string User, string Pass) GetCredentials()
    {
        var u = _config["DeployManager:WmiUsername"];
        var p = _config["DeployManager:WmiPassword"];
        if (!string.IsNullOrWhiteSpace(u) && !string.IsNullOrWhiteSpace(p))
            return (u!, p!);

        var s = _settings.Get();
        var pass = string.IsNullOrEmpty(s.ServiceAccountPassword) ? "" : _enc.Unprotect(s.ServiceAccountPassword);
        return (s.ServiceAccountUpn ?? "", pass);
    }

    // Retrieves the MAC over WinRM (WS-Management) instead of DCOM/WMI. WinRM uses a single port
    // (5985), which is simpler to allow through firewalls than DCOM's dynamic RPC port range.
    private async Task<(string? Mac, string? Error)> GetMacViaWinRmAsync(string target)
    {
        if (!SafeHost.IsMatch(target))
            return (null, "Resolved target name is invalid.");

        var (user, pass) = GetCredentials();
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
            return (null, "No service account configured (Settings → Active Directory) — required to query the endpoint over WinRM.");

        var script =
            "$cred = New-Object System.Management.Automation.PSCredential('" + user.Replace("'", "''") + "', " +
            "(ConvertTo-SecureString '" + pass.Replace("'", "''") + "' -AsPlainText -Force))\r\n" +
            // Non-domain client → domain endpoint over HTTP needs the target in the client TrustedHosts.
            "try { Start-Service WinRM -ErrorAction SilentlyContinue } catch {}\r\n" +
            "try { Set-Item WSMan:\\localhost\\Client\\TrustedHosts -Value '" + target + "' -Concatenate -Force -ErrorAction SilentlyContinue } catch {}\r\n" +
            "try {\r\n" +
            "    $so   = New-CimSessionOption -Protocol Wsman\r\n" +
            "    $sess = New-CimSession -ComputerName '" + target + "' -Credential $cred -Authentication Negotiate -SessionOption $so -OperationTimeoutSec 30 -ErrorAction Stop\r\n" +
            "    $nic  = Get-CimInstance -CimSession $sess -ClassName Win32_NetworkAdapterConfiguration -Filter 'IPEnabled=True' -ErrorAction Stop |\r\n" +
            "            Where-Object { $_.MACAddress } | Select-Object -First 1\r\n" +
            "    Remove-CimSession $sess -ErrorAction SilentlyContinue\r\n" +
            "    if ($nic) { Write-Output $nic.MACAddress } else { Write-Error 'No NIC with a MAC address found on the target.' }\r\n" +
            "} catch { Write-Error $_.Exception.Message }\r\n";

        var scriptPath = Path.Combine(Path.GetTempPath(), $"dm_lookup_{Guid.NewGuid():N}.ps1");
        try
        {
            await File.WriteAllTextAsync(scriptPath, script);

            var psi = new ProcessStartInfo(SystemPaths.PowerShell,
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            using var proc = new Process { StartInfo = psi };
            proc.Start();
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            var mac = stdout.Trim();
            if (!string.IsNullOrEmpty(mac))
                return (mac, null);

            var errLine = stderr
                .Split('\n')
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.Length > 0 && !l.StartsWith("+") && !l.StartsWith("At ")
                                     && !l.StartsWith("CategoryInfo") && !l.StartsWith("FullyQualified"))
                ?? "WinRM returned no MAC address.";

            if (errLine.Contains("Access is denied"))
                errLine += " — the service account needs local admin rights on the target.";
            else if (errLine.Contains("cannot complete the operation") || errLine.Contains("WinRM") || errLine.Contains("connecting to remote server"))
                errLine += " — WinRM may not be enabled/reachable on the target (5985).";

            return (null, errLine);
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }
    }
}
