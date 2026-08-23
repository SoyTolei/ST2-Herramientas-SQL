using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace SBBackup.Services;

/// <summary>
/// Lee <c>manager..dbo.EJE</c> y resuelve <c>eje_descrip</c> cuando la base se vincula
/// de forma estricta a <c>emp_codigo</c> + <c>eje_nroeje</c> (igualdad numérica).
/// </summary>
public sealed class EjeExerciseLinkService
{
    public sealed record EjeDatabaseLink(string? EmpCode, string Descrip);

    public sealed record EjeSchemaInfo(
        string? QualifiedTableName,
        string? LoadError,
        string? EmpCodigoColumn,
        string? DescripColumn,
        string? NroejeColumn,
        int RowCount,
        bool EmpCodigoFound,
        bool DescripFound,
        bool NroejeFound);

    public sealed class EjeCatalog
    {
        private readonly Dictionary<string, string> _descripByDatabase;
        private readonly List<EjeRow> _rows;

        internal EjeCatalog(
            Dictionary<string, string> descripByDatabase,
            List<EjeRow> rows,
            EjeSchemaInfo? schema)
        {
            _descripByDatabase = descripByDatabase;
            _rows = rows;
            Schema = schema;
        }

        public EjeSchemaInfo? Schema { get; }
        public IReadOnlyList<EjeRow> Rows => _rows;
        public IReadOnlyDictionary<string, string> DescripByDatabase => _descripByDatabase;

        public IReadOnlyDictionary<string, EjeDatabaseLink> DatabaseToEmpCode { get; internal init; }
            = new Dictionary<string, EjeDatabaseLink>();

        public void BindDescrip(string databaseName, string? descrip)
        {
            if (string.IsNullOrEmpty(descrip))
                _descripByDatabase.Remove(databaseName);
            else
                _descripByDatabase[databaseName] = descrip;
        }

        public string? ResolveDescrip(string physicalName, string? matchedEmpCode)
        {
            physicalName = physicalName.Trim();

            // Con emp conocido, re-resolver (el cache temprano puede no tener MatchedEmpCode).
            if (!string.IsNullOrWhiteSpace(matchedEmpCode))
            {
                var resolved = TryResolveDescripForDatabase(physicalName, _rows, matchedEmpCode);
                if (!string.IsNullOrEmpty(resolved))
                    return resolved;
            }

            if (_descripByDatabase.TryGetValue(physicalName, out var direct) && direct.Length > 0)
                return direct;

            return TryResolveDescripForDatabase(physicalName, _rows, matchedEmpCode);
        }
    }

    public sealed class EjeRow
    {
        public string? EmpCode { get; init; }
        public string Descrip { get; init; } = "";
        public string? EjeNroeje { get; init; }
        public string? EjeCodigo { get; init; }
        public List<string> TextTokens { get; } = [];
    }

