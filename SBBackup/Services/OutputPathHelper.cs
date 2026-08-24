namespace SBBackup.Services;



/// <summary>Validación de carpetas de salida y rutas protegidas de Windows.</summary>

internal static class OutputPathHelper

{

    private const string WorkspaceFolderName = "ST2 - SBBackup";

    /// <summary>Subcarpeta fija para backups del Programador de tareas.</summary>
    internal const string ScheduledBackupFolderName = "Backups Automaticos Bejerman ST2";

    /// <summary>Se borran las copias automáticas más viejas que esta cantidad de días.</summary>
    internal const int ScheduledBackupRetentionDays = 7;



    private static readonly string[] ProtectedPathPrefixes =

    [

        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),

        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),

        Environment.GetFolderPath(Environment.SpecialFolder.Windows),

        Environment.GetFolderPath(Environment.SpecialFolder.System)

    ];



    /// <summary>

    /// Carpeta por defecto: la primera entre Documentos, Escritorio o AppData local

    /// donde el usuario actual pueda escribir (sin ser administrador).

    /// </summary>

    public static string GetDefaultOutputDirectory()

    {

        // 1) Preferimos la ruta UNC compartida que configura Bejerman en el registro.
        //    Es la que ya sabemos que no tiene problemas para guardar, así el usuario
        //    no tiene que buscarla a mano. La mostramos aunque el chequeo instantáneo
        //    de escritura no pase: la validación real ocurre al ejecutar el backup.
        var bejermanUnc = BejermanRegistry.TryGetSharedUncPath();

        if (!string.IsNullOrWhiteSpace(bejermanUnc))
        {
            try
            {
                return Path.GetFullPath(bejermanUnc.Trim());
            }
            catch
            {
                // ruta inválida: seguimos con las carpetas locales
            }
        }



        var candidates = new[]

        {

            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), WorkspaceFolderName),

            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), WorkspaceFolderName),

            Path.Combine(

                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),

                "ST2-SBBackup")

        };



        foreach (var dir in candidates)

        {

            if (TryValidateWriteAccess(dir, out _))

                return Path.GetFullPath(dir.Trim());

        }



        var fallback = candidates[0];

        try

        {

            Directory.CreateDirectory(fallback);

        }

        catch

        {

            // Se validará al ejecutar el backup

        }



        return Path.GetFullPath(fallback);

    }

    /// <summary>
    /// Destino de backups programados: carpeta por defecto + «Backups Automaticos Bejerman ST2».
    /// </summary>
    public static string GetScheduledBackupDirectory()
    {
        var dir = Path.Combine(GetDefaultOutputDirectory(), ScheduledBackupFolderName);
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch
        {
            // se valida al ejecutar
        }

        try
        {
            return Path.GetFullPath(dir);
        }
        catch
        {
            return dir;
        }
    }

    /// <summary>
    /// Subcarpeta del día dentro de «Backups Automaticos Bejerman ST2», p. ej. «Lunes 10-08-2026».
    /// Ahí se guarda el ZIP del backup programado.
    /// </summary>
    public static string GetScheduledBackupDayDirectory(DateTime? day = null)
    {
        var d = day ?? DateTime.Now;
        var culture = new System.Globalization.CultureInfo("es-AR");
        var dayName = culture.TextInfo.ToTitleCase(d.ToString("dddd", culture));
        var folderName = $"{dayName} {d:dd-MM-yyyy}";
        foreach (var c in Path.GetInvalidFileNameChars())
            folderName = folderName.Replace(c, '-');

        var dir = Path.Combine(GetScheduledBackupDirectory(), folderName);
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch
        {
            // se valida al ejecutar
        }

        try
        {
            return Path.GetFullPath(dir);
        }
        catch
        {
            return dir;
        }
    }

    /// <summary>
    /// Borra subcarpetas de backup automático (y ZIP sueltos) con más de
    /// <see cref="ScheduledBackupRetentionDays"/> días. No toca la carpeta «logs».
    /// </summary>
    public static int PruneExpiredScheduledBackups(string scheduledRoot, Action<string>? log)
    {
        if (string.IsNullOrWhiteSpace(scheduledRoot) || !Directory.Exists(scheduledRoot))
            return 0;

        var cutoff = DateTime.Now.Date.AddDays(-ScheduledBackupRetentionDays);
        var removed = 0;

        foreach (var dir in Directory.GetDirectories(scheduledRoot))
        {
            var name = Path.GetFileName(dir);
            if (string.Equals(name, "logs", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!IsScheduledCopyOlderThanCutoff(dir, name, cutoff))
                continue;

            try
            {
                Directory.Delete(dir, recursive: true);
                removed++;
                log?.Invoke($"Se eliminó la copia automática más vieja: {name}");
            }
            catch (Exception ex)
            {
                log?.Invoke("No se pudo borrar «" + name + "»: " + UserMessageSpanish.ShortTechnical(ex));
            }
        }

        foreach (var zip in Directory.GetFiles(scheduledRoot, "*.zip", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var info = new FileInfo(zip);
                if (info.LastWriteTime.Date >= cutoff)
                    continue;

                File.Delete(zip);
                removed++;
                log?.Invoke("Se eliminó el ZIP más viejo: " + info.Name);
            }
            catch (Exception ex)
            {
                log?.Invoke("No se pudo borrar «" + Path.GetFileName(zip) + "»: " + UserMessageSpanish.ShortTechnical(ex));
            }
        }

        return removed;
    }

    private static bool IsScheduledCopyOlderThanCutoff(string dir, string folderName, DateTime cutoffDate)
    {
        var fromName = TryParseDayFolderDate(folderName);
        var stamp = fromName ?? Directory.GetLastWriteTime(dir).Date;
        return stamp < cutoffDate;
    }

    private static DateTime? TryParseDayFolderDate(string folderName)
    {
        // «Lunes 10-08-2026»
        var s = folderName.Trim();
        var space = s.LastIndexOf(' ');
        var datePart = space >= 0 ? s[(space + 1)..] : s;
        if (DateTime.TryParseExact(
                datePart,
                "dd-MM-yyyy",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var d))
            return d.Date;

        return null;
    }

    /// <summary>Ruta absoluta de la carpeta de trabajo (.bak y ZIP).</summary>

    public static string ResolveWorkspaceDirectory(string path)

    {

        if (!TryValidateWriteAccess(path, out var error))

            throw new ArgumentException(error, nameof(path));



        return Path.GetFullPath(path.Trim());

    }



    public static bool IsProtectedSystemPath(string path)

    {

        if (string.IsNullOrWhiteSpace(path))

            return false;



        try

        {

            var full = Path.GetFullPath(path.Trim());

            foreach (var prefix in ProtectedPathPrefixes)

            {

                if (string.IsNullOrEmpty(prefix))

                    continue;

                var p = Path.GetFullPath(prefix);

                if (full.Equals(p, StringComparison.OrdinalIgnoreCase)

                    || full.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))

                    return true;

            }

        }

        catch

        {

            return false;

        }



        return false;

    }



    /// <summary>Comprueba que el usuario actual puede crear y borrar archivos en la carpeta.</summary>

    public static bool TryValidateWriteAccess(string path, out string errorMessage)

    {

        errorMessage = "";



        if (string.IsNullOrWhiteSpace(path))

        {

            errorMessage = "Indicá una carpeta de destino.";

            return false;

        }



        string full;

        try

        {

            full = Path.GetFullPath(path.Trim());

        }

        catch (Exception ex)

        {

            errorMessage = UserMessageSpanish.FriendlyError(
                "La carpeta indicada no es una ruta válida en Windows.",
                ex);

            return false;

        }



        if (IsProtectedSystemPath(full))

        {

            errorMessage =

                "No se puede guardar en carpetas del sistema (por ejemplo Program Files o Windows).\n\n" +

                "Usá Documentos, Escritorio o una carpeta propia donde tengas permiso de escritura.";

            return false;

        }



        try

        {

            Directory.CreateDirectory(full);

        }

        catch (Exception ex)

        {

            errorMessage = UserMessageSpanish.FriendlyError(
                "No se pudo crear la carpeta de destino:\n" + full,
                ex);

            return false;

        }



        var probe = Path.Combine(full, ".sbbackup_write_test_" + Guid.NewGuid().ToString("N"));

        try

        {

            File.WriteAllText(probe, "ok");

            File.Delete(probe);

            return true;

        }

        catch (UnauthorizedAccessException ex)

        {

            errorMessage = UserMessageSpanish.FriendlyError(
                "No tenés permiso de escritura en:\n" + full + "\n\n" +
                "Elegí Documentos, Escritorio o una carpeta tuya. No uses rutas de instalación de Bejerman ni Program Files.",
                ex);

            return false;

        }

        catch (Exception ex)

        {

            errorMessage = UserMessageSpanish.FriendlyError(
                "No se puede escribir en la carpeta de destino:\n" + full,
                ex);

            return false;

        }

    }



    public static string FormatAccessDeniedMessage(string targetPath, UnauthorizedAccessException? ex = null)
    {
        var baseMsg =
            "Acceso denegado al guardar en:\n" + targetPath +
            "\n\nElegí Documentos, Escritorio o una carpeta propia. Las carpetas de Program Files requieren administrador.";
        return ex is null ? baseMsg : UserMessageSpanish.FriendlyError(baseMsg, ex);
    }



    public static string FormatIOException(IOException ex, string targetPath) =>
        UserMessageSpanish.FriendlyError(
            "Ocurrió un error al leer o escribir archivos en la carpeta de trabajo.\n\n" +
            "Revisá espacio en disco, que el ZIP o los .bak no estén abiertos en otro programa y los permisos de la carpeta.\n\n" +
            "Carpeta:\n" + targetPath,
            ex);

}


