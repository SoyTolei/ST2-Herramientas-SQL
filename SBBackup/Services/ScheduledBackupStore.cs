using System.Text.Json;
using SBBackup.Models;

namespace SBBackup.Services;

internal static class ScheduledBackupStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    internal static string GetProfilePath() =>
        Path.Combine(GetStoreDirectory(), "scheduled-backup.json");

    internal static string GetStoreDirectory()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ST2-SBBackup");
        Directory.CreateDirectory(dir);
        return dir;
    }

    internal static ScheduledBackupProfile? TryLoad()
    {
        var path = GetProfilePath();
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ScheduledBackupProfile>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    internal static void Save(ScheduledBackupProfile profile)
    {
        profile.UpdatedAt = DateTimeOffset.Now;
        var path = GetProfilePath();
        var json = JsonSerializer.Serialize(profile, JsonOptions);
        File.WriteAllText(path, json);
    }

    internal static void Delete()
    {
        var path = GetProfilePath();
        if (File.Exists(path))
            File.Delete(path);
    }
}
