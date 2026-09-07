namespace MonitorServer_V2;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        DotEnv.Load();

        if (StartupTask.TryHandleElevatedCommand(args))
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }
}
