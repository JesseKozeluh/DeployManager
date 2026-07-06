using System.Diagnostics;
using System.ServiceProcess;
using System.Text.Json;
using System.Windows.Forms;

namespace DeployManager.Tray;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class TrayApplicationContext : ApplicationContext
{
    private const string ServiceName     = "DeployManager";
    private const int    DefaultHttpsPort = 8090;

    private readonly NotifyIcon       _tray;
    private readonly ContextMenuStrip _menu;
    private readonly System.Windows.Forms.Timer _poll;

    private readonly string _dataPath;
    private readonly string _auditLog;
    private readonly bool   _isAdmin;

    private ToolStripMenuItem _statusItem  = null!;
    private ToolStripMenuItem _startItem   = null!;
    private ToolStripMenuItem _stopItem    = null!;
    private ToolStripMenuItem _restartItem = null!;

    private ServiceControllerStatus _lastStatus = (ServiceControllerStatus)(-1);
    private bool _firstPoll = true;

    public TrayApplicationContext()
    {
        _dataPath = Environment.ExpandEnvironmentVariables(@"%ProgramData%\DeployManager\data");
        _auditLog = Path.Combine(_dataPath, "audit.jsonl");
        _isAdmin  = CheckIsAdmin();

        _menu = BuildMenu();
        _tray = new NotifyIcon
        {
            Icon             = LoadEmbeddedIcon(),
            Text             = "DeployManager",
            ContextMenuStrip = _menu,
            Visible          = true
        };

        // Double-click tray icon opens the web UI directly
        _tray.DoubleClick += (_, _) => OpenWebUi();

        _poll = new System.Windows.Forms.Timer { Interval = 5000 };
        _poll.Tick += (_, _) => RefreshStatus();
        _poll.Start();
        RefreshStatus();
    }

    // ── Menu construction ────────────────────────────────────────────────────

    private ContextMenuStrip BuildMenu()
    {
        var cms = new ContextMenuStrip();

        var header = new ToolStripMenuItem("DeployManager") { Enabled = false };
        header.Font = new Font(header.Font, FontStyle.Bold);
        cms.Items.Add(header);

        _statusItem = new ToolStripMenuItem("Checking…") { Enabled = false };
        cms.Items.Add(_statusItem);
        cms.Items.Add(new ToolStripSeparator());

        if (!_isAdmin)
        {
            var hint = new ToolStripMenuItem("(Run as administrator to control the service)")
                { Enabled = false, ForeColor = SystemColors.GrayText };
            cms.Items.Add(hint);
        }

        _startItem   = new ToolStripMenuItem("Start Service",   null, async (_, _) => await ControlServiceAsync("start"));
        _stopItem    = new ToolStripMenuItem("Stop Service",    null, async (_, _) => await ControlServiceAsync("stop"));
        _restartItem = new ToolStripMenuItem("Restart Service", null, async (_, _) => await ControlServiceAsync("restart"));

        cms.Items.Add(_startItem);
        cms.Items.Add(_stopItem);
        cms.Items.Add(_restartItem);
        cms.Items.Add(new ToolStripSeparator());

        cms.Items.Add(new ToolStripMenuItem("Open Web UI", null, (_, _) => OpenWebUi()));
        cms.Items.Add(new ToolStripSeparator());

        cms.Items.Add(new ToolStripMenuItem("Open Data Folder", null, OnOpenDataFolder));
        cms.Items.Add(new ToolStripMenuItem("View Audit Log",   null, OnViewAuditLog));
        cms.Items.Add(new ToolStripMenuItem("View Event Log",   null, OnViewEventLog));

        var rebuildScript = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            @"DeployManager\scripts\Update-BootWim.ps1");
        if (File.Exists(rebuildScript))
        {
            cms.Items.Add(new ToolStripSeparator());
            cms.Items.Add(new ToolStripMenuItem("Rebuild boot.wim…", null,
                (_, _) => OnRebuildBootWim(rebuildScript)));
        }

        cms.Items.Add(new ToolStripSeparator());
        cms.Items.Add(new ToolStripMenuItem("Exit", null, OnExit));

