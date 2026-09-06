using System.Text.Json;

namespace SBBackup.Services;

/// <summary>
/// Login SQL habitual de Bejerman. La clave no va en el código: se lee de
/// appsettings.local.json. Si no hay clave, la conexión usa autenticación de Windows.
/// </summary>
internal static class BejermanSqlDefaults
{
    internal static string User { get; private set; } = "bejerman";
    internal static string Password { get; private set; } = "";
    internal static bool HasPassword => !string.IsNullOrWhiteSpace(Password);

    static BejermanSqlDefaults() => Load();

    private static void Load()
    {
        foreach (var path in CandidatePaths())
        {
            try
            {
                if (!File.Exists(path))
                    continue;

                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!TryGetPropertyCaseInsensitive(doc.RootElement, "SqlDefaults", out var sql))
                    continue;

                if (TryGetString(sql, "User", out var user) && user.Length > 0)
                    User = user;
                if (TryGetString(sql, "Password", out var pwd) && pwd.Length > 0 && !IsPlaceholder(pwd))
                    Password = pwd;
            }
            catch
            {
                // archivo inválido: se ignora
            }
        }
    }

    private static IEnumerable<string> CandidatePaths()
    {
        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "appsettings.json");
        yield return Path.Combine(baseDir, "appsettings.local.json");

        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ST2", "appsettings.local.json");
        yield return appData;
    }

    private static bool IsPlaceholder(string value)
    {
        var v = value.Trim();
        return v.Contains("tu-clave", StringComparison.OrdinalIgnoreCase)
               || v.Contains("PEGAR", StringComparison.OrdinalIgnoreCase)
               || v.Contains("TU_CLAVE", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetString(JsonElement obj, string name, out string value)
    {
        value = "";
        if (obj.ValueKind != JsonValueKind.Object)
            return false;
        if (!TryGetPropertyCaseInsensitive(obj, name, out var el))
            return false;
        if (el.ValueKind != JsonValueKind.String)
            return false;
        value = el.GetString() ?? "";
        return true;
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement obj, string name, out JsonElement value)
    {
        value = default;
        if (obj.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        return false;
    }
}
