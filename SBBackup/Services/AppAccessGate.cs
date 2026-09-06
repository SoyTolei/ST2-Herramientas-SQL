using System.Security.Cryptography;

namespace SBBackup.Services;

/// <summary>
/// Control de acceso a la UI. La clave no se guarda en texto plano: solo hash PBKDF2 embebido.
/// </summary>
internal static class AppAccessGate
{
    private const int Iterations = 100_000;
    private const int HashBytes = 32;

    private static readonly byte[] Salt =
    [
        0x53, 0x54, 0x32, 0x41, 0x63, 0x63, 0x65, 0x73, 0x6F, 0x31, 0x36
    ];

    private static readonly byte[] ExpectedHash =
    [
        0xD4, 0xE8, 0x56, 0x4B, 0xE0, 0x41, 0x73, 0xE9, 0xB7, 0x6B, 0x14, 0xB8,
        0xC0, 0xC9, 0x4B, 0x0B, 0xAB, 0x20, 0xB2, 0xAA, 0x46, 0xEA, 0xD9, 0xD4,
        0xCB, 0x91, 0xA9, 0x17, 0xA6, 0x59, 0x38, 0x4B
    ];

    internal static bool TryVerify(string? password)
    {
        if (string.IsNullOrEmpty(password))
            return false;

        password = password.Trim();
        if (password.Length == 0)
            return false;

        try
        {
            using var pbkdf2 = new Rfc2898DeriveBytes(
                password,
                Salt,
                Iterations,
                HashAlgorithmName.SHA256);
            var derived = pbkdf2.GetBytes(HashBytes);
            return CryptographicOperations.FixedTimeEquals(derived, ExpectedHash);
        }
        catch
        {
            return false;
        }
    }
}
