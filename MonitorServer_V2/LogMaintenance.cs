namespace MonitorServer_V2;

internal sealed class LogMaintenance
{
    private static readonly TimeSpan TrayLogRetention = TimeSpan.FromDays(14);
    private static readonly TimeSpan IcueLogRetention = TimeSpan.FromDays(7);
    private static readonly TimeSpan ActiveFileGrace = TimeSpan.FromMinutes(10);
    private const long MaxTrayLogBytes = 5L * 1024L * 1024L;
    private const int MinIcueLogsToKeep = 10;

    private readonly string _trayLogDirectory;
    private readonly string _trayLogPath;
    private readonly string _icueLogDirectory;

    public LogMaintenance(string trayLogPath)
    {
        _trayLogPath = trayLogPath;
        _trayLogDirectory = Path.GetDirectoryName(trayLogPath)!;
        _icueLogDirectory = ResolveIcueLogDirectory();
    }

    public void RunStartup()
    {
        RotateTrayLogIfNeeded();
        RunPeriodic();
    }

    public void RunPeriodic()
    {
        DeleteOldTrayLogs();
        DeleteOldIcueLogs();
    }

    private void RotateTrayLogIfNeeded()
    {
        try
        {
            var file = new FileInfo(_trayLogPath);
            if (!file.Exists || file.Length < MaxTrayLogBytes)
            {
                return;
            }

            Directory.CreateDirectory(_trayLogDirectory);
            var rotatedPath = Path.Combine(
                _trayLogDirectory,
                $"monitor-server-{DateTime.Now:yyyyMMdd-HHmmss}.log");

            File.Move(_trayLogPath, rotatedPath);
        }
        catch
        {
            // Best effort cleanup must not break the tray app.
        }
    }

    private void DeleteOldTrayLogs()
    {
        try
        {
            if (!Directory.Exists(_trayLogDirectory))
            {
                return;
            }

            var cutoff = DateTime.Now - TrayLogRetention;
            foreach (var file in Directory.EnumerateFiles(_trayLogDirectory, "*.log", SearchOption.TopDirectoryOnly))
            {
                var info = new FileInfo(file);
                if (!SamePath(info.FullName, _trayLogPath) && info.LastWriteTime < cutoff)
                {
                    TryDelete(info);
                }
            }
        }
        catch
        {
        }
    }

    private void DeleteOldIcueLogs()
    {
        try
        {
            if (!Directory.Exists(_icueLogDirectory))
            {
                return;
            }

            var cutoff = DateTime.Now - IcueLogRetention;
            var activeCutoff = DateTime.Now - ActiveFileGrace;
            var logs = Directory
                .EnumerateFiles(_icueLogDirectory, "*.csv", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTime)
                .ToList();

            foreach (var file in logs.Skip(MinIcueLogsToKeep))
            {
                if (file.LastWriteTime < cutoff && file.LastWriteTime < activeCutoff)
                {
                    TryDelete(file);
                }
            }
        }
        catch
        {
        }
    }

    private static string ResolveIcueLogDirectory()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("MONITOR_ICUE_LOG_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        var workspaceDirectory = ServerLocator.FindWorkspaceDirectory();
        if (workspaceDirectory != null)
        {
            return Path.Combine(workspaceDirectory, "icue_logs");
        }

        return Path.Combine(AppContext.BaseDirectory, "icue_logs");
    }

    private static void TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
        }
        catch
        {
        }
    }

    private static bool SamePath(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }
}
