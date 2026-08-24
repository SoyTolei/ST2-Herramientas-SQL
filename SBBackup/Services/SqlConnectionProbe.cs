using Microsoft.Data.SqlClient;

namespace SBBackup.Services;

internal static class SqlConnectionProbe
{
    internal static async Task TestAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        // Un reintento corto ante timeout de pre-login (redes de terminal inestables).
        Exception? last = null;
        for (var i = 0; i < 2; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var conn = new SqlConnection(connectionString);
                await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
                // Validación mínima: que el canal responda.
                await using var cmd = new SqlCommand("SELECT 1", conn) { CommandTimeout = 10 };
                _ = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (i == 0 && LooksLikeTransientPreLogin(ex))
            {
                last = ex;
                await Task.Delay(400, cancellationToken).ConfigureAwait(false);
            }
        }

        throw last ?? new InvalidOperationException("No se pudo abrir la conexión SQL.");
    }

    private static bool LooksLikeTransientPreLogin(Exception ex)
    {
        var msg = ex.GetBaseException().Message ?? "";
        return msg.Contains("tiempo de espera", StringComparison.OrdinalIgnoreCase)
               || msg.Contains("timeout", StringComparison.OrdinalIgnoreCase)
               || msg.Contains("pre-login", StringComparison.OrdinalIgnoreCase)
               || msg.Contains("previo al inicio de sesión", StringComparison.OrdinalIgnoreCase)
               || msg.Contains("handshake", StringComparison.OrdinalIgnoreCase)
               || (ex is SqlException sql && sql.Number is -2 or 258);
    }
}
