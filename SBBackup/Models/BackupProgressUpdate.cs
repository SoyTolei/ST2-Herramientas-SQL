namespace SBBackup.Models;

public sealed class BackupProgressUpdate
{
    public int Percent { get; init; }
    public string Status { get; init; } = "";
    public int StepIndex { get; init; }
    public int StepCount { get; init; }
    public bool IsFinalizing { get; init; }
}
