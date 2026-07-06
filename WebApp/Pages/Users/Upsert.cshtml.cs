using DeployManager.Models;
using DeployManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DeployManager.Pages.Users;

[Authorize(Roles = "Administrator")]
public class UpsertModel : PageModel
{
    private readonly DataStore _data;

    [BindProperty] public AppUser Item { get; set; } = new();
    public bool IsNew => string.IsNullOrEmpty(Request.RouteValues["id"] as string);
    public string[] Roles => AppUser.Roles;

    public UpsertModel(DataStore data) => _data = data;

    public IActionResult OnGet(string? id)
    {
        if (id != null)
        {
            var existing = _data.GetUserById(id);
            if (existing == null) return NotFound();
            Item = existing;
        }
        return Page();
    }

    public IActionResult OnPost()
    {
        Item.FirstName = (Item.FirstName ?? "").Trim();
        Item.LastName  = (Item.LastName  ?? "").Trim();
        Item.Upn       = (Item.Upn       ?? "").Trim();

        if (string.IsNullOrWhiteSpace(Item.Upn))
        {
            ModelState.AddModelError("", "UPN is required.");
            return Page();
        }

        var clash = _data.GetUserByUpn(Item.Upn);
        if (clash != null && clash.Id != Item.Id)
        {
            ModelState.AddModelError("", $"A user with UPN '{Item.Upn}' already exists.");
            return Page();
        }

        if (!AppUser.Roles.Contains(Item.Role)) Item.Role = "Operator";

        _data.SaveUser(Item);
        return RedirectToPage("Index");
    }
}
