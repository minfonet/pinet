using System.Text;

namespace PiNet.Providers.Infrastructure;

public sealed record SseEvent(string? Event, string Data);

public static class SseParser
{
    public static async IAsyncEnumerable<SseEvent> ParseAsync(
        StreamReader reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        string? currentEvent = null;
        var data = new StringBuilder();

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0 || currentEvent is not null)
                {
                    yield return new SseEvent(currentEvent, data.ToString());
                    currentEvent = null;
                    data.Clear();
                }
                continue;
            }

            if (line.StartsWith(':'))
                continue;

            var colon = line.IndexOf(':');
            var field = colon >= 0 ? line[..colon] : line;
            var value = colon >= 0 ? line[(colon + 1)..].TrimStart(' ') : string.Empty;

            switch (field)
            {
                case "event":
                    currentEvent = value;
                    break;
                case "data":
                    if (data.Length > 0)
                        data.Append('\n');
                    data.Append(value);
                    break;
            }
        }

        if (data.Length > 0 || currentEvent is not null)
            yield return new SseEvent(currentEvent, data.ToString());
    }
}