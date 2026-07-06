namespace DeployManager.Models;

public class AppUser
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public string FirstName { get; set; } = "";
    public string LastName  { get; set; } = "";

    // The user's Entra UPN (e.g. jane.smith@contoso.com). Sign-in is matched on this.
    public string Upn { get; set; } = "";

    // Administrator (full access incl. configuration) | Operator (day-to-day deployment, no config)
    public string Role { get; set; } = "Operator";

    public bool Enabled { get; set; } = true;
    public DateTime Created { get; set; } = DateTime.UtcNow;

    public string DisplayName => $"{FirstName} {LastName}".Trim();

    public static readonly string[] Roles = { "Administrator", "Operator" };
}
