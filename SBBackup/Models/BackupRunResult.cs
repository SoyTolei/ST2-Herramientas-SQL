namespace SBBackup.Models;

public sealed class BackupRunResult
{
    public string OutputDirectory { get; init; } = "";
    public string? ZipFilePath { get; init; }
    public int DatabasesBackedUp { get; init; }
}
