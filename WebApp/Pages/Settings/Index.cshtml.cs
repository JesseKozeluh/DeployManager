using System.Text.Json;
using DeployManager.Models;
using DeployManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DeployManager.Pages.Settings;

[Authorize(Roles = "Administrator")]
public class IndexModel : PageModel
{
    private readonly ISettingsService    _settings;
    private readonly IAuditService       _audit;
    private readonly IHttpClientFactory  _http;
    private readonly EmailNotificationService _email;

    [BindProperty] public AppSettings Cfg { get; set; } = new();

    public string? SaveResult { get; private set; }
    public bool    SaveOk     { get; private set; }

    // Password / secret fields are NEVER echoed back — only indicate set/unset state
    public bool ServiceAccountPasswordSet { get; private set; }
    public bool WinpeLocalPasswordSet     { get; private set; }
    public bool EntraSecretSet            { get; private set; }
    public bool SmtpPasswordSet           { get; private set; }

    public IndexModel(ISettingsService settings, IAuditService audit,
                      IHttpClientFactory http, EmailNotificationService email)
    {
        _settings = settings;
        _audit    = audit;
        _http     = http;
        _email    = email;
    }

    public void OnGet()
    {
        var s = _settings.Get();
        PopulateFromSettings(s);
    }

    public IActionResult OnPost()
    {
        var s  = _settings.Get();
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        s.OrgName           = Cfg.OrgName?.Trim() ?? "";
        s.ComputerPrefix    = Cfg.ComputerPrefix?.Trim() ?? "";
        s.DeployServerUrl   = Cfg.DeployServerUrl?.Trim().TrimEnd('/') ?? "";
        s.ApiServerUrl      = AppSettings.NormalizeApiUrl(Cfg.ApiServerUrl);
        s.ServerIp          = Cfg.ServerIp?.Trim() ?? "";
        s.DomainFqdn        = Cfg.DomainFqdn?.Trim() ?? "";
        s.ServiceAccountUpn = Cfg.ServiceAccountUpn?.Trim() ?? "";
        s.DefaultComputerOU = Cfg.DefaultComputerOU?.Trim() ?? "";
        s.RecreateComputerAccountOnReuseFailure = Cfg.RecreateComputerAccountOnReuseFailure;
        s.WinpeLocalAccount = Cfg.WinpeLocalAccount?.Trim() ?? "";
        s.DefaultTimezone   = Cfg.DefaultTimezone?.Trim() ?? "";
        s.DefaultLocale     = Cfg.DefaultLocale?.Trim() ?? "";
        s.UpdateCheckUrl    = Cfg.UpdateCheckUrl?.Trim() ?? "";

        // Entra ID SSO — restart required for changes to take effect
        s.AuthMode      = string.Equals(Cfg.AuthMode, "entra", StringComparison.OrdinalIgnoreCase) ? "entra" : "local";
        s.EntraTenantId = Cfg.EntraTenantId?.Trim() ?? "";
        s.EntraClientId = Cfg.EntraClientId?.Trim() ?? "";

        // Intune Autopilot
        s.IntuneAutoRegister = Cfg.IntuneAutoRegister;

        // Software delivery
        s.SoftwareInstallTimeoutMinutes = Cfg.SoftwareInstallTimeoutMinutes > 0 ? Cfg.SoftwareInstallTimeoutMinutes : 60;

        // Job monitoring (watchdog)
        s.JobInactivityTimeoutMinutes = Cfg.JobInactivityTimeoutMinutes > 0 ? Cfg.JobInactivityTimeoutMinutes : 30;
        s.JobMaxDurationMinutes       = Cfg.JobMaxDurationMinutes > 0 ? Cfg.JobMaxDurationMinutes : 480;

        // BitLocker
        s.BitLockerEnable           = Cfg.BitLockerEnable;
        s.BitLockerVolumes          = Cfg.BitLockerVolumes?.Trim() ?? "os";
        s.BitLockerSpecificVolumes  = Cfg.BitLockerSpecificVolumes?.Trim() ?? "";
        s.BitLockerEncryptionMethod = Cfg.BitLockerEncryptionMethod == "XtsAes128" ? "XtsAes128" : "XtsAes256";
        s.BitLockerUsedSpaceOnly    = Cfg.BitLockerUsedSpaceOnly;
        s.BitLockerBackupToAd       = Cfg.BitLockerBackupToAd;
        s.BitLockerAdBackupViaGpo   = Cfg.BitLockerAdBackupViaGpo;
        s.BitLockerBackupToEntra    = Cfg.BitLockerBackupToEntra;
        s.BitLockerSaveToShare      = Cfg.BitLockerSaveToShare;
        s.BitLockerSharePath        = Cfg.BitLockerSharePath?.Trim().TrimEnd('\\') ?? "";
        s.BitLockerRequireEscrow    = Cfg.BitLockerRequireEscrow;

        // SMTP
        s.SmtpHost         = Cfg.SmtpHost?.Trim() ?? "";
        s.SmtpPort         = Cfg.SmtpPort > 0 ? Cfg.SmtpPort : 587;
        s.SmtpStartTls     = Cfg.SmtpStartTls;
        s.SmtpUsername     = Cfg.SmtpUsername?.Trim() ?? "";
        s.SmtpFrom         = Cfg.SmtpFrom?.Trim() ?? "";
        s.NotifyEmail      = Cfg.NotifyEmail?.Trim() ?? "";
        s.NotifyOnComplete = Cfg.NotifyOnComplete;
        s.NotifyOnError    = Cfg.NotifyOnError;

        // Only replace passwords/secrets when a new value is explicitly submitted
        if (!string.IsNullOrEmpty(Cfg.ServiceAccountPassword))
            s.ServiceAccountPassword = Cfg.ServiceAccountPassword;
        if (!string.IsNullOrEmpty(Cfg.WinpeLocalPassword))
            s.WinpeLocalPassword = Cfg.WinpeLocalPassword;
        if (!string.IsNullOrEmpty(Cfg.EntraClientSecret))
            s.EntraClientSecret = Cfg.EntraClientSecret;
        if (!string.IsNullOrEmpty(Cfg.SmtpPassword))
            s.SmtpPassword = Cfg.SmtpPassword;

        _settings.Save(s);
        _audit.Log(new AuditEvent("SETTINGS_CHANGED", User.Identity?.Name ?? "unknown", true, null, ip));

        PopulateFromSettings(_settings.Get());
        SaveOk     = true;
        SaveResult = "Settings saved successfully.";
        return Page();
    }

