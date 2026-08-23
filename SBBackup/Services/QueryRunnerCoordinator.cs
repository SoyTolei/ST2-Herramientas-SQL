using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace SBBackup.Services;

/// <summary>
/// Ejecuta un script SQL sobre una base concreta. Cuando el script modifica datos,
/// corre dentro de una transacción y consulta al llamador si confirma (COMMIT) o
/// cancela (ROLLBACK) antes de que los cambios queden aplicados.
/// </summary>
public sealed class QueryRunnerCoordinator
{
    public sealed class ExecutionResult
    {
        public List<DataTable> ResultSets { get; } = [];
        public List<string> Messages { get; } = [];
        public int RowsAffected { get; set; }
        public bool WasTransactional { get; set; }
        public bool Committed { get; set; }
    }

    /// <param name="useTransaction">
    /// Si es true, corre dentro de una transacción y consulta al llamador si confirma (COMMIT)
    /// o cancela (ROLLBACK) antes de que los cambios queden aplicados.
    /// Algunas operaciones (ALTER DATABASE, SHRINKFILE, etc.) deben ejecutarse con false.
    /// </param>
    /// <param name="confirmCommit">
    /// Se invoca sólo cuando corre en transacción, después de ejecutarlo y antes del COMMIT.
    /// Debe devolver true para confirmar (COMMIT) o false para cancelar (ROLLBACK).
    /// </param>
    public async Task<ExecutionResult> ExecuteAsync(
        string masterConnectionString,
        string database,
        string sql,
        bool useTransaction,
        Func<ExecutionResult, bool>? confirmCommit,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        var result = new ExecutionResult { WasTransactional = useTransaction };

        var builder = new SqlConnectionStringBuilder(masterConnectionString)
        {
            InitialCatalog = database
        };

        await using var conn = new SqlConnection(builder.ConnectionString);
        conn.InfoMessage += (_, e) =>
        {
            foreach (SqlError err in e.Errors)
                result.Messages.Add(err.Message);
        };

        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        log?.Report($"Conectado a la base «{database}».");

        SqlTransaction? tx = null;
        if (useTransaction)
        {
            tx = (SqlTransaction)await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            log?.Report("Ejecutando dentro de una transacción (pendiente de confirmación).");
        }

        try
        {
            var totalAffected = 0;
            foreach (var batch in SplitBatches(sql))
            {
                cancellationToken.ThrowIfCancellationRequested();

                await using var cmd = new SqlCommand(batch, conn, tx) { CommandTimeout = 0 };
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                do
                {
                    if (reader.FieldCount > 0)
                        result.ResultSets.Add(await ReadResultSetAsync(reader, cancellationToken).ConfigureAwait(false));
                }
                while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));

                if (reader.RecordsAffected > 0)
                    totalAffected += reader.RecordsAffected;
            }

            result.RowsAffected = totalAffected;
            log?.Report($"Sentencias ejecutadas. Filas afectadas: {totalAffected}.");

            if (useTransaction && tx is not null)
            {
                var commit = confirmCommit?.Invoke(result) ?? true;
                if (commit)
                {
                    await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                    result.Committed = true;
                    log?.Report("Cambios confirmados (COMMIT).");
                }
                else
                {
                    await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    result.Committed = false;
                    log?.Report("Cambios cancelados (ROLLBACK). No se modificó nada.");
                }
            }

            return result;
        }
        catch
        {
            if (tx is not null)
            {
                try { await tx.RollbackAsync(cancellationToken).ConfigureAwait(false); }
                catch { /* la conexión puede haberse invalidado */ }
                log?.Report("Ocurrió un error: se revirtieron los cambios (ROLLBACK).");
            }
            throw;
        }
        finally
        {
            if (tx is not null)
                await tx.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<DataTable> ReadResultSetAsync(SqlDataReader reader, CancellationToken ct)
    {
        var table = new DataTable();
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var colName = reader.GetName(i);
            if (string.IsNullOrEmpty(colName))
                colName = $"Columna{i + 1}";
            // columnas como object para evitar conflictos de tipo al mostrar
            table.Columns.Add(UniqueColumnName(table, colName));
        }

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            table.Rows.Add(values);
        }

        return table;
    }

    private static string UniqueColumnName(DataTable table, string baseName)
    {
        if (!table.Columns.Contains(baseName))
            return baseName;

        var n = 2;
        while (table.Columns.Contains($"{baseName} ({n})"))
            n++;
        return $"{baseName} ({n})";
    }

    private static IEnumerable<string> SplitBatches(string sql)
    {
        var current = new System.Text.StringBuilder();
        foreach (var line in sql.Replace("\r\n", "\n").Split('\n'))
        {
            if (Regex.IsMatch(line, @"^\s*GO\s*;?\s*$", RegexOptions.IgnoreCase))
            {
                if (current.ToString().Trim().Length > 0)
                    yield return current.ToString();
                current.Clear();
                continue;
            }
            current.AppendLine(line);
        }

        if (current.ToString().Trim().Length > 0)
            yield return current.ToString();
    }
}
