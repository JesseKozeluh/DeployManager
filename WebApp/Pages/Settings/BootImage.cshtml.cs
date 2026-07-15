using System.Diagnostics;
using DeployManager.Models;
using DeployManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DeployManager.Pages.Settings;

[Authorize(Roles = "Administrator")]
public class BootImageModel : PageModel
{
    private readonly ISettingsService    _settings;
    private readonly IEncryptionService  _enc;
    private readonly IConfiguration      _config;
    private readonly ILogger<BootImageModel> _log;

    public string BootWimPath    { get; private set; } = "";
    public bool   WimExists      { get; private set; }
    public string ServerUrl      { get; private set; } = "";
    public bool   PasswordSet    { get; private set; }

    [BindProperty] public string? CustomBootWimPath { get; set; }
    [BindProperty] public string? NewDriverPath { get; set; }

    public string? PatchOutput  { get; private set; }
    public bool    PatchSuccess { get; private set; }
    public string? AdkDismPath  { get; private set; }
    public List<string> DriverPaths { get; private set; } = new();

    // iPXE HTTP boot
    public string TftpRootPath   { get; private set; } = "";
    public bool   IpxeEfiExists  { get; private set; }
    public bool   UndiExists     { get; private set; }
    public bool   WimbootExists  { get; private set; }
    public string IpxeScript     { get; private set; } = "";

    public BootImageModel(ISettingsService settings, IEncryptionService enc,
                          IConfiguration config, ILogger<BootImageModel> log)
    {
        _settings = settings;
        _enc      = enc;
        _config   = config;
        _log      = log;
    }

    public void OnGet() => Populate();

    // Save a custom boot.wim path
    public IActionResult OnPostSavePath()
    {
        if (!string.IsNullOrWhiteSpace(CustomBootWimPath))
        {
            var s = _settings.Get();
            s.BootWimPath = CustomBootWimPath.Trim();
            _settings.Save(s);
        }
        return RedirectToPage();
    }

    public IActionResult OnPostAddDriver()
    {
        if (!string.IsNullOrWhiteSpace(NewDriverPath))
        {
            var path = NewDriverPath.Trim();
            var s = _settings.Get();
            if (!s.WinpeDriverPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                s.WinpeDriverPaths.Add(path);
                _settings.Save(s);
            }
        }
        return RedirectToPage();
    }

