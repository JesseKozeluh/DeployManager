namespace DeployManager.Models;

public class SoftwareInstallResult
{
    public string Name     { get; set; } = "";
    public bool   Success  { get; set; }
    // Nullable so a client that reports no/blank exit code (e.g. an older boot image)
    // does not break JSON deserialization of the result callback.
    public int?   ExitCode { get; set; }
    public string Error    { get; set; } = "";
}
