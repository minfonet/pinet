using System.Net;
using System.Text;
using System.Text.Json;
using PiNet.Abstractions.Chat;

namespace PiNet.Sessions;

/// <summary>
/// Session export / import (spec 08 §8 / Stage 7f): JSONL round-trips that reuse
/// <see cref="SessionJson"/>, plus a standalone styled HTML view.
/// </summary>
public static class SessionExport
{
    // ---------- JSONL ----------

    /// <summary>Writes every entry of the session (full history, including branches) to a JSONL file.</summary>
    public static async Task ExportJsonlAsync(Session session, string path, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var lines = session.Entries.Select(SerializeLine).ToArray();
        await File.WriteAllLinesAsync(path, lines, Encoding.UTF8, ct);
    }

    /// <summary>Returns the JSONL representation of every session entry.</summary>
    public static string ExportJsonlString(Session session) =>
        string.Join('\n', session.Entries.Select(SerializeLine));

    /// <summary>Reads a JSONL file into a new in-memory session.</summary>
    public static async Task<Session> ImportJsonlAsync(string path, CancellationToken ct = default)
    {
        var entries = await SessionStore.ReadAllAsync(path, ct);
        var session = new Session { Id = SessionId.New() };
        session.Restore(entries);
        return session;
    }

    /// <summary>Reads JSONL content into a new in-memory session.</summary>
    public static async Task<Session> ImportJsonlStringAsync(string content, CancellationToken ct = default)
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var entries = await SessionStore.ReadAllFromStreamAsync(ms, ct);
        var session = new Session { Id = SessionId.New() };
        session.Restore(entries);
        return session;
    }

    // ---------- HTML ----------

    /// <summary>Exports the active branch as a standalone styled HTML file.</summary>
    public static async Task ExportHtmlAsync(Session session, string path, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, BuildHtml(session), Encoding.UTF8, ct);
    }

    /// <summary>Builds the standalone HTML document (no external assets).</summary>
    public static string BuildHtml(Session session)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\"><title>PiNet Session</title>");
        sb.AppendLine("<style>body{font-family:system-ui,-apple-system,sans-serif;margin:2rem;line-height:1.5;background:#111;color:#eee}" +
                      ".entry{margin-bottom:1rem;padding:1rem;border-radius:8px;background:#1e1e1e}" +
                      ".role{font-weight:bold;color:#4db5ff;margin-bottom:.5rem}" +
                      "pre{white-space:pre-wrap;word-wrap:break-word;margin:0 0 .5rem 0}" +
                      ".thinking{color:#9a9a9a;font-style:italic}</style>");
        sb.AppendLine("</head><body>");
        sb.AppendLine($"<h1>Session {WebUtility.HtmlEncode(session.Id)}</h1>");
        foreach (var entry in session.GetActiveBranch())
        {
            sb.AppendLine("<div class=\"entry\">");
            sb.AppendLine($"<div class=\"role\">{WebUtility.HtmlEncode(entry.Role.ToString())}</div>");
            foreach (var block in entry.Content)
                sb.AppendLine(RenderBlock(block));
            sb.AppendLine("</div>");
        }
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static string RenderBlock(ContentBlock block) => block switch
    {
        TextContent t => $"<pre>{WebUtility.HtmlEncode(t.Text)}</pre>",
        ThinkingContent th => $"<pre class=\"thinking\">{WebUtility.HtmlEncode(th.Thinking)}</pre>",
        ToolCallContent tc => $"<pre>[tool: {WebUtility.HtmlEncode(tc.Name)}] {WebUtility.HtmlEncode(tc.Arguments.GetRawText())}</pre>",
        ImageContent img => $"<pre>[image: {WebUtility.HtmlEncode(img.MimeType)}]</pre>",
        _ => string.Empty
    };

    private static string SerializeLine(SessionEntry entry) => SessionJson.WriteEntryText(entry);
}
