namespace DeployManager.Models;

public class WimImage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    // Friendly display name, e.g. "Windows 11 25H2 Pro"
    public string Name { get; set; } = "";

    // Path relative to the images root (no .wim extension, forward slashes),
    // e.g. "Win11Pro/Windows11-25H2-Pro". WinPE downloads /images/<RelativePath>.wim.
    public string RelativePath { get; set; } = "";

    public string Description { get; set; } = "";
    public bool   Enabled     { get; set; } = true;
    public DateTime Created   { get; set; } = DateTime.UtcNow;
}
