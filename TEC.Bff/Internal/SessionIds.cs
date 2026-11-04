using System.Buffers.Text;
using System.Security.Cryptography;

namespace TEC.Bff.Internal;

/// <summary>Identificador de sessão: 256 bits do gerador criptográfico, sempre gerado no servidor (nunca aceito do cliente).</summary>
internal static class SessionIds
{
    /// <summary>Tamanho em caracteres (Base64Url de 32 bytes, sem preenchimento).</summary>
    public const int Length = 43;

    public static string New()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url.EncodeToString(bytes);
    }

    public static bool IsWellFormed(string? value)
    {
        if (value is null || value.Length != Length)
            return false;

        foreach (char c in value)
        {
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_'))
                return false;
        }

        return true;
    }
}
