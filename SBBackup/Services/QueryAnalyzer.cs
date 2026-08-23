using System.Text;
using System.Text.RegularExpressions;

namespace SBBackup.Services;

/// <summary>
/// Análisis heurístico (orientativo) de un script SQL: detecta qué tipo de operaciones
/// contiene, qué tablas toca y si tiene sentencias peligrosas. No ejecuta nada.
/// </summary>
public static class QueryAnalyzer
{
    public sealed class Analysis
    {
        public List<string> Operations { get; } = [];
        public List<string> Tables { get; } = [];
        public List<string> Warnings { get; } = [];
        public int StatementCount { get; set; }
        public bool HasModifications { get; set; }
        /// <summary>
        /// True si el script incluye operaciones que SQL Server no permite dentro de una
        /// transacción de usuario (ALTER DATABASE, SHRINKFILE, BACKUP, etc.).
        /// </summary>
        public bool RequiresNoTransaction { get; set; }
        public bool IsEmpty { get; set; }
    }

    private static readonly string[] ModifyingKeywords =
    [
        "INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE",
        "DROP", "ALTER", "CREATE", "EXEC", "EXECUTE",
        "GRANT", "REVOKE", "DENY"
    ];

    public static Analysis Analyze(string rawSql)
    {
        var result = new Analysis();
        var sql = StripCommentsAndStrings(rawSql);

        if (string.IsNullOrWhiteSpace(sql))
        {
            result.IsEmpty = true;
            return result;
        }

        // Detectamos los verbos SQL en CUALQUIER parte del script (ya sin comentarios ni
        // literales). Esto es robusto ante la falta de ';', prefijos como "USE base",
        // separadores GO y saltos de línea. Preferimos sobre-detectar (más backups) antes
        // que dejar pasar un UPDATE/DELETE sin protección.
        const string verbPattern =
            @"\b(SELECT|INSERT|UPDATE|DELETE|MERGE|TRUNCATE|DROP|ALTER|CREATE|EXEC|EXECUTE|GRANT|REVOKE|DENY|USE)\b";

        var ops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var verbCount = 0;
        foreach (Match m in Regex.Matches(sql, verbPattern, RegexOptions.IgnoreCase))
        {
            var verb = m.Groups[1].Value.ToUpperInvariant();
            ops.Add(verb == "EXECUTE" ? "EXEC" : verb);
            verbCount++;
        }

        if (ops.Count == 0)
        {
            var firstWord = FirstKeyword(sql);
            if (!string.IsNullOrEmpty(firstWord))
                ops.Add(firstWord.ToUpperInvariant());
        }

        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in ExtractTables(sql))
            tables.Add(t);

