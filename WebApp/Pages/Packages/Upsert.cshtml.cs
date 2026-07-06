using DeployManager.Models;
using DeployManager.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DeployManager.Pages.Packages;

public class UpsertModel : PageModel
{
    private readonly DataStore _data;

    [BindProperty] public SoftwarePackage Package { get; set; } = new();
    [BindProperty] public string OrderedIds { get; set; } = "";

    public bool IsNew => string.IsNullOrEmpty(Request.RouteValues["id"] as string);
    public List<SoftwareItem> AllSoftware { get; private set; } = new();
    public List<SoftwareItem> Available   { get; private set; } = new();
    public Dictionary<string, SoftwareItem> SoftwareLookup { get; private set; } = new();

    public UpsertModel(DataStore data) => _data = data;

    public IActionResult OnGet(string? id)
    {
        if (id != null)
        {
            var existing = _data.GetPackageById(id);
            if (existing == null) return NotFound();
            Package = existing;
        }
        LoadSoftware();
        return Page();
    }

    public IActionResult OnPost()
    {
        if (!ModelState.IsValid) { LoadSoftware(); return Page(); }

        Package.Description ??= "";
        Package.SoftwareIds = (OrderedIds ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Distinct()
            .ToList();

        _data.SavePackage(Package);
        return RedirectToPage("Index");
    }

    private void LoadSoftware()
    {
        AllSoftware    = _data.GetSoftware();
        SoftwareLookup = AllSoftware.ToDictionary(s => s.Id);
        Available      = AllSoftware.Where(s => !Package.SoftwareIds.Contains(s.Id)).ToList();
    }
}
