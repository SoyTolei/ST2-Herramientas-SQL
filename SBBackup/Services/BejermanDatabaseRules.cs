using System.Text.RegularExpressions;
using SBBackup.Models;

namespace SBBackup.Services;

/// <summary>Reglas de clasificación y presentación de bases Bejerman (compartidas entre backup y scripts).</summary>
internal static class BejermanDatabaseRules
{
    /// <summary>
    /// Bases de definición de derechos: mismo prefijo SBDA/SBDP que la de gestión,
    /// con dígitos opcionales y guiones bajos al final (p. ej. <c>SBDAMODE01___</c>).
    /// </summary>
    private static readonly Regex DefDerechosPattern = new(
        @"^(?i)(SBDA|SBDP).+_{2,}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string ResolveManagerDbName(AppConfig config) =>
        string.IsNullOrWhiteSpace(config.ManagerDatabaseName) ? "manager" : config.ManagerDatabaseName.Trim();

    public static bool IsDefDerechosDatabase(string physicalName) =>
        DefDerechosPattern.IsMatch(physicalName.Trim());

    /// <summary>
    /// De <c>SBDAMODE01___</c> obtiene el candidato a base de gestión <c>SBDAMODE</c>
    /// (quita <c>_</c> finales y dígitos del sufijo).
    /// </summary>
    public static bool TryGetDefDerechosParentCandidate(string physicalName, out string parentCandidate)
    {
        parentCandidate = "";
        var n = physicalName.Trim();
        if (!IsDefDerechosDatabase(n))
            return false;

        var withoutUnderscores = n.TrimEnd('_');
        if (withoutUnderscores.Length == 0)
            return false;

        var parent = Regex.Replace(withoutUnderscores, @"\d+$", "");
        if (parent.Length < 5) // más que "SBDA" / "SBDP"
            return false;

        parentCandidate = parent;
        return true;
    }

    /// <summary>
    /// Copia razón social / emp_codigo desde la base de gestión padre hacia las de DEF. DERECHOS
    /// cuando el nombre quedó vacío por el sufijo <c>01___</c>.
    /// </summary>
    public static int LinkDefDerechosToParentGestion(IReadOnlyList<ServerDatabaseRow> rows)
    {
        var byName = new Dictionary<string, ServerDatabaseRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
            byName[r.PhysicalName.Trim()] = r;

        var linked = 0;
        foreach (var row in rows)
        {
            if (!IsDefDerechosDatabase(row.PhysicalName))
                continue;
            if (!TryGetDefDerechosParentCandidate(row.PhysicalName, out var parentName))
                continue;
            if (!byName.TryGetValue(parentName, out var parent))
                continue;

            if (string.IsNullOrWhiteSpace(row.FriendlyName) && !string.IsNullOrWhiteSpace(parent.FriendlyName))
                row.FriendlyName = parent.FriendlyName;

            if (string.IsNullOrEmpty(row.MatchedEmpCode) && !string.IsNullOrEmpty(parent.MatchedEmpCode))
                row.MatchedEmpCode = parent.MatchedEmpCode;

            linked++;
        }

        return linked;
    }

    public static string FormatPickerLabel(ServerDatabaseRow row)
    {
        var nombre = (row.GridNombre ?? "").Trim();
        if (string.IsNullOrEmpty(nombre))
            nombre = (row.FriendlyName ?? "").Trim();

        var tipo = (row.GridTipo ?? "").Trim();
        if (!string.IsNullOrEmpty(nombre) && !string.IsNullOrEmpty(tipo))
            return $"{nombre}  ·  {tipo}";

        if (!string.IsNullOrEmpty(nombre))
            return $"{nombre}  ({row.PhysicalName})";

        return row.PhysicalName;
    }

    public static void AssignBaseKindLabels(IReadOnlyList<ServerDatabaseRow> rows, string managerDbName)
    {
        foreach (var row in rows)
        {
            var n = row.PhysicalName.Trim();
            if (n.Equals(managerDbName, StringComparison.OrdinalIgnoreCase))
            {
                row.ProductLine = "";
                continue;
            }

            if (row.IsSbdaLinkedExercise || Regex.IsMatch(n, @"^(?i)base\d+$"))
            {
                row.ProductLine = "Ejercicio Contable";
                continue;
            }

            if (n.StartsWith("SJ", StringComparison.OrdinalIgnoreCase))
            {
                row.ProductLine = "SJ";
                continue;
            }

            if (IsDefDerechosDatabase(n))
            {
                row.ProductLine = "Gestión (DEF. DERECHOS)";
                continue;
            }

            if (n.StartsWith("SBDP", StringComparison.OrdinalIgnoreCase))
            {
                row.ProductLine = "Gestión (PRUEBA)";
                continue;
            }

            if (n.StartsWith("SBDA", StringComparison.OrdinalIgnoreCase))
            {
                row.ProductLine = "Gestión";
                continue;
            }

            if (LooksLikeExerciseName(n))
            {
                row.ProductLine = "Ejercicio Contable";
                continue;
            }

            row.ProductLine = "";
        }
    }

    public static bool IsBejermanBaseRow(
        ServerDatabaseRow row,
        string managerDbName,
        IReadOnlyList<(string Code, string Raz)> empDir)
    {
        var n = row.PhysicalName.Trim();

        if (n.Equals(managerDbName, StringComparison.OrdinalIgnoreCase))
            return true;

        if (n.StartsWith("SBDA", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("SBDP", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("SJ", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(n, @"^(?i)base\d+$"))
            return true;

        if (!string.IsNullOrEmpty(row.MatchedEmpCode) || row.IsSbdaLinkedExercise)
            return true;

        return ManagerEmpLookup.TryMatchEmp(n, empDir) is not null;
    }

    public static void ApplyGridDisplayColumns(
        IReadOnlyList<ServerDatabaseRow> rows,
        string managerDbName,
        EjeExerciseLinkService.EjeCatalog ejeCatalog)
    {
        foreach (var row in rows)
        {
            row.GridTipo = FormatTipoCelda(row, managerDbName);
            row.GridNombre = FormatNombreCelda(row, managerDbName, ejeCatalog);
            if (IsContabilidadRow(row, row.PhysicalName.Trim()))
            {
                row.ExerciseSortOrder = ExerciseDisplayHelper.ResolveSortNumber(row.GridNombre, row.PhysicalName);
                if (row.GridNombre.Contains("no usar", StringComparison.OrdinalIgnoreCase))
                    row.ExerciseSortOrder = int.MaxValue;
            }
            else
                row.ExerciseSortOrder = null;
        }
    }

    public static int CompareDatabaseDisplayOrder(ServerDatabaseRow a, ServerDatabaseRow b, string managerDbName)
    {
        var oa = GetDatabaseKindOrder(a, managerDbName);
        var ob = GetDatabaseKindOrder(b, managerDbName);
        var c = oa.CompareTo(ob);
        if (c != 0)
            return c;

        if (oa == 2)
        {
            c = CompareExerciseSortOrder(a, b);
            if (c != 0)
                return c;
        }

        c = string.Compare(a.GridNombre, b.GridNombre, StringComparison.CurrentCultureIgnoreCase);
        if (c != 0)
            return c;
        return string.Compare(a.PhysicalName, b.PhysicalName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeExerciseName(string name) =>
        Regex.IsMatch(name.Trim(), @"\d{4}$");

    private static bool IsContabilidadRow(ServerDatabaseRow row, string physicalName) =>
        row.IsSbdaLinkedExercise
        || Regex.IsMatch(physicalName.Trim(), @"^(?i)base\d+$")
        || LooksLikeExerciseName(physicalName)
        || string.Equals(row.ProductLine, "Ejercicio Contable", StringComparison.OrdinalIgnoreCase);

    private static string FormatTipoCelda(ServerDatabaseRow row, string managerDbName)
    {
        var n = row.PhysicalName.Trim();
        if (n.Equals(managerDbName, StringComparison.OrdinalIgnoreCase))
            return "Manager";

        if (IsContabilidadRow(row, n))
            return "Contabilidad General";

        if (n.StartsWith("SJ", StringComparison.OrdinalIgnoreCase))
            return "Sueldos y Jornales";

        if (IsDefDerechosDatabase(n))
            return "Gestión (DEF. DERECHOS)";

        if (n.StartsWith("SBDP", StringComparison.OrdinalIgnoreCase))
            return "Gestión (PRUEBA)";

        if (n.StartsWith("SBDA", StringComparison.OrdinalIgnoreCase))
            return "Gestión";

        return "";
    }

    private static string FormatNombreCelda(
        ServerDatabaseRow row,
        string managerDbName,
        EjeExerciseLinkService.EjeCatalog ejeCatalog)
    {
        var n = row.PhysicalName.Trim();
        if (n.Equals(managerDbName, StringComparison.OrdinalIgnoreCase))
            return (row.FriendlyName ?? "").Trim();

        if (IsContabilidadRow(row, n))
        {
            var descrip = ejeCatalog.ResolveDescrip(n, row.MatchedEmpCode);
            if (!string.IsNullOrEmpty(descrip))
                return descrip;
            return "";
        }

        var friendly = (row.FriendlyName ?? "").Trim();
        if (!string.IsNullOrEmpty(friendly))
            return friendly;

        // DEF. DERECHOS sin match EMP: al menos mostrar algo usable, no vacío.
        if (IsDefDerechosDatabase(n) && TryGetDefDerechosParentCandidate(n, out var parent))
            return parent;

        return "";
    }

    private static int GetDatabaseKindOrder(ServerDatabaseRow r, string managerDbName)
    {
        var n = r.PhysicalName.Trim();
        if (n.Equals(managerDbName, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (n.StartsWith("SBDA", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("SBDP", StringComparison.OrdinalIgnoreCase))
            return 1;
        if (r.IsSbdaLinkedExercise || Regex.IsMatch(n, @"^(?i)base\d+$"))
            return 2;
        if (n.StartsWith("SJ", StringComparison.OrdinalIgnoreCase))
            return 3;
        return 4;
    }

    private static int CompareExerciseSortOrder(ServerDatabaseRow a, ServerDatabaseRow b)
    {
        var na = a.ExerciseSortOrder
                 ?? ExerciseDisplayHelper.ResolveSortNumber(a.GridNombre, a.PhysicalName);
        var nb = b.ExerciseSortOrder
                 ?? ExerciseDisplayHelper.ResolveSortNumber(b.GridNombre, b.PhysicalName);
        if (na.HasValue && nb.HasValue)
            return na.Value.CompareTo(nb.Value);
        if (na.HasValue)
            return -1;
        if (nb.HasValue)
            return 1;
        return 0;
    }
}
