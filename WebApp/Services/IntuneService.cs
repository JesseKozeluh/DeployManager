using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DeployManager.Models;

namespace DeployManager.Services;

public class IntuneService
{
    private readonly ISettingsService _settings;
    private readonly IEncryptionService _enc;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<IntuneService> _log;

    private string? _cachedToken;
    private DateTimeOffset _tokenExpiry;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    public IntuneService(
        ISettingsService settings,
        IEncryptionService enc,
        IHttpClientFactory httpFactory,
        ILogger<IntuneService> log)
    {
        _settings   = settings;
        _enc        = enc;
        _httpFactory = httpFactory;
        _log        = log;
    }

    public bool IsConfigured()
    {
        var s = _settings.Get();
        return s.IntuneAutoRegister
               && !string.IsNullOrWhiteSpace(s.EntraTenantId)
               && !string.IsNullOrWhiteSpace(s.EntraClientId)
               && !string.IsNullOrWhiteSpace(s.EntraClientSecret);
    }

    private async Task<string?> GetTokenAsync()
    {
        if (_cachedToken != null && DateTimeOffset.UtcNow < _tokenExpiry)
            return _cachedToken;

        await _tokenLock.WaitAsync();
        try
        {
            if (_cachedToken != null && DateTimeOffset.UtcNow < _tokenExpiry)
                return _cachedToken;

            var s      = _settings.Get();
            var secret = _enc.Unprotect(s.EntraClientSecret);
            var client = _httpFactory.CreateClient();

            var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"]    = "client_credentials",
                ["client_id"]     = s.EntraClientId,
                ["client_secret"] = secret,
                ["scope"]         = "https://graph.microsoft.com/.default"
            });

