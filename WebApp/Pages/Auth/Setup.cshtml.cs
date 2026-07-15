using System.Security.Claims;
using DeployManager.Models;
using DeployManager.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DeployManager.Pages.Auth;

[AllowAnonymous]
public class SetupModel : PageModel
{
    private readonly ISettingsService _settings;
    private readonly LocalAuthService _auth;

    public AppSettings Cfg  { get; private set; } = new();
    public string?     Error { get; private set; }

    public SetupModel(ISettingsService settings, LocalAuthService auth)
    {
        _settings = settings;
        _auth     = auth;
    }

    public IActionResult OnGet()
    {
        if (_settings.IsSetupComplete()) return RedirectToPage("/Index");
        Cfg = _settings.Get();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(
        string orgName, string computerPrefix,
        string deployServerUrl, string apiServerUrl,
        string domainFqdn, string defaultComputerOU, string defaultTimezone,
        string breakglassPassword, string breakglassConfirm)
    {
        Cfg = _settings.Get();

        if (breakglassPassword != breakglassConfirm)
        {
            Error = "Passwords do not match.";
            return Page();
        }
        if (breakglassPassword.Length < 8)
        {
            Error = "Password must be at least 8 characters.";
            return Page();
        }

        var s = _settings.Get();
        s.OrgName          = orgName?.Trim() ?? "";
        s.ComputerPrefix   = computerPrefix?.Trim() ?? "";
        s.DeployServerUrl  = deployServerUrl?.Trim().TrimEnd('/') ?? "";
        s.ApiServerUrl     = AppSettings.NormalizeApiUrl(apiServerUrl);
        s.DomainFqdn       = domainFqdn?.Trim() ?? "";
        s.DefaultComputerOU = defaultComputerOU?.Trim() ?? "";
        s.DefaultTimezone  = defaultTimezone?.Trim() ?? "";
        s.BreakglassHash   = BCrypt.Net.BCrypt.HashPassword(breakglassPassword, workFactor: 12);
        s.BreakglassMustChange = false;
        s.SetupComplete    = true;
        _settings.Save(s);

        // Sign in automatically after setup — non-persistent session cookie
        var claims    = new[] { new Claim(ClaimTypes.Name, "breakglass"), new Claim(ClaimTypes.Role, "Administrator"), new Claim("displayName", "Breakglass Admin") };
        var identity  = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var authProps = new AuthenticationProperties { IsPersistent = false, AllowRefresh = true };
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), authProps);

        return RedirectToPage("/Auth/Checklist");
    }
}