    public IActionResult OnPostRemoveDriver(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            var s = _settings.Get();
            s.WinpeDriverPaths.RemoveAll(p => p.Equals(path.Trim(), StringComparison.OrdinalIgnoreCase));
            _settings.Save(s);
        }
        return RedirectToPage();
    }

    // Runs Update-BootWim.ps1 on the server synchronously (takes 60-90 s).
    // Requires: ADK installed, service running as Local System or an admin account.
    public IActionResult OnPostPatch()
    {
        Populate();

        var s       = _settings.Get();
        var rawPass = string.IsNullOrEmpty(s.WinpeLocalPassword)
            ? "" : _enc.Unprotect(s.WinpeLocalPassword);

        if (string.IsNullOrWhiteSpace(s.ApiServerUrl))
        {
            PatchOutput = "API Server URL is not configured in Settings. Set it first.";
            return Page();
        }
        if (string.IsNullOrWhiteSpace(rawPass))
        {
            PatchOutput = "WinPE local password is not configured in Settings. Set it first.";
            return Page();
        }
        if (string.IsNullOrWhiteSpace(BootWimPath) || !System.IO.File.Exists(BootWimPath))
        {
            PatchOutput = $"boot.wim not found at: {BootWimPath}\nCopy your WinPE boot.wim there first.";
            return Page();
        }

        var scriptDir  = Path.Combine(AppContext.BaseDirectory, "Scripts");
        // Also look relative to the project root for development
        var scriptPath = System.IO.File.Exists(Path.Combine(scriptDir, "Update-BootWim.ps1"))
            ? Path.Combine(scriptDir, "Update-BootWim.ps1")
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\Scripts\Update-BootWim.ps1"));

        if (!System.IO.File.Exists(scriptPath))
        {
            PatchOutput = $"Update-BootWim.ps1 not found.\nExpected at: {scriptPath}";
            return Page();
        }

        var driverArg = "";
        if (s.WinpeDriverPaths.Count > 0)
        {
            var joined = string.Join(';', s.WinpeDriverPaths.Where(p => !string.IsNullOrWhiteSpace(p)));
            if (joined.Length > 0)
                driverArg = $" -DriverPathList \"{joined}\"";
        }

        var args = $"-ExecutionPolicy Bypass -NonInteractive -File \"{scriptPath}\" " +
                   $"-BootWimPath \"{BootWimPath}\" " +
                   $"-ServerUrl \"{s.ApiServerUrl}\" " +
                   $"-WinpePassword \"{rawPass.Replace("\"", "`\"")}\"" +
                   driverArg;

        try
        {
            var psi = new ProcessStartInfo(DeployManager.Services.SystemPaths.PowerShell, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            using var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(TimeSpan.FromMinutes(5));

            PatchOutput  = (stdout + (stderr.Length > 0 ? "\n\nSTDERR:\n" + stderr : "")).Trim();
            PatchSuccess = proc.ExitCode == 0;

            if (PatchSuccess)
                _log.LogInformation("boot.wim patched successfully by {User}.", User.Identity?.Name);
            else
                _log.LogWarning("boot.wim patch failed (exit {Code}) by {User}.", proc.ExitCode, User.Identity?.Name);
        }
        catch (Exception ex)
        {
            PatchOutput = $"Failed to start PowerShell: {ex.Message}";
            _log.LogError(ex, "Failed to launch Update-BootWim.ps1");
        }

        Populate();
        return Page();
    }

    private void Populate()
    {
        var s = _settings.Get();

        BootWimPath = string.IsNullOrWhiteSpace(s.BootWimPath)
            ? @"%ProgramData%\DeployManager\tftp\Boot\boot.wim"
            : s.BootWimPath;
        BootWimPath = Environment.ExpandEnvironmentVariables(BootWimPath);

        WimExists    = System.IO.File.Exists(BootWimPath);
        ServerUrl    = s.ApiServerUrl;
        PasswordSet  = !string.IsNullOrWhiteSpace(s.WinpeLocalPassword);
        DriverPaths  = s.WinpeDriverPaths;

        // Detect ADK DISM for the UI hint
        var adkCandidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"Windows Kits\10\Assessment and Deployment Kit\Deployment Tools\amd64\DISM\dism.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                @"Windows Kits\10\Assessment and Deployment Kit\Deployment Tools\amd64\DISM\dism.exe")
        };
        AdkDismPath = adkCandidates.FirstOrDefault(System.IO.File.Exists);

        // iPXE HTTP boot readiness
        TftpRootPath  = Environment.ExpandEnvironmentVariables(
            _config["DeployManager:TftpPath"] ?? @"%ProgramData%\DeployManager\tftp");
        IpxeEfiExists  = System.IO.File.Exists(Path.Combine(TftpRootPath, "ipxe.efi"));
        UndiExists     = System.IO.File.Exists(Path.Combine(TftpRootPath, "undionly.kpxe"));
        WimbootExists  = System.IO.File.Exists(Path.Combine(TftpRootPath, "wimboot"));
        IpxeScript     = BuildIpxeScript(s);
    }

    private string BuildIpxeScript(AppSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.ApiServerUrl)) return "";
        var httpPort = _config.GetValue<int>("DeployManager:HttpPort", 8080);
        try
        {
            var uri      = new Uri(s.ApiServerUrl);
            var httpBase = $"http://{uri.Host}:{httpPort}/winpe";
            return
                "#!ipxe\n" +
                "\n" +
                $"set base {httpBase}\n" +
                "\n" +
                "kernel ${base}/wimboot\n" +
                "initrd --name BCD      ${base}/Boot/BCD\n" +
                "initrd --name boot.sdi ${base}/Boot/boot.sdi\n" +
                "initrd --name boot.wim ${base}/Boot/boot.wim\n" +
                "boot\n";
        }
        catch { return ""; }
    }
}
