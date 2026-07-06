using DeployManager.Models;
using DeployManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DeployManager.Pages.Settings;

[Authorize(Roles = "Administrator")]
public class SitesModel : PageModel
{
    private readonly ISettingsService _settings;

    public List<SiteConfig> Sites { get; private set; } = new();
    public string? SaveResult { get; private set; }
    public bool    SaveOk     { get; private set; }

    public SitesModel(ISettingsService settings) { _settings = settings; }

    public void OnGet() => Sites = _settings.Get().Sites;

    // Save entire sites table (posted as JSON from the JS editor)
    public IActionResult OnPostSave([FromBody] List<SiteConfig>? sites)
    {
        // A null body means the JSON failed to bind — refuse rather than
        // silently overwriting the configured sites with an empty list.
        if (sites == null)
            return new JsonResult(new { ok = false, error = "Request body could not be read." })
                   { StatusCode = StatusCodes.Status400BadRequest };

        var s   = _settings.Get();
        s.Sites = sites;
        _settings.Save(s);
        return new JsonResult(new { ok = true });
    }

    // Add a blank site row (used by JS "Add row")
    public IActionResult OnPostAdd(string name, string subnet, string ou, string timezone)
    {
        var s = _settings.Get();
        s.Sites.Add(new SiteConfig
        {
            Name     = name?.Trim()     ?? "",
            Subnet   = subnet?.Trim()   ?? "",
            OU       = ou?.Trim()       ?? "",
            Timezone = timezone?.Trim() ?? ""
        });
        _settings.Save(s);
        SaveOk = true;
        SaveResult = "Site added.";
        Sites      = s.Sites;
        return Page();
    }

    public IActionResult OnPostDelete(int index)
    {
        var s = _settings.Get();
        if (index >= 0 && index < s.Sites.Count)
            s.Sites.RemoveAt(index);
        _settings.Save(s);
        SaveOk = true;
        SaveResult = "Site removed.";
        Sites      = s.Sites;
        return Page();
    }
}
