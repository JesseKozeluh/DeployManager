using System.Security.Claims;
using DeployManager.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DeployManager.Pages.Auth;

[AllowAnonymous]
public class LoginModel : PageModel
{
    private readonly LocalAuthService _auth;
    private readonly IAuditService    _audit;
    private readonly ISettingsService _settings;

    public string? Error        { get; private set; }
    public bool    IsFirstRun   { get; private set; }
    public bool    MustChange   { get; private set; }
    public string  TempPassword { get; private set; } = "";

    public bool EntraEnabled { get; private set; }

    public LoginModel(LocalAuthService auth, IAuditService audit, ISettingsService settings)
    {
        _auth     = auth;
        _audit    = audit;
        _settings = settings;
    }

    public void OnGet()
    {
        IsFirstRun = _auth.IsPasswordUnset();
        MustChange = !IsFirstRun && _auth.MustChangePassword();

        var s = _settings.Get();
        EntraEnabled = string.Equals(s.AuthMode, "entra", StringComparison.OrdinalIgnoreCase)
                       && !string.IsNullOrWhiteSpace(s.EntraTenantId)
                       && !string.IsNullOrWhiteSpace(s.EntraClientId);
    }

    // Start Entra sign-in — redirects to Microsoft, returns to /signin-oidc.
    public IActionResult OnGetEntra(string? returnUrl)
    {
        var props = new AuthenticationProperties
        {
            RedirectUri = !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/"
        };
        return Challenge(props, OpenIdConnectDefaults.AuthenticationScheme);
    }

    // Normal login
    public async Task<IActionResult> OnPostAsync(string password, string? returnUrl)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        if (!_auth.ValidatePassword(password))
        {
            _audit.Log(new AuditEvent("AUTH_FAILED", "breakglass", false, "Invalid password supplied", ip));
            Error      = "Incorrect password.";
            IsFirstRun = _auth.IsPasswordUnset();
            return Page();
        }

        if (_auth.MustChangePassword())
        {
            MustChange   = true;
            TempPassword = password;
            return Page();
        }

        _audit.Log(new AuditEvent("AUTH_SUCCESS", "breakglass", true, null, ip));
        return await SignInAndRedirect(returnUrl);
    }

    // First-run: no hash stored yet
    public IActionResult OnPostSetPassword(string newPassword, string confirmPassword)
    {
        var ip  = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var err = ValidatePasswordPolicy(newPassword, confirmPassword);
        if (err != null)
        {
            Error = err; IsFirstRun = true; return Page();
        }
        _auth.SetPassword(newPassword);
        _audit.Log(new AuditEvent("AUTH_PASSWORD_SET", "breakglass", true, "Initial breakglass password set", ip));
        return RedirectToPage(new { });
    }

    // Forced-change flow
    public async Task<IActionResult> OnPostChangePassword(
        string currentPassword, string newPassword, string confirmPassword)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        if (!_auth.ValidatePassword(currentPassword))
        {
            _audit.Log(new AuditEvent("AUTH_FAILED", "breakglass", false, "Incorrect current password during forced change", ip));
            Error = "Current password is incorrect."; MustChange = true; return Page();
        }

        var err = ValidatePasswordPolicy(newPassword, confirmPassword);
        if (err != null)
        {
            Error = err; MustChange = true; TempPassword = currentPassword; return Page();
        }

        _auth.SetPassword(newPassword);
        _audit.Log(new AuditEvent("AUTH_PASSWORD_CHANGED", "breakglass", true, "Breakglass password changed (forced)", ip));
        return await SignInAndRedirect(null);
    }

    // Sign out
    public async Task<IActionResult> OnGetLogout()
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        _audit.Log(new AuditEvent("AUTH_LOGOUT", User.Identity?.Name ?? "unknown", true, null, ip));
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToPage();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<IActionResult> SignInAndRedirect(string? returnUrl)
    {
        var claims    = new[]
        {
            new Claim(ClaimTypes.Name, "breakglass"),
            new Claim(ClaimTypes.Role, "Administrator"),
            new Claim("displayName", "Breakglass Admin")
        };
        var identity  = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        // Non-persistent session cookie: no Expires/Max-Age, so it is tied to the browser
        // session and a fresh login is required for new sessions (and after the idle timeout).
        var authProps = new AuthenticationProperties { IsPersistent = false, AllowRefresh = true };
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, authProps);

        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            return Redirect(returnUrl);
        return RedirectToPage("/Index");
    }

    // ISM-0421: minimum 14 chars; upper+lower+digit+special for privileged accounts
    internal static string? ValidatePasswordPolicy(string newPassword, string confirmPassword)
    {
        if (newPassword != confirmPassword)
            return "Passwords do not match.";
        if (newPassword.Length < 14)
            return "Password must be at least 14 characters (ISM-0421 requirement for privileged accounts).";
        if (!newPassword.Any(char.IsUpper))
            return "Password must contain at least one uppercase letter.";
        if (!newPassword.Any(char.IsLower))
            return "Password must contain at least one lowercase letter.";
        if (!newPassword.Any(char.IsDigit))
            return "Password must contain at least one digit.";
        if (!newPassword.Any(c => !char.IsLetterOrDigit(c)))
            return "Password must contain at least one special character.";
        return null;
    }
}
