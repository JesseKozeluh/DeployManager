using DeployManager.Models;
using DeployManager.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DeployManager.Pages.Drivers;

public class IndexModel : PageModel
{
    private readonly DataStore _data;
    private readonly ISettingsService _settings;

    public List<DriverPackage> Packages { get; private set; } = new();
    public Dictionary<string, int> InfCounts { get; private set; } = new();
    public string ServerIp { get; private set; } = "";

    public IndexModel(DataStore data, ISettingsService settings)
    {
        _data     = data;
        _settings = settings;
    }

    public void OnGet()
    {
        Packages = _data.GetDriverPackages();
        ServerIp = _settings.Get().ServerIp?.Trim() ?? "";
        InfCounts = Packages.ToDictionary(p => p.Id, p => _data.CountDriverInfs(p.FolderName));
    }

    public IActionResult OnPostDelete(string id)
    {
        _data.DeleteDriverPackage(id);
        return RedirectToPage();
    }
}
