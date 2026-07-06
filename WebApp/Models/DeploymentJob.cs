namespace DeployManager.Models;

public class DeploymentJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string MacAddress { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string Site { get; set; } = "";
    public string OU { get; set; } = "";
    public string WimName { get; set; } = "";
    public string PackageId { get; set; } = "";
    public string DriverPackageId { get; set; } = "";
    // "domain" (default) = offline domain join via ODJ blob
    // "workgroup" = join a workgroup instead (for Autopilot / Intune scenarios)
    public string JoinMode { get; set; } = "domain";
    public string Workgroup { get; set; } = "WORKGROUP";
    // One-time token embedded in the HTTP job file; WinPE must echo it back as
    // X-Deploy-Token on every job API callback (imaging-started, complete, etc.).
    public string ApiToken { get; set; } = "";
    // pending | imaging | complete | error | timeout
    public string Status { get; set; } = "pending";
    public string ErrorMessage { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public DateTime? Started { get; set; }
    public DateTime? Completed { get; set; }
    public List<string> Log { get; set; } = new();
    public string CurrentInstall { get; set; } = "";
    public int SoftwareTotal { get; set; }
    public List<SoftwareInstallResult> SoftwareResults { get; set; } = new();
}
