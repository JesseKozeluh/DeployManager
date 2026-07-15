namespace DeployManager.Models;

public class AppSettings
{
    // ── Server ────────────────────────────────────────────────────────────────
    public string OrgName         { get; set; } = "";
    public string ComputerPrefix  { get; set; } = "";
    public string DeployServerUrl { get; set; } = "";   // http://ip:8080  (plain-HTTP WinPE file server)
    public string ApiServerUrl    { get; set; } = "";   // https://ip:8090 (this app — HTTPS only)
    public string ServerIp        { get; set; } = "";   // for DHCP/TFTP instructions

    // ── Active Directory ─────────────────────────────────────────────────────
    public string DomainFqdn            { get; set; } = "";
    public string ServiceAccountUpn     { get; set; } = "";
    public string ServiceAccountPassword { get; set; } = "";  // AES-256 encrypted
    public string DefaultComputerOU     { get; set; } = "";
    // When true, re-imaging a machine whose AD computer object already exists will DELETE
    // and recreate that object if the domain controller blocks offline account re-use
    // (KB5020276, error 2732). Makes re-imaging work on DCs that refuse re-use, at the cost
    // of the object's escrowed BitLocker recovery keys and group memberships. Off by default
    // (preserves the existing object and only falls back to recreate when re-use is blocked).
    public bool RecreateComputerAccountOnReuseFailure { get; set; } = false;

    // ── WinPE ─────────────────────────────────────────────────────────────────
    public string WinpeLocalAccount  { get; set; } = "";
    public string WinpeLocalPassword { get; set; } = "";  // AES-256 encrypted

    // ── Regional ─────────────────────────────────────────────────────────────
    public string DefaultTimezone { get; set; } = "";  // Windows TZ ID, e.g. "Eastern Standard Time"
    public string DefaultLocale   { get; set; } = "";  // Windows locale tag, e.g. "en-US" or "en-GB"

    // ── Authentication ────────────────────────────────────────────────────────
    // "local" = breakglass only   "entra" = Entra ID + breakglass
    public string AuthMode              { get; set; } = "local";
    public string BreakglassHash        { get; set; } = "";   // BCrypt hash; empty = not yet set
    public bool   BreakglassMustChange  { get; set; } = true; // force change on first login

    // ── Entra ID (only used when AuthMode == "entra") ─────────────────────────
    public string EntraTenantId     { get; set; } = "";
    public string EntraClientId     { get; set; } = "";
    public string EntraClientSecret { get; set; } = "";  // AES-256 encrypted
    public string EntraRequiredGroup { get; set; } = ""; // display name or object ID

    // ── WinPE ─────────────────────────────────────────────────────────────────
    // Path to boot.wim inside the TFTP root. Patched by Settings > Boot Image.
    public string BootWimPath { get; set; } = @"%ProgramData%\DeployManager\tftp\Boot\boot.wim";
    public List<string> WinpeDriverPaths { get; set; } = new();

    // ── Email notifications ──────────────────────────────────────────────────
    public string SmtpHost         { get; set; } = "";
    public int    SmtpPort         { get; set; } = 587;
    public bool   SmtpStartTls     { get; set; } = true;
    public string SmtpUsername     { get; set; } = "";
    public string SmtpPassword     { get; set; } = "";   // AES-256 encrypted
    public string SmtpFrom         { get; set; } = "";
    public string NotifyEmail      { get; set; } = "";
    public bool   NotifyOnComplete { get; set; } = true;
    public bool   NotifyOnError    { get; set; } = true;

    // ── Intune Autopilot ─────────────────────────────────────────────────────
    public bool IntuneAutoRegister { get; set; } = false;

    // ── Update check ─────────────────────────────────────────────────────────
    public string UpdateCheckUrl { get; set; } = "https://api.github.com/repos/JesseKozeluh/DeployManager/releases/latest";

    // ── Sites ─────────────────────────────────────────────────────────────────
    public List<SiteConfig> Sites { get; set; } = new();

