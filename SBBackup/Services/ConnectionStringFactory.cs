using Microsoft.Data.SqlClient;

namespace SBBackup.Services;

public static class ConnectionStringFactory
{
    /// <summary>Timeout de conexión (segundos). Un poco más holgado que el default (15) para terminales lentas.</summary>
    public const int DefaultConnectTimeoutSeconds = 18;

    public static string Build(
        string server,
        bool integratedSecurity,
        string? sqlUser,
        string? sqlPassword,
        SqlConnectionEncryptOption? encrypt = null,
        int connectTimeoutSeconds = DefaultConnectTimeoutSeconds)
    {
        var dataSource = NormalizeDataSource(server);
        var b = new SqlConnectionStringBuilder
        {
            DataSource = dataSource,
            IntegratedSecurity = integratedSecurity,
            TrustServerCertificate = true,
            Encrypt = encrypt ?? SqlConnectionEncryptOption.Mandatory,
            ConnectTimeout = Math.Clamp(connectTimeoutSeconds, 5, 90),
            // Solo con TCP: en local (shared memory / pipes) SqlClient lanza ArgumentException.
            MultiSubnetFailover = UsesTcpProtocol(dataSource),
            Pooling = true
        };

        if (!integratedSecurity)
        {
            b.UserID = sqlUser?.Trim() ?? "";
            b.Password = sqlPassword ?? "";
        }

        return b.ConnectionString;
    }

    private static bool UsesTcpProtocol(string dataSource)
    {
        if (dataSource.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
            return true;

        // Sin prefijo: SqlClient usa TCP en remoto; en local puede ser shared memory.
        // MultiSubnetFailover solo es válido con TCP explícito o remoto forzado a tcp:.
        return false;
    }

    /// <summary>
    /// Normaliza el DataSource: quita espacios y, si es un host remoto sin prefijo,
    /// fuerza TCP para no intentar Named Pipes (suele fallar entre terminales).
    /// En la misma PC no fuerza TCP: shared memory / named pipes suelen evitar
    /// depender del SQL Browser (error 26 en instancias con nombre).
    /// </summary>
    internal static string NormalizeDataSource(string server)
    {
        var s = server.Trim();
        if (s.Length == 0)
            return s;

        foreach (var prefix in new[] { "tcp:", "np:", "lpc:", "admin:" })
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return s;
        }

        if (LooksLocalDataSource(s))
            return s;

        return "tcp:" + s;
    }

    private static bool LooksLocalDataSource(string s)
    {
        if (s.StartsWith("(localdb)", StringComparison.OrdinalIgnoreCase))
            return true;

        var host = s;
        var slash = s.IndexOf('\\');
        if (slash >= 0)
            host = s[..slash];
        var comma = host.IndexOf(',');
        if (comma >= 0)
            host = host[..comma];

        host = host.Trim();
        if (host.Length == 0)
            return true;

        if (host is "." or "(local)" ||
            host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("::1", StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            var machine = Environment.MachineName;
            if (host.Equals(machine, StringComparison.OrdinalIgnoreCase) ||
                host.Split('.')[0].Equals(machine, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch
        {
            // ignore
        }

        return false;
    }
}
