using System.Text;

namespace PiNet.Extensions.Tests;

/// <summary>
/// Minimal tar writer for tests: files ('0'), dirs ('5'), pax entries ('x'),
/// GNU long names ('L') and traversal attempts.
/// </summary>
public sealed class TarBuilder
{
    private readonly List<(byte[] Header, byte[] Data)> _blocks = [];

    public TarBuilder AddFile(string name, byte[] data)
    {
        _blocks.Add(MakeEntry(name, (byte)'0', data, "0000644\0"));
        return this;
    }

    public TarBuilder AddFile(string name, string content) =>
        AddFile(name, Encoding.UTF8.GetBytes(content));

    public TarBuilder AddDir(string name)
    {
        _blocks.Add(MakeEntry(name, (byte)'5', [], "0000775\0"));
        return this;
    }

    public TarBuilder AddPax(string name, string body)
    {
        var data = Encoding.UTF8.GetBytes(body);
        _blocks.Add(MakeEntry(name, (byte)'x', data, "0000644\0"));
        return this;
    }

    public byte[] Build()
    {
        var stream = new MemoryStream();
        foreach (var (header, data) in _blocks)
        {
            stream.Write(header, 0, header.Length);
            stream.Write(data, 0, data.Length);
            var pad = (data.Length + 511) / 512 * 512 - data.Length;
            stream.Write(new byte[pad], 0, pad);
        }
        stream.Write(new byte[1024], 0, 1024);
        return stream.ToArray();
    }

    public static byte[] Gzip(byte[] tar)
    {
        using var output = new MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Fastest))
            gz.Write(tar, 0, tar.Length);
        return output.ToArray();
    }

    private static (byte[] Header, byte[] Data) MakeEntry(string name, byte type, byte[] data, string mode)
    {
        var header = new byte[512];
        WriteField(header, 0, 100, Encoding.UTF8.GetBytes(name));
        WriteField(header, 100, 8, Encoding.ASCII.GetBytes(mode));
        WriteField(header, 108, 8, Encoding.ASCII.GetBytes("0000000\0"));
        WriteField(header, 116, 8, Encoding.ASCII.GetBytes("0000000\0"));
        WriteField(header, 124, 12, Encoding.ASCII.GetBytes(Octal(data.Length)));
        WriteField(header, 136, 12, Encoding.ASCII.GetBytes("00000000000\0"));
        WriteField(header, 148, 8, new byte[8]); // checksum placeholder
        header[156] = type;
        WriteField(header, 257, 8, Encoding.ASCII.GetBytes("ustar\0"));
        WriteField(header, 265, 2, Encoding.ASCII.GetBytes("00"));

        var checksum = (int)header.Sum(b => (long)b);
        var checksumText = Encoding.ASCII.GetBytes($"{checksum:X6}\0 ");
        WriteField(header, 148, 8, checksumText);

        return (header, data);
    }

    private static string Octal(long value)
    {
        var oct = Convert.ToString(value, 8);
        return oct.PadLeft(11, '0') + "\0";
    }

    private static void WriteField(byte[] header, int offset, int length, byte[] value)
    {
        Array.Clear(header, offset, length);
        for (var i = 0; i < length && i < value.Length; i++)
            header[offset + i] = value[i];
    }
}