    public async Task<EjeCatalog> LoadEjeCatalogAsync(
        string masterConnectionString,
        IReadOnlySet<string> onlineDatabaseNames,
        string managerDatabaseName,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        var descripByDb = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<EjeRow>();
        var dbToEmp = new Dictionary<string, EjeDatabaseLink>(StringComparer.OrdinalIgnoreCase);
        var manager = string.IsNullOrWhiteSpace(managerDatabaseName) ? "manager" : managerDatabaseName.Trim();
        EjeSchemaInfo? schemaInfo = null;

        try
        {
            var builder = new SqlConnectionStringBuilder(masterConnectionString) { InitialCatalog = manager };
            await using var conn = new SqlConnection(builder.ConnectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            var tableRef = await ResolveEjeTableRefAsync(conn, cancellationToken).ConfigureAwait(false);
            if (tableRef is null)
            {
                const string err = "No se encontró la tabla ni vista EJE en la base manager.";
                log?.Report(err);
                schemaInfo = new EjeSchemaInfo(null, err, null, null, null, 0, false, false, false);
                return new EjeCatalog(descripByDb, rows, schemaInfo) { DatabaseToEmpCode = dbToEmp };
            }

            var columnNames = await ListTableColumnsAsync(conn, tableRef, cancellationToken).ConfigureAwait(false);
            var empCol = PickColumn(columnNames, "emp_codigo")
                         ?? PickColumnContaining(columnNames, "emp", "cod");
            var nroejeCol = PickColumn(columnNames, "eje_nroeje")
                            ?? PickColumnContaining(columnNames, "nroeje");
            var descripCol = PickColumn(columnNames, "eje_descrip")
                             ?? PickColumnContaining(columnNames, "descrip");
            var codigoCol = PickColumn(columnNames, "eje_codigo", "eje_cod");

            if (empCol is null)
            {
                var cols = string.Join(", ", columnNames.Take(20));
                var err = $"Tabla {tableRef.Qualified} sin columna emp_codigo. Columnas: {cols}";
                log?.Report(err);
                schemaInfo = new EjeSchemaInfo(tableRef.Qualified, err, null, descripCol, nroejeCol, 0, false, descripCol is not null, nroejeCol is not null);
                return new EjeCatalog(descripByDb, rows, schemaInfo) { DatabaseToEmpCode = dbToEmp };
            }

            var selectParts = new List<string>
            {
                $"CAST([{empCol}] AS nvarchar(128)) AS _emp"
            };
            if (nroejeCol is not null)
                selectParts.Add($"CAST([{nroejeCol}] AS nvarchar(64)) AS _nro");
            if (descripCol is not null)
                selectParts.Add($"CAST([{descripCol}] AS nvarchar(500)) AS _desc");
            if (codigoCol is not null && !codigoCol.Equals(nroejeCol, StringComparison.OrdinalIgnoreCase))
                selectParts.Add($"CAST([{codigoCol}] AS nvarchar(64)) AS _cod");

            var sql = $"SELECT {string.Join(", ", selectParts)} FROM {tableRef.Qualified} WITH (NOLOCK)";
            await using var dataCmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
            await using var r = await dataCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            var ordEmp = r.GetOrdinal("_emp");
            var ordNro = ColumnOrdinalOrNegative(r, "_nro");
            var ordDesc = ColumnOrdinalOrNegative(r, "_desc");
            var ordCod = ColumnOrdinalOrNegative(r, "_cod");

            while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var emp = AsTrimmedInvariant(r, ordEmp);
                var ejeNroeje = ordNro >= 0 ? AsTrimmedInvariant(r, ordNro) : null;
                var descrip = ordDesc >= 0
                    ? NormalizeEjeDescrip(AsTrimmedInvariant(r, ordDesc))
                    : "";
                var ejeCodigo = ordCod >= 0 ? AsTrimmedInvariant(r, ordCod) : null;

                rows.Add(new EjeRow
                {
                    EmpCode = emp,
                    Descrip = descrip,
                    EjeNroeje = ejeNroeje,
                    EjeCodigo = ejeCodigo
                });
            }

            foreach (var db in onlineDatabaseNames)
            {
                var link = TryResolveLinkForDatabase(db, rows);
                if (link is null || string.IsNullOrEmpty(link.Descrip))
                    continue;

                descripByDb[db] = link.Descrip;
                if (!string.IsNullOrEmpty(link.EmpCode))
                    dbToEmp[db] = link;
            }

            schemaInfo = new EjeSchemaInfo(
                tableRef.Qualified,
                null,
                empCol,
                descripCol,
                nroejeCol,
                rows.Count,
                true,
                descripCol is not null,
                nroejeCol is not null);

            var withNroeje = rows.Count(x => !string.IsNullOrEmpty(x.EjeNroeje));
            var withDescrip = rows.Count(x => x.Descrip.Length > 0);
            var resolvedCount = descripByDb.Count;
            log?.Report(
                $"EJE ({tableRef.Qualified}): {rows.Count} fila(s), {withDescrip} con eje_descrip, {withNroeje} con eje_nroeje, {resolvedCount} base(s) vinculada(s).");
            if (descripCol is null)
                log?.Report("AVISO: no se encontró columna eje_descrip.");
            if (nroejeCol is null)
                log?.Report("AVISO: no se encontró columna eje_nroeje.");
        }
        catch (Exception ex)
        {
            log?.Report($"Lectura EJE omitida: {ex.Message}");
            schemaInfo ??= new EjeSchemaInfo(null, ex.Message, null, null, null, 0, false, false, false);
        }

        return new EjeCatalog(descripByDb, rows, schemaInfo) { DatabaseToEmpCode = dbToEmp };
    }

