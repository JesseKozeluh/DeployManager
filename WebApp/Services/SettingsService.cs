using System.Text.Json;
using DeployManager.Models;

namespace DeployManager.Services;

public interface ISettingsService
{
    AppSettings Get();
    void Save(AppSettings settings);
    bool IsSetupComplete();
}

/// <summary>
/// Reads / writes settings.json. Sensitive fields (passwords, secrets) are
/// transparently encrypted at rest using AES-256-GCM via IEncryptionService.
/// In-memory copy is always plaintext for safe use by other services.
/// </summary>
public class SettingsService : ISettingsService
{
    private readonly string _settingsFile;
    private readonly IEncryptionService _enc;
    private AppSettings _cached;
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented               = true,
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public SettingsService(IConfiguration config, IEncryptionService enc)
    {
        _enc = enc;
        var dataPath = Environment.ExpandEnvironmentVariables(
                           config["DeployManager:DataPath"] ?? @"%ProgramData%\DeployManager\data");
        Directory.CreateDirectory(dataPath);
        _settingsFile = Path.Combine(dataPath, "settings.json");
        _cached = LoadAndDecrypt();
        MigrateToEncrypted();  // Re-encrypt any legacy plaintext secrets on startup
    }

    public AppSettings Get()
    {
        lock (_lock)
        {
            // Defensive copy — callers must not be able to mutate the singleton's in-memory state.
            // Without this, Settings page's PopulateFromSettings() (which blanks password fields
            // on the returned object) would corrupt _cached and lose passwords from memory.
            return JsonSerializer.Deserialize<AppSettings>(
                JsonSerializer.Serialize(_cached, _json), _json)!;
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_lock)
        {
            // Write encrypted copy to disk; keep plaintext in memory
            var onDisk = EncryptSecrets(settings);
            File.WriteAllText(_settingsFile, JsonSerializer.Serialize(onDisk, _json));
            _cached = settings;
        }
    }

    public bool IsSetupComplete() => Get().SetupComplete;

    // ── Private ───────────────────────────────────────────────────────────────

    private void MigrateToEncrypted()
    {
        // If any secret on disk is still plaintext, re-save now so it gets encrypted.
        // This transparently upgrades existing installations without requiring manual intervention.
        if (!File.Exists(_settingsFile)) return;
        try
        {
            var raw = File.ReadAllText(_settingsFile);
            var needsMigration =
                _cached.ServiceAccountPassword.Length > 0 && !_enc.IsProtected(GetRawFieldValue(raw, "serviceAccountPassword")) ||
                _cached.WinpeLocalPassword.Length     > 0 && !_enc.IsProtected(GetRawFieldValue(raw, "winpeLocalPassword")) ||
                _cached.EntraClientSecret.Length      > 0 && !_enc.IsProtected(GetRawFieldValue(raw, "entraClientSecret"));

            if (needsMigration) Save(_cached);
        }
        catch { /* non-critical — will encrypt on next settings save */ }
    }

    private static string GetRawFieldValue(string json, string fieldName)
    {
        // Quick extraction to check if the on-disk value has the AES256GCM: prefix
        var key   = $"\"{fieldName}\":";
        var start = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return "";
        start = json.IndexOf('"', start + key.Length) + 1;
        if (start <= 0) return "";
        var end = json.IndexOf('"', start);
        if (end < 0) return "";
        return json[start..end];
    }

    private AppSettings LoadAndDecrypt()
    {
        if (!File.Exists(_settingsFile)) return new AppSettings();
        try
        {
            var s = JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(_settingsFile), _json) ?? new AppSettings();

            // Decrypt sensitive fields — Unprotect handles both legacy plaintext and encrypted values
            s.ServiceAccountPassword = _enc.Unprotect(s.ServiceAccountPassword);
            s.WinpeLocalPassword     = _enc.Unprotect(s.WinpeLocalPassword);
            s.EntraClientSecret      = _enc.Unprotect(s.EntraClientSecret);
            s.SmtpPassword           = _enc.Unprotect(s.SmtpPassword);
            return s;
        }
        catch
        {
            return new AppSettings();
        }
    }

    private AppSettings EncryptSecrets(AppSettings s)
    {
        // Deep-clone via JSON round-trip so we don't mutate the in-memory object
        var copy = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(s, _json), _json)!;
        copy.ServiceAccountPassword = _enc.Protect(s.ServiceAccountPassword);
        copy.WinpeLocalPassword     = _enc.Protect(s.WinpeLocalPassword);
        copy.EntraClientSecret      = _enc.Protect(s.EntraClientSecret);
        copy.SmtpPassword           = _enc.Protect(s.SmtpPassword);
        return copy;
    }
}
