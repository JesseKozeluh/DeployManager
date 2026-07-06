using System.Net.Sockets;
using DeployManager.Models;
using DeployManager.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DeployManager.Pages.Machines;

public class IndexModel : PageModel
{
    private readonly DataStore _data;
    private readonly WolService _wol;

    public List<Machine> Machines { get; private set; } = new();
    public Dictionary<string, DeploymentJob> ActiveJobs { get; private set; } = new();

    public IndexModel(DataStore data, WolService wol) { _data = data; _wol = wol; }

    public void OnGet()
    {
        Machines = _data.GetMachines().OrderBy(m => m.Hostname).ToList();
        ActiveJobs = _data.GetAllJobs()
            .Where(j => j.Status is "pending" or "imaging" or "post-install")
            .GroupBy(j => j.MacAddress)
            .ToDictionary(g => g.Key, g => g.First());
    }

    // Called once on page load — pings all machines in parallel, returns {id: bool}
    public async Task<JsonResult> OnGetStatusAsync()
    {
        var machines = _data.GetMachines();
        var tasks = machines.Select(async m => new
        {
            id     = m.Id,
            online = await IsOnlineAsync(m.IpAddress)
        });
        var results = await Task.WhenAll(tasks);
        return new JsonResult(results.ToDictionary(r => r.id, r => r.online));
    }

    // Sends WoL magic packet for one machine
    public JsonResult OnGetWake(string id)
    {
        var machine = _data.GetMachineById(id);
        if (machine == null) return new JsonResult(new { error = "Not found" });
        var sent = _wol.Send(machine.MacAddress, machine.IpAddress);
        return new JsonResult(new { sent });
    }

    // Checks online status for a single machine — used for manual refresh and post-WoL polling
    public async Task<JsonResult> OnGetCheckAsync(string id)
    {
        var machine = _data.GetMachineById(id);
        if (machine == null) return new JsonResult(new { error = "Not found" });
        var online = await IsOnlineAsync(machine.IpAddress);
        return new JsonResult(new { online });
    }

    public IActionResult OnPostDelete(string id)
    {
        _data.DeleteMachine(id);
        return RedirectToPage();
    }

    // TCP connect to port 445 (SMB) — fast, reliable indicator on domain machines.
    // Times out after 1 s so the parallel page-load scan completes quickly.
    private static async Task<bool> IsOnlineAsync(string? ip, int timeoutMs = 1000)
    {
        if (string.IsNullOrWhiteSpace(ip)) return false;
        try
        {
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(timeoutMs);
            await tcp.ConnectAsync(ip.Trim(), 445, cts.Token);
            return true;
        }
        catch { return false; }
    }
}
