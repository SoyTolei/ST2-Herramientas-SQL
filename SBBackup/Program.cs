namespace SBBackup;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (HasFlag(args, "--run-scheduled-backup"))
        {
            var profile = GetArgValue(args, "--profile");
            var code = Services.ScheduledBackupRunner
                .RunAsync(profile, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            Environment.Exit(code);
            return;
        }

        Application.SetCompatibleTextRenderingDefault(false);
        ApplicationConfiguration.Initialize();
        Application.Run(new HomeForm());
    }

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    private static string? GetArgValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }
}
