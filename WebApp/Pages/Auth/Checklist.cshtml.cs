using DeployManager.Models;
using DeployManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DeployManager.Pages.Auth;

[AllowAnonymous]
public class ChecklistModel : PageModel
{
    private readonly ISettingsService _settings;
    private readonly DataStore        _data;

    public record CheckItem(string Label, bool Done, string? ActionUrl, string? ActionLabel, string? Note);
    public List<CheckItem> Items   { get; private set; } = new();
    public bool            AllDone { get; private set; }

    public ChecklistModel(ISettingsService settings, DataStore data)
    {
        _settings = settings;
        _data     = data;
    }

    public void OnGet()
    {
        var s = _settings.Get();

        Items = new List<CheckItem>
        {
            new("Initial setup complete",
                Done:        true,
                ActionUrl:   null,
                ActionLabel: null,
                Note:        null),

            new("At least one site configured (subnet → OU mapping)",
                Done:        s.Sites.Any(),
                ActionUrl:   "/Settings/Sites",
                ActionLabel: "Configure Sites",
                Note:        "Sites control which Active Directory OU machines are joined to based on their IP subnet."),

            new("Service account configured for domain join",
                Done:        !string.IsNullOrWhiteSpace(s.ServiceAccountUpn) && !string.IsNullOrWhiteSpace(s.ServiceAccountPassword),
                ActionUrl:   "/Settings/Index",
                ActionLabel: "Open Settings",
                Note:        "Required for djoin to pre-stage computer objects in AD before imaging. Not needed if you only deploy in Workgroup (Autopilot) mode."),

            new("WIM image registered",
                Done:        _data.GetWims().Any(),
                ActionUrl:   "/Images/Index",
                ActionLabel: "Add WIM Image",
                Note:        "Add a Windows WIM file that PXE-booted machines will download and apply."),

            new("DHCP PXE options configured",
                Done:        false,
                ActionUrl:   null,
                ActionLabel: null,
                Note:        "Manual step: set DHCP option 66 (TFTP server) to this server's IP, and option 67 (boot file) to 'ipxe-shim.efi' (UEFI, Secure Boot safe) or 'undionly.kpxe' (BIOS), plus the iPXE user-class rule. Full steps are in the Setup Guide (docs\\index.html in the install directory)."),

            new("boot.wim patched with server URL and WinPE password",
                Done:        BootWimPatched(s),
                ActionUrl:   "/Settings/BootImage",
                ActionLabel: "Patch Boot Image",
                Note:        "Run Settings > Boot Image after configuring the API Server URL and WinPE password. Re-run whenever either changes."),

            new("PXE boot tested — a machine has appeared in Machines",
                Done:        _data.GetMachines().Any(),
                ActionUrl:   "/Machines/Index",
                ActionLabel: "View Machines",
                Note:        "Boot any machine on the network to PXE. It should appear in the Machines list within a minute."),
        };

        AllDone = Items.All(i => i.Done);
    }

    private bool BootWimPatched(AppSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.BootWimPath)) return false;
        var path = Environment.ExpandEnvironmentVariables(s.BootWimPath);
        return System.IO.File.Exists(path);
    }
}
