namespace MonitorServer_V2;

internal static class ServerLocator
{
    public static string FindServerExecutable()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            Path.Combine(baseDirectory, "MonitorServer.exe"),
            Path.Combine(baseDirectory, "MonitorServer", "MonitorServer.exe"),
            Path.Combine(baseDirectory, "..", "MonitorServer", "publish", "MonitorServer.exe")
        };

        var current = new DirectoryInfo(baseDirectory);
        while (current != null)
        {
            candidates.Add(Path.Combine(current.FullName, "MonitorServer", "publish", "MonitorServer.exe"));
            current = current.Parent;
        }

        return candidates
            .Select(Path.GetFullPath)
            .FirstOrDefault(File.Exists)
            ?? Path.GetFullPath(candidates[0]);
    }

    public static string? FindWorkspaceDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "MonitorServer")) &&
                Directory.Exists(Path.Combine(current.FullName, "icue_logs")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }
}
