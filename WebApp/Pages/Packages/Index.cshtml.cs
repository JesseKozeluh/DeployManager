using DeployManager.Models;
using DeployManager.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DeployManager.Pages.Packages;

public class IndexModel : PageModel
{
    private readonly DataStore _data;
    public List<SoftwarePackage> Packages { get; private set; } = new();
    public Dictionary<string, SoftwareItem> SoftwareLookup { get; private set; } = new();

    public IndexModel(DataStore data) => _data = data;

    public void OnGet()
    {
        Packages = _data.GetPackages();
        SoftwareLookup = _data.GetSoftware().ToDictionary(s => s.Id);
    }

    public IActionResult OnPostDelete(string id)
    {
        _data.DeletePackage(id);
        return RedirectToPage();
    }

    // Duplicate a package — copies all software items; takes a new name (required) + optional description.
    public IActionResult OnPostDuplicate(string id, string name, string? description)
    {
        var src = _data.GetPackageById(id);
        if (src == null) return RedirectToPage();

        var copy = new SoftwarePackage
        {
            Name        = string.IsNullOrWhiteSpace(name) ? $"Copy of {src.Name}" : name.Trim(),
            Description = description?.Trim() ?? "",
            SoftwareIds = new List<string>(src.SoftwareIds)   // copy the full item list
            // Id and Created are generated fresh
        };
        _data.SavePackage(copy);
        return RedirectToPage();
    }
}
