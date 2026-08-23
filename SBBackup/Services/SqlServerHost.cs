using System.Net;
using Microsoft.Data.SqlClient;

namespace SBBackup.Services;

/// <summary>
/// Resuelve el nombre de host del servidor SQL a partir de la cadena de conexión,
/// detecta si es un servidor remoto y traduce rutas locales del servidor
/// (por ej. C:\...) al recurso administrativo de red (\\SERVIDOR\C$\...).
/// Esto permite que el cliente lea el .bak que SQL escribió en su propio disco.
/// </summary>
internal static class SqlServerHost
{
    /// <summary>Extrae el host (sin instancia ni puerto) de la cadena de conexión.</summary>
    public static string? ExtractHost(string connectionString)
    {
        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            return NormalizeHost(builder.DataSource);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Normaliza un DataSource a su nombre de host puro.</summary>
    public static string? NormalizeHost(string? dataSource)
    {
        if (string.IsNullOrWhiteSpace(dataSource))
            return null;

        var s = dataSource.Trim();

        foreach (var prefix in new[] { "tcp:", "np:", "lpc:", "admin:" })
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                s = s[prefix.Length..].Trim();
                break;
            }
        }

        // Named pipe: \\servidor\pipe\...
        if (s.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var segments = s.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
                return null;
            s = segments[0];
        }

        var comma = s.IndexOf(',');
        if (comma >= 0)
            s = s[..comma];

        var backslash = s.IndexOf('\\');
        if (backslash >= 0)
            s = s[..backslash];

        s = s.Trim();
        return s.Length == 0 ? null : s;
    }

    /// <summary>True si el host apunta a la máquina local.</summary>
    public static bool IsLocal(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return true; // desconocido: asumimos local para no forzar rutas de red

        var h = host.Trim();

        if (h is "." or "(local)")
            return true;
        if (h.StartsWith("(localdb)", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(h, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        if (h is "127.0.0.1" or "::1" or "0.0.0.0")
            return true;

        var machine = Environment.MachineName;
        if (string.Equals(h, machine, StringComparison.OrdinalIgnoreCase))
            return true;

        var hShort = h.Split('.')[0];
        if (string.Equals(hShort, machine, StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            var hostName = Dns.GetHostName();
            if (string.Equals(h, hostName, StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(hShort, hostName.Split('.')[0], StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch
        {
            // sin DNS: seguimos con lo que tenemos
        }

        return false;
    }

    /// <summary>
    /// Convierte una ruta local del servidor a su equivalente por recurso administrativo.
    /// Ej.: ("SERVIDOR", "C:\\Backup\\x.bak") -> "\\\\SERVIDOR\\C$\\Backup\\x.bak".
    /// Si ya es una ruta UNC, la devuelve tal cual. Si no es traducible, devuelve null.
    /// </summary>
    public static string? ToAdminShareUnc(string? host, string? path)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(path))
            return null;

        var p = path.Trim();

        if (p.StartsWith(@"\\", StringComparison.Ordinal))
            return p; // ya es UNC, accesible desde el cliente

        if (p.Length >= 2 && char.IsLetter(p[0]) && p[1] == ':')
        {
            var drive = char.ToUpperInvariant(p[0]);
            var rest = p.Length > 2 ? p[2..].TrimStart('\\', '/') : string.Empty;
            return $@"\\{host}\{drive}$\{rest}";
        }

        return null;
    }
}
