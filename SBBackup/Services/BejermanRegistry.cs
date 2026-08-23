using System.Globalization;
using Microsoft.Win32;

namespace SBBackup.Services;

internal static class BejermanRegistry
{
    /// <summary>Nombres posibles del valor en el Registro (Regedit muestra "Server ODBC").</summary>
    private static readonly string[] ValueNames =
    [
        "Server ODBC",
        "SERVERODBC",
        "ServerODBC"
    ];

    /// <summary>Nombres posibles del valor con la ruta UNC compartida al servidor SQL.</summary>
    private static readonly string[] SharedUncValueNames =
    [
        "Ruta UNC al Servidor - SQL",
        "Ruta UNC al Servidor- SQL",
        "Ruta UNC al Servidor -SQL",
        "Ruta UNC al Servidor-SQL",
        "RutaUNCalServidor-SQL"
    ];

    private static readonly string[] BejermanKeyPaths =
    [
        @"SOFTWARE\WOW6432Node\Sistemas Bejerman",
        @"SOFTWARE\Sistemas Bejerman"
    ];

    internal static string? TryGetServerOdbc() => TryReadAnyValue(ValueNames);

    /// <summary>
    /// Ruta UNC compartida (visible desde el server y las terminales) configurada por Bejerman.
    /// Ideal como carpeta de staging del .bak para evitar depender del recurso administrativo C$.
    /// </summary>
    internal static string? TryGetSharedUncPath()
    {
        // 1) Nombres conocidos del valor.
        var raw = TryReadAnyValue(SharedUncValueNames);

        // 2) Si no, buscamos cualquier valor cuyo nombre mencione "UNC"
        //    (tolerante a espacios o tipo de guion).
        raw ??= TryFindUncValue();

        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var s = raw.Trim().Trim('"');

        // Aceptamos UNC (\\servidor\recurso) o ruta con unidad (C:\...): en el propio
        // servidor el valor suele ser local, y en las terminales es UNC.
        return LooksLikePath(s) ? s : null;
    }

    private static bool LooksLikePath(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return false;

        if (s.StartsWith(@"\\", StringComparison.Ordinal))
            return true;

        try
        {
            return Path.IsPathRooted(s);
        }
        catch
        {
            return false;
        }
    }

    private static string? TryFindUncValue()
    {
        foreach (var relativePath in BejermanKeyPaths)
        {
            var s = TryFindUncValueInKey(Registry.LocalMachine, relativePath);
            if (!string.IsNullOrEmpty(s))
                return s;
        }

        try
        {
            using var base32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            var s = TryFindUncValueInKey(base32, @"SOFTWARE\Sistemas Bejerman");
            if (!string.IsNullOrEmpty(s))
                return s;
        }
        catch
        {
            // ignorar
        }

        return null;
    }

    private static string? TryFindUncValueInKey(RegistryKey root, string relativePath)
    {
        try
        {
            using var k = root.OpenSubKey(relativePath, writable: false);
            if (k is null)
                return null;

            foreach (var name in k.GetValueNames())
            {
                if (name.IndexOf("UNC", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var s = ValueToString(k.GetValue(name)!);
                if (!string.IsNullOrWhiteSpace(s) && LooksLikePath(s.Trim().Trim('"')))
                    return s;
            }
        }
        catch
        {
            // ignorar
        }

        return null;
    }

    private static string? TryReadAnyValue(string[] valueNames)
    {
        foreach (var relativePath in BejermanKeyPaths)
        {
            var s = TryReadFromKey(Registry.LocalMachine, relativePath, valueNames);
            if (!string.IsNullOrEmpty(s))
                return s;
        }

        try
        {
            using var base32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            var s = TryReadFromKey(base32, @"SOFTWARE\Sistemas Bejerman", valueNames);
            if (!string.IsNullOrEmpty(s))
                return s;
        }
        catch
        {
            // ignorar
        }

        return null;
    }

    private static string? TryReadFromKey(RegistryKey root, string relativePath, string[] valueNames)
    {
        try
        {
            using var k = root.OpenSubKey(relativePath, writable: false);
            if (k is null)
                return null;

            foreach (var name in valueNames)
            {
                var o = k.GetValue(name);
                if (o is null)
                    continue;

                var s = ValueToString(o);
                if (!string.IsNullOrEmpty(s))
                    return s;
            }
        }
        catch
        {
            // ignorar
        }

        return null;
    }

    private static string? ValueToString(object o) =>
        o switch
        {
            string s => s.Trim(),
            byte[] bytes when bytes.Length > 0 => System.Text.Encoding.Unicode.GetString(bytes).Trim().TrimEnd('\0'),
            _ => Convert.ToString(o, CultureInfo.InvariantCulture)?.Trim()
        };
}
