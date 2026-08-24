using System.Globalization;
using System.Text;
using SBBackup.Models;

namespace SBBackup.Services;

/// <summary>Resumen legible del backup programado (perfil + tarea Windows + último ZIP).</summary>
internal static class ScheduledBackupStatus
{
    private static readonly (string Code, string Label)[] WeekDays =
    [
        ("MON", "Lunes"),
        ("TUE", "Martes"),
        ("WED", "Miércoles"),
        ("THU", "Jueves"),
        ("FRI", "Viernes"),
        ("SAT", "Sábado"),
        ("SUN", "Domingo")
    ];

    internal static string BuildDetailedSummary()
    {
        var profile = ScheduledBackupStore.TryLoad();
        var taskExists = false;
        string? taskNext = null;
        string? taskStatus = null;

        try
        {
            taskExists = WindowsTaskSchedulerService.Exists();
            if (taskExists)
            {
                var detail = WindowsTaskSchedulerService.TryGetTaskDetail();
                taskNext = detail.NextRun;
                taskStatus = detail.Status;
            }
        }
        catch
        {
            // seguimos con lo del perfil
        }

        if (profile is null && !taskExists)
            return "Sin programación activa.\nConfigurá días, hora y bases, y pulsá «Guardar y programar».";

        var sb = new StringBuilder();

        if (profile is { Enabled: true } && taskExists)
            sb.AppendLine("● Programación ACTIVA");
        else if (profile is { Enabled: true } && !taskExists)
            sb.AppendLine("● Perfil guardado, pero NO hay tarea en Windows (volvé a «Guardar y programar»)");
        else if (profile is { Enabled: false })
            sb.AppendLine("● Programación DESACTIVADA");
        else
            sb.AppendLine("● Hay una tarea en Windows (sin perfil local detallado)");

        if (profile is not null)
        {
            if (!string.IsNullOrWhiteSpace(profile.Server))
                sb.AppendLine("Servidor: " + profile.Server.Trim());

            sb.AppendLine("Días: " + FormatDays(profile));
            sb.AppendLine("Hora: " + (string.IsNullOrWhiteSpace(profile.TimeOfDay) ? "(sin hora)" : profile.TimeOfDay.Trim()));

            if (profile.DatabaseNames.Count > 0)
            {
                var dbs = profile.DatabaseNames
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => n.Trim())
                    .ToList();
                sb.AppendLine(dbs.Count <= 4
                    ? "Bases (" + dbs.Count + "): " + string.Join(", ", dbs)
                    : "Bases (" + dbs.Count + "): " + string.Join(", ", dbs.Take(4)) + "…");
            }

            var dest = OutputPathHelper.GetScheduledBackupDirectory();
            sb.AppendLine("Carpeta: " + dest);
            sb.AppendLine("  (subcarpeta por día; se elimina lo más viejo a los 7 días)");
        }

        if (taskExists)
        {
            if (!string.IsNullOrWhiteSpace(taskStatus))
                sb.AppendLine("Estado Windows: " + taskStatus.Trim());
            if (!string.IsNullOrWhiteSpace(taskNext)
                && !taskNext.Contains("N/A", StringComparison.OrdinalIgnoreCase)
                && !taskNext.Contains("deshabilitad", StringComparison.OrdinalIgnoreCase))
                sb.AppendLine("Próxima ejecución: " + taskNext.Trim());
            else
                sb.AppendLine("Próxima ejecución: (consultar Programador de tareas)");
        }

        var last = TryDescribeLastGeneratedBackup(OutputPathHelper.GetScheduledBackupDirectory());
        if (!string.IsNullOrWhiteSpace(last))
        {
            sb.AppendLine();
            sb.AppendLine("Último backup generado:");
            sb.Append(last);
        }

        if (profile is not null && profile.UpdatedAt != default)
            sb.AppendLine()
                .Append("Actualizado: ")
                .Append(profile.UpdatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture));

        return sb.ToString().TrimEnd();
    }

    private static string FormatDays(ScheduledBackupProfile profile)
    {
        var set = new HashSet<string>(profile.DaysOfWeek ?? [], StringComparer.OrdinalIgnoreCase);
        if (set.Count == 0
            && string.Equals(profile.Frequency, ScheduledBackupProfile.FrequencyDaily, StringComparison.OrdinalIgnoreCase))
            return "Todos los días";

        var labels = WeekDays
            .Where(d => set.Contains(d.Code))
            .Select(d => d.Label)
            .ToList();

        if (labels.Count == 0)
            return "(sin días)";
        if (labels.Count == 7)
            return "Todos los días";
        return string.Join(", ", labels);
    }

    private static string? TryDescribeLastGeneratedBackup(string? configuredRoot)
    {
        try
        {
            var root = string.IsNullOrWhiteSpace(configuredRoot)
                ? OutputPathHelper.GetScheduledBackupDirectory()
                : configuredRoot.Trim();
            if (!Directory.Exists(root))
                return null;

            // Preferimos subcarpetas de día; si no, ZIPs sueltos en la raíz.
            var dayDirs = Directory.GetDirectories(root)
                .Where(d => !string.Equals(Path.GetFileName(d), "logs", StringComparison.OrdinalIgnoreCase))
                .Select(d => new DirectoryInfo(d))
                .OrderByDescending(d => d.LastWriteTimeUtc)
                .ToList();

            if (dayDirs.Count > 0)
            {
                var latest = dayDirs[0];
                var zips = latest.GetFiles("*.zip", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ToList();
                var sb = new StringBuilder();
                sb.AppendLine("  Carpeta: " + latest.Name);
                sb.AppendLine("  Fecha: " + latest.LastWriteTime.ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture));
                if (zips.Count > 0)
                {
                    var z = zips[0];
                    sb.AppendLine("  ZIP: " + z.Name);
                    sb.AppendLine("  Tamaño: " + FormatSize(z.Length));
                }
                else
                {
                    var baks = latest.GetFiles("*.bak", SearchOption.TopDirectoryOnly).Length;
                    sb.AppendLine(baks > 0
                        ? "  Archivos .bak: " + baks + " (sin ZIP todavía)"
                        : "  (carpeta vacía o sin ZIP)");
                }

                return sb.ToString().TrimEnd();
            }

            var rootZips = Directory.GetFiles(root, "*.zip", SearchOption.TopDirectoryOnly)
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
            if (rootZips.Count == 0)
                return null;

            var zip = rootZips[0];
            return "  ZIP: " + zip.Name + "\n  Fecha: " +
                   zip.LastWriteTime.ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture) +
                   "\n  Tamaño: " + FormatSize(zip.Length);
        }
        catch
        {
            return null;
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
            return bytes + " B";
        double kb = bytes / 1024.0;
        if (kb < 1024)
            return kb.ToString("0.#", CultureInfo.CurrentCulture) + " KB";
        double mb = kb / 1024.0;
        if (mb < 1024)
            return mb.ToString("0.#", CultureInfo.CurrentCulture) + " MB";
        return (mb / 1024.0).ToString("0.##", CultureInfo.CurrentCulture) + " GB";
    }
}
