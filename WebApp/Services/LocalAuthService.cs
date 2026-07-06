using DeployManager.Models;

namespace DeployManager.Services;

public class LocalAuthService
{
    private readonly ISettingsService _settings;

    public const string SchemeName   = "LocalBreakglass";
    public const string CookieName   = ".OSDeploy.Auth";
    public const string LoginPath    = "/auth/login";

    public LocalAuthService(ISettingsService settings)
    {
        _settings = settings;
    }

    // Returns true if the password matches the stored hash.
    public bool ValidatePassword(string password)
    {
        var hash = _settings.Get().BreakglassHash;
        if (string.IsNullOrEmpty(hash)) return false;
        return BCrypt.Net.BCrypt.Verify(password, hash);
    }

    // Returns true if no hash is set yet (first-run).
    public bool IsPasswordUnset() => string.IsNullOrEmpty(_settings.Get().BreakglassHash);

    // Sets (or resets) the breakglass password. Clears the must-change flag.
    public void SetPassword(string newPassword)
    {
        var s = _settings.Get();
        s.BreakglassHash       = BCrypt.Net.BCrypt.HashPassword(newPassword, workFactor: 12);
        s.BreakglassMustChange = false;
        _settings.Save(s);
    }

    // True if the user must change their password before proceeding.
    public bool MustChangePassword() => _settings.Get().BreakglassMustChange;
}
