using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using DeployManager.Data;
using DeployManager.Hubs;
using DeployManager.Models;
using DeployManager.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

// ── Windows Service hosting ────────────────────────────────────────────────────
// When running as a Windows Service (installed via MSI), UseWindowsService() hands
// lifetime management to the SCM. When run interactively (development), it is a no-op.
var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();

// ── Paths ─────────────────────────────────────────────────────────────────────
// Paths in appsettings.json use %ProgramData% so the MSI installer can write a
// single value that expands correctly at runtime on any machine.
var dataPath    = Environment.ExpandEnvironmentVariables(
                      builder.Configuration["DeployManager:DataPath"]    ?? @"%ProgramData%\DeployManager\data");
var imagesPath  = Environment.ExpandEnvironmentVariables(
                      builder.Configuration["DeployManager:ImagesPath"]  ?? @"%ProgramData%\DeployManager\images");
var httpJobPath = Environment.ExpandEnvironmentVariables(
                      builder.Configuration["DeployManager:HttpJobPath"] ?? Path.Combine(dataPath, "jobs", "http"));
var tftpPath    = Environment.ExpandEnvironmentVariables(
                      builder.Configuration["DeployManager:TftpPath"] ?? @"%ProgramData%\DeployManager\tftp");
var httpPort    = builder.Configuration.GetValue<int>("DeployManager:HttpPort", 8080);

Directory.CreateDirectory(dataPath);
Directory.CreateDirectory(imagesPath);
Directory.CreateDirectory(httpJobPath);

// ── HTTPS certificate ──────────────────────────────────────────────────────────
// Generated once at first startup and persisted to the data directory so it
// survives service restarts. For production, replace with a certificate signed
// by your internal CA via Settings > Certificate.
builder.WebHost.ConfigureKestrel((ctx, opts) =>
{
    // CertificateHolder.Current is updated live by CertificateService — a renewed or
    // replaced certificate is served immediately without restarting the service.
    opts.ConfigureHttpsDefaults(https =>
        https.ServerCertificateSelector = (_, _) => CertificateHolder.Current);
});

// ── Services ──────────────────────────────────────────────────────────────────

builder.Services.AddRazorPages().AddMvcOptions(o =>
    o.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true);

builder.Services.AddSignalR();

// Persist DataProtection keys to disk so antiforgery tokens and auth cookies
// survive service restarts.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataPath, "keys")));

// SQLite database — factory pattern so singleton services can create short-lived contexts.
var dbPath = Path.Combine(dataPath, "deploymgr.db");
builder.Services.AddDbContextFactory<DeployContext>(opts =>
    opts.UseSqlite($"Data Source={dbPath}"));

// Register in dependency order — EncryptionService must be first.
builder.Services.AddSingleton<IEncryptionService, EncryptionService>();
builder.Services.AddSingleton<IAuditService, AuditService>();
builder.Services.AddSingleton<CertificateService>();
builder.Services.AddSingleton<ISettingsService, SettingsService>();
builder.Services.AddSingleton<LocalAuthService>();
builder.Services.AddSingleton<DataStore>();
builder.Services.AddSingleton<WolService>();
builder.Services.AddSingleton<RemoteBootService>();
builder.Services.AddSingleton<MachineDiscoveryService>();
builder.Services.AddSingleton<EmailNotificationService>();
// StartupInitService must be first: hosted services run in registration order,
// and it performs the slow first-run work the others (and Kestrel) depend on.
builder.Services.AddHostedService<StartupInitService>();
builder.Services.AddHostedService<JobWatchdogService>();
builder.Services.AddHttpClient();

// ── Rate limiting (ISM-1390: account lockout / brute-force mitigation) ────────
builder.Services.AddRateLimiter(o =>
{
    // 10 auth attempts per 15 minutes per IP address
    o.AddPolicy("auth-login", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                Window            = TimeSpan.FromMinutes(15),
                PermitLimit       = 10,
                QueueLimit        = 0,
                AutoReplenishment = true
            }));

    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (ctx, _) =>
    {
        ctx.HttpContext.Response.ContentType = "text/plain";
        await ctx.HttpContext.Response.WriteAsync(
            "Too many login attempts. Try again in 15 minutes.");
    };
});

