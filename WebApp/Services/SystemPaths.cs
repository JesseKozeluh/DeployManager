namespace DeployManager.Services;

/// <summary>
/// Absolute paths to system executables the app shells out to.
///
/// The DeployManager Windows service does not always inherit a PATH that
/// includes %SystemRoot%\System32\WindowsPowerShell\v1.0, so launching
/// "powershell.exe" by name (relying on PATH resolution) can fail with
/// "The system cannot find the file specified". Resolving the absolute path
/// once, from the system directory, avoids depending on the service's PATH.
/// </summary>
public static class SystemPaths
{
    /// <summary>
    /// Full path to Windows PowerShell 5.1 (powershell.exe). Falls back to the
    /// bare name so PATH resolution is still attempted if the expected file is
    /// missing (e.g. a non-standard Windows layout).
    /// </summary>
    public static string PowerShell { get; } = ResolvePowerShell();

    private static string ResolvePowerShell()
    {
        // Environment.SystemDirectory is the 64-bit System32 for a 64-bit process,
        // which is where the inbox Windows PowerShell always lives.
        var candidate = Path.Combine(
            Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(candidate) ? candidate : "powershell.exe";
    }
}
