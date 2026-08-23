namespace SBBackup.Services;

/// <summary>
/// Tras un backup exitoso: solo ZIP. Si falla el ZIP y el usuario guarda .bak: solo .bak.
/// </summary>
internal static class WorkspaceFileCleanup
{
    public static void RemoveBakFilesOnly(
        string workspaceDir,
        IEnumerable<string> knownPaths,
        IProgress<string>? log)
    {
        foreach (var p in knownPaths
                     .Where(static p => !string.IsNullOrWhiteSpace(p))
                     .Select(static p => Path.GetFullPath(p.Trim()))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            TryDeleteFile(p, log);
        }

        if (!Directory.Exists(workspaceDir))
            return;

        foreach (var p in Directory.EnumerateFiles(workspaceDir, "*.bak", SearchOption.TopDirectoryOnly))
            TryDeleteFile(p, log);

        SqlBackupStagingResolver.TryCleanupLegacyWorkspaceSqlTemp(workspaceDir, log);
    }

    public static void RemoveZipFile(string? zipPath, IProgress<string>? log)
    {
        if (string.IsNullOrWhiteSpace(zipPath))
            return;

        TryDeleteFile(Path.GetFullPath(zipPath.Trim()), log);
    }

    public static int CountBakFilesInWorkspace(string workspaceDir) =>
        Directory.Exists(workspaceDir)
            ? Directory.EnumerateFiles(workspaceDir, "*.bak", SearchOption.TopDirectoryOnly).Count()
            : 0;

    private static void TryDeleteFile(string path, IProgress<string>? log)
    {
        if (!File.Exists(path))
            return;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(250);
            }
            catch (Exception ex)
            {
                log?.Report($"No se pudo borrar {Path.GetFileName(path)}: {ex.Message}");
                return;
            }
        }
    }
}
