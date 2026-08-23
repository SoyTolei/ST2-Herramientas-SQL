using System.IO.Compression;
using Microsoft.Data.SqlClient;
using SBBackup.Models;

namespace SBBackup.Services;

public sealed class BackupCoordinator(TransactionLogService logService)
{
    public async Task<BackupRunResult> RunBackupsAsync(
        string masterConnectionString,
        IReadOnlyList<ServerDatabaseRow> selected,
        string zipOutputDirectory,
        IProgress<string> log,
        IProgress<BackupProgressUpdate>? progress,
        CancellationToken cancellationToken,
        bool includeTimeInName = false)
    {
        var workspaceDir = OutputPathHelper.ResolveWorkspaceDirectory(zipOutputDirectory);
        SqlBackupStagingResolver.EnsureWorkspaceDirectory(workspaceDir);
        var sqlStagingCandidates = await SqlBackupStagingResolver.ResolveSqlWritableCandidatesAsync(
                masterConnectionString,
                log,
                cancellationToken)
            .ConfigureAwait(false);
        var serverHost = SqlServerHost.ExtractHost(masterConnectionString);
        var serverIsRemote = !SqlServerHost.IsLocal(serverHost);
        log.Report($"Carpeta de trabajo: {workspaceDir}");
        log.Report("Los .bak se generan de forma temporal; si el ZIP sale bien, se borran y queda solo el ZIP.");
        if (serverIsRemote)
        {
            log.Report(
                $"SQL Server remoto detectado ({serverHost}). Si el motor no puede escribir en tu carpeta, " +
                $"el .bak se traerá desde el servidor por el recurso administrativo \\\\{serverHost}\\C$.");
        }

        var backupDate = DateTime.Now.ToString(
            includeTimeInName ? "ddMMyyyy_HHmmss" : "ddMMyyyy",
            System.Globalization.CultureInfo.InvariantCulture);
        var collation = await GetServerCollationAsync(masterConnectionString, cancellationToken).ConfigureAwait(false);
        var sqlVersion = await GetSqlVersionLabelAsync(masterConnectionString, cancellationToken).ConfigureAwait(false);
        var collPart = SanitizeFileName(string.IsNullOrWhiteSpace(collation) ? "SinCollation" : collation.Trim());
        var verPart = SanitizeFileName(sqlVersion);
        var bakSuffix = $"{collPart}_{verPart}";
        var bakPaths = new List<string>();

        var total = selected.Count;
        var stepCount = Math.Max(1, total + 2);

        for (var i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = selected[i];
            var db = row.PhysicalName;
            ReportProgress(progress, i, stepCount, $"{i + 1}/{total} · {db} — reduciendo log de transacciones…");

            await logService.TryShrinkLogAsync(masterConnectionString, db, log, cancellationToken).ConfigureAwait(false);

            var fileName = $"{SanitizeFileName(db)}_{backupDate}_{bakSuffix}.bak";
            var userBakPath = Path.Combine(workspaceDir, fileName);
            ReportProgress(progress, i, stepCount, $"{i + 1}/{total} · {db} — generando .bak (0 %)…", subStep: 0, subStepTotal: 100);

            var dbIndex = i;
            void OnDbPercent(int pct) =>
                ReportProgress(
                    progress,
                    dbIndex,
                    stepCount,
                    $"{dbIndex + 1}/{total} · {db} — generando .bak ({pct} %)…",
                    subStep: pct,
                    subStepTotal: 100);

            var accessiblePath = await CreateAccessibleBackupAsync(
                    masterConnectionString,
                    db,
                    userBakPath,
                    sqlStagingCandidates,
                    serverHost,
                    serverIsRemote,
                    log,
                    cancellationToken,
                    OnDbPercent)
                .ConfigureAwait(false);
            bakPaths.Add(accessiblePath);
        }

        if (bakPaths.Count == 0)
        {
            ReportProgress(progress, stepCount, stepCount, "Listo.", isDone: true);
            return new BackupRunResult
            {
                OutputDirectory = workspaceDir,
                ZipFilePath = null,
                DatabasesBackedUp = total
            };
        }

        var zipBase = $"BackupBases {backupDate} {collPart} {verPart}";
        var zipPath = MakeUniqueZipPath(workspaceDir, zipBase);
        if (File.Exists(zipPath))
            File.Delete(zipPath);

        var zipStep = total;
        ReportProgress(progress, zipStep, stepCount, $"Finalizando: comprimiendo ZIP ({bakPaths.Count} archivos)…", isFinalizing: true);
        log.Report($"Creando ZIP: {zipPath}");
        try
        {
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                for (var zi = 0; zi < bakPaths.Count; zi++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var p = bakPaths[zi];
                    ReportProgress(
                        progress,
                        zipStep,
                        stepCount,
                        $"Finalizando: comprimiendo ZIP ({zi + 1}/{bakPaths.Count})…",
                        isFinalizing: true,
                        subStep: zi,
                        subStepTotal: bakPaths.Count);
                    if (!File.Exists(p))
                    {
                        throw new ZipCreationException(
                            "Falta un archivo .bak necesario para armar el ZIP.",
                            zipPath,
                            workspaceDir,
                            bakPaths,
                            new FileNotFoundException(
                                $"Archivo esperado:\n{p}",
                                p));
                    }

                    var entryName = Path.GetFileName(p);
                    // Fastest: los .bak son binarios (a menudo ya comprimidos por SQL); con Optimal
                    // se tardaba muchísimo y se ganaba poco tamaño. Fastest reduce el tiempo del ZIP drásticamente.
                    zip.CreateEntryFromFile(p, entryName, CompressionLevel.Fastest);
                }
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            WorkspaceFileCleanup.RemoveZipFile(zipPath, log);
            throw new ZipCreationException(
                "Falló la creación del ZIP por permisos.",
                zipPath,
                workspaceDir,
                bakPaths,
                ex);
        }
        catch (FileNotFoundException ex)
        {
            WorkspaceFileCleanup.RemoveZipFile(zipPath, log);
            throw new ZipCreationException(
                "Falta un archivo .bak necesario para armar el ZIP.",
                zipPath,
                workspaceDir,
                bakPaths,
                ex);
        }
        catch (IOException ex)
        {
            WorkspaceFileCleanup.RemoveZipFile(zipPath, log);
            throw new ZipCreationException(
                "No se pudo comprimir el respaldo en un archivo ZIP.",
                zipPath,
                workspaceDir,
                bakPaths,
                ex);
        }

        log.Report($"ZIP listo ({bakPaths.Count} archivos). Ubicación: {zipPath}");
        ReportProgress(
            progress,
            total + 1,
            stepCount,
            "Finalizando: eliminando archivos .bak (queda solo el ZIP)…",
            isFinalizing: true);

        WorkspaceFileCleanup.RemoveBakFilesOnly(workspaceDir, bakPaths, log);

        var bakLeft = WorkspaceFileCleanup.CountBakFilesInWorkspace(workspaceDir);
        if (bakLeft > 0)
        {
            log.Report(
                $"Atención: quedaron {bakLeft} archivo(s) .bak en la carpeta. " +
                "Si otro programa los tiene abiertos, cerralo y borralos manualmente.");
        }
        else
        {
            log.Report("Quedó solo el archivo ZIP en la carpeta de destino.");
        }

        ReportProgress(progress, stepCount, stepCount, "Listo.", isDone: true);

        return new BackupRunResult
        {
            OutputDirectory = workspaceDir,
            ZipFilePath = zipPath,
            DatabasesBackedUp = total
        };
    }

