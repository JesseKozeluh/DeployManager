namespace DeployManager.Models;

public class SoftwareItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    // MSI | EXE | Script
    public string InstallerType { get; set; } = "MSI";
    // HTTP path relative to deploy server (/software/app/file.msi)
    // OR UNC path (\\server\share\app\file.msi) — run directly, no download
    public string InstallerPath { get; set; } = "";
    // Optional working directory (UNC or local). When blank:
    //   UNC installer → parent directory of the installer
    //   HTTP installer → %TEMP%
    public string WorkingDirectory { get; set; } = "";
    // {installer} is replaced at runtime with the resolved installer path
    public string InstallCommand { get; set; } = "msiexec /i \"{installer}\" /qn /norestart";
    public DateTime Created { get; set; } = DateTime.UtcNow;
}
