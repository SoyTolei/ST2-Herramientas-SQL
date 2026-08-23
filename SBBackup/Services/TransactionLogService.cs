using Microsoft.Data.SqlClient;

namespace SBBackup.Services;

public sealed class TransactionLogService
{
    public async Task TryShrinkLogAsync(
        string masterConnectionString,
        string databaseName,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder(masterConnectionString) { InitialCatalog = "master" };
        await using var conn = new SqlConnection(builder.ConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        string recovery;
        const string recoverySql = "SELECT recovery_model_desc FROM sys.databases WHERE name = @n;";
        await using (var cmd = new SqlCommand(recoverySql, conn))
        {
            cmd.Parameters.AddWithValue("@n", databaseName);
            var scalar = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (scalar is null or DBNull)
            {
                log?.Report($"{databaseName}: no se encontró la base para el ajuste previo.");
                return;
            }

            recovery = (string)scalar;
        }

        var logFiles = new List<string>();
        const string logFileSql = """
            SELECT mf.name
            FROM sys.master_files mf
            INNER JOIN sys.databases d ON d.database_id = mf.database_id
            WHERE d.name = @n AND mf.type = 1;
            """;
        await using (var cmd = new SqlCommand(logFileSql, conn))
        {
            cmd.Parameters.AddWithValue("@n", databaseName);
            await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
                logFiles.Add(r.GetString(0));
        }

        if (logFiles.Count == 0)
        {
            log?.Report($"{databaseName}: ajuste previo omitido (sin archivos internos detectados).");
            return;
        }

        var escapedDb = EscapeSqlBracketIdentifier(databaseName);

        try
        {
            await ExecAsync(conn, $"ALTER DATABASE {escapedDb} SET RECOVERY SIMPLE WITH NO_WAIT;", cancellationToken)
                .ConfigureAwait(false);
            await ExecAsync(conn, $"USE {escapedDb}; CHECKPOINT;", cancellationToken).ConfigureAwait(false);

            foreach (var logName in logFiles)
            {
                var ln = EscapeSqlStringLiteral(logName);
                await ExecAsync(conn, $"DBCC SHRINKFILE (N'{ln}', 1);", cancellationToken).ConfigureAwait(false);
            }

            var restoreRecovery = recovery.ToUpperInvariant() switch
            {
                "FULL" => "FULL",
                "BULK_LOGGED" => "BULK_LOGGED",
                _ => "SIMPLE"
            };

            await ExecAsync(conn, $"ALTER DATABASE {escapedDb} SET RECOVERY {restoreRecovery} WITH NO_WAIT;", cancellationToken)
                .ConfigureAwait(false);

            log?.Report($"{databaseName}: ajuste previo listo (la base quedó igual que antes para el uso diario).");
        }
        catch (Exception ex)
        {
            log?.Report($"{databaseName}: no se pudo completar el ajuste previo: {ex.Message}");
            try
            {
                var restoreRecovery = recovery.ToUpperInvariant() switch
                {
                    "FULL" => "FULL",
                    "BULK_LOGGED" => "BULK_LOGGED",
                    _ => "SIMPLE"
                };
                await ExecAsync(conn, $"ALTER DATABASE {escapedDb} SET RECOVERY {restoreRecovery} WITH NO_WAIT;", cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // ignore secondary failure
            }
        }
    }

    private static async Task ExecAsync(SqlConnection conn, string sql, CancellationToken cancellationToken)
    {
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 0 };
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string EscapeSqlBracketIdentifier(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private static string EscapeSqlStringLiteral(string s) => s.Replace("'", "''", StringComparison.Ordinal);
}