// ── Authentication ────────────────────────────────────────────────────────────
// Cookie holds the session for both breakglass and Entra users. Entra SSO is wired
// only when configured in Settings; breakglass always works.
var startupEnc      = new EncryptionService(builder.Configuration, NullLogger<EncryptionService>.Instance);
var startupSettings = new SettingsService(builder.Configuration, startupEnc).Get();
bool entraEnabled   = string.Equals(startupSettings.AuthMode, "entra", StringComparison.OrdinalIgnoreCase)
                      && !string.IsNullOrWhiteSpace(startupSettings.EntraTenantId)
                      && !string.IsNullOrWhiteSpace(startupSettings.EntraClientId)
                      && !string.IsNullOrWhiteSpace(startupSettings.EntraClientSecret);

var authBuilder = builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, o =>
    {
        o.Cookie.Name         = LocalAuthService.CookieName;
        o.Cookie.HttpOnly     = true;
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        o.Cookie.SameSite     = SameSiteMode.Lax;
        o.LoginPath           = LocalAuthService.LoginPath;
        o.AccessDeniedPath    = "/auth/denied";
        o.Cookie.MaxAge       = null;
        o.ExpireTimeSpan      = TimeSpan.FromMinutes(30);
        o.SlidingExpiration   = true;
    });

if (entraEnabled)
{
    authBuilder.AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, o =>
    {
        o.Authority             = $"https://login.microsoftonline.com/{startupSettings.EntraTenantId}/v2.0";
        o.ClientId              = startupSettings.EntraClientId;
        o.ClientSecret          = startupSettings.EntraClientSecret;
        o.ResponseType          = "code";
        o.UsePkce               = true;
        o.SaveTokens            = false;
        o.CallbackPath          = "/signin-oidc";
        o.SignedOutCallbackPath = "/signout-callback-oidc";
        o.SignInScheme          = CookieAuthenticationDefaults.AuthenticationScheme;
        o.Scope.Clear(); o.Scope.Add("openid"); o.Scope.Add("profile"); o.Scope.Add("email");
        o.TokenValidationParameters.NameClaimType = "preferred_username";

        o.Events = new OpenIdConnectEvents
        {
            OnTokenValidated = ctx =>
            {
                var data  = ctx.HttpContext.RequestServices.GetRequiredService<DataStore>();
                var audit = ctx.HttpContext.RequestServices.GetRequiredService<IAuditService>();
                var ip    = ctx.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                var upn   = ctx.Principal?.FindFirst("preferred_username")?.Value
                            ?? ctx.Principal?.FindFirst(ClaimTypes.Upn)?.Value
                            ?? ctx.Principal?.FindFirst(ClaimTypes.Email)?.Value ?? "";

                var user = data.GetUserByUpn(upn);
                if (user == null || !user.Enabled)
                {
                    audit.Log(new AuditEvent("AUTH_ENTRA_DENIED",
                        string.IsNullOrEmpty(upn) ? "unknown" : upn,
                        false, "UPN not registered or disabled", ip));
                    ctx.Fail("not-authorized");
                    return Task.CompletedTask;
                }

                // Replace the token principal with a minimal app identity (name + role only).
                // No raw Entra/token claims are carried into the session cookie.
                var claims = new[]
                {
                    new Claim(ClaimTypes.Name, user.Upn),
                    new Claim("displayName",   user.DisplayName),
                    new Claim(ClaimTypes.Role, user.Role)
                };
                ctx.Principal = new ClaimsPrincipal(
                    new ClaimsIdentity(claims, ctx.Scheme.Name, ClaimTypes.Name, ClaimTypes.Role));
                ctx.Properties!.IsPersistent = false;

                audit.Log(new AuditEvent("AUTH_ENTRA_SUCCESS", user.Upn, true, $"Role={user.Role}", ip));
                return Task.CompletedTask;
            },
            OnRemoteFailure = ctx =>
            {
                ctx.Response.Redirect("/auth/denied");
                ctx.HandleResponse();
                return Task.CompletedTask;
            }
        };
    });
}