    public IActionResult OnPostChangeBreakglass(
        string currentPassword, string newPassword, string confirmPassword)
    {
        var ip   = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var auth = HttpContext.RequestServices.GetRequiredService<LocalAuthService>();
        PopulateFromSettings(_settings.Get());

        if (!auth.ValidatePassword(currentPassword))
        {
            _audit.Log(new AuditEvent("AUTH_FAILED", User.Identity?.Name ?? "unknown", false,
                "Incorrect current password during breakglass change via Settings", ip));
            SaveResult = "Current password is incorrect.";
            return Page();
        }

        var err = Pages.Auth.LoginModel.ValidatePasswordPolicy(newPassword, confirmPassword);
        if (err != null) { SaveResult = err; return Page(); }

        auth.SetPassword(newPassword);
        _audit.Log(new AuditEvent("AUTH_PASSWORD_CHANGED", User.Identity?.Name ?? "unknown", true,
            "Breakglass password changed via Settings page", ip));
        SaveOk     = true;
        SaveResult = "Breakglass password changed.";
        return Page();
    }

    // ── GET handlers (AJAX) ───────────────────────────────────────────────────

    public IActionResult OnGetTestDomain()
    {
        var s = _settings.Get();
        if (string.IsNullOrWhiteSpace(s.ServiceAccountUpn) || string.IsNullOrWhiteSpace(s.ServiceAccountPassword))
            return new JsonResult(new { ok = false, message = "Save the service account UPN and password first." });
        if (string.IsNullOrWhiteSpace(s.DomainFqdn))
            return new JsonResult(new { ok = false, message = "Domain FQDN is not configured." });

        try
        {
#pragma warning disable CA1416 // Windows-only: this service only runs on Windows Server
            using var entry = new System.DirectoryServices.DirectoryEntry(
                $"LDAP://{s.DomainFqdn}", s.ServiceAccountUpn, s.ServiceAccountPassword);
            var _ = entry.NativeObject;
#pragma warning restore CA1416
            return new JsonResult(new { ok = true, message = $"Connected to {s.DomainFqdn} as {s.ServiceAccountUpn}" });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { ok = false, message = ex.Message });
        }
    }

    public async Task<IActionResult> OnGetTestEmailAsync()
    {
        var s = _settings.Get();
        if (string.IsNullOrWhiteSpace(s.SmtpHost) || string.IsNullOrWhiteSpace(s.NotifyEmail))
            return new JsonResult(new { ok = false, message = "Save SMTP host and notify address first." });

        try
        {
            await _email.SendTestAsync();
            return new JsonResult(new { ok = true, message = $"Test email sent to {s.NotifyEmail}" });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { ok = false, message = ex.Message });
        }
    }

    public async Task<IActionResult> OnGetCheckUpdateAsync()
    {
        var s = _settings.Get();
        if (string.IsNullOrWhiteSpace(s.UpdateCheckUrl))
            return new JsonResult(new { ok = false, message = "No update check URL configured in Settings." });

        try
        {
            var client  = _http.CreateClient();
            var current = typeof(IndexModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
            client.DefaultRequestHeaders.Add("User-Agent", $"DeployManager/{current}");

            var json    = await client.GetStringAsync(s.UpdateCheckUrl);
            using var doc = JsonDocument.Parse(json);

            var tagName  = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var htmlUrl  = doc.RootElement.TryGetProperty("html_url", out var hu) ? hu.GetString() ?? "" : "";
            var latest   = tagName.TrimStart('v');

            bool updateAvailable;
            try { updateAvailable = new Version(latest) > new Version(current); }
            catch { updateAvailable = false; }

            return new JsonResult(new { ok = true, updateAvailable, latest, current, releaseUrl = htmlUrl });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { ok = false, message = ex.Message });
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void PopulateFromSettings(AppSettings s)
    {
        ServiceAccountPasswordSet = !string.IsNullOrEmpty(s.ServiceAccountPassword);
        WinpeLocalPasswordSet     = !string.IsNullOrEmpty(s.WinpeLocalPassword);
        EntraSecretSet            = !string.IsNullOrEmpty(s.EntraClientSecret);
        SmtpPasswordSet           = !string.IsNullOrEmpty(s.SmtpPassword);

        Cfg = s;
        // Never send cleartext passwords/secrets to the browser
        Cfg.ServiceAccountPassword = "";
        Cfg.WinpeLocalPassword     = "";
        Cfg.EntraClientSecret      = "";
        Cfg.SmtpPassword           = "";
    }
}
