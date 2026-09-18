using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace PiNet.Sessions;

public static class SessionId
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    public static string New()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        var value = BinaryPrimitives.ReadUInt64BigEndian(bytes);
        var sb = new StringBuilder(13);
        for (var i = 0; i < 13; i++)
        {
            sb.Append(Alphabet[(int)(value & 0x1F)]);
            value >>= 5;
        }
        return sb.ToString();
    }

    public static bool IsValidPrefix(string token) =>
        !string.IsNullOrWhiteSpace(token) && token.All(c => Alphabet.Contains(c));

    public static string WorkingDirKey(string cwd)
    {
        var normalized = Path.GetFullPath(cwd).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }
}

public sealed class SessionResolutionException(string message) : Exception(message);