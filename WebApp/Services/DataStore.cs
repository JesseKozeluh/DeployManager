using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DeployManager.Data;
using DeployManager.Hubs;
using DeployManager.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace DeployManager.Services;

public class DataStore
{
    private readonly IDbContextFactory<DeployContext> _ctxFactory;
    private readonly IHubContext<DeployHub>            _hub;
    private readonly ISettingsService                  _settings;
    private readonly IEncryptionService                _enc;
    private readonly ILogger<DataStore>                _log;
    private readonly string _dataPath;
    private readonly string _jobPath;
    private readonly string _httpJobPath;
    private readonly string _imagesPath;
    private readonly string _driversPath;

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented        = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public DataStore(
        IDbContextFactory<DeployContext> ctxFactory,
        IHubContext<DeployHub>           hub,
        IConfiguration                   config,
        ISettingsService                 settings,
        IEncryptionService               enc,
        ILogger<DataStore>               log)
    {
        _ctxFactory  = ctxFactory;
        _hub         = hub;
        _settings    = settings;
        _enc         = enc;
        _log         = log;
        _dataPath    = Environment.ExpandEnvironmentVariables(
                           config["DeployManager:DataPath"]    ?? @"%ProgramData%\DeployManager\data");
        _jobPath     = Path.Combine(_dataPath, "jobs");
        _httpJobPath = Environment.ExpandEnvironmentVariables(
                           config["DeployManager:HttpJobPath"] ?? Path.Combine(_dataPath, "jobs", "http"));
        _imagesPath  = Environment.ExpandEnvironmentVariables(
                           config["DeployManager:ImagesPath"]  ?? @"%ProgramData%\DeployManager\images");
        _driversPath = Environment.ExpandEnvironmentVariables(
                           config["DeployManager:DriversPath"] ?? @"%ProgramData%\DeployManager\drivers");
    }

    private DeployContext Ctx() => _ctxFactory.CreateDbContext();

    private string HttpJobFile(string mac) =>
        Path.Combine(_httpJobPath, mac.Replace(":", "") + ".json");

    // Fire-and-forget SignalR broadcast — called after every job status change.
    // IHubContext is thread-safe and designed for singleton use.
    private void Push(string mac, string status) =>
        _ = _hub.Clients.All.SendAsync("JobUpdated", new { mac, status });

    private static string NormMac(string mac) =>
        mac.Replace(":", "").Replace("-", "").ToUpperInvariant()
           .Chunk(2).Select(c => new string(c))
           .Aggregate((a, b) => a + ":" + b);

    public void EnsureInitialized()
    {
        Directory.CreateDirectory(_dataPath);
        Directory.CreateDirectory(_jobPath);
        Directory.CreateDirectory(_httpJobPath);

        using var ctx = Ctx();
        ctx.Database.EnsureCreated();
        ctx.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        ctx.Database.ExecuteSqlRaw("PRAGMA synchronous=NORMAL;");

        MigrateSchema(ctx);
        MigrateFromJsonIfNeeded(ctx);
        SeedWimsFromDisk(ctx);

        if (OperatingSystem.IsWindows())
        {
            if (!EventLog.SourceExists("DeployManager"))
                EventLog.CreateEventSource("DeployManager", "Application");
        }
    }

    private void MigrateSchema(DeployContext ctx)
    {
        // EnsureCreated() does not alter existing tables when the model changes.
        // Add missing columns here so upgrades from earlier versions work.
        using var cmd = ctx.Database.GetDbConnection().CreateCommand();
        ctx.Database.OpenConnection();
        cmd.CommandText = "PRAGMA table_info(Jobs)";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) columns.Add(reader.GetString(1));