    private async Task BackupDatabaseAsync(
        string masterConnectionString,
        string databaseName,
        string backupFilePath,
        IProgress<string> log,
        CancellationToken cancellationToken,
        Action<int>? onPercent = null)
    {
        var builder = new SqlConnectionStringBuilder(masterConnectionString) { InitialCatalog = "master" };
        await using var conn = new SqlConnection(builder.ConnectionString);
        conn.InfoMessage += (_, e) =>
        {
            log.Report($"{databaseName}: {e.Message}");
            if (onPercent is not null && TryParseBackupPercent(e.Message, out var pct))
                onPercent(pct);
        };

        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        var escapedDb = EscapeSqlBracketIdentifier(databaseName);
        var pathParam = backupFilePath.Replace("'", "''", StringComparison.Ordinal);

        var withCompression = await SupportsBackupCompressionAsync(conn, cancellationToken).ConfigureAwait(false);
        var compressionClause = withCompression ? ", COMPRESSION" : "";

        var sql = $"""
            BACKUP DATABASE {escapedDb}
            TO DISK = N'{pathParam}'
            WITH INIT, CHECKSUM, STATS = 10{compressionClause};
            """;

        log.Report($"{databaseName}: iniciando backup → {backupFilePath}");

        try
        {
            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 0 };
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException ex) when (withCompression && BackupCompressionUnavailable(ex))
        {
            log.Report($"{databaseName}: compresión SQL no disponible ({ex.Number}); reintentando sin COMPRESSION…");
            await ExecuteBackupWithoutCompressionAsync(conn, escapedDb, pathParam, databaseName, log, cancellationToken)
                .ConfigureAwait(false);
        }

