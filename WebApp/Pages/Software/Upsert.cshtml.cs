using DeployManager.Models;
using DeployManager.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DeployManager.Pages.Software;

public class UpsertModel : PageModel
{
    private readonly DataStore _data;

    [BindProperty] public SoftwareItem Item { get; set; } = new();
    public bool IsNew => string.IsNullOrEmpty(Request.RouteValues["id"] as string);

    public UpsertModel(DataStore data) => _data = data;

    public IActionResult OnGet(string? id)
    {
        if (id != null)
        {
            var existing = _data.GetSoftwareById(id);
            if (existing == null) return NotFound();
            Item = existing;
        }
        return Page();
    }

    public IActionResult OnPost()
    {
        if (!ModelState.IsValid) return Page();
        Item.Description ??= "";
        _data.SaveSoftware(Item);
        return RedirectToPage("Index");
    }
}
