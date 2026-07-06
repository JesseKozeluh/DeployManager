using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DeployManager.Pages.Audit;

public record AuditEntry(
    string    Timestamp,
    string    Action,
    string    Actor,
    bool      Success,
    string?   Detail,
    string?   SourceIp);

public class IndexModel : PageModel
{
    private readonly IConfiguration _config;

    public List<AuditEntry> Entries { get; private set; } = new();
    public string? LogPath   { get; private set; }
    public bool    LogExists { get; private set; }
    public int     TotalMatched { get; private set; }

    [BindProperty(SupportsGet = true)] public string? FilterAction   { get; set; }
    [BindProperty(SupportsGet = true)] public string? FilterActor    { get; set; }
    [BindProperty(SupportsGet = true)] public string? FilterResult   { get; set; }  // "" | "success" | "fail"
    [BindProperty(SupportsGet = true)] public string? FilterDateFrom { get; set; }  // yyyy-MM-dd
    [BindProperty(SupportsGet = true)] public string? FilterDateTo   { get; set; }  // yyyy-MM-dd

    public bool HasFilter =>
        !string.IsNullOrEmpty(FilterAction)   ||
        !string.IsNullOrEmpty(FilterActor)    ||
        !string.IsNullOrEmpty(FilterResult)   ||
        !string.IsNullOrEmpty(FilterDateFrom) ||
        !string.IsNullOrEmpty(FilterDateTo);

    public IndexModel(IConfiguration config) => _config = config;

    public void OnGet()
    {
        var all = LoadAll();
        var filtered = Apply(all);
        TotalMatched = filtered.Count;
        Entries = filtered.Take(500).ToList();
    }

    // Export the full filtered result set (no page cap) as a UTF-8 CSV.
    public IActionResult OnGetExport()
    {
        var filtered = Apply(LoadAll());
        var sb = new StringBuilder();
        sb.AppendLine("Timestamp,Result,Action,Actor,Detail,SourceIP");
        foreach (var e in filtered)
        {
            sb.AppendLine(string.Join(",",
                Csv(e.Timestamp),
                Csv(e.Success ? "OK" : "FAIL"),
                Csv(e.Action),
                Csv(e.Actor),
                Csv(e.Detail  ?? ""),
                Csv(e.SourceIp ?? "")));
        }

        // File() uses PageModel.File() — System.IO.File is referenced by full name elsewhere.
        return File(Encoding.UTF8.GetBytes(sb.ToString()),
                    "text/csv;charset=utf-8",
                    $"audit-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private List<AuditEntry> LoadAll()
    {
        var dataPath = Environment.ExpandEnvironmentVariables(
            _config["DeployManager:DataPath"] ?? @"%ProgramData%\DeployManager\data");
        LogPath   = Path.Combine(dataPath, "audit.jsonl");
        LogExists = System.IO.File.Exists(LogPath);
        if (!LogExists) return new();

        var opts    = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var entries = new List<AuditEntry>();
        try
        {
            foreach (var line in System.IO.File.ReadLines(LogPath).Reverse())
            {
                try
                {
                    var e = JsonSerializer.Deserialize<AuditEntry>(line, opts);
                    if (e != null) entries.Add(e);
                }
                catch { }
            }
        }
        catch { }
        return entries;
    }

    private List<AuditEntry> Apply(List<AuditEntry> src)
    {
        IEnumerable<AuditEntry> q = src;

        if (!string.IsNullOrWhiteSpace(FilterAction))
            q = q.Where(e => e.Action.Contains(FilterAction.Trim(), StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(FilterActor))
            q = q.Where(e => e.Actor.Contains(FilterActor.Trim(), StringComparison.OrdinalIgnoreCase));

        if (FilterResult == "success")
            q = q.Where(e => e.Success);
        else if (FilterResult == "fail")
            q = q.Where(e => !e.Success);

        if (!string.IsNullOrWhiteSpace(FilterDateFrom) &&
            DateTimeOffset.TryParse(FilterDateFrom, out var from))
            q = q.Where(e => DateTimeOffset.TryParse(e.Timestamp, out var ts) && ts >= from);

        if (!string.IsNullOrWhiteSpace(FilterDateTo) &&
            DateTimeOffset.TryParse(FilterDateTo + "T23:59:59Z", out var to))
            q = q.Where(e => DateTimeOffset.TryParse(e.Timestamp, out var ts) && ts <= to);

        return q.ToList();
    }

    private static string Csv(string s) => '"' + s.Replace("\"", "\"\"") + '"';
}