// Secure by default: every endpoint requires authentication unless explicitly
// marked [AllowAnonymous].
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // ReExecute, NOT WithRedirects: it renders the NotFound page body while
    // PRESERVING the original status code. WithRedirects turned every error
    // (including antiforgery 400s on fetch POSTs) into a 302 -> /NotFound -> 200,
    // making failed API calls look successful to client-side JS, and created a
    // redirect loop on the port-8080 file server where /NotFound is itself a 404.
    app.UseStatusCodePagesWithReExecute("/NotFound");
}

// ── HTTP file server (port 8080) ──────────────────────────────────────────────
// Plain-HTTP listener for WinPE clients (no auth, no HTTPS).
// Serves two roots only — never reaches the auth or
// security-header stack, so WinPE can download files without a session.
//
//   GET /jobs/{mac}.json   → data/jobs/http/{mac}.json   (polled by WinPE every 30 s)
//   GET /boot/{script}     → scripts/{script}               (staging scripts for Secure Boot)
//   GET /images/{path}     → images/{path}                 (WIM download during imaging)
//
// Path traversal is prevented by verifying the resolved path stays inside its root.
app.Use(async (ctx, next) =>
{
    if (ctx.Connection.LocalPort != httpPort)
    {
        await next();
        return;
    }

    var reqPath = ctx.Request.Path.Value?.TrimStart('/') ?? "";
    string? filePath = null;

    if (reqPath.StartsWith("jobs/", StringComparison.OrdinalIgnoreCase))
    {
        // /jobs/{mac}.json — HTTP job file polled by WinPE
        var fileName = reqPath["jobs/".Length..];
        if (!fileName.Contains('/') && fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            filePath = Path.Combine(httpJobPath, fileName);
    }
    else if (reqPath.Equals("boot.ipxe", StringComparison.OrdinalIgnoreCase))
    {
        // /boot.ipxe — iPXE boot script generated from current server settings.
        // iPXE fetches this after the tiny TFTP chainload, then loads WinPE files over HTTP.
        var s       = ctx.RequestServices.GetRequiredService<ISettingsService>().Get();
        var apiUrl  = s.ApiServerUrl ?? "";
        string httpBase;
        try
        {
            var uri = new Uri(apiUrl);
            httpBase = $"http://{uri.Host}:{httpPort}/winpe";
        }
        catch
        {
            httpBase = $"http://{ctx.Connection.LocalIpAddress}:{httpPort}/winpe";
        }

        var script =
            "#!ipxe\n" +
            $"# Deploy Manager iPXE boot script - generated {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC\n" +
            "\n" +
            $"set base {httpBase}\n" +
            "\n" +
            $"kernel ${{base}}/wimboot\n" +
            $"initrd --name BCD      ${{base}}/Boot/BCD\n" +
            $"initrd --name boot.sdi ${{base}}/Boot/boot.sdi\n" +
            $"initrd --name boot.wim ${{base}}/Boot/boot.wim\n" +
            "boot\n";

        ctx.Response.ContentType = "text/plain";
        await ctx.Response.WriteAsync(script);
        return;
    }
    else if (reqPath.StartsWith("winpe/", StringComparison.OrdinalIgnoreCase) ||
             reqPath.Equals("winpe",    StringComparison.OrdinalIgnoreCase))
    {
        // /winpe/{path} — serves WinPE boot files from the TFTP root over HTTP.
        // iPXE uses this to download boot.wim, BCD, boot.sdi, and wimboot at LAN speed
        // rather than the TFTP rate cap (~100 KB/s typical on PXE stacks).
        var sub      = reqPath.Length > "winpe/".Length ? reqPath["winpe/".Length..] : "";
        var tftpRoot = Path.GetFullPath(tftpPath) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(Path.Combine(tftpPath, sub));
        if (resolved.StartsWith(tftpRoot, StringComparison.OrdinalIgnoreCase))
            filePath = resolved;
    }
    else if (reqPath.StartsWith("boot/", StringComparison.OrdinalIgnoreCase))
    {
        // /boot/{script} — staging scripts downloaded by the orchestrator on target machines
        var fileName    = reqPath["boot/".Length..];
        var scriptsRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "scripts"));
        var resolved    = Path.GetFullPath(Path.Combine(scriptsRoot, fileName));
        if (resolved.StartsWith(scriptsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && resolved.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            filePath = resolved;
        }
    }
    else if (reqPath.StartsWith("images/", StringComparison.OrdinalIgnoreCase))
    {
        // /images/{path} → deployment WIM / driver package downloads
        var sub      = reqPath["images/".Length..];
        var root     = Path.GetFullPath(imagesPath) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(Path.Combine(imagesPath, sub));
        if (resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            filePath = resolved;
    }
    else if (!string.IsNullOrEmpty(reqPath))
    {
        // Bare path fallback (e.g. /Win11Pro/install.wim without /images/ prefix)
        var resolved = Path.GetFullPath(Path.Combine(imagesPath, reqPath));
        var root     = Path.GetFullPath(imagesPath) + Path.DirectorySeparatorChar;
        if (resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            filePath = resolved;
    }

    if (filePath != null && File.Exists(filePath))
    {
        var fi = new FileInfo(filePath);
        ctx.Response.ContentType = "application/octet-stream";
        ctx.Response.ContentLength = fi.Length;
        await ctx.Response.SendFileAsync(filePath);
        return;
    }

    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
});

// ── Security headers (ISO 27001 A.14.1.2, ISM-1267) ──────────────────────────
// Applied only on the HTTPS port — the HTTP file server short-circuited above.
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Frame-Options"]           = "DENY";
    h["X-Content-Type-Options"]    = "nosniff";
    h["X-XSS-Protection"]          = "1; mode=block";
    h["Referrer-Policy"]           = "strict-origin-when-cross-origin";
    h["Permissions-Policy"]        = "geolocation=(), camera=(), microphone=(), usb=()";
    h["Content-Security-Policy"]   =
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "font-src 'self'; " +
        "frame-ancestors 'none'";
    h["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
    await next();
});

app.UseRateLimiter();
app.UseStaticFiles();

var docsDir = Path.Combine(AppContext.BaseDirectory, "docs");
if (Directory.Exists(docsDir))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(docsDir),
        RequestPath  = "/docs"
    });
}