    public async Task<Dictionary<string, EjeDatabaseLink>> LoadDatabaseLinksFromEjeAsync(
        string masterConnectionString,
        IReadOnlySet<string> onlineDatabaseNames,
        string managerDatabaseName,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        var catalog = await LoadEjeCatalogAsync(
                masterConnectionString,
                onlineDatabaseNames,
                managerDatabaseName,
                log,
                cancellationToken)
            .ConfigureAwait(false);

        return new Dictionary<string, EjeDatabaseLink>(catalog.DatabaseToEmpCode, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<Dictionary<string, string>> LoadDatabaseToEmpCodeFromEjeAsync(
        string masterConnectionString,
        IReadOnlySet<string> onlineDatabaseNames,
        string managerDatabaseName,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        var links = await LoadDatabaseLinksFromEjeAsync(
                masterConnectionString,
                onlineDatabaseNames,
                managerDatabaseName,
                log,
                cancellationToken)
            .ConfigureAwait(false);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (db, link) in links)
        {
            if (!string.IsNullOrEmpty(link.EmpCode))
                map[db] = link.EmpCode;
        }

        return map;
    }

    /// <summary>
    /// Nombre de base = prefijo + dígitos, o <paramref name="matchedEmpCode"/> + sufijo numérico.
    /// El sufijo debe coincidir numéricamente con <c>eje_nroeje</c> (sin matches parciales).
    /// </summary>
    internal static string? TryResolveDescripForDatabase(
        string physicalName,
        IReadOnlyList<EjeRow> rows,
        string? matchedEmpCode = null)
        => TryResolveLinkForDatabase(physicalName, rows, matchedEmpCode)?.Descrip;

    /// <summary>
    /// Igual que <see cref="TryResolveDescripForDatabase"/> pero también devuelve el <c>emp_codigo</c> de la fila EJE.
    /// Ante empate de score, no asigna (null) para evitar nombres cruzados/repetidos.
    /// </summary>
    internal static EjeDatabaseLink? TryResolveLinkForDatabase(
        string physicalName,
        IReadOnlyList<EjeRow> rows,
        string? matchedEmpCode = null)
    {
        physicalName = physicalName.Trim();
        if (physicalName.Length == 0)
            return null;

        if (!TryParseDatabaseParts(physicalName, out var empFromDb, out var exerciseDigits))
        {
            empFromDb = "";
            if (!TryExtractTrailingDigits(physicalName, out exerciseDigits))
                return null;
        }

        if (exerciseDigits.Length == 0)
            return null;

        var empKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (empFromDb.Length > 0)
            empKeys.Add(empFromDb);
        if (!string.IsNullOrWhiteSpace(matchedEmpCode))
            empKeys.Add(matchedEmpCode.Trim());

        if (empKeys.Count == 0)
            return null;

        var bestScore = 0;
        EjeRow? best = null;
        var tie = false;

        foreach (var row in rows)
        {
            if (row.Descrip.Length == 0)
                continue;

            var emp = row.EmpCode?.Trim();
            if (string.IsNullOrEmpty(emp) || !empKeys.Contains(emp))
                continue;

            var score = ScoreNumberMatch(row, exerciseDigits);
            if (score <= 0)
                continue;

            if (score > bestScore)
            {
                bestScore = score;
                best = row;
                tie = false;
            }
            else if (score == bestScore)
            {
                // Mismo score: si es la misma descripción/emp+nro, no es empate conflictivo.
                if (best is not null
                    && string.Equals(best.Descrip, row.Descrip, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(best.EmpCode?.Trim(), emp, StringComparison.OrdinalIgnoreCase))
                    continue;

                tie = true;
            }
        }

        if (best is null || tie)
            return null;

        return new EjeDatabaseLink(best.EmpCode?.Trim(), best.Descrip);
    }

    private static readonly Regex PrefixAndDigits = new(
        @"^(?<prefix>[A-Za-z][A-Za-z0-9_]*)(?<digits>\d+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TrailingDigits = new(@"\d+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static bool TryParseDatabaseParts(string physicalName, out string empPrefix, out string exerciseDigits)
    {
        empPrefix = "";
        exerciseDigits = "";
        var m = PrefixAndDigits.Match(physicalName.Trim());
        if (!m.Success)
            return false;

        empPrefix = m.Groups["prefix"].Value.Trim();
        exerciseDigits = m.Groups["digits"].Value.Trim();
        return empPrefix.Length > 0 && exerciseDigits.Length > 0;
    }

    private static bool TryExtractTrailingDigits(string physicalName, out string digits)
    {
        var m = TrailingDigits.Match(physicalName.Trim());
        if (!m.Success)
        {
            digits = "";
            return false;
        }

        digits = m.Value;
        return digits.Length > 0;
    }

    private static readonly CultureInfo DisplayCulture = CultureInfo.GetCultureInfo("es-AR");

    internal static string NormalizeEjeDescrip(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        var collapsed = Regex.Replace(raw.Trim(), @"\s+", " ");
        var lower = collapsed.ToLower(DisplayCulture);
        var titled = DisplayCulture.TextInfo.ToTitleCase(lower);

        titled = Regex.Replace(titled, @"\bNo\.\s*", "No. ", RegexOptions.CultureInvariant);
        return titled.Trim();
    }

    /// <summary>
    /// Compara el sufijo del nombre de la base (ej. <c>0015</c>) con <c>eje_nroeje</c> por igualdad numérica
    /// (<c>15</c> == <c>0015</c>). Sin matches parciales por EndsWith.
    /// </summary>
    internal static bool ExerciseNumberMatches(string? ejeField, string dbSuffix)
        => ScoreFieldDigits(ejeField, dbSuffix) > 0;

    /// <summary>
    /// Score: 4 = match exacto de dígitos en eje_nroeje; 3 = igualdad numérica en eje_nroeje;
    /// 2 = exacto en eje_codigo (solo si no hay nroeje); 1 = numérico en eje_codigo (solo si no hay nroeje).
    /// </summary>
    private static int ScoreNumberMatch(EjeRow row, string suffix)
    {
        var hasNroeje = !string.IsNullOrWhiteSpace(row.EjeNroeje);
        if (hasNroeje)
        {
            var nroScore = ScoreFieldDigits(row.EjeNroeje, suffix);
            if (nroScore > 0)
                return nroScore == 2 ? 4 : 3;
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(row.EjeCodigo))
        {
            var codScore = ScoreFieldDigits(row.EjeCodigo, suffix);
            if (codScore > 0)
                return codScore == 2 ? 2 : 1;
        }

        return 0;
    }

    /// <summary>2 = mismos dígitos; 1 = mismo entero; 0 = no coincide.</summary>
    private static int ScoreFieldDigits(string? ejeField, string dbSuffix)
    {
        if (string.IsNullOrWhiteSpace(ejeField))
            return 0;

        var fieldDigits = OnlyDigits(ejeField);
        var suffixDigits = OnlyDigits(dbSuffix);
        if (fieldDigits.Length == 0 || suffixDigits.Length == 0)
            return 0;

        if (fieldDigits.Equals(suffixDigits, StringComparison.Ordinal))
            return 2;

        if (TryParseExerciseInt(fieldDigits, out var fieldNum)
            && TryParseExerciseInt(suffixDigits, out var suffixNum)
            && fieldNum == suffixNum)
            return 1;

        return 0;
    }

    private static string OnlyDigits(string value)
    {
        var chars = value.Where(char.IsDigit).ToArray();
        return chars.Length == 0 ? "" : new string(chars);
    }

    private static bool TryParseExerciseInt(string digits, out int number)
    {
        number = 0;
        return digits.Length > 0
               && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    private sealed record EjeTableRef(string Schema, string Table)
    {
        public string Qualified => $"[{Schema}].[{Table}]";
    }

    private static async Task<EjeTableRef?> ResolveEjeTableRefAsync(SqlConnection conn, CancellationToken cancellationToken)
    {
        const string fromInfoSchema = """
            SELECT TOP (1) TABLE_SCHEMA, TABLE_NAME
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_TYPE IN ('BASE TABLE', 'VIEW')
              AND TABLE_NAME = 'EJE' COLLATE Latin1_General_CI_AI
            ORDER BY CASE WHEN TABLE_SCHEMA = 'dbo' THEN 0 ELSE 1 END;
            """;
        await using (var cmd = new SqlCommand(fromInfoSchema, conn) { CommandTimeout = 15 })
        {
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var schema = reader.GetString(0);
                var table = reader.GetString(1);
                return new EjeTableRef(schema, table);
            }
        }

        const string fromSys = """
            SELECT TOP (1) s.name, o.name
            FROM sys.objects o
            INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
            WHERE o.type IN ('U', 'V')
              AND o.name = 'EJE' COLLATE Latin1_General_CI_AI
            ORDER BY CASE WHEN s.name = 'dbo' THEN 0 ELSE 1 END;
            """;
        await using var cmd2 = new SqlCommand(fromSys, conn) { CommandTimeout = 15 };
        await using var reader2 = await cmd2.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader2.ReadAsync(cancellationToken).ConfigureAwait(false))
            return new EjeTableRef(reader2.GetString(0), reader2.GetString(1));

        return null;
    }

    private static async Task<List<string>> ListTableColumnsAsync(
        SqlConnection conn,
        EjeTableRef table,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COLUMN_NAME
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table
            ORDER BY ORDINAL_POSITION;
            """;
        var list = new List<string>();
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 15 };
        cmd.Parameters.AddWithValue("@schema", table.Schema);
        cmd.Parameters.AddWithValue("@table", table.Table);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            list.Add(reader.GetString(0));
        return list;
    }

    private static string? PickColumn(IReadOnlyList<string> columns, params string[] exactNames)
    {
        foreach (var exact in exactNames)
        {
            foreach (var col in columns)
            {
                if (col.Equals(exact, StringComparison.OrdinalIgnoreCase))
                    return col;
            }
        }

        return null;
    }

    private static string? PickColumnContaining(IReadOnlyList<string> columns, string part1, string part2)
    {
        foreach (var col in columns)
        {
            if (col.Contains(part1, StringComparison.OrdinalIgnoreCase)
                && col.Contains(part2, StringComparison.OrdinalIgnoreCase))
                return col;
        }

        return null;
    }

    private static string? PickColumnContaining(IReadOnlyList<string> columns, string fragment)
    {
        foreach (var col in columns)
        {
            if (col.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return col;
        }

        return null;
    }

    private static int ColumnOrdinalOrNegative(SqlDataReader reader, string name)
    {
        try
        {
            return reader.GetOrdinal(name);
        }
        catch (IndexOutOfRangeException)
        {
            return -1;
        }
    }

    private static string? AsTrimmedInvariant(SqlDataReader r, int ordinal)
    {
        if (r.IsDBNull(ordinal))
            return null;
        var v = r.GetValue(ordinal);
        return Convert.ToString(v, CultureInfo.InvariantCulture)?.Trim();
    }
}
