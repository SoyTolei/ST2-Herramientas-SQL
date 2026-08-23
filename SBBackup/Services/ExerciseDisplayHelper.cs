using System.Text.RegularExpressions;

namespace SBBackup.Services;

internal static class ExerciseDisplayHelper
{
    private static readonly Regex[] LabelNumberPatterns =
    [
        new(@"(?i)ejercicio\s+econ[oó]mico\s+n[º°o#\.]?\s*(\d+)", RegexOptions.Compiled),
        new(@"(?i)ejercicio\s+economico\s+n[º°o#\.]?\s*(\d+)", RegexOptions.Compiled),
        new(@"(?i)ejercicio\s+econ[oó]mico\s+(\d+)\b", RegexOptions.Compiled),
        new(@"(?i)ejercicio\s+economico\s+(\d+)\b", RegexOptions.Compiled),
        new(@"(?i)ejercicio\s+contable\s+n[º°o#\.]?\s*(\d+)", RegexOptions.Compiled),
        new(@"(?i)ejercicio\s+contable\s+(\d+)\b", RegexOptions.Compiled),
        new(@"(?i)ejercicio\s+n[º°o#\.]?\s*(\d+)", RegexOptions.Compiled),
        new(@"(?i)ejercicio\s+(\d+)\b", RegexOptions.Compiled),
        new(@"(?i)n[º°o#\.]\s*(\d+)\s*$", RegexOptions.Compiled)
    ];

    private static readonly Regex BaseNumberPattern = new(@"^(?i)base(\d+)$", RegexOptions.Compiled);

    /// <summary>Número de ejercicio para ordenar (1, 2, 3…), si se puede inferir del nombre mostrado o de la base.</summary>
    public static int? ResolveSortNumber(string? gridNombre, string physicalName)
    {
        if (!string.IsNullOrWhiteSpace(gridNombre))
        {
            var label = gridNombre.Trim();
            foreach (var rx in LabelNumberPatterns)
            {
                var m = rx.Match(label);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var n))
                    return n;
            }
        }

        var db = physicalName.Trim();
        var baseMatch = BaseNumberPattern.Match(db);
        if (baseMatch.Success && int.TryParse(baseMatch.Groups[1].Value, out var baseN))
            return baseN;

        return null;
    }
}
