namespace DeployManager.Models;

public class SoftwarePackage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    // Ordered list of SoftwareItem IDs
    public List<string> SoftwareIds { get; set; } = new();
    public DateTime Created { get; set; } = DateTime.UtcNow;
}