        log.Report($"{databaseName}: backup finalizado.");
    }

    private async Task<string> CreateAccessibleBackupAsync(
        string masterConnectionString,
        string databaseName,
        string userBakPath,
        IReadOnlyList<string> stagingCandidates,
        string? serverHost,
        bool serverIsRemote,
        IProgress<string> log,
        CancellationToken cancellationToken,
        Action<int>? onPercent = null)
    {
        var workspaceDir = Path.GetDirectoryName(userBakPath) ?? Path.GetTempPath();
        var fileName = Path.GetFileName(userBakPath);

        // Intento 1: backup directo a la carpeta de destino del usuario.
        try
        {
            await BackupDatabaseAsync(masterConnectionString, databaseName, userBakPath, log, cancellationToken, onPercent)
                .ConfigureAwait(false);

            if (File.Exists(userBakPath))
            {
                EnsureBackupReadable(userBakPath, databaseName);
                return userBakPath;
            }

            // SQL informó éxito pero el archivo no está en esta PC: el motor lo escribió en su
            // propio disco (servidor remoto). Lo traemos por el recurso administrativo \\HOST\C$.
            if (serverIsRemote)
            {
                var serverSide = SqlServerHost.ToAdminShareUnc(serverHost, userBakPath);
                if (!string.IsNullOrEmpty(serverSide) && File.Exists(serverSide))
                {
                    log.Report($"{databaseName}: leyendo el .bak desde el servidor ({serverSide})…");
                    CopyBackupForZip(serverSide, userBakPath, databaseName, log);
                    EnsureBackupReadable(userBakPath, databaseName);
                    TryDeleteQuiet(serverSide, log);
                    return userBakPath;
                }
            }

            log.Report($"{databaseName}: el .bak no quedó accesible en tu carpeta; probando carpetas alternativas…");
        }
        catch (SqlException ex) when (IsSqlBackupPathAccessDenied(ex))
        {
            log.Report($"{databaseName}: SQL no pudo escribir en la carpeta de destino (permiso del servicio); probando carpetas alternativas…");
        }

        // Intento 2: probar cada carpeta de staging hasta que SQL pueda escribir y el cliente leer.
        SqlException? lastWriteError = null;
        var wroteButUnreadable = false;

        foreach (var dir in stagingCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(dir))
                continue;

            var stagedPath = Path.Combine(dir, fileName);
            try
            {
                await BackupDatabaseAsync(masterConnectionString, databaseName, stagedPath, log, cancellationToken, onPercent)
                    .ConfigureAwait(false);
            }
            catch (SqlException ex)
            {
                lastWriteError = ex;
                log.Report($"{databaseName}: SQL no pudo escribir en «{dir}» (SQL {ex.Number}); probando otra ubicación…");
                continue;
            }

            if (await TryMaterializeBackupAsync(
                    masterConnectionString, databaseName, stagedPath, userBakPath, dir,
                    serverHost, serverIsRemote, workspaceDir, log, cancellationToken)
                .ConfigureAwait(false))
            {
                EnsureBackupReadable(userBakPath, databaseName);
                return userBakPath;
            }

            wroteButUnreadable = true;
            log.Report($"{databaseName}: SQL grabó en «{dir}» pero no se pudo traer el archivo; probando otra ubicación…");
        }

        // Ninguna ubicación funcionó: mensaje claro según el motivo predominante.
        if (wroteButUnreadable && lastWriteError is null)
        {
            var serverSidePath = stagingCandidates.Count > 0
                ? Path.Combine(stagingCandidates[0], fileName)
                : fileName;
            throw new InvalidOperationException(
                UserMessageSpanish.SqlBackupUnreachable(databaseName, serverHost, serverSidePath));
        }

        throw new InvalidOperationException(
            UserMessageSpanish.SqlServiceCannotWrite(databaseName, serverHost, serverIsRemote, lastWriteError),
            lastWriteError);
    }

    /// <summary>
    /// Lleva el .bak generado por SQL (en <paramref name="stagedPath"/>) hasta la carpeta del
    /// usuario, probando en orden: lectura directa / \\HOST\C$ / descarga por la conexión SQL.
    /// </summary>
    private async Task<bool> TryMaterializeBackupAsync(
        string masterConnectionString,
        string databaseName,
        string stagedPath,
        string userBakPath,
        string stagingDir,
        string? serverHost,
        bool serverIsRemote,
        string workspaceDir,
        IProgress<string> log,
        CancellationToken cancellationToken)
    {
        // 1) Lectura directa por sistema de archivos (SQL local, UNC compartida o C$ con admin).
        var readable = ResolveClientReadablePath(stagedPath, serverHost, serverIsRemote, stagingDir, workspaceDir);
        if (readable is not null)
        {
            if (!string.Equals(readable, userBakPath, StringComparison.OrdinalIgnoreCase))
            {
                log.Report($"{databaseName}: trayendo el .bak a tu carpeta de trabajo…");
                CopyBackupForZip(readable, userBakPath, databaseName, log);
            }

            TryDeleteQuiet(readable, log);
            return true;
        }

        // 2) Descarga por la propia conexión SQL (sin compartir archivos ni C$).
        log.Report($"{databaseName}: descargando el .bak por la conexión SQL (sin acceso de red al archivo)…");
        if (await SqlFileTransfer.TryDownloadViaSqlAsync(
                masterConnectionString, stagedPath, userBakPath, log, cancellationToken)
            .ConfigureAwait(false))
        {
            await SqlFileTransfer.TryDeleteServerFileAsync(
                    masterConnectionString, stagedPath, serverHost, serverIsRemote, log, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Devuelve una ruta que el cliente puede leer para el .bak generado por SQL.
    /// Si el servidor es remoto, intenta el recurso administrativo \\HOST\C$.
    /// </summary>
    private static string? ResolveClientReadablePath(
        string sqlBakPath,
        string? serverHost,
        bool serverIsRemote,
        string sqlWritableDir,
        string workspaceDir)
    {
        if (File.Exists(sqlBakPath))
            return sqlBakPath;

        var fileName = Path.GetFileName(sqlBakPath);

        if (serverIsRemote && !string.IsNullOrWhiteSpace(serverHost))
        {
            var uncFile = SqlServerHost.ToAdminShareUnc(serverHost, sqlBakPath);
            if (!string.IsNullOrEmpty(uncFile) && File.Exists(uncFile))
                return uncFile;

            var uncDir = SqlServerHost.ToAdminShareUnc(serverHost, sqlWritableDir);
            if (!string.IsNullOrEmpty(uncDir))
            {
                var candidate = Path.Combine(uncDir, fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return SqlBackupStagingResolver.TryFindBackupFile(fileName, sqlWritableDir, workspaceDir);
    }

    private static bool IsSqlBackupPathAccessDenied(SqlException ex)
    {
        foreach (SqlError e in ex.Errors)
        {
            if (e.Number is 3201 or 3013 or 5 or 3271 or 15105)
                return true;
            if (ContainsBackupPathHints(e.Message))
                return true;
        }

        return ex.Number is 3201 or 3013 or 5 or 3271 or 15105
            || ContainsBackupPathHints(ex.Message);
    }

    private static bool ContainsBackupPathHints(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return false;

        return message.Contains("15105", StringComparison.Ordinal)
            || message.Contains("access", StringComparison.OrdinalIgnoreCase)
            || message.Contains("denied", StringComparison.OrdinalIgnoreCase)
            || message.Contains("permission", StringComparison.OrdinalIgnoreCase)
            || message.Contains("error 5", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Operating system error", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Cannot open backup device", StringComparison.OrdinalIgnoreCase)
            || message.Contains("nonrecoverable I/O", StringComparison.OrdinalIgnoreCase)
            || message.Contains("I/O error occurred", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureBackupReadable(string path, string databaseName)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"No se encuentra el backup de '{databaseName}' en:\n{path}",
                path);
        }

        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException(
                $"No hay permiso para leer el backup de '{databaseName}' en:\n{path}\n\n{ex.Message}",
                ex);
        }
    }

    private static void CopyBackupForZip(string source, string dest, string databaseName, IProgress<string> log)
    {
        try
        {
            if (File.Exists(dest))
                File.Delete(dest);
            File.Copy(source, dest, overwrite: true);
            log.Report($"{databaseName}: copiado a la carpeta de trabajo.");
        }
        catch (Exception ex)
        {
            throw new IOException(
                "No se pudo copiar el backup a tu carpeta de trabajo.\n\n" +
                $"Destino:\n{dest}\n\n" +
                $"{ex.Message}\n\n" +
                "Usá Documentos o Escritorio como carpeta de destino y verificá permiso de escritura.",
                ex);
        }
    }

    private static void TryDeleteQuiet(string path, IProgress<string>? log)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            log?.Report($"No se pudo borrar .bak en carpeta SQL ({Path.GetFileName(path)}): {ex.Message}");
        }
    }

    private static async Task ExecuteBackupWithoutCompressionAsync(
        SqlConnection conn,
        string escapedDb,
        string pathParam,
        string databaseName,
        IProgress<string> log,
        CancellationToken cancellationToken)
    {
        var sqlNoComp = $"""
            BACKUP DATABASE {escapedDb}
            TO DISK = N'{pathParam}'
            WITH INIT, CHECKSUM, STATS = 10;
            """;
        await using var cmd = new SqlCommand(sqlNoComp, conn) { CommandTimeout = 0 };
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        log.Report($"{databaseName}: backup sin compresión SQL finalizado.");
    }

    private static bool BackupCompressionUnavailable(SqlException ex) =>
        ex.Number is 1844 or 32240
        || ex.Message.Contains("COMPRESSION", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("Express Edition", StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> SupportsBackupCompressionAsync(SqlConnection conn, CancellationToken cancellationToken)
    {
        try
        {
            const string sql = """
                SELECT CAST(SERVERPROPERTY('EngineEdition') AS int) AS Edition,
                       CAST(SERVERPROPERTY('ProductMajorVersion') AS int) AS Major;
                """;
            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 15 };
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return false;

            var edition = reader.IsDBNull(0) ? -1 : reader.GetInt32(0);
            var major = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);

            // https://learn.microsoft.com/sql/database-engine/editions-and-components-of-sql-server
            // 1 Personal/Desktop, 2 Standard, 3 Enterprise, 4 Express, 5 Azure, 8 Managed Instance
            return edition switch
            {
                3 or 8 => true,
                2 => major >= 10, // Standard: compresión desde 2008 R2+
                4 or 1 => false,  // Express / Desktop: sin compresión en BACKUP
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }

    private static void ReportProgress(
        IProgress<BackupProgressUpdate>? progress,
        int stepIndex,
        int stepCount,
        string status,
        bool isFinalizing = false,
        bool isDone = false,
        int? subStep = null,
        int? subStepTotal = null)
    {
        if (progress is null)
            return;

        double fraction = stepCount <= 0 ? 0 : (double)stepIndex / stepCount;
        if (subStep is not null && subStepTotal is > 0)
        {
            var slice = 1.0 / stepCount;
            fraction += slice * (subStep.Value + 1) / subStepTotal.Value;
        }

        var percent = isDone ? 100 : Math.Clamp((int)Math.Round(fraction * 100), 0, 99);
        progress.Report(new BackupProgressUpdate
        {
            Percent = percent,
            Status = status,
            StepIndex = stepIndex,
            StepCount = stepCount,
            IsFinalizing = isFinalizing || isDone
        });
    }

    // SQL emite, por STATS, mensajes como "10 percent processed." (inglés) o
    // "10 por ciento procesado." (español). Tomamos ese número para mover la barra en vivo.
    private static readonly System.Text.RegularExpressions.Regex BackupPercentRegex =
        new(@"^\s*(\d{1,3})\s+(?:percent|por\s*ciento)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static bool TryParseBackupPercent(string? message, out int percent)
    {
        percent = 0;
        if (string.IsNullOrEmpty(message))
            return false;

        var m = BackupPercentRegex.Match(message);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var p))
            return false;

        percent = Math.Clamp(p, 0, 100);
        return true;
    }

    private static string EscapeSqlBracketIdentifier(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    private static async Task<string?> GetServerCollationAsync(string masterConnectionString, CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder(masterConnectionString) { InitialCatalog = "master" };
        await using var conn = new SqlConnection(builder.ConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new SqlCommand("SELECT CAST(SERVERPROPERTY('Collation') AS nvarchar(256));", conn)
        {
            CommandTimeout = 30
        };
        var o = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return o as string;
    }

    private static async Task<string> GetSqlVersionLabelAsync(string masterConnectionString, CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder(masterConnectionString) { InitialCatalog = "master" };
        await using var conn = new SqlConnection(builder.ConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new SqlCommand(
            "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(64));",
            conn)
        { CommandTimeout = 30 };
        var o = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (o is not string raw || string.IsNullOrWhiteSpace(raw))
            return "SQL";

        return ToSqlYearLabel(raw.Trim());
    }

    /// <summary>
    /// Convierte ProductVersion (p. ej. 15.0.2xxx) a etiqueta de año (SQL2019)
    /// para nombres de ZIP/.bak más claros.
    /// </summary>
    internal static string ToSqlYearLabel(string productVersion)
    {
        var parts = productVersion.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], out var major))
            return "SQL";

        var minor = 0;
        if (parts.Length >= 2)
            _ = int.TryParse(parts[1], out minor);

        var year = major switch
        {
            >= 17 => 2025,
            16 => 2022,
            15 => 2019,
            14 => 2017,
            13 => 2016,
            12 => 2014,
            11 => 2012,
            10 => 2008,
            9 => 2005,
            8 => 2000,
            _ => 0
        };

        if (major == 10 && minor >= 50)
            return "SQL2008R2";

        if (year > 0)
            return $"SQL{year}";

        return parts.Length >= 2 ? $"SQL{parts[0]}.{parts[1]}" : $"SQL{parts[0]}";
    }

    private static string MakeUniqueZipPath(string outputDirectory, string baseNameWithoutExtension)
    {
        var path = Path.Combine(outputDirectory, baseNameWithoutExtension + ".zip");
        if (!File.Exists(path))
            return path;

        var suffix = DateTime.Now.ToString("HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        return Path.Combine(outputDirectory, $"{baseNameWithoutExtension}_{suffix}.zip");
    }
}
