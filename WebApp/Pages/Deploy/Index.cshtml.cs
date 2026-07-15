using DeployManager.Models;
using DeployManager.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DeployManager.Pages.Deploy;

public class IndexModel : PageModel
{
    private readonly DataStore _data;
    private readonly RemoteBootService _remoteBoot;
    private readonly IConfiguration _config;
    private readonly ISettingsService _settings;

    public List<Machine> Machines               { get; private set; } = new();
    public List<SoftwarePackage> Packages       { get; private set; } = new();
    public List<DriverPackage> DriverPackages   { get; private set; } = new();
    public List<WimImage> Wims                  { get; private set; } = new();
    public List<SiteConfig> Sites               { get; private set; } = new();
    public string ComputerPrefix                { get; private set; } = "";

    [BindProperty(SupportsGet = true)] public string? SelectedMachineId      { get; set; }
    [BindProperty(SupportsGet = true)] public string? SelectedPackageId       { get; set; }
    [BindProperty(SupportsGet = true)] public string? SelectedDriverPackageId { get; set; }

    public string DeviceName { get; private set; } = "";
    public string Site       { get; private set; } = "";
    public string OU         { get; private set; } = "";
    public string WimName    { get; private set; } = "";

    public (bool Success, string Message)? Result { get; private set; }
    public List<string> PreflightErrors   { get; private set; } = new();
    public List<string> PreflightWarnings { get; private set; } = new();

    public IndexModel(DataStore data, RemoteBootService boot, IConfiguration config, ISettingsService settings)
    {
        _data     = data;
        _remoteBoot = boot;
        _config   = config;
        _settings = settings;
    }

    public void OnGet()
    {
        Load();
        if (SelectedMachineId != null)
        {
            var m = _data.GetMachineById(SelectedMachineId);
            if (m != null) { DeviceName = m.Hostname; Site = m.Site; OU = m.OU; }
        }
    }

    public async Task<IActionResult> OnPostAsync(
        string machineId, string deviceName, string site, string ou,
        string wimName, string packageId, string driverPackageId,
        string joinMode, string workgroup, string groupTag, string action)
    {
        Load();
        DeviceName = deviceName; Site = site; OU = ou; WimName = wimName;
        SelectedMachineId = machineId; SelectedPackageId = packageId;
        SelectedDriverPackageId = driverPackageId;

        try
        {
            var machine = _data.GetMachineById(machineId);
            if (machine == null) { Result = (false, "Machine not found."); return Page(); }

            if (!RunPreflightChecks(wimName, joinMode))
            {
                Result = (false, "Pre-flight checks failed — see errors above. The job was not saved.");
                return Page();
            }

            var messages = new List<string>();
            bool ok = true;

            var job = new DeploymentJob
            {
                MacAddress      = machine.MacAddress,
                DeviceName      = deviceName.Trim().ToUpper(),
                Site            = site,
                OU              = joinMode == "workgroup" ? "" : ou,
                WimName         = wimName,
                PackageId       = packageId ?? "",
                DriverPackageId = driverPackageId ?? "",
                JoinMode        = joinMode ?? "domain",
                Workgroup       = string.IsNullOrWhiteSpace(workgroup) ? "WORKGROUP" : workgroup.Trim().ToUpper(),
                GroupTag        = joinMode == "workgroup" ? TruncateTag(groupTag) : ""
            };

            if (action == "pxeboot")
            {
                // IMPORTANT: trigger the reboot-to-PXE BEFORE saving the job.
                // SaveJob provisions the offline domain-join blob, which resets the
                // machine's computer-account password in AD. For a machine still
                // running its current OS, that breaks its domain secure channel and
                // makes this WMI/DCOM reboot fail ("RPC server unavailable"). So we
                // reboot while the machine's trust is intact, then provision — by then
                // the machine is rebooting away, and the blob is only needed ~30-60s
                // later when WinPE fetches the job file.
                var target = string.IsNullOrEmpty(machine.IpAddress) ? machine.Hostname : machine.IpAddress;
                var (success, msg) = await _remoteBoot.SetPxeBootAndRestart(target);
                messages.Add(msg);
                if (!success)
                {
                    // Reboot failed — do NOT provision, so the machine's domain trust
                    // stays intact and you can fix the issue and retry.
                    Result = (false, msg + " Job was not saved, so the machine's domain trust is left intact for a retry.");
                    return Page();
                }
            }
            else if (action == "stage")
            {
                // Local-disk WinPE staging (Secure Boot compatible, works on PXE-only
                // models and over the WAN). Same ordering rationale as pxeboot: launch
                // the staging via WMI BEFORE provisioning resets the AD password, or the
                // WMI auth to the still-running machine would fail.
                var target = string.IsNullOrEmpty(machine.IpAddress) ? machine.Hostname : machine.IpAddress;
                var (success, msg) = await _remoteBoot.StageLocalWinPE(target);
                messages.Add(msg);
                if (!success)
                {
                    Result = (false, msg + " Job was not saved, so the machine's domain trust is left intact for a retry.");
                    return Page();
                }
            }

            _data.SaveJob(job);
            messages.Add($"Job saved for {machine.MacAddress}.");

            Result = (ok, string.Join(" ", messages));
        }
        catch (Exception ex)
        {
            Result = (false, $"Unexpected error: {ex.Message}");
        }

        return Page();
    }

