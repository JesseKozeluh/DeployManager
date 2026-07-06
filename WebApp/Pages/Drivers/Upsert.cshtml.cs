using DeployManager.Models;
using DeployManager.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DeployManager.Pages.Drivers;

public class UpsertModel : PageModel
{
    private readonly DataStore _data;
    private readonly ISettingsService _settings;

    [BindProperty] public DriverPackage Package { get; set; } = new();

    public bool IsNew     => string.IsNullOrEmpty(Request.RouteValues["id"] as string);
    public int  InfCount  { get; private set; } = -1;
    public string ServerIp { get; private set; } = "";

    public UpsertModel(DataStore data, ISettingsService settings)
    {
        _data     = data;
        _settings = settings;
    }

    public IActionResult OnGet(string? id)
    {
        ServerIp = _settings.Get().ServerIp?.Trim() ?? "";
        if (id != null)
        {
            var existing = _data.GetDriverPackageById(id);
            if (existing == null) return NotFound();
            Package  = existing;
            InfCount = _data.CountDriverInfs(Package.FolderName);
        }
        return Page();
    }

    public IActionResult OnPost()
    {
        if (!ModelState.IsValid)
        {
            ServerIp = _settings.Get().ServerIp?.Trim() ?? "";
            InfCount = _data.CountDriverInfs(Package.FolderName);
            return Page();
        }

        Package.FolderName = (Package.FolderName ?? "").Trim();
        Package.Description ??= "";
        _data.SaveDriverPackage(Package);
        return RedirectToPage("Index");
    }
}
