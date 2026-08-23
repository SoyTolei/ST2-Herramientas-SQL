using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace SBBackup.Services;

/// <summary>
/// Restaura una base SQL desde un archivo .bak, sobrescribiendo la base con el mismo nombre
/// que contiene el backup (WITH REPLACE). Resuelve el acceso al .bak cuando SQL es remoto
/// (copia a una carpeta legible por el servicio) y reubica los archivos de datos/log con MOVE.
/// </summary>
public sealed class RestoreCoordinator
{
    public sealed record BackupFileEntry(string LogicalName, string Type, string PhysicalName);

    public sealed class RestoreFilePreview
    {
        public required string LocalPath { get; init; }
        public string? DatabaseName { get; set; }
        public bool? DatabaseExists { get; set; }
        public DateTime? BackupDate { get; set; }
        public long? BackupSizeBytes { get; set; }
        public bool Compressed { get; set; }
        public string? BackupServerName { get; set; }
        public int DataFiles { get; set; }
        public int LogFiles { get; set; }
        public string? PeekNote { get; set; }
    }

    public sealed class RestorePlanItem
    {
        public required string LocalBakPath { get; init; }
        public required string ServerReadPath { get; init; }
        public string? ClientCleanupPath { get; init; }
        public required string DatabaseName { get; init; }
        public bool DatabaseExists { get; init; }
        public List<BackupFileEntry> Files { get; init; } = [];
    }

    public sealed class RestoreProgress
    {
        public int Percent { get; init; }
        public string Status { get; init; } = "";
    }

