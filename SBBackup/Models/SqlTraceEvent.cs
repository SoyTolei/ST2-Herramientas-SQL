namespace SBBackup.Models;

/// <summary>Evento capturado por la traza SQL (Extended Events).</summary>
public sealed class SqlTraceEvent
{
    public DateTime Timestamp { get; init; }
    public string Category { get; init; } = "";
    public string EventName { get; init; } = "";
    public int? Severity { get; init; }
    public int? ErrorNumber { get; init; }
    public long? DurationMs { get; init; }
    public int? SessionId { get; init; }
    public string DatabaseName { get; init; } = "";
    public string LoginName { get; init; } = "";
    public string ClientApp { get; init; } = "";
    public string ClientHost { get; init; } = "";
    public string Message { get; init; } = "";
    public string SqlText { get; init; } = "";

    public string Key =>
        $"{Timestamp:O}|{EventName}|{SessionId}|{ErrorNumber}|{DurationMs}|{SqlText.GetHashCode()}|{Message.GetHashCode()}";
}
