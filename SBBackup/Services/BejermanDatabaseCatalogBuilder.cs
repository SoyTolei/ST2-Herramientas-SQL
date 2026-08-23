using SBBackup.Models;

namespace SBBackup.Services;

/// <summary>
/// Arma el listado de bases Bejerman con nombres de empresa (manager..EMP),
/// mismos filtros y columnas de presentación que la pantalla de backup.
/// </summary>
public sealed class BejermanDatabaseCatalogBuilder(AppConfig config)
{
    private readonly DatabaseCatalogService _catalog = new(config);
    private readonly ManagerEmpLookup _empLookup = new(config);
    private readonly EjeExerciseLinkService _ejeLink = new();

    public async Task<IReadOnlyList<ServerDatabaseRow>> LoadAsync(
        string connectionString,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        var rows = new List<ServerDatabaseRow>();
        var mgrName = BejermanDatabaseRules.ResolveManagerDbName(config);

        log?.Report("Listando bases Bejerman…");
        var list = await _catalog.ListCandidateDatabasesAsync(connectionString, log, cancellationToken)
            .ConfigureAwait(false);
        rows.AddRange(list);

        var onlineSet = await _catalog.ListOnlineDatabaseNamesExcludedAsync(connectionString, cancellationToken)
            .ConfigureAwait(false);
        var ejeCatalog = await _ejeLink.LoadEjeCatalogAsync(
                connectionString,
                onlineSet,
                mgrName,
                log,
                cancellationToken)
            .ConfigureAwait(false);

        var byName = rows.ToDictionary(r => r.PhysicalName, StringComparer.OrdinalIgnoreCase);
        foreach (var (dbName, link) in ejeCatalog.DatabaseToEmpCode)
        {
            if (!onlineSet.Contains(dbName))
                continue;
            if (byName.TryGetValue(dbName, out var existing))
            {
                if (string.IsNullOrEmpty(existing.MatchedEmpCode) && !string.IsNullOrEmpty(link.EmpCode))
                    existing.MatchedEmpCode = link.EmpCode;
                continue;
            }

            var added = new ServerDatabaseRow
            {
                PhysicalName = dbName,
                FriendlyName = "",
                MatchedEmpCode = link.EmpCode,
                Selected = false
            };
            rows.Add(added);
            byName[dbName] = added;
        }

        log?.Report("Leyendo empresas desde manager.dbo.EMP…");
        var empDir = await _empLookup.GetEmpDirectoryAsync(connectionString, log, cancellationToken)
            .ConfigureAwait(false);

        foreach (var row in rows)
        {
            var m = ManagerEmpLookup.TryMatchEmp(row.PhysicalName, empDir);
            if (m is not null)
            {
                row.FriendlyName = m.Value.Raz;
                row.MatchedEmpCode = m.Value.Code;
            }
            else if (!string.IsNullOrEmpty(row.MatchedEmpCode))
            {
                row.FriendlyName = ManagerEmpLookup.TryGetRazsocForEmpCode(empDir, row.MatchedEmpCode) ?? "";
            }

            if (row.PhysicalName.Equals(mgrName, StringComparison.OrdinalIgnoreCase))
            {
                row.FriendlyName = "MANAGER";
                row.MatchedEmpCode = null;
            }
        }

        var addedExercise = SbdaPrefixExerciseLinker.AppendMatchingExerciseDatabases(rows, onlineSet);
        if (addedExercise > 0)
            log?.Report($"Ejercicios contables vinculados: {addedExercise} base(s).");

        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.FriendlyName) && !string.IsNullOrEmpty(row.MatchedEmpCode))
                row.FriendlyName = ManagerEmpLookup.TryGetRazsocForEmpCode(empDir, row.MatchedEmpCode) ?? "";
        }

        var linkedDef = BejermanDatabaseRules.LinkDefDerechosToParentGestion(rows);
        if (linkedDef > 0)
            log?.Report($"Bases DEF. DERECHOS vinculadas a gestión: {linkedDef}.");

        var beforeFilter = rows.Count;
        rows.RemoveAll(r => !BejermanDatabaseRules.IsBejermanBaseRow(r, mgrName, empDir));
        var removed = beforeFilter - rows.Count;
        if (removed > 0)
            log?.Report($"Se ocultaron {removed} base(s) ajenas al sistema Bejerman.");

        BejermanDatabaseRules.AssignBaseKindLabels(rows, mgrName);

        // Después del linker SBDA ya tenemos MatchedEmpCode: resolver eje_descrip en serio.
        foreach (var row in rows)
        {
            if (!IsContabilidadRow(row))
                continue;

            var link = EjeExerciseLinkService.TryResolveLinkForDatabase(
                row.PhysicalName,
                ejeCatalog.Rows,
                row.MatchedEmpCode);

            ejeCatalog.BindDescrip(row.PhysicalName, link?.Descrip);

            if (link is not null
                && string.IsNullOrEmpty(row.MatchedEmpCode)
                && !string.IsNullOrEmpty(link.EmpCode))
            {
                row.MatchedEmpCode = link.EmpCode;
                if (string.IsNullOrWhiteSpace(row.FriendlyName))
                    row.FriendlyName = ManagerEmpLookup.TryGetRazsocForEmpCode(empDir, link.EmpCode) ?? "";
            }
        }

        BejermanDatabaseRules.ApplyGridDisplayColumns(rows, mgrName, ejeCatalog);
        rows.Sort((a, b) => BejermanDatabaseRules.CompareDatabaseDisplayOrder(a, b, mgrName));
        return rows;
    }

    private static bool IsContabilidadRow(ServerDatabaseRow row)
    {
        var n = row.PhysicalName.Trim();
        return row.IsSbdaLinkedExercise
            || System.Text.RegularExpressions.Regex.IsMatch(n, @"^(?i)base\d+$")
            || System.Text.RegularExpressions.Regex.IsMatch(n, @"\d{4}$")
            || string.Equals(row.ProductLine, "Ejercicio Contable", StringComparison.OrdinalIgnoreCase);
    }
}
