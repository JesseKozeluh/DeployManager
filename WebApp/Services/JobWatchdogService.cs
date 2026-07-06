namespace DeployManager.Services;

// Runs every 10 minutes and marks any job that has been in "imaging" status for
// more than 2 hours as "timeout", then sends an email notification.
// Prevents abandoned jobs from staying "imaging" forever after a WinPE crash or power loss.
public class JobWatchdogService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TimeoutAfter  = TimeSpan.FromHours(2);

    private readonly DataStore                 _data;
    private readonly EmailNotificationService  _email;
    private readonly ILogger<JobWatchdogService> _log;

    public JobWatchdogService(
        DataStore                  data,
        EmailNotificationService   email,
        ILogger<JobWatchdogService> log)
    {
        _data  = data;
        _email = email;
        _log   = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stagger the first check by 1 minute so startup I/O settles first.
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var timedOut = _data.TimeoutStaleJobs(TimeoutAfter);
                foreach (var job in timedOut)
                {
                    _log.LogWarning(
                        "Job for {Device} ({Mac}) timed out after {Hours:F1} h with no WinPE response.",
                        job.DeviceName, job.MacAddress, TimeoutAfter.TotalHours);
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
