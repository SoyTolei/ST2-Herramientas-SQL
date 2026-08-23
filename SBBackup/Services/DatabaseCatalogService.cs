using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using SBBackup.Models;

namespace SBBackup.Services;

public sealed class DatabaseCatalogService(AppConfig config)
{
    // Patrón histórico (angosto). Se trata como "sin filtro" porque dejaba afuera ejercicios
    // contables con nombres libres (p. ej. MIR_2025) que varían en cada cliente.
    private const string LegacyNarrowPattern = "(?i)^(manager|SBDA.*|SJ.*|base\\d+)$";

    public async Task<IReadOnlyList<ServerDatabaseRow>> ListCandidateDatabasesAsync(
        string connectionString,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        // Por defecto NO filtramos por nombre: mostramos todas las bases en línea menos
        // sistema y plantillas genéricas. Sólo aplicamos regex si appsettings define una
        // distinta del patrón histórico angosto.
        var custom = config.DatabaseNameIncludeRegex?.Trim();
        Regex? filter = null;
        if (!string.IsNullOrWhiteSpace(custom)
            && !custom.Equals(LegacyNarrowPattern, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                filter = new Regex(custom, RegexOptions.CultureInvariant);
            }
            catch (Exception ex)
            {
                log?.Report($"Regex en appsettings inválida; se listan todas las bases Bejerman. ({ex.Message})");
                filter = null;
            }
        }

        var list = new List<string>();

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        const string sql = """
            SELECT d.[name]
            FROM sys.databases d
            WHERE d.[state] = 0
            ORDER BY d.[name];
            """;

        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = r.GetString(0);

            if (IsSystemDatabase(name))
                continue;

            if (IsExcludedBejermanGeneric(name))
                continue;

            if (filter is not null && !filter.IsMatch(name))
                continue;

            list.Add(name);
        }

        return list
            .Select(x => new ServerDatabaseRow { PhysicalName = x, FriendlyName = "" })
            .ToList();
    }

    /// <summary>Todas las bases en línea, excluyendo sistema y plantillas Bejerman genéricas (sin filtro por nombre SBDA/SJ).</summary>
    public async Task<HashSet<string>> ListOnlineDatabaseNamesExcludedAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        const string sql = """
            SELECT d.[name]
            FROM sys.databases d
            WHERE d.[state] = 0
            ORDER BY d.[name];
            """;

        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = r.GetString(0);
            if (IsSystemDatabase(name))
                continue;
            if (IsExcludedBejermanGeneric(name))
                continue;
            set.Add(name);
        }

        return set;
    }

    private static bool IsExcludedBejermanGeneric(string name)
    {
        if (name.Equals("SBDAMODE", StringComparison.OrdinalIgnoreCase)
            || name.Equals("SJGUIA", StringComparison.OrdinalIgnoreCase))
            return true;

        ReadOnlySpan<string> extra =
        [
            "SBCH",
            "MODC2010",
            "MODP2010",
            "MSOC2010",
            // Infraestructura SQL Server (no son bases Bejerman)
            "ReportServer",
            "ReportServerTempDB",
            "SSISDB",
            "distribution"
        ];
        foreach (var x in extra)
        {
            if (name.Equals(x, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsSystemDatabase(string name) =>
        name.Equals("master", StringComparison.OrdinalIgnoreCase)
        || name.Equals("model", StringComparison.OrdinalIgnoreCase)
        || name.Equals("msdb", StringComparison.OrdinalIgnoreCase)
        || name.Equals("tempdb", StringComparison.OrdinalIgnoreCase);
}
