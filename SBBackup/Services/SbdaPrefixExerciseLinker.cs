using System.Text.RegularExpressions;
using SBBackup.Models;

namespace SBBackup.Services;

/// <summary>
/// Incluye bases de ejercicio contable cuyo nombre es <c>{mismo prefijo que SBDA}{4 dígitos}</c>,
/// p. ej. <c>SBDAPRUEBA</c> → <c>PRUEBA0001</c>, <c>PRUEBA2026</c>.
/// </summary>
public static class SbdaPrefixExerciseLinker
{
    private static readonly Regex SbdaBody = new(@"^(?i)SBDA(.+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ExerciseSuffix = new(@"^(?i)(.+)(\d{4})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Agrega filas para bases online que sigan el patrón ejercicio y reutilicen el prefijo de una base SBDA ya presente.
    /// Debe ejecutarse cuando las filas SBDA ya tengan resuelto <see cref="ServerDatabaseRow.FriendlyName"/> / <see cref="ServerDatabaseRow.MatchedEmpCode"/> si aplica.
    /// </summary>
    public static int AppendMatchingExerciseDatabases(List<ServerDatabaseRow> rows, IReadOnlySet<string> onlineSet)
    {
        var before = rows.Count;
        var byName = new Dictionary<string, ServerDatabaseRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
            byName[r.PhysicalName] = r;

        var prefixToSbdaTemplate = new Dictionary<string, ServerDatabaseRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var m = SbdaBody.Match(row.PhysicalName);
            if (!m.Success)
                continue;
            var prefix = m.Groups[1].Value.Trim();
            if (prefix.Length == 0)
                continue;
            prefixToSbdaTemplate.TryAdd(prefix, row);
        }

        if (prefixToSbdaTemplate.Count == 0)
            return 0;

        foreach (var db in onlineSet)
        {
            if (byName.ContainsKey(db))
                continue;

            var m = ExerciseSuffix.Match(db);
            if (!m.Success)
                continue;

            var prefix = m.Groups[1].Value.Trim();
            if (prefix.Length == 0)
                continue;

            if (!prefixToSbdaTemplate.TryGetValue(prefix, out var template))
                continue;

            var added = new ServerDatabaseRow
            {
                PhysicalName = db,
                FriendlyName = template.FriendlyName ?? "",
                MatchedEmpCode = template.MatchedEmpCode,
                Selected = false,
                ProductLine = "",
                IsSbdaLinkedExercise = true
            };
            rows.Add(added);
            byName[db] = added;
        }

        return rows.Count - before;
    }
}