app.UseRouting();
app.UseAuthentication();

// ── Setup-wizard redirect ─────────────────────────────────────────────────────
// Runs before authorization so a brand-new install can reach the anonymous setup
// page without being challenged to log in first.
app.Use(async (ctx, next) =>
{
    var settings = ctx.RequestServices.GetRequiredService<ISettingsService>();
    var path     = ctx.Request.Path.Value ?? "";
    var skip     = path.StartsWith("/auth")                  ||
                   path.StartsWith("/setup")                 ||
                   path.StartsWith("/signin-oidc")           ||
                   path.StartsWith("/signout-callback-oidc") ||
                   path.StartsWith("/_framework")            ||
                   path.StartsWith("/lib")                   ||
                   path.StartsWith("/api")                   ||
                   path.StartsWith("/docs")                  ||
                   path == "/favicon.ico"                    ||
                   path == "/health"                         ||
                   path == "/auth/refresh"                   ||
                   path == "/NotFound"                       ||
                   path == "/Auth/Checklist";
    if (!skip && !settings.IsSetupComplete())
    {
        ctx.Response.Redirect("/auth/setup");
        return;
    }
    await next();
});

app.UseAuthorization();

app.MapRazorPages().RequireRateLimiting("auth-login");
app.MapHub<DeployHub>("/deploy-hub").RequireAuthorization();

// First-run initialisation (database creation, certificate load/generation)
// happens in StartupInitService, NOT here. Anything before app.Run() executes
// before the Windows service has reported to the SCM — on a cold machine that
// blows the SCM's 30-second start window and the service is killed with
// error 1053 even though it is starting normally.

var _jsonOpts = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };

var api = app.MapGroup("/api").AllowAnonymous();

// Token-protected group: all /api/jobs/{mac}/* endpoints require X-Deploy-Token.
// The token is generated when the job is created and embedded in the HTTP job file
// served to WinPE over port 8080. Any device that can fake a MAC but cannot read
// that file cannot forge imaging-started / complete / error callbacks.
var jobApi = app.MapGroup("/api/jobs/{mac}").AllowAnonymous()
    .AddEndpointFilter(async (ctx, next) =>
    {
        var mac   = ctx.HttpContext.GetRouteValue("mac")?.ToString() ?? "";
        var token = ctx.HttpContext.Request.Headers["X-Deploy-Token"].FirstOrDefault();
        if (string.IsNullOrEmpty(token))
            return Results.Unauthorized();
        var data = ctx.HttpContext.RequestServices.GetRequiredService<DataStore>();
        var job  = data.GetJobByMac(Machine.NormalizeMac(mac));
        if (job == null || !string.Equals(job.ApiToken, token, StringComparison.Ordinal))
            return Results.Unauthorized();
        return await next(ctx);
    });

