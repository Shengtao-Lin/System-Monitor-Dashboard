using System.Diagnostics;

namespace MonitorServer_V2;

internal static class StartupTask
{
    private const string TaskName = "SystemMonitorDashboardTray";

    public static bool TryHandleElevatedCommand(string[] args)
    {
        if (args.Length == 0)
        {
            return false;
        }

        if (args.Contains("--enable-startup", StringComparer.OrdinalIgnoreCase))
        {
            CreateTask();
            return true;
        }

        if (args.Contains("--disable-startup", StringComparer.OrdinalIgnoreCase))
        {
            DeleteTask();
            return true;
        }

        return false;
    }

    public static bool IsEnabled()
    {
        using var process = StartSchtasks($"/Query /TN \"{TaskName}\"");
        process.WaitForExit(3000);
        return process.ExitCode == 0;
    }

    public static bool SetEnabled(bool enabled)
    {
        if (enabled)
        {
            return CreateTask() || RunSelfElevated("--enable-startup");
        }

        return DeleteTask() || RunSelfElevated("--disable-startup");
    }

    private static bool CreateTask()
    {
        var exePath = Environment.ProcessPath ?? Application.ExecutablePath;
        var taskRun = $"\\\"{exePath}\\\"";
        using var process = StartSchtasks($"/Create /TN \"{TaskName}\" /TR \"{taskRun}\" /SC ONLOGON /RL HIGHEST /F");
        process.WaitForExit(5000);
        return process.ExitCode == 0;
    }

    private static bool DeleteTask()
    {
        using var process = StartSchtasks($"/Delete /TN \"{TaskName}\" /F");
        process.WaitForExit(5000);
        return process.ExitCode == 0;
    }

    private static Process StartSchtasks(string arguments)
    {
        return Process.Start(new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
    }

    private static bool RunSelfElevated(string arguments)
    {
        try
        {
            var exePath = Environment.ProcessPath ?? Application.ExecutablePath;
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });

            process?.WaitForExit(15000);
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
