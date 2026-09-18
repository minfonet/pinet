using System.Text;
using System.Text.Json;

namespace PiNet.Sessions;

public static class SessionStore
{
    public static async Task<List<SessionEntry>> ReadAllAsync(string path, CancellationToken ct = default)
    {
        var entries = new List<SessionEntry>();
        if (!File.Exists(path))
            return entries;

        await foreach (var line in ReadLinesAsync(path, ct))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            entries.Add(SessionJson.ReadEntryLine(line));
        }
        return entries;
    }

    public static async Task WriteAllAsync(string path, IEnumerable<SessionEntry> entries, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lines = entries.Select(e => SerializeLine(e)).ToArray();
        await File.WriteAllLinesAsync(path, lines, Encoding.UTF8, ct);
    }

    public static async Task AppendAsync(string path, IEnumerable<SessionEntry> entries, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var builder = new StringBuilder();
        foreach (var entry in entries)
        {
            builder.Append(SerializeLine(entry));
            builder.Append('\n');
        }
        await File.AppendAllTextAsync(path, builder.ToString(), Encoding.UTF8, ct);
    }

    private static string SerializeLine(SessionEntry entry)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, SessionJson.WriterOptions))
            SessionJson.WriteEntry(writer, entry);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static async IAsyncEnumerable<string> ReadLinesAsync(string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(path, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
            yield return line;
    }
}