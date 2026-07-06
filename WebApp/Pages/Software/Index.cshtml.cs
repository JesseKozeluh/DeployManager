using DeployManager.Models;
using DeployManager.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DeployManager.Pages.Software;

public class IndexModel : PageModel
{
    private readonly DataStore _data;
    public List<SoftwareItem> Items { get; private set; } = new();

    public IndexModel(DataStore data) => _data = data;

    public void OnGet() => Items = _data.GetSoftware();

    public IActionResult OnPostDelete(string id)
    {
        _data.DeleteSoftware(id);
        return RedirectToPage();
    }
}
