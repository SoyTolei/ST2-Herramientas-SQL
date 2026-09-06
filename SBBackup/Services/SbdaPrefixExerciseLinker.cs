using System.Text.RegularExpressions;
using SBBackup.Models;

namespace SBBackup.Services;

/// <summary>
/// Incluye bases de ejercicio contable cuyo nombre es <c>{mismo prefijo que SBDA}{4 dígitos}</c>,
/// p. ej. <c>SBDAPRUEBA</c> → <c>PRUEBA0001</c>, <c>PRUEBA2026</c>.
/// </summary>
public static class SbdaPrefixExerciseLinker
{
    private static readonly Regex SbdaBody = new(@"^(?i)SBD[AP](.+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ExerciseSuffix = new(@"^(?i)(.+)(\d{4})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Agrega o enriquece filas de ejercicio vinculadas al prefijo de una base SBDA ya presente.
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

            // Preferir SBDA sobre SBDP si aparecen las dos.
            if (!prefixToSbdaTemplate.TryGetValue(prefix, out var existing) ||
                (existing.PhysicalName.StartsWith("SBDP", StringComparison.OrdinalIgnoreCase)
                 && row.PhysicalName.StartsWith("SBDA", StringComparison.OrdinalIgnoreCase)))
                prefixToSbdaTemplate[prefix] = row;
        }

        if (prefixToSbdaTemplate.Count == 0)
            return 0;

        var enriched = 0;
        foreach (var db in onlineSet)
        {
            var m = ExerciseSuffix.Match(db);
            if (!m.Success)
                continue;

            var prefix = m.Groups[1].Value.Trim();
            if (prefix.Length == 0)
                continue;

            if (!prefixToSbdaTemplate.TryGetValue(prefix, out var template))
                continue;

            if (byName.TryGetValue(db, out var existing))
            {
                // Ya estaba en el listado: igual heredar emp/razón de la SBDA (para resolver EJE).
                if (string.IsNullOrEmpty(existing.MatchedEmpCode) && !string.IsNullOrEmpty(template.MatchedEmpCode))
                {
                    existing.MatchedEmpCode = template.MatchedEmpCode;
                    enriched++;
                }

                if (string.IsNullOrWhiteSpace(existing.FriendlyName) && !string.IsNullOrWhiteSpace(template.FriendlyName))
                    existing.FriendlyName = template.FriendlyName;

                existing.IsSbdaLinkedExercise = true;
                continue;
            }

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

        return rows.Count - before + enriched;
    }
}
