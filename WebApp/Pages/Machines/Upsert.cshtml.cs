using DeployManager.Models;
using DeployManager.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DeployManager.Pages.Machines;

public class UpsertModel : PageModel
{
    private readonly DataStore _data;
    private readonly MachineDiscoveryService _discovery;
    private readonly ISettingsService _settings;

    [BindProperty] public Machine Machine { get; set; } = new();
    public bool IsNew => string.IsNullOrEmpty(Request.RouteValues["id"] as string);

    public List<SiteConfig> Sites { get; private set; } = new();

    public UpsertModel(DataStore data, MachineDiscoveryService discovery, ISettingsService settings)
    {
        _data      = data;
        _discovery = discovery;
        _settings  = settings;
    }

    public IActionResult OnGet(string? id)
    {
        Sites = _settings.Get().Sites;
        if (id != null)
        {
            var existing = _data.GetMachineById(id);
            if (existing == null) return NotFound();
            Machine = existing;
        }
        return Page();
    }

    public async Task<IActionResult> OnGetLookupAsync(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return new JsonResult(new { error = "Please enter a hostname or IP address." });

        var (machine, error, warning) = await _discovery.LookupAsync(host);

        if (error != null)
            return new JsonResult(new { error });

        // Auto-select the site from the resolved IP via the configured subnet→site mapping.
        var site = string.IsNullOrEmpty(machine!.IpAddress)
            ? null
            : MachineDiscoveryService.MatchSite(machine.IpAddress, _settings.Get().Sites);

        return new JsonResult(new
        {
            hostname   = machine.Hostname,
            ipAddress  = machine.IpAddress,
            macAddress = machine.MacAddress,
            site       = site?.Name,
            ou         = site?.OU,
            warning
        });
    }

    public IActionResult OnPost()
    {
        Sites = _settings.Get().Sites;

        if (!ModelState.IsValid)
        {
            foreach (var entry in ModelState)
                foreach (var err in entry.Value.Errors)
                    ModelState.AddModelError("", $"{entry.Key}: {err.ErrorMessage}");
            return Page();
        }

        try
        {
            Machine.MacAddress = Models.Machine.NormalizeMac(Machine.MacAddress);
            Machine.Notes ??= "";
            Machine.Model ??= "";
            Machine.SerialNumber ??= "";
            Machine.IpAddress ??= "";
            Machine.OU ??= "";
            _data.SaveMachine(Machine);
            return RedirectToPage("Index");
        }
        catch (Exception ex)
        {
            ModelState.AddModelError("", $"Save failed: {ex.Message}");
            return Page();
        }
    }
}
