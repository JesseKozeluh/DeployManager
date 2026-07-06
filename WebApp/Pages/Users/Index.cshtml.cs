using DeployManager.Models;
using DeployManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DeployManager.Pages.Users;

[Authorize(Roles = "Administrator")]
public class IndexModel : PageModel
{
    private readonly DataStore _data;
    public List<AppUser> Items { get; private set; } = new();

    public IndexModel(DataStore data) => _data = data;

    public void OnGet() => Items = _data.GetUsers().OrderBy(u => u.Upn).ToList();

    public IActionResult OnPostDelete(string id)
    {
        _data.DeleteUser(id);
        return RedirectToPage();
    }
}
