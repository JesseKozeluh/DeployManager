using DeployManager.Models;
using DeployManager.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DeployManager.Pages;

public class IndexModel : PageModel
{
    private readonly DataStore        _data;
    private readonly IConfiguration   _config;

    public int    PendingJobs    { get; private set; }
    public int    ActiveImaging  { get; private set; }
    public int    CompletedToday { get; private set; }
    public string SuccessRate    { get; private set; } = "—";
    public int    MachineCount   { get; private set; }
    public int    PackageCount   { get; private set; }

    public int?  CertExpireDays   { get; private set; }
    public long? ImagesFreeDiskGb { get; private set; }

    public List<DeploymentJob> RecentJobs { get; private set; } = new();

    public IndexModel(DataStore data, IConfiguration config)
    {
        _data   = data;
        _config = config;
    }

    public void OnGet()
    {
        var jobs = _data.GetAllJobs();

        PendingJobs    = jobs.Count(j => j.Status == "pending");
        ActiveImaging  = jobs.Count(j => j.Status == "imaging");
        CompletedToday = jobs.Count(j => j.Status == "complete" && j.Completed?.Date == DateTime.UtcNow.Date);
        MachineCount   = _data.GetMachines().Count;
        PackageCount   = _data.GetPackages().Count;

        var finished  = jobs.Where(j => j.Status is "complete" or "error").ToList();
        var succeeded = finished.Count(j => j.Status == "complete");
        SuccessRate   = finished.Count > 0 ? $"{(int)Math.Round(100.0 * succeeded / finished.Count)}%" : "—";

        RecentJobs = jobs.Take(8).ToList();

        // Certificate expiry
        var cert = CertificateHolder.Current;
        if (cert != null)
            CertExpireDays = (int)(cert.NotAfter - DateTime.Now).TotalDays;

        // Disk space on images drive
        try
        {
            var imgPath = Environment.ExpandEnvironmentVariables(
                _config["DeployManager:ImagesPath"] ?? @"%ProgramData%\DeployManager\images");
            var driveRoot = Path.GetPathRoot(imgPath) ?? "C:\\";
            ImagesFreeDiskGb = new DriveInfo(driveRoot).AvailableFreeSpace / 1_073_741_824L;
        }
        catch { }
    }
}
