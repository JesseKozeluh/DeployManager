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
    public string GroupTag { get; set; } = "";
    public string HardwareHash { get; set; } = "";
    public string WindowsProductId { get; set; } = "";
    public string IntuneStatus { get; set; } = "";
    // BitLocker post-imaging result: "" (not attempted) | protected | failed | skipped | none
    public string BitLockerStatus { get; set; } = "";
    // Per-volume detail, e.g. "C: protected (escrowed); D: protected (escrowed)"
    public string BitLockerDetail { get; set; } = "";
    // One-time token embedded in the HTTP job file; WinPE must echo it back as
    // X-Deploy-Token on every job API callback (imaging-started, complete, etc.).
    public string ApiToken { get; set; } = "";
    // pending | imaging | complete | error | timeout
    public string Status { get; set; } = "pending";
    public string ErrorMessage { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public DateTime? Started { get; set; }
    public DateTime? Completed { get; set; }
    // Last time WinPE/PostInstall reported progress or a heartbeat while imaging.
    // The watchdog times out on inactivity based on this, not total elapsed time.
    public DateTime? LastActivity { get; set; }
    public List<string> Log { get; set; } = new();
    public string CurrentInstall { get; set; } = "";
    public int SoftwareTotal { get; set; }
    public List<SoftwareInstallResult> SoftwareResults { get; set; } = new();
}