        return cms;
    }

    // ── Service status polling ───────────────────────────────────────────────

    private void RefreshStatus()
    {
        ServiceControllerStatus status;
        try
        {
            using var sc = new ServiceController(ServiceName);
            status = sc.Status;
        }
        catch (InvalidOperationException)
        {
            SetDisplay("○ Service: Not installed", "DeployManager — Not installed",
                start: false, stop: false, restart: false);
            return;
        }

        if (status == _lastStatus) return;

        var wasFirst = _firstPoll;
        _lastStatus  = status;
        _firstPoll   = false;

        var (label, tip, canStart, canStop) = status switch
        {
            ServiceControllerStatus.Running      => ("● Service: Running",   "DeployManager — Running",   false, true),
            ServiceControllerStatus.Stopped      => ("○ Service: Stopped",   "DeployManager — Stopped",   true,  false),
            ServiceControllerStatus.StartPending => ("◌ Service: Starting…", "DeployManager — Starting…", false, false),
            ServiceControllerStatus.StopPending  => ("◌ Service: Stopping…", "DeployManager — Stopping…", false, false),
            _                                    => ($"Service: {status}",    $"DeployManager — {status}", false, false),
        };

        SetDisplay(label, tip,
            start:   _isAdmin && canStart,
            stop:    _isAdmin && canStop,
            restart: _isAdmin && canStop);

        if (!wasFirst)
        {
            if (status == ServiceControllerStatus.Running)
                _tray.ShowBalloonTip(3000, "DeployManager", "Service started.", ToolTipIcon.Info);
            else if (status == ServiceControllerStatus.Stopped)
                _tray.ShowBalloonTip(4000, "DeployManager", "Service has stopped.", ToolTipIcon.Warning);
        }
    }

    private void SetDisplay(string label, string tip, bool start, bool stop, bool restart)
    {
        _statusItem.Text     = label;
        _tray.Text           = tip.Length > 63 ? tip[..63] : tip;  // NotifyIcon tooltip max
        _startItem.Enabled   = start;
        _stopItem.Enabled    = stop;
        _restartItem.Enabled = restart;
    }

    // ── Service control ──────────────────────────────────────────────────────

    private async Task ControlServiceAsync(string action)
    {
        _startItem.Enabled = _stopItem.Enabled = _restartItem.Enabled = false;
        _statusItem.Text   = action == "start" ? "◌ Service: Starting…" : "◌ Service: Stopping…";

        try
        {
            await Task.Run(() =>
            {
                using var sc = new ServiceController(ServiceName);
                switch (action)
                {
                    case "start":
                        sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                        break;
                    case "stop":
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                        break;
                    case "restart":
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                        sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                        break;
                }
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not {action} the service:\n\n{ex.Message}",
                "DeployManager", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        // Force immediate re-poll rather than waiting for the next tick
        _lastStatus = (ServiceControllerStatus)(-1);
        RefreshStatus();
    }

    // ── Menu actions ─────────────────────────────────────────────────────────

    private void OpenWebUi()
    {
        var port = ReadHttpsPort();
        Process.Start(new ProcessStartInfo($"https://localhost:{port}")
            { UseShellExecute = true });
    }

    private void OnOpenDataFolder(object? s, EventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", _dataPath)
            { UseShellExecute = true });

    private void OnViewAuditLog(object? s, EventArgs e)
    {
        if (!File.Exists(_auditLog))
        {
            MessageBox.Show(
                "No audit log found yet.\n\nThe log is created once the first auditable action occurs.",
                "DeployManager", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Process.Start(new ProcessStartInfo(_auditLog) { UseShellExecute = true });
    }

    private void OnViewEventLog(object? s, EventArgs e) =>
        Process.Start(new ProcessStartInfo("eventvwr.msc") { UseShellExecute = true });

    private void OnRebuildBootWim(string scriptPath)
    {
        var confirm = MessageBox.Show(
            "This will mount boot.wim, update the embedded scripts, and commit the changes.\n" +
            "The process takes 1–3 minutes and requires the Windows ADK to be installed.\n\nProceed?",
            "Rebuild boot.wim",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);

        if (confirm != DialogResult.Yes) return;

        Process.Start(new ProcessStartInfo
        {
            FileName        = "powershell.exe",
            Arguments       = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            UseShellExecute = true,
            Verb            = "runas"
        });
    }

    private void OnExit(object? s, EventArgs e)
    {
        _poll.Stop();
        _tray.Visible = false;
        Application.Exit();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private int ReadHttpsPort()
    {
        try
        {
            var appSettings = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"DeployManager\appsettings.json");

            if (!File.Exists(appSettings)) return DefaultHttpsPort;

            using var doc  = JsonDocument.Parse(File.ReadAllText(appSettings));
            var root = doc.RootElement;

            // Explicit HttpsPort key takes priority
            if (root.TryGetProperty("DeployManager", out var dm) &&
                dm.TryGetProperty("HttpsPort", out var portEl))
                return portEl.GetInt32();

            // Fall back to parsing the Urls binding string (e.g. "https://*:8090;http://*:8080")
            if (root.TryGetProperty("Urls", out var urlsEl))
            {
                foreach (var part in (urlsEl.GetString() ?? "").Split(';'))
                {
                    var candidate = part.Trim().Replace("*", "localhost");
                    if (candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                        Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
                        return uri.Port;
                }
            }
        }
        catch { }

        return DefaultHttpsPort;
    }

    private static Icon LoadEmbeddedIcon()
    {
        var stream = typeof(TrayApplicationContext).Assembly
            .GetManifestResourceStream("DeployManager.Tray.deploymgr.ico");
        return stream is not null ? new Icon(stream) : SystemIcons.Application;
    }

    private static bool CheckIsAdmin()
    {
        try
        {
            var id  = System.Security.Principal.WindowsIdentity.GetCurrent();
            var pri = new System.Security.Principal.WindowsPrincipal(id);
            return pri.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _poll.Dispose();
            _menu.Dispose();
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}
