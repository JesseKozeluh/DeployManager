using DeployManager.Models;
using DeployManager.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DeployManager.Pages.Jobs;

public class IndexModel : PageModel
{
    private const int PageSize = 50;

    private readonly DataStore _data;

    public List<DeploymentJob>              Jobs          { get; private set; } = new();
    public Dictionary<string, SoftwarePackage> PackageLookup { get; private set; } = new();
    public List<string>                     Sites         { get; private set; } = new();
    public int  TotalJobs  { get; private set; }
    public int  TotalPages { get; private set; }
    public bool AnyActive  { get; private set; }

    [BindProperty(SupportsGet = true)] public string? FilterStatus { get; set; }
    [BindProperty(SupportsGet = true)] public string? FilterSite   { get; set; }
    [BindProperty(SupportsGet = true)] public int     CurrentPage  { get; set; } = 1;

    public static readonly string[] Statuses = { "pending", "imaging", "complete", "error", "timeout" };

    public IndexModel(DataStore data) => _data = data;

    public void OnGet()
    {
        if (CurrentPage < 1) CurrentPage = 1;
        Jobs          = _data.GetJobsFiltered(FilterStatus, FilterSite, CurrentPage, PageSize, out var total);
        TotalJobs     = total;
        TotalPages    = (int)Math.Ceiling(total / (double)PageSize);
        PackageLookup = _data.GetPackages().ToDictionary(p => p.Id);
        Sites         = _data.GetDistinctJobSites();
        AnyActive     = _data.AnyActiveJobs();
    }

    public IActionResult OnPostDelete(string mac)
    {
        _data.DeleteJob(mac);
        return RedirectToPage(new { FilterStatus, FilterSite, CurrentPage });
    }

    public IActionResult OnPostClearTerminal()
    {
        _data.DeleteTerminalJobs();
        return RedirectToPage(new { FilterStatus, FilterSite, CurrentPage = 1 });
    }

    public IActionResult OnGetComplete(string mac)
    {
        if (string.IsNullOrWhiteSpace(mac))
            return BadRequest("mac required");
        _data.MarkJobImaging(mac);
        return Content("ok");
    }

    public IActionResult OnGetAutopilotCsv(string? mac = null)
    {
        List<DeploymentJob> jobs;
        string filename;
        if (!string.IsNullOrEmpty(mac))
        {
            var job = _data.GetJobsWithHardwareHash().FirstOrDefault(j => j.MacAddress == mac);
            jobs = job != null ? new() { job } : new();
            filename = $"Autopilot_{jobs.FirstOrDefault()?.DeviceName ?? mac}_{DateTime.Now:yyyyMMdd}.csv";
        }
        else
        {
            jobs = _data.GetJobsWithHardwareHash();
            filename = $"AutopilotHashes_{DateTime.Now:yyyyMMdd_HHmm}.csv";
        }
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Device Serial Number,Windows Product ID,Hardware Hash,Group Tag");
        foreach (var j in jobs)
        {
            var serial = _data.GetMachineByMac(j.MacAddress)?.SerialNumber ?? "";
            sb.AppendLine(CsvEscape(serial) + "," + CsvEscape(j.WindowsProductId) + "," +
                          CsvEscape(j.HardwareHash) + "," + CsvEscape(j.GroupTag));
        }
        return File(System.Text.Encoding.UTF8.GetBytes(sb.ToString()),
            "text/csv", filename);
    }

    private static string CsvEscape(string v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        if (v.Contains(',') || v.Contains('"') || v.Contains('\n'))
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        return v;
    }
}
