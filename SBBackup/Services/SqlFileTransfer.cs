using System.Data;
using Microsoft.Data.SqlClient;

namespace SBBackup.Services;

/// <summary>
/// Trae un archivo que vive en el disco del servidor SQL hacia el cliente usando la
/// propia conexión SQL (OPENROWSET BULK), sin necesidad de carpetas compartidas ni
/// del recurso administrativo C$. Pensado para terminales que tienen el sistema pero
/// no el motor SQL instalado.
/// </summary>
internal static class SqlFileTransfer
{
    private const int BufferSize = 1 << 20; // 1 MB

    /// <summary>
    /// Descarga el archivo server-side <paramref name="serverFilePath"/> y lo escribe en
    /// <paramref name="destPath"/> (local del cliente). Devuelve true si lo logró.
    /// Requiere permiso de operaciones masivas (lo tiene sysadmin).
    /// </summary>
    public static async Task<bool> TryDownloadViaSqlAsync(
        string masterConnectionString,
        string serverFilePath,
        string destPath,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        try
        {
            var builder = new SqlConnectionStringBuilder(masterConnectionString) { InitialCatalog = "master" };
            await using var conn = new SqlConnection(builder.ConnectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            var pathLiteral = serverFilePath.Replace("'", "''", StringComparison.Ordinal);
            var sql = $"SELECT BulkColumn FROM OPENROWSET(BULK N'{pathLiteral}', SINGLE_BLOB) AS f;";

            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 0 };
            await using var reader = await cmd
                .ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken)
                .ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return false;

            var destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);

            await using (var source = reader.GetStream(0))
            await using (var target = new FileStream(
                destPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                await source.CopyToAsync(target, BufferSize, cancellationToken).ConfigureAwait(false);
            }

            return File.Exists(destPath) && new FileInfo(destPath).Length > 0;
        }
        catch (Exception ex)
        {
            log?.Report($"No se pudo descargar el .bak por la conexión SQL: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Intenta borrar el archivo temporal que quedó en el servidor (best-effort):
    /// primero por sistema de archivos (local o \\HOST\C$) y, si no, por xp_cmdshell.
    /// </summary>
    public static async Task TryDeleteServerFileAsync(
        string masterConnectionString,
        string serverFilePath,
        string? serverHost,
        bool serverIsRemote,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        try
        {
            if (File.Exists(serverFilePath))
            {
                File.Delete(serverFilePath);
                return;
            }

            if (serverIsRemote)
            {
                var unc = SqlServerHost.ToAdminShareUnc(serverHost, serverFilePath);
                if (!string.IsNullOrEmpty(unc) && File.Exists(unc))
                {
                    File.Delete(unc);
                    return;
                }
            }
        }
        catch
        {
            // seguimos con el intento por SQL
        }

        try
        {
            var builder = new SqlConnectionStringBuilder(masterConnectionString) { InitialCatalog = "master" };
            await using var conn = new SqlConnection(builder.ConnectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            var pathLiteral = serverFilePath.Replace("'", "''", StringComparison.Ordinal);
            var sql = $"""
                IF EXISTS (SELECT 1 FROM sys.configurations
                           WHERE name = 'xp_cmdshell' AND CAST(value_in_use AS int) = 1)
                BEGIN
                    DECLARE @cmd nvarchar(4000) = N'del /q "{pathLiteral}"';
                    EXEC master.dbo.xp_cmdshell @cmd;
                END
                """;

            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log?.Report(
                $"Quedó un .bak temporal en el servidor ({Path.GetFileName(serverFilePath)}). " +
                $"Si querés, borralo manualmente. Detalle: {ex.Message}");
        }
    }
}
