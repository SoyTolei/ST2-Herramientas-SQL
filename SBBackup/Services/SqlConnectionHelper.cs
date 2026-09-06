using System.Net;
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

        Exception? last = null;
        string? failedResolutionKey = null;

        foreach (var attempt in BuildAttempts(server))
        {
            // Error 26 en esta variante de nombre: saltar el resto de auth/cifrado
            // (usuario/Windows no cambia nada si no se encuentra la instancia).
            if (failedResolutionKey is not null &&
                string.Equals(attempt.DataSourceKey, failedResolutionKey, StringComparison.OrdinalIgnoreCase))
                continue;

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

                if (IsInstanceResolutionFailure(ex))
                {
                    failedResolutionKey = attempt.DataSourceKey;
                    log?.Report(
                        "No se encontró la instancia (SQL Browser / nombre). " +
                        "Probando otra forma de escribir el servidor…");
                }
            }
        }

        throw BuildConnectFailure(server, last);
    }

    private static List<Attempt> BuildAttempts(string server)
    {
        var list = new List<Attempt>();
        var variants = ExpandServerVariants(server);

        for (var i = 0; i < variants.Count; i++)
        {
            var variant = variants[i];
            var prefix = i == 0
                ? ""
                : $"Con «{variant.Display}»: ";

            // Timeout más corto en variantes alternativas (ya falló la principal).
            var timeout = i == 0
                ? ConnectionStringFactory.DefaultConnectTimeoutSeconds
                : 10;

            AddAuthAttempts(list, variant, prefix, timeout);
        }

        return list;
    }

    private static void AddAuthAttempts(
        List<Attempt> list,
        ServerVariant variant,
        string prefix,
        int timeoutSeconds)
    {
        var key = variant.DataSource;

        if (BejermanSqlDefaults.HasPassword)
        {
            list.Add(new Attempt(
                prefix + "Intentando conexión con usuario SQL (bejerman, cifrado)",
                "Conectado con usuario SQL (cifrado).",
                ConnectionStringFactory.Build(
                    variant.DataSource,
                    integratedSecurity: false,
                    BejermanSqlDefaults.User,
                    BejermanSqlDefaults.Password,
                    SqlConnectionEncryptOption.Mandatory,
                    timeoutSeconds),
                key));

            list.Add(new Attempt(
                prefix + "Reintentando usuario SQL (bejerman, cifrado opcional)",
                "Conectado con usuario SQL (cifrado opcional).",
                ConnectionStringFactory.Build(
                    variant.DataSource,
                    integratedSecurity: false,
                    BejermanSqlDefaults.User,
                    BejermanSqlDefaults.Password,
                    SqlConnectionEncryptOption.Optional,
                    timeoutSeconds),
                key));
        }

        list.Add(new Attempt(
            prefix + "Probando autenticación de Windows (cifrado)",
            "Conectado con Windows (cifrado).",
            ConnectionStringFactory.Build(
                variant.DataSource,
                integratedSecurity: true,
                null,
                null,
                SqlConnectionEncryptOption.Mandatory,
                timeoutSeconds),
            key));

        list.Add(new Attempt(
            prefix + "Probando autenticación de Windows (cifrado opcional)",
            "Conectado con Windows (cifrado opcional).",
            ConnectionStringFactory.Build(
                variant.DataSource,
                integratedSecurity: true,
                null,
                null,
                SqlConnectionEncryptOption.Optional,
                timeoutSeconds),
            key));
    }

    /// <summary>
    /// Variantes de nombre para instancias con nombre (HOST\INSTANCIA).
    /// En la misma PC, .\INSTANCIA / localhost\INSTANCIA suelen evitar depender del SQL Browser.
    /// </summary>
    private static List<ServerVariant> ExpandServerVariants(string server)
    {
        var result = new List<ServerVariant>();
        void Add(string value)
        {
            var t = value.Trim();
            if (t.Length == 0)
                return;
            if (result.Any(v => string.Equals(v.DataSource, t, StringComparison.OrdinalIgnoreCase)))
                return;
            result.Add(new ServerVariant(t, t));
        }

        Add(server);

        if (!TrySplitNamedInstance(server, out var host, out var instance))
            return result;

        // Si el host es esta máquina (o vacío / punto), probar formas locales.
        if (IsLocalHostName(host))
        {
            Add(@$".\{instance}");
            Add(@$"localhost\{instance}");
            Add(@$"(local)\{instance}");
            Add(@$"127.0.0.1\{instance}");
        }

        return result;
    }

    private static bool TrySplitNamedInstance(string server, out string host, out string instance)
    {
        host = "";
        instance = "";

        var s = server.Trim();
        foreach (var prefix in new[] { "tcp:", "np:", "lpc:", "admin:" })
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                s = s[prefix.Length..];
        }

        // HOST,puerto → no es instancia con nombre
        if (s.Contains(',', StringComparison.Ordinal))
            return false;

        var slash = s.IndexOf('\\');
        if (slash <= 0 || slash >= s.Length - 1)
            return false;

        host = s[..slash].Trim();
        instance = s[(slash + 1)..].Trim();
        return host.Length > 0 && instance.Length > 0;
    }

    private static bool IsLocalHostName(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return true;

        if (host is "." or "(local)" ||
            host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("::1", StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            var machine = Environment.MachineName;
            if (host.Equals(machine, StringComparison.OrdinalIgnoreCase))
                return true;

            // W11880.dominio.local → comparar solo el hostname
            var hostShort = host.Split('.')[0];
            if (hostShort.Equals(machine, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch
        {
            // ignore
        }

        try
        {
            var dns = Dns.GetHostName();
            if (host.Equals(dns, StringComparison.OrdinalIgnoreCase) ||
                host.Split('.')[0].Equals(dns.Split('.')[0], StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch
        {
            // ignore
        }

        return false;
    }

    internal static bool IsInstanceResolutionFailure(Exception ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is SqlException sql)
            {
                if (sql.Number is 26)
                    return true;

                foreach (SqlError err in sql.Errors)
                {
                    if (err.Number == 26)
                        return true;
                }
            }

            var msg = cur.Message ?? "";
            if (msg.Contains("error: 26", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("error 26", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("buscar el servidor/instancia", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("locating server/instance", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("server/instance specified", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static Exception BuildConnectFailure(string server, Exception? last)
    {
        if (last is not null && IsInstanceResolutionFailure(last))
        {
            return new InvalidOperationException(
                "No se encontró la instancia SQL «" + server + "».\n\n" +
                "Esto suele ser el SQL Browser (UDP 1434) o el nombre de la instancia, " +
                "no el usuario ni la contraseña.\n\n" +
                "Qué probar:\n" +
                "• En esta misma PC: .\\" + GuessInstance(server) + "  o  localhost\\" + GuessInstance(server) + "\n" +
                "• Si conocés el puerto TCP: " + GuessHost(server) + ",PUERTO  (ej. " + GuessHost(server) + ",1433)\n" +
                "• En el servidor: servicio «SQL Server Browser» en Ejecución, y firewall permitiendo UDP 1434\n" +
                "• Confirmar que la instancia está levantada (services.msc → SQL Server (INSTANCIA))",
                last);
        }

        return new InvalidOperationException(
            "No se pudo conectar al servidor SQL «" + server + "».\n\n" +
            "En terminales secundarias suele ser red o firewall (puerto 1433 / SQL Browser), " +
            "DNS lento o cifrado TLS incompatible.\n\n" +
            "Probá:\n" +
            "• Que el nombre del servidor sea el mismo que en una PC que sí conecta\n" +
            "• ping / Test-NetConnection al puerto de SQL desde esta terminal\n" +
            "• Si es instancia con nombre (SERVIDOR\\INSTANCIA), usá SERVIDOR,PUERTO",
            last);
    }

    private static string GuessInstance(string server)
    {
        if (TrySplitNamedInstance(server, out _, out var instance))
            return instance;
        return "INSTANCIA";
    }

    private static string GuessHost(string server)
    {
        if (TrySplitNamedInstance(server, out var host, out _))
            return host.Length == 0 ? "SERVIDOR" : host;
        var s = server.Trim();
        foreach (var prefix in new[] { "tcp:", "np:", "lpc:", "admin:" })
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                s = s[prefix.Length..];
        }

        var comma = s.IndexOf(',');
        if (comma > 0)
            s = s[..comma];
        var slash = s.IndexOf('\\');
        if (slash > 0)
            s = s[..slash];
        return string.IsNullOrWhiteSpace(s) ? "SERVIDOR" : s;
    }

    private static string ShortFail(Exception ex)
    {
        var msg = ex.GetBaseException().Message.Trim();
        if (msg.Length > 220)
            msg = msg[..217] + "…";
        return msg;
    }

    private sealed record ServerVariant(string DataSource, string Display);

    private sealed record Attempt(
        string Label,
        string SuccessLabel,
        string ConnectionString,
        string DataSourceKey);
}