// ── /api/deploy-config ────────────────────────────────────────────────────────
// Serves WinPE configuration as a PowerShell variable block.
// SECURITY: Unauthenticated — must never expose credentials. WinpeLocalPassword
//           is intentionally excluded; it is baked into boot.wim by the setup wizard.
api.MapGet("/deploy-config", (ISettingsService settings) =>
{
    var s     = settings.Get();
    var sites = string.Join("\n    ", s.Sites.Select(site =>
        $"[PSCustomObject]@{{ Name = '{Escape(site.Name)}'; Subnet = '{Escape(site.Subnet)}'; " +
        $"OU = '{Escape(site.OU)}'; " +
        $"Timezone = '{Escape(string.IsNullOrEmpty(site.Timezone) ? s.DefaultTimezone : site.Timezone)}'; " +
        $"Locale = '{Escape(string.IsNullOrEmpty(site.Locale) ? s.DefaultLocale : site.Locale)}' }}"));

    var ps = $@"# Auto-generated by DeployManager — do not edit manually.
# Generated: {DateTimeOffset.UtcNow:O}
$global:DeployServer      = '{Escape(s.DeployServerUrl)}'
$global:ApiServer         = '{Escape(s.ApiServerUrl)}'
$global:Domain            = '{Escape(s.DomainFqdn)}'
$global:OrgName           = '{Escape(s.OrgName)}'
$global:ComputerPrefix    = '{Escape(s.ComputerPrefix)}'
$global:DefaultTimezone   = '{Escape(s.DefaultTimezone)}'
$global:DefaultLocale     = '{Escape(s.DefaultLocale)}'
$global:WinpeLocalAccount = '{Escape(s.WinpeLocalAccount)}'
$global:EnableBranchCache = ${(s.EnableBranchCache ? "true" : "false")}
$global:SiteConfig = @(
    {sites}
)
";
    return Results.Content(ps, "text/plain");
});

// ── Deployment API endpoints — token-protected ───────────────────────────────

jobApi.MapPost("/software/start", async (string mac, HttpContext ctx, DataStore data) =>
{
    var body = await System.Text.Json.JsonSerializer.DeserializeAsync<SoftwareStartReport>(ctx.Request.Body, _jsonOpts);
    data.UpdateCurrentInstall(mac, body?.Name ?? "", body?.Total ?? 0);
    return Results.Ok("ok");
});

jobApi.MapPost("/software/result", async (string mac, HttpContext ctx, DataStore data) =>
{
    var result = await System.Text.Json.JsonSerializer.DeserializeAsync<SoftwareInstallResult>(ctx.Request.Body, _jsonOpts);
    if (result != null) data.AddSoftwareResult(mac, result);
    return Results.Ok("ok");
});

jobApi.MapPost("/complete", async (string mac, HttpContext ctx, DataStore data, EmailNotificationService email) =>
{
    var body = await System.Text.Json.JsonSerializer.DeserializeAsync<PostInstallReport>(ctx.Request.Body, _jsonOpts);
    data.CompleteJob(mac, body?.Results);
    var job = data.GetJobByMac(Machine.NormalizeMac(mac));
    if (job != null) _ = email.SendJobCompleteAsync(job);
    return Results.Ok("ok");
});

jobApi.MapPost("/imaging-started", (string mac, DataStore data) =>
{
    data.MarkJobImaging(mac);
    return Results.Ok("ok");
});

