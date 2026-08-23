using System.ComponentModel;
using System.Text;
using Microsoft.Data.SqlClient;

namespace SBBackup.Services;

/// <summary>
/// Mensajes para el usuario siempre en español, con un detalle técnico breve
/// (el mensaje original del sistema puede venir en inglés).
/// </summary>
internal static class UserMessageSpanish
{
    private const int MaxTechnicalLength = 420;

    /// <summary>Texto amigable + bloque "Detalle técnico".</summary>
    public static string FriendlyError(string mensajeAmigable, Exception? ex)
    {
        if (ex is null)
            return mensajeAmigable.TrimEnd();

        return $"{mensajeAmigable.TrimEnd()}\n\nDetalle técnico:\n{ShortTechnical(ex)}";
    }

    /// <summary>Una sola línea o párrafo corto para logs o subtítulos.</summary>
    public static string ShortTechnical(Exception? ex)
    {
        if (ex is null)
            return "Sin información adicional.";

        var root = ex.GetBaseException();
        var msg = NormalizeMessage(root.Message);

        return root switch
        {
            SqlException sql => $"SQL {sql.Number}: {Truncate(msg)}",
            Win32Exception w32 => $"Sistema {w32.NativeErrorCode}: {Truncate(msg)}",
            UnauthorizedAccessException => $"Acceso denegado: {Truncate(msg)}",
            FileNotFoundException => $"Archivo no encontrado: {Truncate(msg)}",
            DirectoryNotFoundException => $"Carpeta no encontrada: {Truncate(msg)}",
            IOException => $"E/S ({root.GetType().Name}): {Truncate(msg)}",
            _ => $"{root.GetType().Name}: {Truncate(msg)}"
        };
    }

    private static string NormalizeMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "(sin mensaje)";

        var s = message.Trim().Replace("\r\n", " ").Replace('\n', ' ');
        while (s.Contains("  ", StringComparison.Ordinal))
            s = s.Replace("  ", " ", StringComparison.Ordinal);

        return s;
    }

    private static string Truncate(string text)
    {
        if (text.Length <= MaxTechnicalLength)
            return text;
        return text[..(MaxTechnicalLength - 1)] + "…";
    }

    /// <summary>
    /// El servicio SQL no pudo escribir el .bak ni en el destino ni en su propia carpeta de backup.
    /// </summary>
    public static string SqlServiceCannotWrite(string databaseName, string? serverHost, bool serverIsRemote, Exception? ex)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"El servicio de SQL Server no pudo crear el backup de «{databaseName}».");
        sb.AppendLine();
        sb.AppendLine("El archivo .bak lo escribe la cuenta con la que corre el servicio SQL (no tu usuario de Windows).");
        sb.AppendLine("Esa cuenta no tiene permiso de escritura ni en la carpeta de destino ni en la carpeta de backup del servidor.");
        sb.AppendLine();
        sb.AppendLine("Cómo resolverlo:");
        sb.AppendLine("• Si usás una carpeta de red, dale permiso de escritura a la cuenta del servicio SQL");
        sb.AppendLine($"  (o a la cuenta de equipo del servidor, por ej. DOMINIO\\{(serverHost ?? "SERVIDOR").Split('.')[0].ToUpperInvariant()}$).");
        sb.AppendLine("• O ejecutá el backup desde el propio servidor donde está instalado SQL.");
        sb.AppendLine();
        sb.AppendLine("Detalle técnico:");
        sb.Append(ShortTechnical(ex));
        return sb.ToString();
    }

    /// <summary>
    /// SQL generó el .bak en el servidor pero el cliente no puede leerlo (servidor remoto, sin acceso al recurso administrativo).
    /// </summary>
    public static string SqlBackupUnreachable(string databaseName, string? serverHost, string serverSidePath)
    {
        var host = string.IsNullOrWhiteSpace(serverHost) ? "SERVIDOR" : serverHost;
        var sb = new StringBuilder();
        sb.AppendLine($"El backup de «{databaseName}» se generó en el servidor SQL, pero esta PC no pudo leer el archivo para comprimirlo.");
        sb.AppendLine();
        sb.AppendLine("Pasa cuando SQL Server está en otra máquina: el .bak queda en el disco del servidor y la app no llega a él.");
        sb.AppendLine();
        sb.AppendLine("Cómo resolverlo:");
        sb.AppendLine($"• Que tu usuario de Windows tenga acceso de administrador al servidor (recurso \\\\{host}\\C$), o");
        sb.AppendLine("• Usá una carpeta de red compartida con permiso de escritura para la cuenta del servicio SQL, o");
        sb.AppendLine("• Ejecutá la app desde el propio servidor donde está SQL.");
        sb.AppendLine();
        sb.AppendLine("Archivo en el servidor:");
        sb.Append(serverSidePath);
        return sb.ToString();
    }

    public static string ZipFailurePrompt(string zipPath, string outputDirectory, Exception failure)
    {
        var sb = new StringBuilder();
        sb.AppendLine("No se pudo crear el archivo ZIP en tu carpeta de destino.");
        sb.AppendLine("Puede deberse a permisos, falta de espacio en disco o a que otro programa tenga abierto el archivo.");
        sb.AppendLine();
        sb.AppendLine("Detalle técnico:");
        sb.AppendLine(ShortTechnical(failure));
        sb.AppendLine();
        sb.AppendLine("Ruta del ZIP intentada:");
        sb.AppendLine(zipPath);
        sb.AppendLine();
        sb.AppendLine("Carpeta de trabajo:"); sb.AppendLine(outputDirectory);
        sb.AppendLine();
        sb.Append("¿Deseás guardar igual los archivos .bak generados?");
        return sb.ToString();
    }
}
