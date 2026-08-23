namespace SBBackup.Models;

/// <summary>Perfil de backup automático (sin contraseñas; la conexión usa la misma lógica que la UI).</summary>
public sealed class ScheduledBackupProfile
{
    public const string FrequencyDaily = "Daily";
    public const string FrequencyWeekly = "Weekly";

    public bool Enabled { get; set; }

    public string Server { get; set; } = "";

    /// <summary>Nombres físicos de las bases a respaldar.</summary>
    public List<string> DatabaseNames { get; set; } = [];

    /// <summary>Carpeta destino (por defecto …\Respaldo de Backups).</summary>
    public string OutputDirectory { get; set; } = "";

    /// <summary><see cref="FrequencyDaily"/> o <see cref="FrequencyWeekly"/>.</summary>
    public string Frequency { get; set; } = FrequencyDaily;

    /// <summary>Hora local en formato HH:mm (24 h).</summary>
    public string TimeOfDay { get; set; } = "02:00";

    /// <summary>
    /// Días para frecuencia semanal, abreviaturas en inglés que entiende schtasks:
    /// MON, TUE, WED, THU, FRI, SAT, SUN.
    /// </summary>
    public List<string> DaysOfWeek { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; }
}
