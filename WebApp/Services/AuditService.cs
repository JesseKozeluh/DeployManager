using System.Text.Json;

namespace DeployManager.Services;

public record AuditEvent(
    string  Action,
    string  Actor,
    bool    Success,
    string? Detail   = null,
    string? SourceIp = null);

public interface IAuditService
{
    void Log(AuditEvent evt);
}

/// <summary>
/// Append-only structured audit log (JSONL) at %ProgramData%\DeployManager\data\audit.jsonl.
/// Each line is a self-contained JSON object for easy parsing / SIEM ingestion.
/// Satisfies ISO 27001 A.12.4 and ASD ISM audit logging controls.
/// </summary>
public class AuditService : IAuditService
{
    private readonly string _logFile;
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public AuditService(IConfiguration config)
    {
        var dataPath = Environment.ExpandEnvironmentVariables(
                           config["DeployManager:DataPath"] ?? @"%ProgramData%\DeployManager\data");
        Directory.CreateDirectory(dataPath);
        _logFile = Path.Combine(dataPath, "audit.jsonl");
    }

    public void Log(AuditEvent evt)
    {
        var entry = new
        {
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
            evt.Action,
            evt.Actor,
            evt.Success,
            evt.Detail,
            evt.SourceIp
        };

        var line = JsonSerializer.Serialize(entry, _json);

        lock (_lock)
        {
            File.AppendAllText(_logFile, line + Environment.NewLine);
        }
    }
}
