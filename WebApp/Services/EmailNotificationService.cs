using System.Net;
using System.Net.Mail;
using DeployManager.Models;

namespace DeployManager.Services;

public class EmailNotificationService
{
    private readonly ISettingsService _settings;
    private readonly ILogger<EmailNotificationService> _log;

    public EmailNotificationService(ISettingsService settings, ILogger<EmailNotificationService> log)
    {
        _settings = settings;
        _log      = log;
    }

    public Task SendJobCompleteAsync(DeploymentJob job) => SendAsync(job, success: true);
    public Task SendJobErrorAsync(DeploymentJob job)    => SendAsync(job, success: false);

    public Task SendJobTimeoutAsync(DeploymentJob job) =>
        SendAsync(job, success: false,
            subject: $"[DeployManager] {job.DeviceName} — imaging timed out (no response for 2 h)");

    public async Task SendTestAsync()
    {
        var s = _settings.Get();
        var dummy = new DeploymentJob
        {
            DeviceName = "TEST-DEVICE",
            MacAddress = "AA:BB:CC:DD:EE:FF",
            Site       = "Test",
            WimName    = "test.wim",
            Completed  = DateTime.UtcNow
        };
        await SendAsync(dummy, success: true, subject: "[DeployManager] Test notification — email is working");
    }

    private async Task SendAsync(DeploymentJob job, bool success, string? subject = null)
    {
        var s = _settings.Get();
        if (string.IsNullOrWhiteSpace(s.SmtpHost) || string.IsNullOrWhiteSpace(s.NotifyEmail))
            return;
        if (success && !s.NotifyOnComplete) return;
        if (!success && !s.NotifyOnError)   return;

        subject ??= success
            ? $"[DeployManager] {job.DeviceName} — imaging complete"
            : $"[DeployManager] {job.DeviceName} — imaging FAILED";

        var lines = new List<string>
        {
            $"Device : {job.DeviceName}",
            $"MAC    : {job.MacAddress}",
            $"Site   : {job.Site}",
            $"Image  : {job.WimName}",
        };
        if (!success && !string.IsNullOrEmpty(job.ErrorMessage))
            lines.Add($"Error  : {job.ErrorMessage}");
        lines.Add($"Time   : {job.Completed?.ToLocalTime():dd/MM/yyyy HH:mm}");

        var body = string.Join(Environment.NewLine, lines);
        var from = string.IsNullOrWhiteSpace(s.SmtpFrom)
            ? $"deploymgr@{s.SmtpHost}"
            : s.SmtpFrom;

        try
        {
            using var client = new SmtpClient(s.SmtpHost, s.SmtpPort)
            {
                EnableSsl   = s.SmtpStartTls,
                Credentials = string.IsNullOrWhiteSpace(s.SmtpUsername)
                    ? null
                    : new NetworkCredential(s.SmtpUsername, s.SmtpPassword)
            };
            using var msg = new MailMessage(from, s.NotifyEmail, subject, body);
            await client.SendMailAsync(msg);
            _log.LogInformation("Notification sent to {Addr} for job {Device}", s.NotifyEmail, job.DeviceName);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Email notification failed for job {Device}", job.DeviceName);
        }
    }
}
