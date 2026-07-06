using DeployManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DeployManager.Pages.Settings;

[Authorize(Roles = "Administrator")]
public class CertificateModel : PageModel
{
    private readonly CertificateService _cert;
    private readonly ISettingsService   _settings;

    public CertificateModel(CertificateService cert, ISettingsService settings)
    {
        _cert     = cert;
        _settings = settings;
    }

    public CertInfo                  Info       { get; private set; } = default!;
    public string?                   PendingCsr { get; private set; }
    public (bool Ok, string Message)? Result    { get; private set; }

    // ── Generate-request form ──
    [BindProperty] public string  CommonName     { get; set; } = "";
    [BindProperty] public string  SanDnsNames    { get; set; } = "";
    [BindProperty] public string  SanIpAddresses { get; set; } = "";
    [BindProperty] public int     KeySize        { get; set; } = 4096;
    [BindProperty] public string? Org            { get; set; }
    [BindProperty] public string? Ou             { get; set; }
    [BindProperty] public string? Locality       { get; set; }
    [BindProperty] public string? State          { get; set; }
    [BindProperty] public string? Country        { get; set; }

    // ── Install-signed-cert form ──
    [BindProperty] public string  CertPem  { get; set; } = "";
    [BindProperty] public string? ChainPem { get; set; }

    private string Actor => User.Identity?.Name ?? "unknown";

    public void OnGet()
    {
        Load();
        SeedDefaults();
    }

    private void Load()
    {
        Info       = _cert.GetInfo();
        PendingCsr = _cert.GetPendingCsr();
    }

    // Pre-fill sensible, editable defaults from Settings (portable — no hardcoding).
    private void SeedDefaults()
    {
        var s    = _settings.Get();
        var host = "";
        if (Uri.TryCreate(s.ApiServerUrl, UriKind.Absolute, out var u)) host = u.Host;

        CommonName     = host;
        SanDnsNames    = string.Join("\n",
            new[] { host, "localhost" }.Where(x => !string.IsNullOrWhiteSpace(x) && !System.Net.IPAddress.TryParse(x, out _)).Distinct());
        SanIpAddresses = s.ServerIp ?? "";
    }

    public IActionResult OnPostGenerate()
    {
        if (string.IsNullOrWhiteSpace(CommonName))
        {
            Result = (false, "Common Name is required.");
            Load();
            return Page();
        }
        try
        {
            _cert.GenerateCsr(CommonName.Trim(), Split(SanDnsNames), Split(SanIpAddresses),
                              KeySize, Org, Ou, Locality, State, Country, Actor);
            Result = (true, "Signing request generated. Download or copy the CSR below, sign it on your CA, then upload the signed certificate.");
        }
        catch (Exception ex)
        {
            Result = (false, "Failed to generate CSR: " + ex.Message);
        }
        Load();
        return Page();
    }

    public IActionResult OnPostInstall()
    {
        if (string.IsNullOrWhiteSpace(CertPem))
        {
            Result = (false, "Paste the signed certificate (Base64 / PEM) before installing.");
            Load();
            return Page();
        }
        Result = _cert.InstallSignedCert(CertPem, ChainPem, Actor);
        Load();
        return Page();
    }

    public IActionResult OnPostRevert()
    {
        Result = _cert.RevertToSelfSigned(Actor);
        Load();
        return Page();
    }

    public IActionResult OnGetDownloadCsr()
    {
        var csr = _cert.GetPendingCsr();
        if (csr == null) return RedirectToPage();
        return File(System.Text.Encoding.ASCII.GetBytes(csr), "application/pkcs10", "deploymgr.csr");
    }

    private static IEnumerable<string> Split(string? s) =>
        (s ?? "").Split(new[] { '\n', '\r', ',', ';', ' ', '\t' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
