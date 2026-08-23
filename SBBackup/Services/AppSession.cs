namespace SBBackup.Services;

/// <summary>Estado de conexión SQL compartido entre pantalla principal, backup y restaurar.</summary>
internal static class AppSession
{
    public static string? ServerName { get; private set; }
    public static string? ConnectionString { get; private set; }
    public static bool IsConnected => !string.IsNullOrEmpty(ConnectionString);

    public static event Action? ConnectionChanged;

    public static async Task ConnectAsync(
        string server,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        var cs = await SqlConnectionHelper.ConnectAsync(server, log, cancellationToken).ConfigureAwait(false);
        ServerName = server.Trim();
        ConnectionString = cs;
        ConnectionChanged?.Invoke();
    }

    public static void Disconnect()
    {
        if (!IsConnected)
            return;

        ServerName = null;
        ConnectionString = null;
        ConnectionChanged?.Invoke();
    }
}
