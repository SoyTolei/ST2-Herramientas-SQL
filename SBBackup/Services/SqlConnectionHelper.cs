namespace SBBackup.Services;

internal static class SqlConnectionHelper
{
    public static async Task<string> ConnectAsync(
        string server,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        server = server.Trim();
        if (string.IsNullOrWhiteSpace(server))
            throw new InvalidOperationException("Indicá el servidor SQL (instancia o nombre de equipo).");

        if (BejermanSqlDefaults.HasPassword)
        {
            log?.Report("Intentando conexión con usuario SQL (bejerman)…");
            var sqlCs = ConnectionStringFactory.Build(
                server,
                integratedSecurity: false,
                BejermanSqlDefaults.User,
                BejermanSqlDefaults.Password);

            try
            {
                await SqlConnectionProbe.TestAsync(sqlCs, cancellationToken).ConfigureAwait(false);
                log?.Report("Conectado con usuario SQL.");
                return sqlCs;
            }
            catch (Exception exSql)
            {
                log?.Report("No se pudo conectar con usuario SQL: " + exSql.Message);
                log?.Report("Probando autenticación de Windows…");
            }
        }
        else
        {
            log?.Report("Sin clave SQL local (appsettings.local.json). Se usa autenticación de Windows…");
        }

        var winCs = ConnectionStringFactory.Build(server, integratedSecurity: true, null, null);
        await SqlConnectionProbe.TestAsync(winCs, cancellationToken).ConfigureAwait(false);
        log?.Report("Conectado con Windows.");
        return winCs;
    }
}