        result.StatementCount = Math.Max(1, verbCount);
        result.Operations.AddRange(ops.OrderBy(x => x));
        result.Tables.AddRange(tables.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase));
        result.HasModifications = ops.Any(o =>
            ModifyingKeywords.Contains(o, StringComparer.OrdinalIgnoreCase))
            || DetectsModifyingProcedures(rawSql);

        // Varias de estas ops suelen ir dentro de EXEC dinámico: miramos el SQL crudo.
        result.RequiresNoTransaction = DetectsNoTransactionOps(rawSql);

        AddWarnings(sql, result.Warnings);

        return result;
    }

    private static bool DetectsModifyingProcedures(string rawSql) =>
        !string.IsNullOrWhiteSpace(rawSql)
        && Regex.IsMatch(
            rawSql,
            @"\bsp_(addlogin|droplogin|grantdbaccess|revokedbaccess|addrolemember|droprolemember|addsrvrolemember|dropsrvrolemember)\b",
            RegexOptions.IgnoreCase);

    /// <summary>
    /// Operaciones que SQL Server rechaza dentro de una transacción multi-statement.
    /// Se busca en el texto original porque a menudo aparecen armadas como string + EXEC.
    /// </summary>
    private static bool DetectsNoTransactionOps(string rawSql)
    {
        if (string.IsNullOrWhiteSpace(rawSql))
            return false;

        return Regex.IsMatch(rawSql, @"\bALTER\s+DATABASE\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(rawSql, @"\bDBCC\s+SHRINK(FILE|DATABASE)\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(rawSql, @"\bDBCC\s+CHECKDB\b.*\bREPAIR_", RegexOptions.IgnoreCase | RegexOptions.Singleline)
            || Regex.IsMatch(rawSql, @"\bSHRINKFILE\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(rawSql, @"\bSET\s+RECOVERY\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(rawSql, @"\bBACKUP\s+(DATABASE|LOG)\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(rawSql, @"\bRESTORE\s+(DATABASE|LOG)\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(rawSql, @"\b(CREATE|DROP)\s+DATABASE\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(rawSql, @"\bsp_(addlogin|droplogin|addsrvrolemember|dropsrvrolemember)\b", RegexOptions.IgnoreCase);
    }

    private static void AddWarnings(string sql, List<string> warnings)
    {
        var upper = sql.ToUpperInvariant();

        if (HasVerbWithoutWhere(upper, "UPDATE"))
            warnings.Add("Hay un UPDATE sin WHERE: afecta TODAS las filas de la tabla.");

        if (HasVerbWithoutWhere(upper, "DELETE"))
            warnings.Add("Hay un DELETE sin WHERE: borra TODAS las filas de la tabla.");

        if (Regex.IsMatch(upper, @"\bTRUNCATE\s+TABLE\b"))
            warnings.Add("Hay un TRUNCATE TABLE: vacía la tabla por completo y no se puede deshacer fácilmente.");

        if (Regex.IsMatch(upper, @"\bDROP\s+(TABLE|DATABASE|PROCEDURE|VIEW|FUNCTION|INDEX)\b"))
            warnings.Add("Hay un DROP: elimina objetos de la base de forma permanente.");
    }

    /// <summary>True si alguna sentencia con el verbo dado no tiene WHERE antes del próximo ';'.</summary>
    private static bool HasVerbWithoutWhere(string upper, string verb)
    {
        foreach (Match m in Regex.Matches(upper, $@"\b{verb}\b"))
        {
            var semi = upper.IndexOf(';', m.Index);
            var end = semi < 0 ? upper.Length : semi;
            var segment = upper[m.Index..end];
            if (!Regex.IsMatch(segment, @"\bWHERE\b"))
                return true;
        }
        return false;
    }

    private static string FirstKeyword(string stmt)
    {
        var m = Regex.Match(stmt.TrimStart(), @"^\s*([A-Za-z]+)");
        return m.Success ? m.Groups[1].Value : "";
    }

    private static IEnumerable<string> ExtractTables(string stmt)
    {
        // Captura nombres tras FROM / JOIN / INTO / UPDATE / DELETE FROM / TABLE.
        var found = new List<string>();
        var patterns = new[]
        {
            @"\bFROM\s+([A-Za-z0-9_\.\[\]]+)",
            @"\bJOIN\s+([A-Za-z0-9_\.\[\]]+)",
            @"\bINTO\s+([A-Za-z0-9_\.\[\]]+)",
            @"\bUPDATE\s+([A-Za-z0-9_\.\[\]]+)",
            @"\bTABLE\s+([A-Za-z0-9_\.\[\]]+)"
        };

        foreach (var pat in patterns)
        {
            foreach (Match m in Regex.Matches(stmt, pat, RegexOptions.IgnoreCase))
            {
                var name = m.Groups[1].Value.Trim();
                if (name.Length == 0)
                    continue;
                if (name.StartsWith('(') || name.Equals("SELECT", StringComparison.OrdinalIgnoreCase))
                    continue;
                found.Add(CleanTableName(name));
            }
        }

        return found;
    }

    private static string CleanTableName(string name) =>
        name.Replace("[", "").Replace("]", "").Trim();

    /// <summary>Quita comentarios (-- y /* */) y contenido de literales para no confundir el parser.</summary>
    private static string StripCommentsAndStrings(string sql)
    {
        if (string.IsNullOrEmpty(sql))
            return "";

        var sb = new StringBuilder(sql.Length);
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';

            if (c == '-' && next == '-')
            {
                while (i < sql.Length && sql[i] != '\n')
                    i++;
                continue;
            }

            if (c == '/' && next == '*')
            {
                i += 2;
                while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/'))
                    i++;
                i += 2;
                continue;
            }

            if (c == '\'')
            {
                sb.Append("''");
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == '\'' && i + 1 < sql.Length && sql[i + 1] == '\'')
                    {
                        i += 2;
                        continue;
                    }
                    if (sql[i] == '\'')
                    {
                        i++;
                        break;
                    }
                    i++;
                }
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }
}