        if (!columns.Contains("JoinMode"))
            ctx.Database.ExecuteSqlRaw("ALTER TABLE Jobs ADD COLUMN JoinMode TEXT NOT NULL DEFAULT 'domain'");
        if (!columns.Contains("Workgroup"))
            ctx.Database.ExecuteSqlRaw("ALTER TABLE Jobs ADD COLUMN Workgroup TEXT NOT NULL DEFAULT 'WORKGROUP'");
        if (!columns.Contains("GroupTag"))
            ctx.Database.ExecuteSqlRaw("ALTER TABLE Jobs ADD COLUMN GroupTag TEXT NOT NULL DEFAULT ''");
        if (!columns.Contains("HardwareHash"))
            ctx.Database.ExecuteSqlRaw("ALTER TABLE Jobs ADD COLUMN HardwareHash TEXT NOT NULL DEFAULT ''");
        if (!columns.Contains("WindowsProductId"))
            ctx.Database.ExecuteSqlRaw("ALTER TABLE Jobs ADD COLUMN WindowsProductId TEXT NOT NULL DEFAULT ''");
        if (!columns.Contains("IntuneStatus"))
            ctx.Database.ExecuteSqlRaw("ALTER TABLE Jobs ADD COLUMN IntuneStatus TEXT NOT NULL DEFAULT ''");
        if (!columns.Contains("BitLockerStatus"))
            ctx.Database.ExecuteSqlRaw("ALTER TABLE Jobs ADD COLUMN BitLockerStatus TEXT NOT NULL DEFAULT ''");
        if (!columns.Contains("BitLockerDetail"))
            ctx.Database.ExecuteSqlRaw("ALTER TABLE Jobs ADD COLUMN BitLockerDetail TEXT NOT NULL DEFAULT ''");
        if (!columns.Contains("LastActivity"))
            ctx.Database.ExecuteSqlRaw("ALTER TABLE Jobs ADD COLUMN LastActivity TEXT NULL");
    }

    private void MigrateFromJsonIfNeeded(DeployContext ctx)
    {
        // Only run if the database is completely empty
        if (ctx.Software.Any() || ctx.Machines.Any() || ctx.Jobs.Any() ||
            ctx.Packages.Any() || ctx.Wims.Any() || ctx.Users.Any() || ctx.Drivers.Any())
            return;

        bool migrated = false;

        migrated |= TryMigrateList<SoftwareItem>(ctx.Software,   Path.Combine(_dataPath, "software.json"));
        migrated |= TryMigrateList<SoftwarePackage>(ctx.Packages, Path.Combine(_dataPath, "packages.json"));
        migrated |= TryMigrateList<Machine>(ctx.Machines,         Path.Combine(_dataPath, "machines.json"));
        migrated |= TryMigrateList<WimImage>(ctx.Wims,            Path.Combine(_dataPath, "wims.json"));
        migrated |= TryMigrateList<AppUser>(ctx.Users,            Path.Combine(_dataPath, "users.json"));
        migrated |= TryMigrateList<DriverPackage>(ctx.Drivers,    Path.Combine(_dataPath, "drivers.json"));

        foreach (var f in Directory.GetFiles(_jobPath, "*.json"))
        {
            try
            {
                var job = JsonSerializer.Deserialize<DeploymentJob>(File.ReadAllText(f), _json);
                if (job != null) { ctx.Jobs.Add(job); migrated = true; }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Skipping unreadable job file {File} during JSON→SQLite migration.", f);
            }
        }

        if (migrated)
        {
            ctx.SaveChanges();
            _log.LogInformation("Migrated existing JSON data files to SQLite database.");
        }
    }

    private bool TryMigrateList<T>(DbSet<T> dbSet, string path) where T : class
    {
        if (!File.Exists(path)) return false;
        try
        {
            var items = JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path), _json);
            if (items == null || items.Count == 0) return false;
            dbSet.AddRange(items);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Skipping unreadable file {Path} during migration.", path);
            return false;
        }
    }

    // ── Software ─────────────────────────────────────────────────────────────

    public List<SoftwareItem> GetSoftware()
    {
        using var ctx = Ctx();
        return ctx.Software.OrderBy(s => s.Name).ToList();
    }

    public SoftwareItem? GetSoftwareById(string id)
    {
        using var ctx = Ctx();
        return ctx.Software.Find(id);
    }

    public void SaveSoftware(SoftwareItem item)
    {
        using var ctx = Ctx();
        ctx.Entry(item).State = ctx.Software.AsNoTracking().Any(s => s.Id == item.Id)
            ? EntityState.Modified : EntityState.Added;
        ctx.SaveChanges();
    }

    public bool DeleteSoftware(string id)
    {
        using var ctx = Ctx();
        var item = ctx.Software.Find(id);
        if (item == null) return false;
        ctx.Software.Remove(item);
        ctx.SaveChanges();
        return true;
    }

    // ── Packages ─────────────────────────────────────────────────────────────

    public List<SoftwarePackage> GetPackages()
    {
        using var ctx = Ctx();
        return ctx.Packages.OrderBy(p => p.Name).ToList();
    }

    public SoftwarePackage? GetPackageById(string id)
    {
        using var ctx = Ctx();
        return ctx.Packages.Find(id);
    }

    public void SavePackage(SoftwarePackage pkg)
    {
        using var ctx = Ctx();
        ctx.Entry(pkg).State = ctx.Packages.AsNoTracking().Any(p => p.Id == pkg.Id)
            ? EntityState.Modified : EntityState.Added;
        ctx.SaveChanges();
    }

    public bool DeletePackage(string id)
    {
        using var ctx = Ctx();
        var item = ctx.Packages.Find(id);
        if (item == null) return false;
        ctx.Packages.Remove(item);
        ctx.SaveChanges();
        return true;
    }

    // ── Driver Packages ──────────────────────────────────────────────────────

    public List<DriverPackage> GetDriverPackages()
    {
        using var ctx = Ctx();
        return ctx.Drivers.OrderBy(d => d.Name).ToList();
    }

    public DriverPackage? GetDriverPackageById(string id)
    {
        using var ctx = Ctx();
        return ctx.Drivers.Find(id);
    }

    public void SaveDriverPackage(DriverPackage pkg)
    {
        using var ctx = Ctx();
        ctx.Entry(pkg).State = ctx.Drivers.AsNoTracking().Any(d => d.Id == pkg.Id)
            ? EntityState.Modified : EntityState.Added;
        ctx.SaveChanges();
    }

    public bool DeleteDriverPackage(string id)
    {
        using var ctx = Ctx();
        var item = ctx.Drivers.Find(id);
        if (item == null) return false;
        ctx.Drivers.Remove(item);
        ctx.SaveChanges();
        return true;
    }

    public int CountDriverInfs(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName)) return 0;
        var dir = Path.Combine(_driversPath, folderName);
        if (!Directory.Exists(dir)) return 0;
        return Directory.GetFiles(dir, "*.inf", SearchOption.AllDirectories).Length;
    }

    // ── Machines ─────────────────────────────────────────────────────────────

    public List<Machine> GetMachines()
    {
        using var ctx = Ctx();
        return ctx.Machines.OrderBy(m => m.Hostname).ThenBy(m => m.MacAddress).ToList();
    }

    public Machine? GetMachineById(string id)
    {
        using var ctx = Ctx();
        return ctx.Machines.Find(id);
    }

    public Machine? GetMachineByMac(string mac)
    {
        using var ctx = Ctx();
        var norm = NormMac(mac);
        return ctx.Machines.FirstOrDefault(m => m.MacAddress == norm);
    }

    public void SaveMachine(Machine machine)
    {
        using var ctx = Ctx();
        ctx.Entry(machine).State = ctx.Machines.AsNoTracking().Any(m => m.Id == machine.Id)
            ? EntityState.Modified : EntityState.Added;
        ctx.SaveChanges();
    }

    public bool DeleteMachine(string id)
    {
        using var ctx = Ctx();
        var item = ctx.Machines.Find(id);
        if (item == null) return false;
        ctx.Machines.Remove(item);
        ctx.SaveChanges();
        return true;
    }

    // ── Jobs ─────────────────────────────────────────────────────────────────

    public List<DeploymentJob> GetAllJobs()
    {
        using var ctx = Ctx();
        return ctx.Jobs.OrderByDescending(j => j.Created).ToList();
    }

    public List<DeploymentJob> GetJobsFiltered(
        string? status, string? site,
        int page, int pageSize,
        out int total)
    {
        using var ctx = Ctx();
        var q = ctx.Jobs.AsQueryable();
        if (!string.IsNullOrEmpty(status)) q = q.Where(j => j.Status == status);
        if (!string.IsNullOrEmpty(site))   q = q.Where(j => j.Site   == site);
        total = q.Count();
        return q.OrderByDescending(j => j.Created)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();
    }

    public bool AnyActiveJobs()
    {
        using var ctx = Ctx();
        return ctx.Jobs.Any(j => j.Status == "pending" || j.Status == "imaging");
    }

    public void DeleteTerminalJobs()
    {
        using var ctx = Ctx();
        var terminal = ctx.Jobs
            .Where(j => j.Status == "complete" || j.Status == "error" || j.Status == "timeout")
            .ToList();
        ctx.Jobs.RemoveRange(terminal);
        ctx.SaveChanges();
    }

    public List<string> GetDistinctJobSites()
    {
        using var ctx = Ctx();
        return ctx.Jobs.Where(j => j.Site != "")
                       .Select(j => j.Site)
                       .Distinct()
                       .OrderBy(s => s)
                       .ToList();
    }

    // Bumps the job's last-activity timestamp. Called by the lightweight /heartbeat
    // callback during long client operations (WIM download, a long installer) so the
    // watchdog can distinguish "still working" from "hung".
    public void Heartbeat(string mac)
    {
        using var ctx = Ctx();
        var norm = NormMac(mac);
        var job = ctx.Jobs.Where(j => j.MacAddress == norm)
                          .OrderByDescending(j => j.Created)
                          .FirstOrDefault();
        if (job == null || job.Status != "imaging") return;
        job.LastActivity = DateTime.UtcNow;
        ctx.SaveChanges();   // deliberately no SignalR push - heartbeats are silent
    }

    // Marks jobs stuck in "imaging" as "timeout". A job times out when it has been
    // INACTIVE (no callback or heartbeat) for longer than inactivityThreshold, or when
    // it exceeds the absolute maxDuration ceiling regardless of activity. Activity-based
    // so a legitimately long-but-progressing deployment is never falsely timed out.
    // Returns the newly timed-out jobs so the caller can send email notifications.
    public List<DeploymentJob> TimeoutStaleJobs(TimeSpan inactivityThreshold, TimeSpan maxDuration)
    {
        using var ctx = Ctx();
        var now              = DateTime.UtcNow;
        var inactivityCutoff = now - inactivityThreshold;
        var maxCutoff        = now - maxDuration;

        var stale = ctx.Jobs
            .Where(j => j.Status == "imaging" && j.Started != null &&
                        (((j.LastActivity ?? j.Started) < inactivityCutoff) || (j.Started < maxCutoff)))
            .ToList();
        if (stale.Count == 0) return stale;

        var mins = (int)inactivityThreshold.TotalMinutes;
        foreach (var job in stale)
        {
            var hitMax = job.Started < maxCutoff;
            job.Status       = "timeout";
            job.ErrorMessage = hitMax
                ? $"Imaging exceeded the {(int)maxDuration.TotalHours}h maximum — job marked as timed out."
                : $"No activity from WinPE/PostInstall for over {mins} minutes — job marked as timed out.";
            job.Completed    = DateTime.UtcNow;
        }
        ctx.SaveChanges();
        return stale;
    }

    public List<DeploymentJob> GetJobsWithHardwareHash()
    {
        using var ctx = Ctx();
        return ctx.Jobs
                  .Where(j => j.HardwareHash != "")
                  .OrderByDescending(j => j.Created)
                  .ToList();
    }

    public DeploymentJob? GetJobByMac(string mac)
    {
        using var ctx = Ctx();
        var norm = NormMac(mac);
        return ctx.Jobs
                  .Where(j => j.MacAddress == norm)
                  .OrderByDescending(j => j.Created)
                  .FirstOrDefault();
    }

    public void SaveJob(DeploymentJob job)
    {
        using var ctx = Ctx();

        List<SoftwareItem> softwareItems = new();
        if (!string.IsNullOrEmpty(job.PackageId))
        {
            var pkg = ctx.Packages.Find(job.PackageId);
            if (pkg != null)
                softwareItems = pkg.SoftwareIds
                    .Select(id => ctx.Software.Find(id))
                    .Where(sw => sw != null)
                    .Select(sw => sw!)
                    .ToList();
        }

        object? driverPackageInfo = null;
        if (!string.IsNullOrEmpty(job.DriverPackageId))
        {
            var drv = ctx.Drivers.Find(job.DriverPackageId);
            if (drv != null && drv.Enabled)
            {
                var serverIp = _settings.Get().ServerIp?.Trim();
                var uncPath  = !string.IsNullOrEmpty(serverIp)
                    ? $@"\\{serverIp}\DeployManagerDrivers\{drv.FolderName}"
                    : null;
                driverPackageInfo = new { drv.Name, UncPath = uncPath };
            }
        }

        // Generate a per-job token on first save; WinPE echoes it back as
        // X-Deploy-Token so the server can reject forged API callbacks.
        if (string.IsNullOrEmpty(job.ApiToken))
            job.ApiToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        var djoinBlob = job.JoinMode == "workgroup" ? null : ProvisionDomainJoin(job.DeviceName, job.OU);

        File.WriteAllText(HttpJobFile(job.MacAddress), JsonSerializer.Serialize(new
        {
            job.MacAddress,
            job.DeviceName,
            job.Site,
            job.OU,
            job.WimName,
            job.PackageId,
            job.DriverPackageId,
            job.Status,
            job.ApiToken,
            job.JoinMode,
            job.Workgroup,
            job.GroupTag,
            DjoinBlob     = djoinBlob ?? "",
            SoftwareItems = softwareItems,
            DriverPackage = driverPackageInfo
        }, _json));

        ctx.Entry(job).State = ctx.Jobs.AsNoTracking().Any(j => j.Id == job.Id)
            ? EntityState.Modified : EntityState.Added;
        ctx.SaveChanges();

        Push(job.MacAddress, job.Status);
    }

    public void DeleteJob(string mac)
    {
        var h = HttpJobFile(mac);
        if (File.Exists(h)) File.Delete(h);

        using var ctx = Ctx();
        var norm = NormMac(mac);
        var job = ctx.Jobs.Where(j => j.MacAddress == norm)
                          .OrderByDescending(j => j.Created)
                          .FirstOrDefault();
        if (job != null)
        {
            ctx.Jobs.Remove(job);
            ctx.SaveChanges();
        }
    }

    public void SetHardwareHash(string mac, string hash, string productId = "")
    {
        using var ctx = Ctx();
        var norm = NormMac(mac);
        var job = ctx.Jobs.Where(j => j.MacAddress == norm)
                          .OrderByDescending(j => j.Created)
                          .FirstOrDefault();
        if (job == null) return;
        job.HardwareHash = hash;
        if (!string.IsNullOrEmpty(productId))
            job.WindowsProductId = productId;
        ctx.SaveChanges();
    }

    public void SetIntuneStatus(string mac, string status)
    {
        using var ctx = Ctx();
        var norm = NormMac(mac);
        var job = ctx.Jobs.Where(j => j.MacAddress == norm)
                          .OrderByDescending(j => j.Created)
                          .FirstOrDefault();
        if (job == null) return;
        job.IntuneStatus = status;
        ctx.SaveChanges();
        Push(norm, job.Status);
    }

    public void SetBitLockerStatus(string mac, string status, string detail)
    {
        using var ctx = Ctx();
        var norm = NormMac(mac);
        var job = ctx.Jobs.Where(j => j.MacAddress == norm)
                          .OrderByDescending(j => j.Created)
                          .FirstOrDefault();
        if (job == null) return;
        job.BitLockerStatus = status;
        job.BitLockerDetail = detail;
        ctx.SaveChanges();
        Push(norm, job.Status);
    }

    public void MarkJobImaging(string mac)
    {
        var h = HttpJobFile(mac);
        if (File.Exists(h)) File.Delete(h);

        using var ctx = Ctx();
        var norm = NormMac(mac);
        var job = ctx.Jobs.Where(j => j.MacAddress == norm)
                          .OrderByDescending(j => j.Created)
                          .FirstOrDefault();
        if (job == null) return;
        job.Status       = "imaging";
        job.Started      = DateTime.UtcNow;
        job.LastActivity = DateTime.UtcNow;
        ctx.SaveChanges();

        Push(job.MacAddress, "imaging");
    }

    public void UpdateCurrentInstall(string mac, string name, int total)
    {
        using var ctx = Ctx();
        var norm = NormMac(mac);
        var job = ctx.Jobs.Where(j => j.MacAddress == norm)
                          .OrderByDescending(j => j.Created)
                          .FirstOrDefault();
        if (job == null) return;
        if (job.Status == "pending")
        {
            job.Status  = "imaging";
            job.Started ??= DateTime.UtcNow;
        }
        job.CurrentInstall = name;
        job.LastActivity   = DateTime.UtcNow;
        if (total > 0) job.SoftwareTotal = total;
        ctx.SaveChanges();
    }

    public void AddSoftwareResult(string mac, SoftwareInstallResult result)
    {
        using var ctx = Ctx();
        var norm = NormMac(mac);
        var job = ctx.Jobs.Where(j => j.MacAddress == norm)
                          .OrderByDescending(j => j.Created)
                          .FirstOrDefault();
        if (job == null) return;
        job.SoftwareResults = job.SoftwareResults.Append(result).ToList();
        job.CurrentInstall  = "";
        job.LastActivity    = DateTime.UtcNow;
        ctx.Entry(job).Property(j => j.SoftwareResults).IsModified = true;
        ctx.SaveChanges();
    }

    public void MarkImagingError(string mac, string message)
    {
        using var ctx = Ctx();
        var norm = NormMac(mac);
        var job = ctx.Jobs.Where(j => j.MacAddress == norm)
                          .OrderByDescending(j => j.Created)
                          .FirstOrDefault();
        if (job == null) return;
        job.Status       = "error";
        job.ErrorMessage = message;
        job.Completed    = DateTime.UtcNow;
        ctx.SaveChanges();

        Push(job.MacAddress, "error");
    }

    public void CompleteJob(string mac, List<SoftwareInstallResult>? results = null)
    {
        using var ctx = Ctx();
        var norm = NormMac(mac);
        var job = ctx.Jobs.Where(j => j.MacAddress == norm)
                          .OrderByDescending(j => j.Created)
                          .FirstOrDefault();
        if (job == null) return;

        if (results?.Count > 0)
        {
            job.SoftwareResults = results;
            ctx.Entry(job).Property(j => j.SoftwareResults).IsModified = true;
        }
        job.CurrentInstall = "";
        job.Completed      = DateTime.UtcNow;
        var failures = job.SoftwareResults.Count(r => !r.Success);
        if (failures > 0)
        {
            job.Status       = "error";
            job.ErrorMessage = $"{failures} software item(s) failed — see details.";
        }
        else
        {
            job.Status       = "complete";
            job.ErrorMessage = "";
        }
        ctx.SaveChanges();

        var finalStatus = job.Status;
        var finalMac    = job.MacAddress;

        var deviceName = job.DeviceName;
        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            var machine = ctx.Machines.FirstOrDefault(m => m.MacAddress == norm);
            if (machine != null)
            {
                machine.Hostname = deviceName;
                ctx.SaveChanges();
            }
        }

        Push(finalMac, finalStatus);
    }

    // ── App users (Entra authorization allowlist) ────────────────────────────

    public List<AppUser> GetUsers()
    {
        using var ctx = Ctx();
        return ctx.Users.OrderBy(u => u.LastName).ThenBy(u => u.FirstName).ToList();
    }

    public AppUser? GetUserById(string id)
    {
        using var ctx = Ctx();
        return ctx.Users.Find(id);
    }

    public AppUser? GetUserByUpn(string upn)
    {
        if (string.IsNullOrWhiteSpace(upn)) return null;
        using var ctx = Ctx();
        var needle = upn.Trim().ToUpperInvariant();
        return ctx.Users.FirstOrDefault(u => u.Upn.ToUpper() == needle);
    }

    public void SaveUser(AppUser user)
    {
        using var ctx = Ctx();
        ctx.Entry(user).State = ctx.Users.AsNoTracking().Any(u => u.Id == user.Id)
            ? EntityState.Modified : EntityState.Added;
        ctx.SaveChanges();
    }

    public bool DeleteUser(string id)
    {
        using var ctx = Ctx();
        var item = ctx.Users.Find(id);
        if (item == null) return false;
        ctx.Users.Remove(item);
        ctx.SaveChanges();
        return true;
    }

    // ── WIM images (managed) ─────────────────────────────────────────────────

    public List<WimImage> GetWims()
    {
        using var ctx = Ctx();
        return ctx.Wims.OrderBy(w => w.Name).ToList();
    }

    public WimImage? GetWimById(string id)
    {
        using var ctx = Ctx();
        return ctx.Wims.Find(id);
    }

    public void SaveWim(WimImage wim)
    {
        using var ctx = Ctx();
        ctx.Entry(wim).State = ctx.Wims.AsNoTracking().Any(w => w.Id == wim.Id)
            ? EntityState.Modified : EntityState.Added;
        ctx.SaveChanges();
    }

    public bool DeleteWim(string id)
    {
        using var ctx = Ctx();
        var item = ctx.Wims.Find(id);
        if (item == null) return false;
        ctx.Wims.Remove(item);
        ctx.SaveChanges();
        return true;
    }

    private void SeedWimsFromDisk(DeployContext ctx)
    {
        if (ctx.Wims.Any()) return;
        foreach (var rel in GetAvailableWims(_imagesPath))
        {
            var name = rel.Contains('/') ? rel.Split('/').Last() : rel;
            ctx.Wims.Add(new WimImage { Name = name, RelativePath = rel, Enabled = true });
        }
        ctx.SaveChanges();
    }

    // ── WIM discovery (filesystem) ───────────────────────────────────────────

    public List<string> GetAvailableWims(string imagesPath)
    {
        if (!Directory.Exists(imagesPath)) return new();
        var root = imagesPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Directory.GetFiles(imagesPath, "*.wim", SearchOption.AllDirectories)
            .Select(f =>
            {
                var rel   = f[root.Length..];
                var noExt = rel[..^4];
                return noExt.Replace(Path.DirectorySeparatorChar, '/');
            })
            .Distinct()
            .OrderBy(n => n)
            .ToList();
    }

    // ── Domain join provisioning ─────────────────────────────────────────────

    private string? ProvisionDomainJoin(string deviceName, string ou)
    {
        if (string.IsNullOrWhiteSpace(deviceName) || string.IsNullOrWhiteSpace(ou))
            return null;

        if (!OperatingSystem.IsWindows())
        {
            _log.LogWarning("Domain-join provisioning is Windows-only — skipping for {Device}.", deviceName);
            return null;
        }

        var s      = _settings.Get();
        var domain = s.DomainFqdn;
        if (string.IsNullOrWhiteSpace(domain))
        {
            _log.LogWarning("Domain FQDN not configured in Settings — skipping provision for {Device}", deviceName);
            return null;
        }

        var saUpn  = s.ServiceAccountUpn ?? "";
        var saPass = string.IsNullOrEmpty(s.ServiceAccountPassword) ? "" : _enc.Unprotect(s.ServiceAccountPassword);
        if (string.IsNullOrWhiteSpace(saUpn) || string.IsNullOrWhiteSpace(saPass))
        {
            _log.LogWarning("Service account not configured in Settings — cannot provision {Device}.", deviceName);
            return null;
        }

        string saUser, saDomain;
        if (saUpn.Contains('\\'))     { saDomain = saUpn.Split('\\')[0]; saUser = saUpn.Split('\\')[1]; }
        else if (saUpn.Contains('@')) { saUser = saUpn.Split('@')[0];   saDomain = saUpn.Split('@')[1]; }
        else                          { saUser = saUpn;                 saDomain = domain; }

        string? pdcName = null;
        try
        {
            var pdcPsi = new ProcessStartInfo(@"C:\Windows\System32\nltest.exe", $"/dsgetdc:{domain} /pdc")
            {
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };
            using var pdcProc = Process.Start(pdcPsi)!;
            var pdcOut = pdcProc.StandardOutput.ReadToEnd();
            pdcProc.WaitForExit(5000);
            var m = System.Text.RegularExpressions.Regex.Match(pdcOut, @"\\\\(\S+)");
            if (m.Success) pdcName = m.Groups[1].Value.Trim();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PDC lookup failed for {Domain} — letting the API auto-locate a DC.", domain);
        }

        var targetOu   = ou;
        var existingOu = FindComputerOu(deviceName, domain, saUpn, saPass);
        if (!string.IsNullOrEmpty(existingOu) && !string.Equals(existingOu, ou, StringComparison.OrdinalIgnoreCase))
        {
            _log.LogInformation("{Device} already exists in AD at {ExistingOu}; provisioning there instead of site OU {SiteOu}.",
                deviceName, existingOu, ou);
            targetOu = existingOu;
        }

        var blob = OfflineDomainJoin.Provision(
            domain, deviceName, targetOu, pdcName, saUser, saDomain, saPass, out var error, out var rc);

        // KB5020276 hardening: some DCs block re-use of an existing computer account
        // (NERR_AccountReuseBlockedByPolicy = 2732) regardless of ownership or the
        // "Allow computer account re-use during domain join" policy — which stalls
        // re-imaging of any machine that already has an AD object. When the operator has
        // opted in, delete the stale object and recreate it (a create has no re-use gate).
        // Opt-in because deleting the object also removes its escrowed BitLocker recovery
        // keys and group memberships.
        if (blob == null
            && rc == OfflineDomainJoin.NERR_AccountReuseBlockedByPolicy
            && s.RecreateComputerAccountOnReuseFailure)
        {
            _log.LogWarning(
                "Account re-use blocked (2732) for {Device} and RecreateComputerAccountOnReuseFailure is enabled — deleting and recreating the computer object.",
                deviceName);
            if (DeleteComputerObject(deviceName, domain, saUpn, saPass, out var delErr))
            {
                blob = OfflineDomainJoin.Provision(
                    domain, deviceName, targetOu, pdcName, saUser, saDomain, saPass, out error, out rc);
                if (blob != null)
                    _log.LogInformation("Recreated computer object for {Device} after re-use block; provisioning succeeded.", deviceName);
            }
            else
            {
                _log.LogWarning("Could not delete existing object for {Device} to recreate it: {Err}", deviceName, delErr);
            }
        }

        if (blob == null)
        {
            _log.LogWarning("Offline domain-join provisioning failed for {Device}: {Error}", deviceName, error);
            return null;
        }

        _log.LogInformation("Provisioned domain-join blob for {Device} as {User}@{SaDomain} (blob length: {Len})",
            deviceName, saUser, saDomain, blob.Length);
        return blob;
    }

    private string? FindComputerOu(string deviceName, string domain, string bindUser, string bindPass)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(deviceName) || string.IsNullOrWhiteSpace(domain))
            return null;
        try
        {
#pragma warning disable CA1416
            using var root = new System.DirectoryServices.DirectoryEntry(
                $"LDAP://{domain}", bindUser, bindPass,
                System.DirectoryServices.AuthenticationTypes.Secure);
            using var ds = new System.DirectoryServices.DirectorySearcher(root)
            {
                Filter   = $"(&(objectCategory=computer)(cn={deviceName}))",
                PageSize = 2
            };
            ds.PropertiesToLoad.Add("distinguishedName");
            var r  = ds.FindOne();
            var dn = (r != null && r.Properties["distinguishedName"].Count > 0)
                        ? r.Properties["distinguishedName"][0]?.ToString() : null;
            if (!string.IsNullOrEmpty(dn))
            {
                var idx = dn.IndexOf(',');
                if (idx > 0) return dn[(idx + 1)..];
            }
#pragma warning restore CA1416
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AD OU lookup failed for {Device} — using the site OU.", deviceName);
        }
        return null;
    }

    // Deletes the existing computer object (and its child objects) so it can be recreated
    // during a re-image. Bound with the service-account credentials — the same account that
    // provisions the join, which already holds delete rights via delegation. Used only when
    // RecreateComputerAccountOnReuseFailure is enabled and the DC blocked account re-use.
    private bool DeleteComputerObject(string deviceName, string domain, string bindUser, string bindPass, out string error)
    {
        error = "";
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(deviceName) || string.IsNullOrWhiteSpace(domain))
        {
            error = "invalid arguments or non-Windows host";
            return false;
        }
        try
        {
#pragma warning disable CA1416
            using var root = new System.DirectoryServices.DirectoryEntry(
                $"LDAP://{domain}", bindUser, bindPass,
                System.DirectoryServices.AuthenticationTypes.Secure);
            using var ds = new System.DirectoryServices.DirectorySearcher(root)
            {
                Filter   = $"(&(objectCategory=computer)(cn={deviceName}))",
                PageSize = 2
            };
            var r = ds.FindOne();
            if (r == null)
            {
                error = "computer object not found";
                return false;
            }
            using var entry = r.GetDirectoryEntry();
            var dn = entry.Properties["distinguishedName"].Value?.ToString();
            entry.DeleteTree();   // removes the object and its child records (incl. BitLocker recovery info)
            _log.LogInformation("Deleted existing computer object {Dn} to recreate it for re-image.", dn);
#pragma warning restore CA1416
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _log.LogWarning(ex, "Failed to delete existing computer object for {Device}.", deviceName);
            return false;
        }
    }
}
