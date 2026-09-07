using System.Diagnostics;
using System.Net.Http.Json;

namespace MonitorServer_V2;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private const string DashboardUrl = "http://127.0.0.1:8765";

    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _statusItem = new("状态：检测中") { Enabled = false };
    private readonly ToolStripMenuItem _startItem = new("启动服务");
    private readonly ToolStripMenuItem _stopItem = new("停止服务");
    private readonly ToolStripMenuItem _restartItem = new("重启服务");
    private readonly ToolStripMenuItem _startupItem = new("开机启动");
    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 3000 };
    private readonly System.Windows.Forms.Timer _cleanupTimer = new() { Interval = 6 * 60 * 60 * 1000 };
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(1) };
    private readonly string _serverPath;
    private readonly string _logPath;
    private readonly LogMaintenance _logMaintenance;

    private Process? _serverProcess;
    private bool _serverHealthy;

    public TrayApplicationContext()
    {
        _serverPath = ServerLocator.FindServerExecutable();
        _logPath = Path.Combine(AppContext.BaseDirectory, "logs", "monitor-server.log");
        _logMaintenance = new LogMaintenance(_logPath);

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("System Monitor") { Enabled = false });
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("打开 Dashboard", null, (_, _) => OpenDashboard()));
        menu.Items.Add(_startItem);
        menu.Items.Add(_stopItem);
        menu.Items.Add(_restartItem);
        menu.Items.Add(new ToolStripMenuItem("打开日志", null, (_, _) => OpenLog()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitTray()));

        _notifyIcon = new NotifyIcon
        {
            Text = "System Monitor",
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };

        _notifyIcon.DoubleClick += (_, _) => OpenDashboard();
        _startItem.Click += async (_, _) => await StartServerAsync();
        _stopItem.Click += (_, _) => StopServer();
        _restartItem.Click += async (_, _) => await RestartServerAsync();
        _startupItem.Click += (_, _) => ToggleStartup();
        _statusTimer.Tick += async (_, _) => await RefreshStatusAsync();
        _cleanupTimer.Tick += (_, _) => RunPeriodicLogMaintenance();

        RunStartupLogMaintenance();
        _ = StartServerAsync();
        _statusTimer.Start();
        _cleanupTimer.Start();
        _ = RefreshStatusAsync();
    }

    private async Task StartServerAsync()
    {
        if (_serverHealthy || IsManagedProcessRunning() || await IsServerHealthyAsync())
        {
            _serverHealthy = true;
            return;
        }

        RunStartupLogMaintenance();

        if (!File.Exists(_serverPath))
        {
            MessageBox.Show(
                $"找不到 MonitorServer.exe：{_serverPath}",
                "System Monitor",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
        var logWriter = new StreamWriter(new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true
        };

        logWriter.WriteLine();
        logWriter.WriteLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] Starting {_serverPath}");

        var startInfo = new ProcessStartInfo
        {
            FileName = _serverPath,
            WorkingDirectory = Path.GetDirectoryName(_serverPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        _serverProcess = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        _serverProcess.OutputDataReceived += (_, e) => WriteLogLine(logWriter, e.Data);
        _serverProcess.ErrorDataReceived += (_, e) => WriteLogLine(logWriter, e.Data);
        _serverProcess.Exited += (_, _) =>
        {
            logWriter.WriteLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] MonitorServer exited.");
            logWriter.Dispose();
        };

        _serverProcess.Start();
        _serverProcess.BeginOutputReadLine();
        _serverProcess.BeginErrorReadLine();
    }

    private static void WriteLogLine(TextWriter writer, string? line)
    {
        if (!string.IsNullOrWhiteSpace(line))
        {
            writer.WriteLine(line);
        }
    }

    private void StopServer()
    {
        foreach (var process in FindServerProcesses())
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"停止服务失败：{ex.Message}",
                    "System Monitor",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            finally
            {
                process.Dispose();
            }
        }

        _serverProcess = null;
        _ = RefreshStatusAsync();
    }

    private async Task RestartServerAsync()
    {
        StopServer();
        await StartServerAsync();
    }

    private async Task RefreshStatusAsync()
    {
        _serverHealthy = await IsServerHealthyAsync();
        var startupEnabled = StartupTask.IsEnabled();

        _statusItem.Text = _serverHealthy ? "状态：运行中" : "状态：已停止";
        _notifyIcon.Text = _serverHealthy ? "System Monitor - 运行中" : "System Monitor - 已停止";
        _startItem.Enabled = !_serverHealthy;
        _stopItem.Enabled = _serverHealthy;
        _restartItem.Enabled = _serverHealthy;
        _startupItem.Checked = startupEnabled;
    }

    private void RunStartupLogMaintenance()
    {
        _logMaintenance.RunStartup();
    }

    private void RunPeriodicLogMaintenance()
    {
        _logMaintenance.RunPeriodic();
    }

    private async Task<bool> IsServerHealthyAsync()
    {
        try
        {
            var health = await _httpClient.GetFromJsonAsync<HealthResponse>($"{DashboardUrl}/health");
            return string.Equals(health?.Status, "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private bool IsManagedProcessRunning()
    {
        try
        {
            return _serverProcess is { HasExited: false };
        }
        catch
        {
            return false;
        }
    }

    private IEnumerable<Process> FindServerProcesses()
    {
        var fullServerPath = Path.GetFullPath(_serverPath);
        var matches = new List<Process>();

        foreach (var process in Process.GetProcessesByName("MonitorServer"))
        {
            if (process.Id == Environment.ProcessId)
            {
                process.Dispose();
                continue;
            }

            try
            {
                var modulePath = process.MainModule?.FileName;
                if (string.Equals(modulePath, fullServerPath, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(process);
                }
                else
                {
                    process.Dispose();
                }
            }
            catch
            {
                matches.Add(process);
            }
        }

        return matches;
    }

    private static void OpenDashboard()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = DashboardUrl,
            UseShellExecute = true
        });
    }

    private void OpenLog()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);

        if (!File.Exists(_logPath))
        {
            File.WriteAllText(_logPath, string.Empty);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = _logPath,
            UseShellExecute = true
        });
    }

    private void ToggleStartup()
    {
        var enable = !StartupTask.IsEnabled();
        var ok = StartupTask.SetEnabled(enable);

        if (!ok)
        {
            MessageBox.Show(
                "开机启动设置失败。请确认已允许管理员权限。",
                "System Monitor",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        _ = RefreshStatusAsync();
    }

    private void ExitTray()
    {
        var result = MessageBox.Show(
            "退出托盘程序时同时停止后端服务吗？",
            "System Monitor",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question);

        if (result == DialogResult.Cancel)
        {
            return;
        }

        if (result == DialogResult.Yes)
        {
            StopServer();
        }

        _statusTimer.Stop();
        _cleanupTimer.Stop();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _httpClient.Dispose();
        ExitThread();
    }

    private sealed record HealthResponse(string Status);
}
