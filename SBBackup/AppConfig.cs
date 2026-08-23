using System.Text.Json;
using System.Text.Json.Serialization;

namespace SBBackup;

public sealed class AppConfig
{
    public static readonly string[] DefaultCompanyNameQueries =
    [
        "SELECT TOP (1) CAST(RazonSocial AS nvarchar(4000)) AS N FROM dbo.Empresa WITH (NOLOCK) WHERE NULLIF(LTRIM(RTRIM(RazonSocial)), N'') IS NOT NULL",
        "SELECT TOP (1) CAST(NombreFantasia AS nvarchar(4000)) AS N FROM dbo.Empresa WITH (NOLOCK) WHERE NULLIF(LTRIM(RTRIM(NombreFantasia)), N'') IS NOT NULL",
        "SELECT TOP (1) CAST(Descripcion AS nvarchar(4000)) AS N FROM dbo.Empresa WITH (NOLOCK) WHERE NULLIF(LTRIM(RTRIM(Descripcion)), N'') IS NOT NULL",
        "SELECT TOP (1) CAST(RazonSocial AS nvarchar(4000)) AS N FROM dbo.EMPRESA WITH (NOLOCK) WHERE NULLIF(LTRIM(RTRIM(RazonSocial)), N'') IS NOT NULL",
        "SELECT TOP (1) CAST(Nombre AS nvarchar(4000)) AS N FROM dbo.Empresa WITH (NOLOCK) WHERE NULLIF(LTRIM(RTRIM(Nombre)), N'') IS NOT NULL"
    ];

    // Vacío = sin filtro por nombre (se listan todas las bases en línea menos sistema y
    // plantillas genéricas). Esto evita perder ejercicios con nombres libres (p. ej. MIR_2025).
    [JsonPropertyName("DatabaseNameIncludeRegex")]
    public string DatabaseNameIncludeRegex { get; init; } = "";

    [JsonPropertyName("ExcludeSystemDatabases")]
    public bool ExcludeSystemDatabases { get; init; } = true;

    [JsonPropertyName("ManagerDatabaseName")]
    public string ManagerDatabaseName { get; init; } = "manager";

    [JsonPropertyName("CompanyFriendlyNameQueries")]
    public string[] CompanyFriendlyNameQueries { get; init; } = [];

    public static AppConfig Load(string baseDirectory)
    {
        var path = Path.Combine(baseDirectory, "appsettings.json");
        if (!File.Exists(path))
            return WithDefaultQueries(new AppConfig());

        try
        {
            var json = File.ReadAllText(path);
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
            return WithDefaultQueries(cfg);
        }
        catch
        {
            return WithDefaultQueries(new AppConfig());
        }
    }

    private static AppConfig WithDefaultQueries(AppConfig cfg) =>
        cfg.CompanyFriendlyNameQueries.Length == 0
            ? new AppConfig
            {
                DatabaseNameIncludeRegex = cfg.DatabaseNameIncludeRegex,
                ExcludeSystemDatabases = cfg.ExcludeSystemDatabases,
                ManagerDatabaseName = cfg.ManagerDatabaseName,
                CompanyFriendlyNameQueries = DefaultCompanyNameQueries
            }
            : cfg;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true
    };
}
