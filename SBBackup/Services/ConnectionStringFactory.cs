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
        var b = new SqlConnectionStringBuilder
        {
            DataSource = NormalizeDataSource(server),
            IntegratedSecurity = integratedSecurity,
            TrustServerCertificate = true,
            Encrypt = encrypt ?? SqlConnectionEncryptOption.Mandatory,
            ConnectTimeout = Math.Clamp(connectTimeoutSeconds, 5, 90),
            // Si el DNS devuelve varias IPs (típico en redes de sucursal / VLAN),
            // evita quedarse colgado en la primera IP muerta durante el pre-login.
            MultiSubnetFailover = true,
            // La prueba de conexión no debe dejar una entrada mala en el pool.
            Pooling = true
        };

        if (!integratedSecurity)
        {
            b.UserID = sqlUser?.Trim() ?? "";
            b.Password = sqlPassword ?? "";
        }

        return b.ConnectionString;
    }

    /// <summary>
    /// Normaliza el DataSource: quita espacios y, si es un host remoto sin prefijo,
    /// fuerza TCP para no intentar Named Pipes (suele fallar entre terminales).
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

        // Local: no forzar tcp (LocalDB / shared memory pueden ser válidos).
        if (s is "." or "(local)" ||
            s.StartsWith("(localdb)", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase))
            return s;

        return "tcp:" + s;
    }
}