api.MapPost("/discover", async (HttpContext ctx, DataStore data) =>
{
    var body = await System.Text.Json.JsonSerializer.DeserializeAsync<DiscoverReport>(ctx.Request.Body, _jsonOpts);
    if (body == null || string.IsNullOrWhiteSpace(body.Mac))
        return Results.BadRequest("mac is required");

    var normalizedMac = Machine.NormalizeMac(body.Mac);
    var machine       = data.GetMachineByMac(normalizedMac) ?? new Machine { MacAddress = normalizedMac };
    if (!string.IsNullOrWhiteSpace(body.Ip))     machine.IpAddress    = body.Ip.Trim();
    if (!string.IsNullOrWhiteSpace(body.Model))  machine.Model        = body.Model.Trim();
    if (!string.IsNullOrWhiteSpace(body.Serial)) machine.SerialNumber = body.Serial.Trim();
    machine.DiscoveredAt = DateTime.UtcNow;
    data.SaveMachine(machine);

    var job        = data.GetJobByMac(normalizedMac);
    var jobPending = job != null && job.Status == "pending";
    return Results.Ok(new { jobPending, machineId = machine.Id });
});

jobApi.MapPost("/imaging-error", async (string mac, HttpContext ctx, DataStore data, EmailNotificationService email) =>
{
    var body = await System.Text.Json.JsonSerializer.DeserializeAsync<ImagingErrorReport>(ctx.Request.Body, _jsonOpts);
    data.MarkImagingError(mac, body?.Message ?? "Unknown imaging error.");
    var job = data.GetJobByMac(Machine.NormalizeMac(mac));
    if (job != null) _ = email.SendJobErrorAsync(job);
    return Results.Ok("ok");
});

// ── /auth/refresh ─────────────────────────────────────────────────────────────
// Authenticated keep-alive: called by the session-timeout toast to slide the
// cookie without navigating away. Returns 200 if still authenticated; the auth
// middleware returns 401/302 if the session has already expired.
app.MapGet("/auth/refresh", () => Results.Ok("ok")).RequireAuthorization();

// ── /health ───────────────────────────────────────────────────────────────────
// Unauthenticated JSON health check. Useful for monitoring and the tray app.
app.MapGet("/health", (IConfiguration config) =>
{
    var dataPath2 = Environment.ExpandEnvironmentVariables(
        config["DeployManager:DataPath"]   ?? @"%ProgramData%\DeployManager\data");
    var imgPath2  = Environment.ExpandEnvironmentVariables(
        config["DeployManager:ImagesPath"] ?? @"%ProgramData%\DeployManager\images");

    long? diskFreeGb = null;
    try
    {
        var driveRoot = Path.GetPathRoot(imgPath2) ?? "C:\\";
        diskFreeGb    = new DriveInfo(driveRoot).AvailableFreeSpace / 1_073_741_824L;
    }
    catch { }

    var cert         = CertificateHolder.Current;
    var certDaysLeft = cert != null ? (int)(cert.NotAfter - DateTime.Now).TotalDays : (int?)null;
    var dataOk       = Directory.Exists(dataPath2);

    return Results.Json(new
    {
        status  = dataOk ? "healthy" : "degraded",
        version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
        certificate = new
        {
            subject       = cert?.Subject,
            expires       = cert?.NotAfter,
            daysRemaining = certDaysLeft
        },
        storage = new
        {
            dataPath         = dataPath2,
            dataAccessible   = dataOk,
            imagesDiskFreeGb = diskFreeGb
        },
        timestamp = DateTimeOffset.UtcNow
    });
}).AllowAnonymous();

app.Run();

// ── Helpers ───────────────────────────────────────────────────────────────────

static string Escape(string s) => s.Replace("'", "''");

record PostInstallReport(List<SoftwareInstallResult>? Results);
record SoftwareStartReport(string Name, int Total);
record ImagingErrorReport(string Message);
record DiscoverReport(string Mac, string Ip, string Model, string Serial);

/// <summary>
/// Performs slow first-run initialisation (SQLite database creation, TLS
/// certificate load/generation) as the first hosted service. By the time
/// hosted services start, the Windows service lifetime has already connected
/// to the SCM, so this work no longer counts against the SCM's 30-second
/// service-start timeout. It still completes before Kestrel accepts requests,
/// because the web host is always the last hosted service to start.
/// </summary>
sealed class StartupInitService : IHostedService
{
    private readonly IServiceProvider _services;
    public StartupInitService(IServiceProvider services) => _services = services;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _services.GetRequiredService<DataStore>().EnsureInitialized();
        _services.GetRequiredService<CertificateService>();   // ctor loads or generates the TLS cert
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
