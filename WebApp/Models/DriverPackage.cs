namespace DeployManager.Models;

public class DriverPackage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    // Subfolder name under Drivers/OS/ on the host.
    // The admin places .inf files (and their supporting files) here.
    public string FolderName { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTime Created { get; set; } = DateTime.UtcNow;
}
