using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DeployManager.Services;

/// <summary>
/// Generates an offline domain-join (ODJ) blob by calling the same Win32 API that
/// djoin.exe uses internally — NetProvisionComputerAccount — while impersonating the
/// configured service account.
///
/// Why impersonation instead of running djoin.exe:
///   The Windows Service may run as SYSTEM or a local account with NO domain identity,
///   so djoin.exe (which uses the caller's process context) cannot write to AD.
///   A child djoin.exe process also would not inherit a thread impersonation token.
///   By calling NetProvisionComputerAccount IN-PROCESS under LogonUser(NEW_CREDENTIALS),
///   the AD writes authenticate as the service account over the network, while the local
///   process stays as-is (the service account needs no local logon right).
///
/// The REUSE_ACCOUNT option makes this idempotent for re-imaging: it resets an existing
/// computer account's password instead of failing — eliminating the djoin /reuse 1354
/// error and the need to delete-and-recreate.
/// </summary>
[SupportedOSPlatform("windows")]
public static class OfflineDomainJoin
{
    // dwOptions flags for NetProvisionComputerAccount
    private const uint NETSETUP_PROVISION_DOWNLEVEL_PRIV_SUPPORT = 0x00000001;
    private const uint NETSETUP_PROVISION_REUSE_ACCOUNT          = 0x00000002;

    // LogonUser
    private const int LOGON32_LOGON_NEW_CREDENTIALS = 9;
    private const int LOGON32_PROVIDER_WINNT50      = 3;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LogonUser(string lpszUsername, string lpszDomain, string lpszPassword,
        int dwLogonType, int dwLogonProvider, out IntPtr phToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ImpersonateLoggedOnUser(IntPtr hToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool RevertToSelf();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetProvisionComputerAccount(
        string lpDomain,
        string lpMachineName,
        string? lpMachineAccountOU,
        string? lpDcName,
        uint dwOptions,
        IntPtr pProvisionBinData,        // PBYTE*  — NULL: we want the text form
        IntPtr pdwProvisionBinDataSize,  // DWORD*  — NULL
        out IntPtr pProvisionTextData);  // LPWSTR* — base64 ODJ blob

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr Buffer);

    /// <summary>
    /// Provisions (or reuses) the computer account and returns the base64 ODJ blob, or
    /// null on failure. <paramref name="error"/> carries a human-readable reason on failure.
    /// </summary>
    public static string? Provision(
        string domain, string machineName, string? machineOu, string? dcName,
        string saUser, string saDomain, string saPassword, out string error)
    {
        error = "";
        IntPtr token = IntPtr.Zero;
        bool impersonating = false;
        try
        {
            if (!LogonUser(saUser, saDomain, saPassword, LOGON32_LOGON_NEW_CREDENTIALS,
                           LOGON32_PROVIDER_WINNT50, out token))
            {
                error = $"LogonUser failed for {saUser}@{saDomain} (Win32 {Marshal.GetLastWin32Error()}).";
                return null;
            }

            if (!ImpersonateLoggedOnUser(token))
            {
                error = $"ImpersonateLoggedOnUser failed (Win32 {Marshal.GetLastWin32Error()}).";
                return null;
            }
            impersonating = true;

            uint options = NETSETUP_PROVISION_DOWNLEVEL_PRIV_SUPPORT | NETSETUP_PROVISION_REUSE_ACCOUNT;
            int rc = NetProvisionComputerAccount(
                domain, machineName,
                string.IsNullOrWhiteSpace(machineOu) ? null : machineOu,
                string.IsNullOrWhiteSpace(dcName)    ? null : dcName,
                options, IntPtr.Zero, IntPtr.Zero, out IntPtr textPtr);

            if (rc != 0)
            {
                // rc is a Win32/NERR code; surface it so callers can log precisely.
                error = $"NetProvisionComputerAccount returned {rc} (0x{rc:X8}).";
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(textPtr);
            }
            finally
            {
                if (textPtr != IntPtr.Zero) NetApiBufferFree(textPtr);
            }
        }
        finally
        {
            if (impersonating) RevertToSelf();
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }
}
