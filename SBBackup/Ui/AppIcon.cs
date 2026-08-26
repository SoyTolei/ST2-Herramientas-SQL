using System.Reflection;

namespace SBBackup.Ui;

/// <summary>
/// Icono de la app para ventanas y barra de tareas.
/// Carga el .ico multi-tamaño (embebido / Assets / icono del .exe) una sola vez.
/// </summary>
internal static class AppIcon
{
    private static Icon? _cached;
    private static bool _loaded;

    internal static Icon? Get()
    {
        if (_loaded)
            return _cached;

        _loaded = true;
        try
        {
            _cached = TryLoadFromEmbedded()
                      ?? TryLoadFromAssetsFile()
                      ?? TryLoadFromExecutable();
        }
        catch
        {
            _cached = null;
        }

        return _cached;
    }

    internal static void Apply(Form form)
    {
        var icon = Get();
        if (icon is not null)
            form.Icon = (Icon)icon.Clone();
    }

    private static Icon? TryLoadFromEmbedded()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("ST2.app.ico");
        if (stream is null || stream.Length == 0)
            return null;
        return new Icon(stream);
    }

    private static Icon? TryLoadFromAssetsFile()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (!File.Exists(path))
            return null;
        return new Icon(path);
    }

    private static Icon? TryLoadFromExecutable()
    {
        var fromExe = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        return fromExe is null ? null : (Icon)fromExe.Clone();
    }
}
