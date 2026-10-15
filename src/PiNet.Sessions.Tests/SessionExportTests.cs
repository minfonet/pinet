using System.Text.Json;
using PiNet.Abstractions.Chat;

namespace PiNet.Sessions.Tests;

/// <summary>7f — JSONL round-trip and standalone HTML export (spec 08 §8).</summary>
public class SessionExportTests : IDisposable
{
    private readonly string _root;

    public SessionExportTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pinet-export-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, true);

    private static Session NewSession()
    {
        var session = new Session { Id = SessionId.New() };
        session.AppendMessage(new ChatMessage(ChatRole.User,
            [new TextContent("what is in @cat.png?"), new ImageContent("aGVsbG8=", "image/png")], Timestamp: 1000));
        session.AppendMessage(new ChatMessage(ChatRole.Assistant,
        [
            new ThinkingContent("let me look at the image"),
            new TextContent("A cat."),
            new ToolCallContent("call-1", "read", JsonDocument.Parse("""{"path":"cat.png"}""").RootElement)
        ], Timestamp: 1001));
        session.AppendMessage(ChatMessage.Tool("call-1", "read", "binary data", isError: false));
        return session;
    }

    [Fact]
    public async Task Jsonl_RoundTrips_AllEntries_AndBlockKinds()
    {
        var session = NewSession();
        var path = Path.Combine(_root, "export.jsonl");

        await SessionExport.ExportJsonlAsync(session, path);
        Assert.True(File.Exists(path));

        var imported = await SessionExport.ImportJsonlAsync(path);

        Assert.Equal(session.Entries.Count, imported.Entries.Count);
        Assert.Equal(session.Entries.Select(e => SessionJson.WriteEntryText(e)),
                     imported.Entries.Select(e => SessionJson.WriteEntryText(e)));

        // Thinking / image / tool blocks survive the round-trip.
        var assistant = imported.Entries[1];
        Assert.Contains(assistant.Content, b => b is ThinkingContent { Thinking: "let me look at the image" });
        Assert.Contains(assistant.Content, b => b is TextContent { Text: "A cat." });
        Assert.Contains(assistant.Content, b => b is ToolCallContent { Id: "call-1" });

        var user = imported.Entries[0];
        Assert.Contains(user.Content, b => b is ImageContent { MimeType: "image/png", Data: "aGVsbG8=" });
    }

    [Fact]
    public void Jsonl_String_RoundTrips()
    {
        var session = NewSession();

        var jsonl = SessionExport.ExportJsonlString(session);
        var lines = jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(session.Entries.Count, lines.Length);
        foreach (var line in lines)
            using (var doc = JsonDocument.Parse(line))
                Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    public async Task Import_ImportedSession_IsIndependent_WithFreshId()
    {
        var session = NewSession();

        var imported = await SessionExport.ImportJsonlStringAsync(SessionExport.ExportJsonlString(session));

        Assert.NotEqual(session.Id, imported.Id);
        Assert.Equal(session.Head!.Id, imported.Head!.Id); // entries preserved verbatim
        Assert.Equal(0, imported.PersistedEntryCount); // in-memory until the CLI appends/persists
    }

    [Fact]
    public async Task Import_MalformedLine_Throws()
    {
        var path = Path.Combine(_root, "broken.jsonl");
        await File.WriteAllTextAsync(path, "{not json at all\n");

        await Assert.ThrowsAnyAsync<Exception>(() => SessionExport.ImportJsonlAsync(path));
    }

    [Fact]
    public async Task Html_Export_WritesStandaloneFile_WithActiveBranch()
    {
        var session = NewSession();
        var path = Path.Combine(_root, "export.html");

        await SessionExport.ExportHtmlAsync(session, path);

        var html = await File.ReadAllTextAsync(path);
        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.EndsWith("</html>" + Environment.NewLine, html);
        Assert.Contains("<h1>Session ", html);
        Assert.Contains("cat.png", html);          // user text rendered
        Assert.Contains("class=\"thinking\"", html); // thinking block styled
        Assert.Contains("A cat.", html);
        Assert.Contains("[tool: read]", html);
        Assert.Contains("[image: image/png]", html);
    }

    [Fact]
    public void Html_EscapesMarkup()
    {
        var session = new Session { Id = SessionId.New() };
        session.AppendMessage(new ChatMessage(ChatRole.User,
            [new TextContent("<script>alert('x')</script>")], Timestamp: 0));

        var html = SessionExport.BuildHtml(session);

        Assert.DoesNotContain("<script>alert", html);
        Assert.Contains("&lt;script&gt;", html);
    }
}
