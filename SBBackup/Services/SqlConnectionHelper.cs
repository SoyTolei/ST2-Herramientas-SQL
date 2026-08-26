using Microsoft.Data.SqlClient;

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

        var attempts = BuildAttempts(server);
        Exception? last = null;

        foreach (var attempt in attempts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            log?.Report(attempt.Label + "…");

            try
            {
                await SqlConnectionProbe.TestAsync(attempt.ConnectionString, cancellationToken)
                    .ConfigureAwait(false);
                log?.Report(attempt.SuccessLabel);
                return attempt.ConnectionString;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                log?.Report("No: " + ShortFail(ex));
            }
        }

        throw new InvalidOperationException(
            "No se pudo conectar al servidor SQL «" + server + "».\n\n" +
            "En terminales secundarias suele ser red o firewall (puerto 1433 / SQL Browser), " +
            "DNS lento o cifrado TLS incompatible.\n\n" +
            "Probá:\n" +
            "• Que el nombre del servidor sea el mismo que en una PC que sí conecta\n" +
            "• ping / Test-NetConnection al puerto de SQL desde esta terminal\n" +
            "• Si es instancia con nombre (SERVIDOR\\INSTANCIA), usá SERVIDOR,PUERTO",
            last);
    }

    private static List<Attempt> BuildAttempts(string server)
    {
        var list = new List<Attempt>();

        // 1) SQL bejerman + Encrypt Mandatory (entornos modernos)
        list.Add(new Attempt(
            "Intentando conexión con usuario SQL (bejerman, cifrado)",
            "Conectado con usuario SQL (cifrado).",
            ConnectionStringFactory.Build(
                server,
                integratedSecurity: false,
                BejermanSqlDefaults.User,
                BejermanSqlDefaults.Password,
                SqlConnectionEncryptOption.Mandatory)));

        // 2) SQL bejerman + Encrypt Optional (SQL viejo / TLS raro en sucursales)
        list.Add(new Attempt(
            "Reintentando usuario SQL (bejerman, cifrado opcional)",
            "Conectado con usuario SQL (cifrado opcional).",
            ConnectionStringFactory.Build(
                server,
                integratedSecurity: false,
                BejermanSqlDefaults.User,
                BejermanSqlDefaults.Password,
                SqlConnectionEncryptOption.Optional)));

        // 3) Windows + Mandatory
        list.Add(new Attempt(
            "Probando autenticación de Windows (cifrado)",
            "Conectado con Windows (cifrado).",
            ConnectionStringFactory.Build(
                server,
                integratedSecurity: true,
                null,
                null,
                SqlConnectionEncryptOption.Mandatory)));

        // 4) Windows + Optional
        list.Add(new Attempt(
            "Probando autenticación de Windows (cifrado opcional)",
            "Conectado con Windows (cifrado opcional).",
            ConnectionStringFactory.Build(
                server,
                integratedSecurity: true,
                null,
                null,
                SqlConnectionEncryptOption.Optional)));

        return list;
    }

    private static string ShortFail(Exception ex)
    {
        var msg = ex.GetBaseException().Message.Trim();
        if (msg.Length > 220)
            msg = msg[..217] + "…";
        return msg;
    }

    private sealed record Attempt(string Label, string SuccessLabel, string ConnectionString);
}
