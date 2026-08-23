using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace SBBackup.Services;

/// <summary>
/// Le da a la IA el esquema REAL (tablas/columnas) de la base que está conectada en ese momento,
/// leído en vivo desde el propio servidor del cliente. Reemplaza cualquier glosario "adivinado":
/// cada instalación de Bejerman tiene sus propias tablas, así que la fuente de verdad es la base
/// misma, no una descripción genérica escrita a mano.
/// </summary>
public static class SchemaContextBuilder
{
    private const int MaxTables = 8;
    private const int MaxColumnsPerTable = 40;

    private static readonly HashSet<string> SqlNoise = new(StringComparer.OrdinalIgnoreCase)
    {
        "dbo", "select", "from", "where", "join", "inner", "left", "right", "outer", "on",
        "set", "values", "into", "update", "delete", "insert", "table", "as", "go", "declare",
        "exec", "execute", "begin", "end", "and", "or", "not", "null", "is", "top", "order",
        "by", "group", "having", "distinct", "union", "all", "case", "when", "then", "else",
        "master", "sysobjects"
    };

    private static readonly Regex TableRefRegex = new(
        @"(?:\bFROM\b|\bJOIN\b|\bINTO\b|\bUPDATE\b|\bALTER\s+TABLE\b|\bTABLE\b)\s+\[?([A-Za-z0-9_\.\[\]]+)\]?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Extrae nombres de tabla mencionados en un script/mensaje (heurístico, sin parsear SQL real).</summary>
    public static IReadOnlyList<string> ExtractTableNames(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return [];

        var names = new List<string>();
        foreach (Match m in TableRefRegex.Matches(sql))
        {
            var raw = m.Groups[1].Value.Trim().Trim('[', ']');
            var lastPart = raw.Split('.')[^1].Trim('[', ']');
            if (lastPart.Length == 0 || SqlNoise.Contains(lastPart))
                continue;
            if (!names.Contains(lastPart, StringComparer.OrdinalIgnoreCase))
                names.Add(lastPart);
            if (names.Count >= MaxTables)
                break;
        }
        return names;
    }

    /// <summary>
    /// Se conecta a <paramref name="database"/> (en el mismo servidor de <paramref name="masterConnectionString"/>)
    /// y devuelve, para las tablas encontradas, sus columnas reales. Si algo falla (permisos, tabla
    /// inexistente, sin conexión), devuelve null en silencio: la IA sigue funcionando sin ese contexto.
    /// </summary>
    public static async Task<string?> BuildAsync(
        string? masterConnectionString,
        string? database,
        IReadOnlyList<string> tableNames,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(masterConnectionString)
            || string.IsNullOrWhiteSpace(database)
            || tableNames.Count == 0)
            return null;

        try
        {
            var builder = new SqlConnectionStringBuilder(masterConnectionString) { InitialCatalog = database };
            await using var conn = new SqlConnection(builder.ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            var sb = new StringBuilder();
            var foundAny = false;

            foreach (var table in tableNames)
            {
                var cols = await ReadColumnsAsync(conn, table, ct).ConfigureAwait(false);
                if (cols.Count == 0)
                    continue;

                foundAny = true;
                sb.AppendLine($"Tabla {table} (base {database}): " + string.Join(", ", cols));
            }

            return foundAny ? sb.ToString().TrimEnd() : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<List<string>> ReadColumnsAsync(SqlConnection conn, string table, CancellationToken ct)
    {
        var cols = new List<string>();

        await using var cmd = new SqlCommand(
            """
            SELECT TOP (@maxCols) c.name AS Columna, ty.name AS Tipo, c.is_nullable AS Nulable
            FROM sys.tables t
            INNER JOIN sys.columns c ON c.object_id = t.object_id
            INNER JOIN sys.types ty ON c.user_type_id = ty.user_type_id
            WHERE t.name = @table
            ORDER BY c.column_id
            """, conn);
        cmd.Parameters.AddWithValue("@table", table);
        cmd.Parameters.AddWithValue("@maxCols", MaxColumnsPerTable);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var colName = reader.GetString(0);
            var type = reader.GetString(1);
            var nullable = reader.GetBoolean(2);
            cols.Add(nullable ? $"{colName} ({type}, nullable)" : $"{colName} ({type})");
        }

        return cols;
    }
}
