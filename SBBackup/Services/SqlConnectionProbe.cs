using Microsoft.Data.SqlClient;

namespace SBBackup.Services;

internal static class SqlConnectionProbe
{
    internal static async Task TestAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
    }
}
