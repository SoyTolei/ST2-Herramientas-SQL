using System.Text.Json;
using SBBackup.Models;

namespace SBBackup.Services;

/// <summary>Ejecuta un backup programado sin UI (invocado por el Programador de tareas).</summary>
internal static class ScheduledBackupRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal static async Task<int> RunAsync(string? profilePath, CancellationToken cancellationToken)
    {
        var path = string.IsNullOrWhiteSpace(profilePath)
            ? ScheduledBackupStore.GetProfilePath()
            : Path.GetFullPath(profilePath.Trim());

        var logLines = new List<string>();
        void Log(string msg)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            logLines.Add(line);
            try { Console.WriteLine(line); } catch { /* WinExe sin consola */ }
        }

        string? logFile = null;

        try
        {
            if (!File.Exists(path))
            {
                Log("No se encontró el perfil de backup programado: " + path);
                return 1;
            }

            ScheduledBackupProfile? profile;
            try
            {
                var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                profile = JsonSerializer.Deserialize<ScheduledBackupProfile>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                Log("No se pudo leer el perfil: " + UserMessageSpanish.ShortTechnical(ex));
                return 1;
            }

            if (profile is null)
            {
                Log("El perfil de backup programado está vacío o es inválido.");
                return 1;
            }

            if (!profile.Enabled)
            {
                Log("El perfil existe pero está desactivado; no se ejecuta el backup.");
                return 0;
            }

            if (string.IsNullOrWhiteSpace(profile.Server))
            {
                Log("El perfil no tiene servidor SQL.");
                return 1;
            }

            if (profile.DatabaseNames.Count == 0)
            {
                Log("El perfil no tiene bases seleccionadas.");
                return 1;
            }

            var baseDir = OutputPathHelper.GetScheduledBackupDirectory();

            if (!OutputPathHelper.TryValidateWriteAccess(baseDir, out var pathError))
            {
                Log(pathError);
                return 1;
            }

            baseDir = OutputPathHelper.ResolveWorkspaceDirectory(baseDir);
            // ZIP del día: …\Backups Automaticos Bejerman ST2\Lunes 10-08-2026\
            var outputDir = OutputPathHelper.GetScheduledBackupDayDirectory(DateTime.Now);
            if (!OutputPathHelper.TryValidateWriteAccess(outputDir, out pathError))
            {
                Log(pathError);
                return 1;
            }

            outputDir = OutputPathHelper.ResolveWorkspaceDirectory(outputDir);
            var logsDir = Path.Combine(baseDir, "logs");
            Directory.CreateDirectory(logsDir);
            logFile = Path.Combine(logsDir, $"backup-{DateTime.Now:yyyyMMdd-HHmmss}.log");

            Log($"Servidor: {profile.Server}");
            Log($"Bases ({profile.DatabaseNames.Count}): {string.Join(", ", profile.DatabaseNames)}");
            Log($"Destino del día: {outputDir}");

            var progress = new Progress<string>(Log);
            Log("Conectando…");
            var cs = await SqlConnectionHelper.ConnectAsync(profile.Server, progress, cancellationToken)
                .ConfigureAwait(false);

            var selected = profile.DatabaseNames
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => new ServerDatabaseRow
                {
                    PhysicalName = n.Trim(),
                    Selected = true,
                    FriendlyName = n.Trim(),
                    GridNombre = n.Trim()
                })
                .ToList();

            var coordinator = new BackupCoordinator(new TransactionLogService());
            var result = await coordinator.RunBackupsAsync(
                    cs,
                    selected,
                    outputDir,
                    progress,
                    progress: null,
                    cancellationToken,
                    includeTimeInName: true)
                .ConfigureAwait(false);

            Log($"OK · {result.DatabasesBackedUp} base(s) · " +
                (result.ZipFilePath ?? result.OutputDirectory));

            var pruned = OutputPathHelper.PruneExpiredScheduledBackups(baseDir, Log);
            if (pruned == 0)
                Log($"Retención: se conservan las copias de los últimos {OutputPathHelper.ScheduledBackupRetentionDays} días.");

            return 0;
        }
        catch (ZipCreationException ex)
        {
            Log("ERROR ZIP: " + UserMessageSpanish.ShortTechnical(ex.InnerException ?? ex));
            Log("Se conservan los archivos .bak en: " + ex.OutputDirectory);
            return 2;
        }
        catch (OperationCanceledException)
        {
            Log("Backup cancelado.");
            return 1;
        }
        catch (Exception ex)
        {
            Log("ERROR: " + UserMessageSpanish.FriendlyError("Falló el backup programado.", ex));
            return 1;
        }
        finally
        {
            if (logFile is not null && logLines.Count > 0)
            {
                try
                {
                    await File.WriteAllLinesAsync(logFile, logLines, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // no fallar por el log
                }
            }
        }
    }
}
