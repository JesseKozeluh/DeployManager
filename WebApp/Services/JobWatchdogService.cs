namespace DeployManager.Services;

// Runs every few minutes and marks any "imaging" job that has gone too long with no
// activity (no status callback or heartbeat) as "timeout", then emails a notification.
// Activity-based rather than total-elapsed, so a legitimately long-but-progressing
// deployment is never falsely failed; an absolute ceiling is a secondary backstop.
public class JobWatchdogService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    private readonly DataStore                 _data;
    private readonly EmailNotificationService  _email;
    private readonly ISettingsService          _settings;
    private readonly ILogger<JobWatchdogService> _log;

    public JobWatchdogService(
        DataStore                  data,
        EmailNotificationService   email,
        ISettingsService           settings,
        ILogger<JobWatchdogService> log)
    {
        _data     = data;
        _email    = email;
        _settings = settings;
        _log      = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stagger the first check by 1 minute so startup I/O settles first.
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var s          = _settings.Get();
                var inactivity = TimeSpan.FromMinutes(s.JobInactivityTimeoutMinutes > 0 ? s.JobInactivityTimeoutMinutes : 30);
                var maxDur     = TimeSpan.FromMinutes(s.JobMaxDurationMinutes > 0 ? s.JobMaxDurationMinutes : 480);

                var timedOut = _data.TimeoutStaleJobs(inactivity, maxDur);
                foreach (var job in timedOut)
                {
                    _log.LogWarning(
                        "Job for {Device} ({Mac}) timed out — no activity for over {Mins} min (or exceeded the {Hours}h ceiling).",
                        job.DeviceName, job.MacAddress, (int)inactivity.TotalMinutes, (int)maxDur.TotalHours);
                    _ = _email.SendJobTimeoutAsync(job);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "JobWatchdogService encountered an error during stale-job check.");
            }

            await Task.Delay(CheckInterval, stoppingToken);
        }
    }
}
