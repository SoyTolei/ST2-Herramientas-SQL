namespace SBBackup.Models;

public sealed class ZipCreationException(
    string message,
    string zipPath,
    string outputDirectory,
    IReadOnlyList<string> backupPaths,
    Exception? innerException = null) : IOException(message, innerException)
{
    public string ZipPath { get; } = zipPath;
    public string OutputDirectory { get; } = outputDirectory;
    public IReadOnlyList<string> BackupPaths { get; } = backupPaths;
}