            var resp = await client.PostAsync(
                $"https://login.microsoftonline.com/{s.EntraTenantId}/oauth2/v2.0/token", body);

            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync();
                _log.LogError("Intune token request failed ({Status}): {Error}", resp.StatusCode, err);
                return null;
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            _cachedToken = doc.RootElement.GetProperty("access_token").GetString();
            var expiresIn = doc.RootElement.GetProperty("expires_in").GetInt32();
            _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 120);
            return _cachedToken;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to acquire Graph API token for Intune.");
            return null;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private HttpClient AuthedClient(string token)
    {
        var client = _httpFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static string SanitizeODataString(string value) =>
        value.Replace("'", "''").Replace("%", "%25");

    private static bool IsValidGuid(string value) =>
        Guid.TryParse(value, out _);

    public async Task<AutopilotDeviceResult?> FindDeviceBySerialAsync(string serialNumber)
    {
        var token = await GetTokenAsync();
        if (token == null) return null;

        var client = AuthedClient(token);
        var safe   = SanitizeODataString(serialNumber);
        var url    = $"https://graph.microsoft.com/v1.0/deviceManagement/windowsAutopilotDeviceIdentities" +
                     $"?$filter=contains(serialNumber,'{safe}')";

        var resp = await client.GetAsync(url);
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning("Intune device lookup failed ({Status}): {Body}",
                resp.StatusCode, await resp.Content.ReadAsStringAsync());
            return null;
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var values = doc.RootElement.GetProperty("value");
        if (values.GetArrayLength() == 0) return null;

        var first = values[0];
        return new AutopilotDeviceResult
        {
            Id                             = first.GetProperty("id").GetString() ?? "",
            SerialNumber                   = first.TryGetProperty("serialNumber", out var sn) ? sn.GetString() ?? "" : "",
            GroupTag                       = first.TryGetProperty("groupTag", out var gt) ? gt.GetString() ?? "" : "",
            DeploymentProfileAssignmentStatus = first.TryGetProperty("deploymentProfileAssignmentStatus", out var dp)
                                                ? dp.GetString() ?? "" : ""
        };
    }

    public async Task<string?> ImportDeviceAsync(string serialNumber, string hardwareHash, string groupTag)
    {
        var token = await GetTokenAsync();
        if (token == null) return null;

        var client = AuthedClient(token);
        var payload = JsonSerializer.Serialize(new
        {
            importedWindowsAutopilotDeviceIdentities = new[]
            {
                new
                {
                    serialNumber       = serialNumber,
                    hardwareIdentifier = hardwareHash,
                    groupTag           = groupTag
                }
            }
        });

        var resp = await client.PostAsync(
            "https://graph.microsoft.com/v1.0/deviceManagement/importedWindowsAutopilotDeviceIdentities/import",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            _log.LogError("Intune device import failed ({Status}): {Error}", resp.StatusCode, err);
            return null;
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var values = doc.RootElement.GetProperty("value");
        if (values.GetArrayLength() == 0) return null;

        var first = values[0];
        var importId = first.GetProperty("id").GetString();
        var status   = first.TryGetProperty("state", out var stateEl)
                       && stateEl.TryGetProperty("deviceImportStatus", out var dis)
            ? dis.GetString() ?? "" : "";

        _log.LogInformation("Intune import submitted for {Serial}, id={Id}, status={Status}",
            serialNumber, importId, status);
        return importId;
    }

    public async Task<string> CheckImportStatusAsync(string importId)
    {
        if (!IsValidGuid(importId)) return "error";

        var token = await GetTokenAsync();
        if (token == null) return "error";

        var client = AuthedClient(token);
        var resp   = await client.GetAsync(
            $"https://graph.microsoft.com/v1.0/deviceManagement/importedWindowsAutopilotDeviceIdentities/{importId}");

        if (!resp.IsSuccessStatusCode) return "error";

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        if (root.TryGetProperty("state", out var stateEl)
            && stateEl.TryGetProperty("deviceImportStatus", out var dis))
        {
            return dis.GetString() ?? "unknown";
        }
        return "unknown";
    }

    public async Task<bool> UpdateGroupTagAsync(string deviceId, string groupTag)
    {
        if (!IsValidGuid(deviceId)) return false;

        var token = await GetTokenAsync();
        if (token == null) return false;

        var client  = AuthedClient(token);
        var payload = JsonSerializer.Serialize(new { groupTag });
        var request = new HttpRequestMessage(HttpMethod.Patch,
            $"https://graph.microsoft.com/v1.0/deviceManagement/windowsAutopilotDeviceIdentities/{deviceId}")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        var resp = await client.SendAsync(request);
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning("Intune group tag update failed ({Status})", resp.StatusCode);
            return false;
        }
        return true;
    }

    public async Task RegisterDeviceAsync(
        string mac, string serialNumber, string hardwareHash, string groupTag,
        Action<string, string> updateStatus)
    {
        try
        {
            updateStatus(mac, "checking");

            var existing = await FindDeviceBySerialAsync(serialNumber);
            if (existing != null)
            {
                _log.LogInformation("Device {Serial} already registered in Intune (id={Id}).",
                    serialNumber, existing.Id);

                if (!string.IsNullOrEmpty(groupTag)
                    && !string.Equals(existing.GroupTag, groupTag, StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogInformation("Updating group tag from '{Old}' to '{New}' for {Serial}.",
                        existing.GroupTag, groupTag, serialNumber);
                    await UpdateGroupTagAsync(existing.Id, groupTag);
                }

                var profileStatus = existing.DeploymentProfileAssignmentStatus;
                if (string.Equals(profileStatus, "assigned", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(profileStatus, "assignedUnkownSyncState", StringComparison.OrdinalIgnoreCase))
                {
                    updateStatus(mac, "profile_assigned");
                }
                else
                {
                    updateStatus(mac, "registered");
                }
                return;
            }

            updateStatus(mac, "importing");

            var importId = await ImportDeviceAsync(serialNumber, hardwareHash, groupTag);
            if (importId == null)
            {
                updateStatus(mac, "error:Import request failed");
                return;
            }

            for (int i = 0; i < 60; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(15));
                var importStatus = await CheckImportStatusAsync(importId);
                _log.LogDebug("Import {Id} poll #{Attempt}: {Status}", importId, i + 1, importStatus);

                if (string.Equals(importStatus, "complete", StringComparison.OrdinalIgnoreCase))
                {
                    updateStatus(mac, "registered");

                    for (int j = 0; j < 20; j++)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30));
                        var device = await FindDeviceBySerialAsync(serialNumber);
                        if (device != null)
                        {
                            var ps = device.DeploymentProfileAssignmentStatus;
                            if (string.Equals(ps, "assigned", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(ps, "assignedUnkownSyncState", StringComparison.OrdinalIgnoreCase))
                            {
                                updateStatus(mac, "profile_assigned");
                                _log.LogInformation("Autopilot profile assigned for {Serial}.", serialNumber);
                                return;
                            }
                        }
                    }
                    _log.LogInformation("Profile assignment timed out for {Serial} — device is registered but profile not yet assigned.", serialNumber);
                    return;
                }

                if (string.Equals(importStatus, "error", StringComparison.OrdinalIgnoreCase))
                {
                    updateStatus(mac, "error:Import failed in Intune");
                    return;
                }
            }

            updateStatus(mac, "error:Import timed out");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Intune registration failed for {Mac}.", mac);
            updateStatus(mac, "error:Registration failed — check server logs");
        }
    }
}

public class AutopilotDeviceResult
{
    public string Id { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public string GroupTag { get; set; } = "";
    public string DeploymentProfileAssignmentStatus { get; set; } = "";
}