    private void Load()
    {
        var s = _settings.Get();
        Sites          = s.Sites;
        ComputerPrefix = s.ComputerPrefix;
        Machines       = _data.GetMachines().OrderBy(m => m.Hostname).ToList();
        Packages       = _data.GetPackages();
        DriverPackages = _data.GetDriverPackages().Where(d => d.Enabled).ToList();
        Wims           = _data.GetWims().Where(w => w.Enabled).ToList();
    }

    private static string TruncateTag(string? v)
    {
        var t = v?.Trim() ?? "";
        return t.Length > 256 ? t[..256] : t;
    }

    private bool RunPreflightChecks(string wimName, string? joinMode = "domain")
    {
        var s = _settings.Get();

        if (string.IsNullOrWhiteSpace(s.DeployServerUrl))
            PreflightErrors.Add("Deploy Server URL is not set — WinPE cannot download the WIM. Configure it in Settings.");
        if (string.IsNullOrWhiteSpace(s.ApiServerUrl))
            PreflightErrors.Add("API Server URL is not set — WinPE cannot report job status. Configure it in Settings.");
        if (!s.Sites.Any())
            PreflightErrors.Add("No sites configured — a site is needed for timezone and locale. Add at least one site in Settings → Sites.");
        if (joinMode != "workgroup" && (string.IsNullOrWhiteSpace(s.ServiceAccountUpn) || string.IsNullOrWhiteSpace(s.ServiceAccountPassword)))
            PreflightErrors.Add("Service account not configured — domain join will fail. Set the UPN and password in Settings.");

        if (!string.IsNullOrWhiteSpace(wimName))
        {
            var imagesPath = Environment.ExpandEnvironmentVariables(
                _config["DeployManager:ImagesPath"] ?? @"%ProgramData%\DeployManager\images");
            var wimFile = Path.Combine(imagesPath, wimName.Replace('/', Path.DirectorySeparatorChar) + ".wim");
            if (!System.IO.File.Exists(wimFile))
                PreflightErrors.Add($"WIM file not found on disk: {wimFile} — copy or register the correct file.");
        }

        // ── Warnings (inform but do not block) ───────────────────────────────
        try
        {
            var imagesPath = Environment.ExpandEnvironmentVariables(
                _config["DeployManager:ImagesPath"] ?? @"%ProgramData%\DeployManager\images");
            var root = Path.GetPathRoot(imagesPath) ?? "C:\\";
            var freeGb = new DriveInfo(root).AvailableFreeSpace / 1_073_741_824L;
            if (freeGb < 10)
                PreflightWarnings.Add($"Low disk space: only {freeGb} GB free on the images drive. Large WIM files may fail to serve.");
        }
        catch { }

        return PreflightErrors.Count == 0;
    }
}
