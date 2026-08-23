using Microsoft.Data.SqlClient;

namespace SBBackup.Services;

public static class ConnectionStringFactory
{
    public static string Build(string server, bool integratedSecurity, string? sqlUser, string? sqlPassword)
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = server.Trim(),
            IntegratedSecurity = integratedSecurity,
            TrustServerCertificate = true,
            Encrypt = SqlConnectionEncryptOption.Mandatory
        };

        if (!integratedSecurity)
        {
            b.UserID = sqlUser?.Trim() ?? "";
            b.Password = sqlPassword ?? "";
        }

        return b.ConnectionString;
    }
}
