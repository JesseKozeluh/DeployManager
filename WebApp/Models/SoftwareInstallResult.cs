namespace DeployManager.Models;

public class SoftwareInstallResult
{
    public string Name     { get; set; } = "";
    public bool   Success  { get; set; }
    public int    ExitCode { get; set; }
    public string Error    { get; set; } = "";
}