    // ── Software delivery ─────────────────────────────────────────────────────
    // When true, PostInstall enables BranchCache Distributed Cache mode on the
    // newly imaged machine and uses BITS (instead of Invoke-WebRequest) for HTTP
    // software package downloads. Subsequent machines at the same site can then
    // serve cached packages to each other, reducing WAN traffic.
    // Requires planning: all WAN sites must have at least one machine online that
    // already has BranchCache enabled before the next machine is imaged.
    public bool EnableBranchCache { get; set; } = false;

    // Maximum minutes a single software installer may run during PostInstall before it is
    // terminated and recorded as a timeout failure. Prevents one hung installer from
    // stalling the whole deployment indefinitely. Falls back to 60 on the client if unset.
    public int SoftwareInstallTimeoutMinutes { get; set; } = 60;

    // ── Job monitoring (watchdog) ─────────────────────────────────────────────
    // A job in "imaging" is marked timed-out if it goes this long with no activity
    // (no status callback or heartbeat from WinPE/PostInstall). Activity-based so a
    // legitimately long-but-progressing deployment is never falsely failed.
    public int JobInactivityTimeoutMinutes { get; set; } = 30;
    // Absolute ceiling: a job in "imaging" is timed out after this long regardless of
    // activity, as a backstop against a client stuck heartbeating forever.
    public int JobMaxDurationMinutes { get; set; } = 480;

    // ── BitLocker (drive encryption after imaging) ────────────────────────────
    public bool   BitLockerEnable           { get; set; } = false;
    // "os" = OS drive only | "osdata" = OS + all fixed data drives | "specific" = named volumes
    public string BitLockerVolumes          { get; set; } = "os";
    // Semicolon-separated volume labels or drive letters (used only when Volumes == "specific")
    public string BitLockerSpecificVolumes  { get; set; } = "";
    // "XtsAes256" (default) or "XtsAes128"
    public string BitLockerEncryptionMethod { get; set; } = "XtsAes256";
    // Encrypt used space only (fast, ideal for a fresh image) vs the full disk
    public bool   BitLockerUsedSpaceOnly    { get; set; } = true;
    // Escrow destinations (any combination). Recovery keys are backed up to each enabled target.
    public bool   BitLockerBackupToAd       { get; set; } = true;   // AD DS (domain-joined)
    // When AD backup is owned by the customer's own BitLocker Group Policy ("Store recovery
    // information in AD DS"), that policy escrows the key at encryption time and refuses the
    // manual cmdlet. Enable this so Deploy Manager skips the manual backup and trusts the GPO.
    public bool   BitLockerAdBackupViaGpo   { get; set; } = false;
    public bool   BitLockerBackupToEntra    { get; set; } = false;  // Entra ID / Intune (BackupToAAD)
    public bool   BitLockerSaveToShare      { get; set; } = false;  // write the key to a secured share
    public string BitLockerSharePath        { get; set; } = "";     // UNC path for save-to-share
    // If no enabled escrow target succeeds, flag the job as failed for operator attention. The
    // drive is left encrypted and functional (never auto-decrypted - decryption would expose data
    // and the drive already has a TPM/auto-unlock protector); the operator escrows or investigates.
    public bool   BitLockerRequireEscrow    { get; set; } = true;

    // ── Setup ─────────────────────────────────────────────────────────────────
    public bool SetupComplete { get; set; } = false;

    // Trims a user-entered API URL and coerces http:// to https://. The API only
    // ever listens on HTTPS (Kestrel, port 8090) — there is no plain-HTTP API
    // listener. An http:// value makes WinPE and PostInstall callbacks fail with
    // "the connection was closed unexpectedly", silently breaking job reporting
    // and Autopilot registration, so normalise it at the point of entry.
    public static string NormalizeApiUrl(string? raw)
    {
        var url = raw?.Trim().TrimEnd('/') ?? "";
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url["http://".Length..];
        return url;
    }
}

public class SiteConfig
{
    public string Name     { get; set; } = "";
    public string Subnet   { get; set; } = "";   // CIDR, e.g. "192.168.20.0/24"
    public string OU       { get; set; } = "";   // full distinguished name
    public string Timezone { get; set; } = "";   // Windows TZ ID; empty = use AppSettings.DefaultTimezone
    public string Locale   { get; set; } = "";   // Windows locale tag; empty = use AppSettings.DefaultLocale
}
