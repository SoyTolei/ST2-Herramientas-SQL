using System.Diagnostics;
using System.Globalization;
using System.Text;
using SBBackup.Models;

namespace SBBackup.Services;

/// <summary>Registra o quita la tarea de Windows que dispara el backup programado.</summary>
internal static class WindowsTaskSchedulerService
{
    internal const string TaskName = "ST2-HerramientasSQL-BackupProgramado";

    internal static string ResolveExecutablePath()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath)
            && !string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(processPath);

        // Single-file: Assembly.Location viene vacío; usamos el directorio de la app.
        var candidate = Path.Combine(AppContext.BaseDirectory, "ST2 - Herramientas SQL.exe");
        if (File.Exists(candidate))
            return Path.GetFullPath(candidate);

        throw new InvalidOperationException(
            "No se pudo determinar la ruta del ejecutable. Publicá la app (publish) y programá el backup desde ese .exe.");
    }

    internal static bool IsLikelyDevHost(string? exePath = null)
    {
        exePath ??= Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
            return false;
        return string.Equals(
            Path.GetFileNameWithoutExtension(exePath),
            "dotnet",
            StringComparison.OrdinalIgnoreCase);
    }

    internal static string GetLauncherScriptPath() =>
        Path.Combine(ScheduledBackupStore.GetStoreDirectory(), "run-scheduled-backup.cmd");

    internal static void Register(ScheduledBackupProfile profile, string executablePath, string profilePath)
    {
        if (string.IsNullOrWhiteSpace(profile.TimeOfDay)
            || !TimeSpan.TryParseExact(profile.TimeOfDay.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out _))
            throw new InvalidOperationException("La hora del backup debe tener formato HH:mm (por ejemplo 02:00).");

        var exe = Path.GetFullPath(executablePath);
        var profileFull = Path.GetFullPath(profilePath);
        WriteLauncherScript(exe, profileFull);

        var launcher = GetLauncherScriptPath();
        var args = new StringBuilder();
        args.Append("/Create /TN \"").Append(TaskName).Append("\" /F /RL LIMITED /IT ");
        args.Append("/TR \"").Append(launcher).Append("\" ");
        args.Append("/ST ").Append(profile.TimeOfDay.Trim()).Append(' ');

        var days = NormalizeDays(profile.DaysOfWeek);
        // Perfiles antiguos "Daily" sin lista de días → todos los días.
        if (days.Count == 0
            && string.Equals(profile.Frequency, ScheduledBackupProfile.FrequencyDaily, StringComparison.OrdinalIgnoreCase))
        {
            days = ["MON", "TUE", "WED", "THU", "FRI", "SAT", "SUN"];
        }

        if (days.Count == 0)
            throw new InvalidOperationException("Marcá al menos un día de la semana.");

        if (days.Count == 7)
            args.Append("/SC DAILY");
        else
            args.Append("/SC WEEKLY /D ").Append(string.Join(',', days));

        RunSchtasks(args.ToString(), allowNonZero: false);
    }

    internal static void Unregister()
    {
        if (!Exists())
            return;

        RunSchtasks($"/Delete /TN \"{TaskName}\" /F", allowNonZero: false);
    }

    internal static bool Exists()
    {
        var result = RunSchtasks($"/Query /TN \"{TaskName}\"", allowNonZero: true);
        return result.ExitCode == 0;
    }

    internal static string DescribeStatus() =>
        TryGetTaskDetail().SummaryLine;

    internal static (string? NextRun, string? Status, string? Schedule, string SummaryLine) TryGetTaskDetail()
    {
        if (!Exists())
            return (null, null, null, "No hay una tarea de backup programada en Windows.");

        var result = RunSchtasks($"/Query /TN \"{TaskName}\" /FO LIST /V", allowNonZero: true);
        if (result.ExitCode != 0)
            return (null, null, null, "Hay una tarea registrada, pero no se pudo leer el detalle.");

        string? next = null;
        string? status = null;
        string? schedule = null;
        string? lastRun = null;
        foreach (var raw in result.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.StartsWith("Next Run Time:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Próxima ejecución:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Hora de próxima ejecución:", StringComparison.OrdinalIgnoreCase))
                next = AfterColon(line);
            else if (line.StartsWith("Last Run Time:", StringComparison.OrdinalIgnoreCase)
                     || line.StartsWith("Última ejecución:", StringComparison.OrdinalIgnoreCase)
                     || line.StartsWith("Hora de última ejecución:", StringComparison.OrdinalIgnoreCase))
                lastRun = AfterColon(line);
            else if (line.StartsWith("Status:", StringComparison.OrdinalIgnoreCase)
                     || line.StartsWith("Estado:", StringComparison.OrdinalIgnoreCase))
                status = AfterColon(line);
            else if (line.StartsWith("Schedule Type:", StringComparison.OrdinalIgnoreCase)
                     || line.StartsWith("Tipo de programación:", StringComparison.OrdinalIgnoreCase))
                schedule = AfterColon(line);
        }

        var sb = new StringBuilder("Tarea de Windows activa");
        if (!string.IsNullOrWhiteSpace(status))
            sb.Append(" · ").Append(status.Trim());
        if (!string.IsNullOrWhiteSpace(schedule))
            sb.Append(" · ").Append(schedule.Trim());
        if (!string.IsNullOrWhiteSpace(next))
            sb.Append(" · próxima: ").Append(next.Trim());
        if (!string.IsNullOrWhiteSpace(lastRun))
            sb.Append(" · última: ").Append(lastRun.Trim());
        return (next, status, schedule, sb.ToString());
    }

    private static void WriteLauncherScript(string executablePath, string profilePath)
    {
        var script = GetLauncherScriptPath();
        var content =
            "@echo off\r\n" +
            "rem Generado por ST2 - Herramientas SQL (backup programado)\r\n" +
            $"\"{executablePath}\" --run-scheduled-backup --profile \"{profilePath}\"\r\n";
        File.WriteAllText(script, content, Encoding.ASCII);
    }

    private static List<string> NormalizeDays(IEnumerable<string>? days)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "MON", "TUE", "WED", "THU", "FRI", "SAT", "SUN"
        };
        var list = new List<string>();
        foreach (var d in days ?? [])
        {
            var t = (d ?? "").Trim().ToUpperInvariant();
            if (allowed.Contains(t) && !list.Contains(t, StringComparer.OrdinalIgnoreCase))
                list.Add(t);
        }

        return list;
    }

    private static string? AfterColon(string line)
    {
        var i = line.IndexOf(':');
        return i < 0 ? null : line[(i + 1)..].Trim();
    }

    private static (int ExitCode, string StdOut, string StdErr) RunSchtasks(string arguments, bool allowNonZero)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.Default,
            StandardErrorEncoding = Encoding.Default
        };

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("No se pudo iniciar schtasks.exe.");
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);

        if (!allowNonZero && p.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException(
                "No se pudo configurar el Programador de tareas de Windows.\n\n" +
                (string.IsNullOrWhiteSpace(detail) ? $"Código de salida: {p.ExitCode}" : detail.Trim()));
        }

        return (p.ExitCode, stdout, stderr);
    }
}
