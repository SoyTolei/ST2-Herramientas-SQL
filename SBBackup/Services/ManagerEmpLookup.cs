using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace SBBackup.Services;

/// <summary>
/// Nombres de empresa desde la base <c>manager</c>, tabla <c>dbo.EMP</c> (emp_codigo ↔ nombre de base).
/// </summary>
public sealed class ManagerEmpLookup(AppConfig config)
{
    private static readonly Regex TrailingDigits = new(@"\d+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<IReadOnlyDictionary<string, string>> ResolveNamesAsync(
        string masterConnectionString,
        IReadOnlyList<string> databaseNames,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var manager = config.ManagerDatabaseName.Trim();
        if (string.IsNullOrEmpty(manager))
            manager = "manager";

        List<(string CodeKey, string Raz)> rows;
        try
        {
            rows = await LoadEmpRowsAsync(masterConnectionString, manager, log, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log?.Report($"No se pudo leer manager..EMP: {ex.Message}");
            return map;
        }

        foreach (var db in databaseNames)
        {
            if (map.ContainsKey(db))
                continue;

            var m = TryMatchEmp(db, rows);
            if (m is not null && !string.IsNullOrWhiteSpace(m.Value.Raz))
                map[db] = m.Value.Raz.Trim();
        }

        return map;
    }

    public async Task<IReadOnlyList<(string Code, string Raz)>> GetEmpDirectoryAsync(
        string connectionString,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        var manager = config.ManagerDatabaseName.Trim();
        if (string.IsNullOrEmpty(manager))
            manager = "manager";

        try
        {
            return await LoadEmpRowsAsync(connectionString, manager, log, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log?.Report($"No se pudo leer manager..EMP: {ex.Message}");
            return [];
        }
    }

    public static (string Code, string Raz)? TryMatchEmp(string databaseName, IReadOnlyList<(string CodeKey, string Raz)> emps)
    {
        (string Code, string Raz)? best = null;
        var bestLen = -1;

        foreach (var (code, raz) in emps)
        {
            if (string.IsNullOrEmpty(code))
                continue;

            if (!DatabaseMatchesCode(databaseName, code))
                continue;

            // El código más largo gana (FACI antes que FA), para no cruzar empresas.
            if (code.Length > bestLen)
            {
                bestLen = code.Length;
                best = (code.Trim(), raz.Trim());
            }
        }

        return best;
    }

    public static string? TryGetRazsocForEmpCode(IReadOnlyList<(string CodeKey, string Raz)> emps, string empCode)
    {
        if (string.IsNullOrWhiteSpace(empCode))
            return null;
        var key = empCode.Trim();
        foreach (var (code, raz) in emps)
        {
            if (code.Equals(key, StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrWhiteSpace(raz) ? null : raz.Trim();
        }

        return null;
    }

    private static async Task<List<(string CodeKey, string Raz)>> LoadEmpRowsAsync(
        string masterConnectionString,
        string managerDatabase,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder(masterConnectionString) { InitialCatalog = managerDatabase };
        await using var conn = new SqlConnection(builder.ConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        Exception? last = null;
        foreach (var sql in EmpSelectStatements())
        {
            try
            {
                var list = new List<(string CodeKey, string Raz)>();
                await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
                await using var r = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken)
                    .ConfigureAwait(false);
                while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var code = Convert.ToString(r.GetValue(0), System.Globalization.CultureInfo.InvariantCulture)?.Trim() ?? "";
                    var raz = r.IsDBNull(1) ? "" : Convert.ToString(r.GetValue(1), System.Globalization.CultureInfo.InvariantCulture)?.Trim() ?? "";
                    if (code.Length > 0 && raz.Length > 0)
                        list.Add((code, raz));
                }

                if (list.Count > 0)
                {
                    log?.Report($"Empresas en manager: {list.Count} filas en EMP.");
                    return list;
                }
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        if (last is not null)
            log?.Report($"Lectura EMP sin filas útiles: {last.Message}");

        return [];
    }

    private static IEnumerable<string> EmpSelectStatements()
    {
        yield return """
                     SELECT CAST(emp_codigo AS nvarchar(64)) AS c,
                            NULLIF(LTRIM(RTRIM(CAST(emp_razsoc AS nvarchar(400)))), N'') AS r
                     FROM dbo.EMP WITH (NOLOCK);
                     """;
        yield return """
                     SELECT CAST(emp_codigo AS nvarchar(64)) AS c,
                            NULLIF(LTRIM(RTRIM(CAST(emp_razsoc AS nvarchar(400)))), N'') AS r
                     FROM dbo.Emp WITH (NOLOCK);
                     """;
    }

    internal static bool DatabaseMatchesCode(string databaseName, string empCode)
    {
        var db = databaseName.Trim();
        var code = empCode.Trim();
        if (code.Length == 0)
            return false;

        if (db.EndsWith(code, StringComparison.OrdinalIgnoreCase))
            return true;

        if (code.Length >= 2 && db.StartsWith(code, StringComparison.OrdinalIgnoreCase))
            return true;

        if (long.TryParse(code, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var codeNum))
        {
            var m = TrailingDigits.Match(db);
            if (m.Success && long.TryParse(m.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var suffixNum) && suffixNum == codeNum)
                return true;
        }

        return false;
    }
}