    /// <summary>Lee metadatos del .bak sin copiarlo ni preparar la restauración.</summary>
    public async Task<RestoreFilePreview> PeekBackupAsync(
        string masterConnectionString,
        string localBakPath,
        CancellationToken cancellationToken)
    {
        var preview = new RestoreFilePreview { LocalPath = localBakPath };
        preview.DatabaseName = Path.GetFileNameWithoutExtension(localBakPath);

        try
        {
            var builder = new SqlConnectionStringBuilder(masterConnectionString) { InitialCatalog = "master" };
            await using var conn = new SqlConnection(builder.ConnectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            if (!await TryPopulateHeaderAsync(conn, localBakPath, preview, cancellationToken).ConfigureAwait(false))
            {
                preview.PeekNote = "SQL no puede leer el archivo desde esta ruta; se analizará al restaurar.";
                return preview;
            }

            var files = await ReadFileListAsync(masterConnectionString, localBakPath, cancellationToken).ConfigureAwait(false);
            preview.DataFiles = files.Count(f => !string.Equals(f.Type, "L", StringComparison.OrdinalIgnoreCase));
            preview.LogFiles = files.Count(f => string.Equals(f.Type, "L", StringComparison.OrdinalIgnoreCase));
            preview.DatabaseExists = await DatabaseExistsAsync(
                    masterConnectionString, preview.DatabaseName!, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            preview.PeekNote = "No se pudo leer el encabezado: " + ex.Message;
        }

        return preview;
    }

    /// <summary>
    /// Lee el .bak (encabezado y lista de archivos) y deja una ruta accesible para el servicio SQL.
    /// No modifica nada en el servidor; solo prepara la información para confirmar la restauración.
    /// </summary>
    public async Task<RestorePlanItem> AnalyzeAsync(
        string masterConnectionString,
        string localBakPath,
        IReadOnlyList<string> stagingCandidates,
        IProgress<string> log,
        CancellationToken cancellationToken)
    {
        var host = SqlServerHost.ExtractHost(masterConnectionString);
        var isRemote = !SqlServerHost.IsLocal(host);
        var fileName = Path.GetFileName(localBakPath);

        var (serverRead, cleanup) = await ResolveServerReadablePathAsync(
                masterConnectionString, localBakPath, host, isRemote, stagingCandidates, log, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var dbName = await ReadDatabaseNameAsync(masterConnectionString, serverRead, cancellationToken).ConfigureAwait(false);
            var files = await ReadFileListAsync(masterConnectionString, serverRead, cancellationToken).ConfigureAwait(false);
            var exists = await DatabaseExistsAsync(masterConnectionString, dbName, cancellationToken).ConfigureAwait(false);

            log.Report($"{fileName}: base detectada «{dbName}» — {(exists ? "YA EXISTE, se sobrescribirá" : "no existe, se creará")}.");

            return new RestorePlanItem
            {
                LocalBakPath = localBakPath,
                ServerReadPath = serverRead,
                ClientCleanupPath = cleanup,
                DatabaseName = dbName,
                DatabaseExists = exists,
                Files = files
            };
        }
        catch
        {
            TryDelete(cleanup);
            throw;
        }
    }

    /// <summary>Ejecuta la restauración del item ya analizado. Acción destructiva (WITH REPLACE).</summary>
    public async Task ExecuteAsync(
        string masterConnectionString,
        RestorePlanItem item,
        IProgress<string> log,
        IProgress<RestoreProgress>? progress,
        CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder(masterConnectionString) { InitialCatalog = "master" };
        await using var conn = new SqlConnection(builder.ConnectionString);
        conn.InfoMessage += (_, e) =>
        {
            log.Report($"{item.DatabaseName}: {e.Message}");
            if (progress is not null && TryParsePercent(e.Message, out var pct))
                progress.Report(new RestoreProgress { Percent = pct, Status = $"Restaurando «{item.DatabaseName}» ({pct} %)…" });
        };
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        var moves = await BuildMoveListAsync(conn, item, cancellationToken).ConfigureAwait(false);

        var dbEsc = "[" + item.DatabaseName.Replace("]", "]]", StringComparison.Ordinal) + "]";
        var dbLit = item.DatabaseName.Replace("'", "''", StringComparison.Ordinal);

        if (item.DatabaseExists)
        {
            log.Report($"{item.DatabaseName}: cerrando conexiones activas (SINGLE_USER)…");
            await ExecAsync(
                    conn,
                    $"IF DB_ID(N'{dbLit}') IS NOT NULL ALTER DATABASE {dbEsc} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var p = item.ServerReadPath.Replace("'", "''", StringComparison.Ordinal);
        var sb = new StringBuilder();
        sb.Append($"RESTORE DATABASE {dbEsc} FROM DISK = N'{p}' WITH REPLACE, RECOVERY, STATS = 5");
        foreach (var mv in moves)
        {
            var lg = mv.Logical.Replace("'", "''", StringComparison.Ordinal);
            var ph = mv.Physical.Replace("'", "''", StringComparison.Ordinal);
            sb.Append($",\n  MOVE N'{lg}' TO N'{ph}'");
        }
        sb.Append(';');

        try
        {
            log.Report($"{item.DatabaseName}: iniciando restauración (REPLACE)…");
            await ExecAsync(conn, sb.ToString(), cancellationToken, commandTimeout: 0).ConfigureAwait(false);
            log.Report($"{item.DatabaseName}: restauración completada.");
        }
        finally
        {
            try
            {
                await ExecAsync(
                        conn,
                        $"IF DB_ID(N'{dbLit}') IS NOT NULL ALTER DATABASE {dbEsc} SET MULTI_USER;",
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.Report($"{item.DatabaseName}: aviso al restablecer acceso multiusuario: {ex.Message}");
            }
        }

        await ApplyBasServerAsync(masterConnectionString, item.DatabaseName, log, cancellationToken)
            .ConfigureAwait(false);
        await ClearAdminAndCnvPasswordsAsync(masterConnectionString, item.DatabaseName, log, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Tras restaurar, actualiza bas_server con el servidor de la conexión actual
    /// para que la base pueda levantarse correctamente en el entorno Bejerman.
    /// </summary>
    private static async Task ApplyBasServerAsync(
        string masterConnectionString,
        string databaseName,
        IProgress<string> log,
        CancellationToken cancellationToken)
    {
        var serverName = new SqlConnectionStringBuilder(masterConnectionString).DataSource?.Trim();
        if (string.IsNullOrEmpty(serverName))
        {
            log.Report($"{databaseName}: no se pudo determinar el servidor para actualizar bas_server.");
            return;
        }

        try
        {
            var dbBuilder = new SqlConnectionStringBuilder(masterConnectionString)
            {
                InitialCatalog = databaseName
            };
            await using var conn = new SqlConnection(dbBuilder.ConnectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = new SqlCommand("UPDATE bas SET bas_server = @s;", conn) { CommandTimeout = 30 };
            cmd.Parameters.AddWithValue("@s", serverName);
            var rows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            log.Report($"{databaseName}: bas_server actualizado a «{serverName}» ({rows} fila(s)).");
        }
        catch (SqlException ex) when (ex.Number is 208 or 4902)
        {
            log.Report($"{databaseName}: aviso — no se encontró la tabla «bas»; se omitió bas_server.");
        }
        catch (Exception ex)
        {
            log.Report($"{databaseName}: aviso — no se pudo actualizar bas_server: {ex.Message}");
        }
    }

    /// <summary>
    /// Blanquea usu_clave de ADMIN y CNV en dbo.usu (por usu_codigo).
    /// </summary>
    private static async Task ClearAdminAndCnvPasswordsAsync(
        string masterConnectionString,
        string databaseName,
        IProgress<string> log,
        CancellationToken cancellationToken)
    {
        try
        {
            var dbBuilder = new SqlConnectionStringBuilder(masterConnectionString)
            {
                InitialCatalog = databaseName
            };
            await using var conn = new SqlConnection(dbBuilder.ConnectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = new SqlCommand(
                """
                UPDATE dbo.usu
                SET usu_clave = N''
                WHERE LTRIM(RTRIM(usu_codigo)) IN (N'ADMIN', N'CNV');
                """,
                conn)
            { CommandTimeout = 30 };

            var rows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            log.Report($"{databaseName}: claves de ADMIN y CNV blanqueadas ({rows} fila(s)).");
        }
        catch (SqlException ex) when (ex.Number is 208 or 4902 or 207)
        {
            // 208/4902: tabla inexistente; 207: columna inexistente
            log.Report($"{databaseName}: aviso — no se pudo blanquear ADMIN/CNV (tabla o columnas usu no encontradas); se omitió.");
        }
        catch (Exception ex)
        {
            log.Report($"{databaseName}: aviso — no se pudieron blanquear claves ADMIN/CNV: {ex.Message}");
        }
    }

    private async Task<(string serverRead, string? cleanup)> ResolveServerReadablePathAsync(
        string connectionString,
        string localBak,
        string? host,
        bool isRemote,
        IReadOnlyList<string> stagingCandidates,
        IProgress<string> log,
        CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(localBak);

        // SQL local: probar que el servicio lea directamente el archivo elegido por el usuario.
        if (!isRemote)
        {
            if (await CanSqlReadAsync(connectionString, localBak, cancellationToken).ConfigureAwait(false))
                return (localBak, null);
            log.Report("El servicio SQL no puede leer el .bak desde tu carpeta; copiando a una ubicación accesible…");
        }
        else
        {
            log.Report("SQL es remoto; copiando el .bak a una ubicación que el servidor pueda leer…");
        }

        foreach (var dir in stagingCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(dir))
                continue;

            string clientWrite;
            string serverRead;
            try
            {
                if (dir.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    Directory.CreateDirectory(dir);
                    clientWrite = Path.Combine(dir, fileName);
                    serverRead = clientWrite; // el servidor ve la misma ruta UNC
                }
                else if (isRemote)
                {
                    var unc = SqlServerHost.ToAdminShareUnc(host, dir);
                    if (string.IsNullOrEmpty(unc))
                        continue;
                    Directory.CreateDirectory(unc);
                    clientWrite = Path.Combine(unc, fileName);
                    serverRead = Path.Combine(dir, fileName); // ruta LOCAL del servidor (SQL lee su disco)
                }
                else
                {
                    Directory.CreateDirectory(dir);
                    clientWrite = Path.Combine(dir, fileName);
                    serverRead = clientWrite;
                }

                log.Report($"Copiando .bak a: {clientWrite}");
                File.Copy(localBak, clientWrite, overwrite: true);
            }
            catch (Exception ex)
            {
                log.Report($"No se pudo usar «{dir}»: {ex.Message}");
                continue;
            }

            if (await CanSqlReadAsync(connectionString, serverRead, cancellationToken).ConfigureAwait(false))
                return (serverRead, clientWrite);

            TryDelete(clientWrite);
            log.Report("El servicio SQL no pudo leer la copia; probando otra ubicación…");
        }

        throw new InvalidOperationException(
            "No se encontró una ubicación desde la cual el servicio SQL Server pueda leer el archivo .bak.\n\n" +
            "Sugerencia: copiá el .bak a una carpeta compartida visible por el servidor, o ejecutá la app en el propio servidor.");
    }

    private static async Task<bool> CanSqlReadAsync(string connectionString, string path, CancellationToken cancellationToken)
    {
        try
        {
            await ReadDatabaseNameAsync(connectionString, path, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SqlException)
        {
            return false;
        }
    }

    private static async Task<bool> TryPopulateHeaderAsync(
        SqlConnection conn,
        string path,
        RestoreFilePreview preview,
        CancellationToken cancellationToken)
    {
        try
        {
            var p = path.Replace("'", "''", StringComparison.Ordinal);
            await using var cmd = new SqlCommand($"RESTORE HEADERONLY FROM DISK = N'{p}'", conn) { CommandTimeout = 120 };
            await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await r.ReadAsync(cancellationToken).ConfigureAwait(false))
                return false;

            var name = GetStringOrNull(r, "DatabaseName")?.Trim();
            if (!string.IsNullOrEmpty(name))
                preview.DatabaseName = name;

            preview.BackupDate = GetDateTimeOrNull(r, "BackupStartDate") ?? GetDateTimeOrNull(r, "BackupFinishDate");
            preview.BackupSizeBytes = GetLongOrNull(r, "BackupSize");
            preview.Compressed = GetIntOrNull(r, "Compressed") == 1;
            preview.BackupServerName = GetStringOrNull(r, "ServerName");
            return true;
        }
        catch (SqlException)
        {
            return false;
        }
    }

    private static string? GetStringOrNull(SqlDataReader r, string column)
    {
        try
        {
            var ord = r.GetOrdinal(column);
            return r.IsDBNull(ord) ? null : Convert.ToString(r.GetValue(ord));
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static DateTime? GetDateTimeOrNull(SqlDataReader r, string column)
    {
        try
        {
            var ord = r.GetOrdinal(column);
            if (r.IsDBNull(ord))
                return null;
            return Convert.ToDateTime(r.GetValue(ord));
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static long? GetLongOrNull(SqlDataReader r, string column)
    {
        try
        {
            var ord = r.GetOrdinal(column);
            if (r.IsDBNull(ord))
                return null;
            return Convert.ToInt64(r.GetValue(ord));
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static int? GetIntOrNull(SqlDataReader r, string column)
    {
        try
        {
            var ord = r.GetOrdinal(column);
            if (r.IsDBNull(ord))
                return null;
            return Convert.ToInt32(r.GetValue(ord));
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static async Task<string> ReadDatabaseNameAsync(string connectionString, string path, CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" };
        await using var conn = new SqlConnection(builder.ConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        var p = path.Replace("'", "''", StringComparison.Ordinal);
        await using var cmd = new SqlCommand($"RESTORE HEADERONLY FROM DISK = N'{p}'", conn) { CommandTimeout = 120 };
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await r.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("El archivo no contiene un backup válido.");

        var ord = r.GetOrdinal("DatabaseName");
        var name = r.IsDBNull(ord) ? "" : Convert.ToString(r.GetValue(ord))?.Trim() ?? "";
        if (string.IsNullOrEmpty(name))
            throw new InvalidOperationException("No se pudo leer el nombre de la base desde el backup.");
        return name;
    }

    private static async Task<List<BackupFileEntry>> ReadFileListAsync(string connectionString, string path, CancellationToken cancellationToken)
    {
        var list = new List<BackupFileEntry>();
        var builder = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" };
        await using var conn = new SqlConnection(builder.ConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        var p = path.Replace("'", "''", StringComparison.Ordinal);
        await using var cmd = new SqlCommand($"RESTORE FILELISTONLY FROM DISK = N'{p}'", conn) { CommandTimeout = 120 };
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var ordL = r.GetOrdinal("LogicalName");
        var ordP = r.GetOrdinal("PhysicalName");
        var ordT = r.GetOrdinal("Type");
        while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new BackupFileEntry(
                Convert.ToString(r.GetValue(ordL))?.Trim() ?? "",
                Convert.ToString(r.GetValue(ordT))?.Trim() ?? "D",
                Convert.ToString(r.GetValue(ordP))?.Trim() ?? ""));
        }

        return list;
    }

    private static async Task<bool> DatabaseExistsAsync(string connectionString, string dbName, CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" };
        await using var conn = new SqlConnection(builder.ConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new SqlCommand("SELECT DB_ID(@n);", conn) { CommandTimeout = 30 };
        cmd.Parameters.AddWithValue("@n", dbName);
        var o = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return o is not null && o != DBNull.Value;
    }

    private static async Task<List<(string Logical, string Physical)>> BuildMoveListAsync(
        SqlConnection conn,
        RestorePlanItem item,
        CancellationToken cancellationToken)
    {
        var moves = new List<(string Logical, string Physical)>();

        var current = new List<(string Name, string Physical, string Type)>();
        if (item.DatabaseExists)
        {
            await using var cmd = new SqlCommand(
                "SELECT name, physical_name, type_desc FROM sys.master_files WHERE database_id = DB_ID(@n);",
                conn)
            { CommandTimeout = 30 };
            cmd.Parameters.AddWithValue("@n", item.DatabaseName);
            await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
                current.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
        }

        var (dataDir, logDir) = await GetDefaultDirsAsync(conn, current, cancellationToken).ConfigureAwait(false);

        foreach (var f in item.Files)
        {
            var match = current.FirstOrDefault(c => string.Equals(c.Name, f.LogicalName, StringComparison.OrdinalIgnoreCase));
            if (match.Physical is not null)
            {
                moves.Add((f.LogicalName, match.Physical));
                continue;
            }

            var isLog = string.Equals(f.Type, "L", StringComparison.OrdinalIgnoreCase);
            var dir = isLog ? logDir : dataDir;

            var ext = Path.GetExtension(f.PhysicalName);
            if (string.IsNullOrEmpty(ext))
                ext = isLog ? ".ldf" : ".mdf";

            var baseName = SanitizeFileName(Path.GetFileNameWithoutExtension(f.PhysicalName));
            if (string.IsNullOrEmpty(baseName))
                baseName = SanitizeFileName(f.LogicalName);

            moves.Add((f.LogicalName, Path.Combine(dir, baseName + ext)));
        }

        return moves;
    }

    private static async Task<(string dataDir, string logDir)> GetDefaultDirsAsync(
        SqlConnection conn,
        List<(string Name, string Physical, string Type)> current,
        CancellationToken cancellationToken)
    {
        string? dataDir = null;
        string? logDir = null;

        var existingData = current.FirstOrDefault(c => !string.Equals(c.Type, "LOG", StringComparison.OrdinalIgnoreCase));
        var existingLog = current.FirstOrDefault(c => string.Equals(c.Type, "LOG", StringComparison.OrdinalIgnoreCase));
        if (existingData.Physical is not null)
            dataDir = Path.GetDirectoryName(existingData.Physical);
        if (existingLog.Physical is not null)
            logDir = Path.GetDirectoryName(existingLog.Physical);

        if (dataDir is null || logDir is null)
        {
            try
            {
                await using var cmd = new SqlCommand(
                    "SELECT CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(512)), CAST(SERVERPROPERTY('InstanceDefaultLogPath') AS nvarchar(512));",
                    conn)
                { CommandTimeout = 30 };
                await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var d = r.IsDBNull(0) ? null : r.GetString(0)?.Trim();
                    var l = r.IsDBNull(1) ? null : r.GetString(1)?.Trim();
                    if (dataDir is null && !string.IsNullOrEmpty(d))
                        dataDir = d;
                    if (logDir is null && !string.IsNullOrEmpty(l))
                        logDir = l;
                }
            }
            catch
            {
                // SERVERPROPERTY puede no estar disponible en versiones viejas
            }
        }

        if (dataDir is null || logDir is null)
        {
            try
            {
                await using var cmd = new SqlCommand(
                    "SELECT TOP 1 physical_name FROM sys.master_files WHERE database_id = DB_ID('master') AND type_desc = 'ROWS';",
                    conn)
                { CommandTimeout = 30 };
                var o = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (o is string mp && !string.IsNullOrWhiteSpace(mp))
                {
                    var dir = Path.GetDirectoryName(mp);
                    dataDir ??= dir;
                    logDir ??= dir;
                }
            }
            catch
            {
                // sin acceso a master_files
            }
        }

        dataDir ??= @"C:\";
        logDir ??= dataDir;
        return (dataDir!, logDir!);
    }

    private static async Task ExecAsync(SqlConnection conn, string sql, CancellationToken cancellationToken, int commandTimeout = 60)
    {
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = commandTimeout };
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static void TryDelete(string? path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // limpieza best-effort
        }
    }

    private static readonly Regex PercentRegex =
        new(@"^\s*(\d{1,3})\s+(?:percent|por\s*ciento)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool TryParsePercent(string? message, out int percent)
    {
        percent = 0;
        if (string.IsNullOrEmpty(message))
            return false;
        var m = PercentRegex.Match(message);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var p))
            return false;
        percent = Math.Clamp(p, 0, 100);
        return true;
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }
}
