using Microsoft.Data.SqlClient;



namespace SBBackup.Services;



/// <summary>

/// Carpeta donde el servicio SQL Server puede escribir (no perfiles de usuario).

/// Los .bak finales y el ZIP van solo a la carpeta de trabajo del usuario.

/// </summary>

internal static class SqlBackupStagingResolver

{

    private const string LegacyWorkspaceSqlSubfolder = ".sql-temp";



    public static string EnsureWorkspaceDirectory(string workspaceDirectory)

    {

        var dir = Path.GetFullPath(workspaceDirectory.Trim());

        Directory.CreateDirectory(dir);

        return dir;

    }



    /// <summary>

    /// Ruta donde el servicio SQL puede crear .bak temporalmente (nunca Documentos/Escritorio del usuario).

    /// </summary>

    public static async Task<string> ResolveSqlWritableDirectoryAsync(

        string masterConnectionString,

        IProgress<string>? log,

        CancellationToken cancellationToken)

    {

        var sharedUnc = TryResolveSharedUncStaging(log);

        if (!string.IsNullOrEmpty(sharedUnc))

            return sharedUnc;



        var sqlDefault = await TryGetSqlDefaultBackupDirectoryAsync(masterConnectionString, cancellationToken)

            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(sqlDefault))

        {

            var dir = sqlDefault.Trim();

            try

            {

                Directory.CreateDirectory(dir);

                log?.Report("SQL usa su carpeta de backup interna; el archivo se copia a tu carpeta de destino.");

                return dir;

            }

            catch (Exception ex)

            {

                log?.Report($"No se pudo usar la carpeta de backup de SQL: {ex.Message}");

            }

        }



        foreach (var dir in GetMachineTempCandidates())

        {

            try

            {

                Directory.CreateDirectory(dir);

                log?.Report("SQL usa carpeta temporal del sistema; el archivo se copia a tu carpeta de destino.");

                return dir;

            }

            catch (Exception ex)

            {

                log?.Report($"No se pudo usar {dir}: {ex.Message}");

            }

        }



        var fallback = GetMachineTempCandidates().First();

        Directory.CreateDirectory(fallback);

        return fallback;

    }



    /// <summary>
    /// Lista ordenada de carpetas donde el servicio SQL puede intentar dejar el .bak.
    /// Se prueban en orden hasta que una funcione:
    ///   1) Ruta UNC compartida de Bejerman (el cliente la lee directo).
    ///   2) Carpeta de backup por defecto del propio servidor SQL (el cliente la lee por \\HOST\C$).
    ///   3) Carpetas temporales del sistema (útil cuando SQL es local).
    /// </summary>
    public static async Task<IReadOnlyList<string>> ResolveSqlWritableCandidatesAsync(
        string masterConnectionString,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        var candidates = new List<string>();

        var sharedUnc = TryResolveSharedUncStaging(log);
        if (!string.IsNullOrEmpty(sharedUnc))
            candidates.Add(sharedUnc);

        var sqlDefault = await TryGetSqlDefaultBackupDirectoryAsync(masterConnectionString, cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(sqlDefault))
            candidates.Add(sqlDefault.Trim());

        foreach (var dir in GetMachineTempCandidates())
            candidates.Add(dir);

        return candidates
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }



    private const string SharedStagingSubfolder = "SBBackup_tmp";



    /// <summary>
    /// Carpeta de staging sobre la ruta UNC compartida de Bejerman (visible desde el server y las
    /// terminales). Es la opción ideal: SQL deja el .bak ahí y el cliente lo lee directo, sin C$.
    /// </summary>
    private static string? TryResolveSharedUncStaging(IProgress<string>? log)
    {
        var unc = BejermanRegistry.TryGetSharedUncPath();

        if (string.IsNullOrWhiteSpace(unc))

            return null;

        try
        {
            var staging = Path.Combine(unc.TrimEnd('\\', '/'), SharedStagingSubfolder);

            Directory.CreateDirectory(staging);

            log?.Report($"Usando la ruta UNC compartida de Bejerman para el .bak: {staging}");

            return staging;
        }
        catch (Exception ex)
        {
            log?.Report($"No se pudo usar la ruta UNC compartida de Bejerman ({unc}): {ex.Message}");

            return null;
        }
    }



    private static IEnumerable<string> GetMachineTempCandidates()

    {

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        yield return Path.Combine(windows, "Temp", "ST2_SBBackup_sql");

        yield return Path.Combine(

            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),

            "ST2_SBBackup",

            "sql-temp");

        yield return Path.Combine(Path.GetTempPath(), "ST2_SBBackup_sql");

    }



    public static string? TryFindBackupFile(string fileName, params string[] searchDirs)

    {

        foreach (var d in searchDirs.Where(d => !string.IsNullOrWhiteSpace(d))

                     .Select(d => Path.GetFullPath(d.Trim()))

                     .Distinct(StringComparer.OrdinalIgnoreCase))

        {

            if (!Directory.Exists(d))

                continue;

            var p = Path.Combine(d, fileName);

            if (File.Exists(p))

                return p;

        }



        return null;

    }



    /// <summary>Limpia restos de versiones anteriores (.sql-temp bajo Documentos).</summary>

    public static void TryCleanupLegacyWorkspaceSqlTemp(string workspaceDirectory, IProgress<string>? log)

    {

        var dir = Path.Combine(Path.GetFullPath(workspaceDirectory.Trim()), LegacyWorkspaceSqlSubfolder);

        if (!Directory.Exists(dir))

            return;



        try

        {

            foreach (var f in Directory.GetFiles(dir))

            {

                try

                {

                    File.Delete(f);

                }

                catch

                {

                    // ignore

                }

            }



            Directory.Delete(dir, false);

        }

        catch (Exception ex)

        {

            log?.Report($"No se pudo limpiar carpeta temporal antigua: {ex.Message}");

        }

    }



    private static async Task<string?> TryGetSqlDefaultBackupDirectoryAsync(

        string masterConnectionString,

        CancellationToken cancellationToken)

    {

        try

        {

            var builder = new SqlConnectionStringBuilder(masterConnectionString) { InitialCatalog = "master" };

            await using var conn = new SqlConnection(builder.ConnectionString);

            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);



            const string sql = """

                DECLARE @path nvarchar(512);

                EXEC master.dbo.xp_instance_regread

                    N'HKEY_LOCAL_MACHINE',

                    N'Software\Microsoft\MSSQLServer\MSSQLServer',

                    N'BackupDirectory',

                    @path OUTPUT, NULL, NULL;

                SELECT @path AS BackupDirectory;

                """;



            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };

            var o = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            if (o is string s && !string.IsNullOrWhiteSpace(s))

                return s.Trim();

        }

        catch

        {

            // Sin xp_instance_regread

        }



        return null;

    }

}


