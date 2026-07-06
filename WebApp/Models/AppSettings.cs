namespace DeployManager.Models;

public class AppSettings
{
    // ── Server ────────────────────────────────────────────────────────────────
    public string OrgName         { get; set; } = "";
    public string ComputerPrefix  { get; set; } = "";
    public string DeployServerUrl { get; set; } = "";   // http://ip:8080  (IIS/Nginx static)
    public string ApiServerUrl    { get; set; } = "";   // http://ip:8090  (this app)
    public string ServerIp        { get; set; } = "";   // for DHCP/TFTP instructions

    // ── Active Directory ─────────────────────────────────────────────────────
    public string DomainFqdn            { get; set; } = "";
    public string ServiceAccountUpn     { get; set; } = "";
    public string ServiceAccountPassword { get; set; } = "";  // AES-256 encrypted
    public string DefaultComputerOU     { get; set; } = "";

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

    // ── Setup ─────────────────────────────────────────────────────────────────
    public bool SetupComplete { get; set; } = false;
}

public class SiteConfig
{
    public string Name     { get; set; } = "";
    public string Subnet   { get; set; } = "";   // CIDR, e.g. "192.168.20.0/24"
    public string OU       { get; set; } = "";   // full distinguished name
    public string Timezone { get; set; } = "";   // Windows TZ ID; empty = use AppSettings.DefaultTimezone
    public string Locale   { get; set; } = "";   // Windows locale tag; empty = use AppSettings.DefaultLocale
}
