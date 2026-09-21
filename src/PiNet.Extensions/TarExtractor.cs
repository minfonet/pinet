using System.IO.Compression;
using System.Text;

namespace PiNet.Extensions;

public sealed class TarExtractionException : Exception
{
    public TarExtractionException(string message) : base(message) { }
}

/// <summary>
/// Minimal gzip+tar reader (BCL only). Handles regular files ('0') and
/// directories ('5'), GNU long names ('L'), and skips pax ('x'/'g') entries,
/// symbolic links and other types. Rejects absolute paths and '..' traversal.
/// </summary>
public static class TarExtractor
{
    public static void Extract(string gzipFile, string destDir, string? stripPrefix = null)
    {
        Directory.CreateDirectory(destDir);
        using var gz = new GZipStream(File.OpenRead(gzipFile), CompressionMode.Decompress);
        ExtractFromStream(gz, destDir, stripPrefix);
    }

    public static void ExtractFromBytes(byte[] gzipBytes, string destDir, string? stripPrefix = null)
    {
        Directory.CreateDirectory(destDir);
        using var gz = new GZipStream(new MemoryStream(gzipBytes), CompressionMode.Decompress);
        ExtractFromStream(gz, destDir, stripPrefix);
    }

    private static void ExtractFromStream(Stream stream, string destDir, string? stripPrefix)
    {
        var header = new byte[512];
        string? pendingLongName = null;

        while (true)
        {
            var read = ReadFull(stream, header);
            if (read == 0)
                return; // EOF, truncated archive ends here
            if (read < 512)
                throw new TarExtractionException("truncated tar header");

            if (IsZeroBlock(header))
            {
                var next = ReadFull(stream, header);
                if (next == 0 || IsZeroBlock(header))
                    return;
                read = next;
                if (read < 512)
                    throw new TarExtractionException("truncated tar header");
            }

            var type = header[156];
            var size = ParseOctal(header.AsSpan(124, 12));

            if (type == (byte)'L')
            {
                pendingLongName = ReadDataBlock(stream, size);
                continue;
            }

            var rawName = ReadName(header);
            var effectiveName = pendingLongName ?? rawName;
            pendingLongName = null;

            if (size == 0 && effectiveName.Length == 0)
                continue;

            if (!IsSupportedType(type))
            {
                SkipData(stream, size);
                continue;
            }

            var resolved = ResolvePath(effectiveName, destDir, stripPrefix);
            if (resolved is null)
            {
                SkipData(stream, size);
                continue;
            }

            if (type == (byte)'5')
            {
                SkipData(stream, size);
                Directory.CreateDirectory(resolved);
                continue;
            }

            var data = ReadData(stream, size);
            var dir = Path.GetDirectoryName(resolved);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllBytes(resolved, data);
        }
    }

    private static string? ResolvePath(string name, string destDir, string? stripPrefix)
    {
        var normalized = name.Replace('\\', '/');
        if (stripPrefix is not null && normalized.StartsWith(stripPrefix + "/", StringComparison.Ordinal))
            normalized = normalized[(stripPrefix.Length + 1)..];
        if (normalized.StartsWith('/'))
            return null; // absolute path in archive (also matches windows 'C:/')
        normalized = normalized.TrimEnd('/');

        if (normalized.Length == 0)
            return destDir;

        var segments = normalized.Split('/');
        if (segments.Any(s => s.Length == 0) || segments.Any(s => s == ".."))
            return null;
        if (segments.Length > 0 && segments[0].Length == 2 && segments[0][1] == ':')
            return null; // windows drive path, e.g. 'C:/evil.txt'

        var fullDest = Path.GetFullPath(destDir);
        var combined = Path.GetFullPath(Path.Combine(fullDest, normalized));
        if (combined.StartsWith(fullDest + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            combined.Equals(fullDest, StringComparison.Ordinal))
        {
            return combined;
        }

        return null;
    }

    private static string ReadName(byte[] header)
    {
        var rawName = ReadNullTerminated(header, 0, 100);
        var prefix = ReadNullTerminated(header, 345, 155);
        return prefix.Length > 0 ? $"{prefix}/{rawName}" : rawName;
    }

    private static string ReadNullTerminated(byte[] buffer, int offset, int length)
    {
        var end = offset;
        var limit = Math.Min(offset + length, buffer.Length);
        while (end < limit && buffer[end] != 0)
            end++;
        return Encoding.UTF8.GetString(buffer, offset, end - offset);
    }

    private static bool IsSupportedType(byte type) =>
        type == (byte)'0' || type == 0 || type == (byte)'5';

    private static void SkipData(Stream stream, long size)
    {
        var padded = (size + 511) / 512 * 512;
        Skip(stream, padded);
    }

    private static void Skip(Stream stream, long count)
    {
        var buffer = new byte[4096];
        var remaining = count;
        while (remaining > 0)
        {
            var chunk = (int)Math.Min(remaining, buffer.Length);
            var read = stream.Read(buffer, 0, chunk);
            if (read == 0)
                throw new TarExtractionException("unexpected end of tar data");
            remaining -= read;
        }
    }

    private static string ReadDataBlock(Stream stream, long size)
    {
        var data = ReadData(stream, size);
        var length = Array.IndexOf(data, (byte)0);
        length = length < 0 ? data.Length : length;
        return Encoding.UTF8.GetString(data, 0, length);
    }

    private static byte[] ReadData(Stream stream, long size)
    {
        var data = new byte[size];
        var offset = 0;
        while (offset < size)
        {
            var read = stream.Read(data, offset, (int)(size - offset));
            if (read == 0)
                throw new TarExtractionException("unexpected end of tar data");
            offset += read;
        }
        var padded = (size + 511) / 512 * 512;
        Skip(stream, padded - size);
        return data;
    }

    private static int ReadFull(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
                break;
            offset += read;
        }
        return offset;
    }

    private static long ParseOctal(ReadOnlySpan<byte> bytes)
    {
        var text = Encoding.ASCII.GetString(TrimZero(bytes)).Trim();
        if (text.EndsWith(" ", StringComparison.Ordinal) || text.EndsWith("\0", StringComparison.Ordinal))
            text = text.Trim();
        if (text.Length == 0)
            return 0;
        if (text.StartsWith("0", StringComparison.Ordinal) && text.Length > 1)
            text = text.TrimStart('0');
        if (text.Length == 0 || !text.All(c => c is >= '0' and <= '7'))
            return 0;
        return Convert.ToInt64(text, 8);
    }

    private static byte[] TrimZero(ReadOnlySpan<byte> bytes)
    {
        var length = bytes.Length;
        while (length > 0 && (bytes[length - 1] == 0 || bytes[length - 1] == ' '))
            length--;
        return bytes[..length].ToArray();
    }

    private static bool IsZeroBlock(byte[] block) =>
        block.All(b => b == 0);
}