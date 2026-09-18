using System.Text;
using System.Text.Json;
using PiNet.Abstractions.Chat;

namespace PiNet.Sessions.Tests;

public class SessionTests : IDisposable
{
    private readonly string _root;
    private readonly string _cwd;

    public SessionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pinet-sessions-tests", Guid.NewGuid().ToString("N"));
        _cwd = Path.Combine(_root, "work");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_cwd);
    }

    public void Dispose() => Directory.Delete(_root, true);

    private SessionManager NewManager() => new(_root);

    private static ChatMessage Txt(ChatRole role, string text) =>
        new(role, [new TextContent(text)], Timestamp: 1000 + System.Random.Shared.Next(9999));

    [Fact]
    public void Create_PersistsSeed_UnderWorkingDirHash_WithParentChain()
    {
        var manager = NewManager();
        var seed = new[] { Txt(ChatRole.User, "u1"), Txt(ChatRole.Assistant, "a1"), Txt(ChatRole.User, "u2") };

        var session = manager.CreateSession(_cwd, seed);

        var expectedPath = manager.SessionFilePath(_cwd, session.Id);
        Assert.True(File.Exists(expectedPath));
        Assert.StartsWith(Path.Combine(_root, SessionId.WorkingDirKey(_cwd)), expectedPath);
        Assert.Equal(3, session.Entries.Count);
        Assert.Equal(3, session.PersistedEntryCount);
        Assert.Equal("u1", session.Entries[0].Content[0] is TextContent t0 ? t0.Text : "");
        Assert.Equal("u2", session.Head?.Content[0] is TextContent t2 ? t2.Text : "");
        Assert.Null(session.Entries[0].ParentId);
        Assert.Equal(session.Entries[0].Id, session.Entries[1].ParentId);
        Assert.Equal(session.Entries[1].Id, session.Entries[2].ParentId);
    }

    [Fact]
    public async Task AppendAsync_PersistsOnlyNewEntries()
    {
        var manager = NewManager();
        var session = manager.CreateSession(_cwd, [Txt(ChatRole.User, "u1")]);

        session.AppendMessage(Txt(ChatRole.Assistant, "a1"));
        session.AppendMessage(Txt(ChatRole.User, "u2"));
        await manager.AppendAsync(session, _cwd);

        var path = manager.SessionFilePath(_cwd, session.Id);
        Assert.Equal(3, CountLines(path));
        Assert.Equal(3, session.PersistedEntryCount);

        session.AppendMessage(Txt(ChatRole.Assistant, "a2"));
        await manager.AppendAsync(session, _cwd);
        Assert.Equal(4, CountLines(path));
        Assert.Equal(4, session.PersistedEntryCount);
    }

    [Fact]
    public async Task Load_ResolvesByPartialId_AndByPath()
    {
        var manager = NewManager();
        var dir = Path.Combine(_root, SessionId.WorkingDirKey(_cwd));
        Directory.CreateDirectory(dir);
        var entry = SessionEntry.FromMessage(Txt(ChatRole.Assistant, "hello"));
        await SessionStore.WriteAllAsync(Path.Combine(dir, "abcde00001.jsonl"), [entry]);
        await SessionStore.WriteAllAsync(Path.Combine(dir, "abcde00002.jsonl"), [entry]);

        var exact = await manager.LoadSessionAsync(_cwd, "abcde00002");
        Assert.Equal("abcde00002", exact.Id);

        var byPath = await manager.LoadSessionAsync(_cwd, Path.Combine(dir, "abcde00001.jsonl"));
        Assert.Equal("abcde00001", byPath.Id);

        await Assert.ThrowsAsync<SessionResolutionException>(
            () => manager.LoadSessionAsync(_cwd, "zzzz"));
        await Assert.ThrowsAsync<SessionResolutionException>(
            () => manager.LoadSessionAsync(_cwd, "abcde0000"));
    }

    [Fact]
    public async Task LoadLatest_ReturnsNewestWrite()
    {
        var manager = NewManager();
        var first = manager.CreateSession(_cwd, [Txt(ChatRole.User, "one")]);
        File.SetLastWriteTimeUtc(manager.SessionFilePath(_cwd, first.Id), DateTime.UtcNow.AddHours(-2));

        var second = manager.CreateSession(_cwd, [Txt(ChatRole.User, "two")]);
        var latest = await manager.LoadLatestAsync(_cwd);
        Assert.Equal(second.Id, latest!.Id);

        first.AppendMessage(Txt(ChatRole.Assistant, "touch"));
        await manager.AppendAsync(first, _cwd);
        File.SetLastWriteTimeUtc(manager.SessionFilePath(_cwd, first.Id), DateTime.UtcNow.AddHours(2));

        latest = await manager.LoadLatestAsync(_cwd);
        Assert.Equal(first.Id, latest!.Id);
    }

    [Fact]
    public async Task RichEntry_RoundTrips_Exactly()
    {
        var args = JsonDocument.Parse("""{"path":"a/b.txt","retries":3}""").RootElement;
        var original = new SessionEntry(
            "ent-1",
            ChatRole.Assistant,
            [
                new TextContent("plain"),
                new ThinkingContent("hmm"),
                new ImageContent("base64===", "image/png"),
                new ToolCallContent("call_9", "edit_file", args)
            ],
            ParentId: "parent-0",
            ToolCallId: "call_9",
            IsError: true,
            Timestamp: 1715000000000,
            Metadata: new Dictionary<string, string> { ["k"] = "v" });

        var path = Path.Combine(_root, "rich.jsonl");
        await SessionStore.WriteAllAsync(path, [original]);
        var loaded = (await SessionStore.ReadAllAsync(path)).Single();

        Assert.Equal("ent-1", loaded.Id);
        Assert.Equal(ChatRole.Assistant, loaded.Role);
        Assert.Equal("parent-0", loaded.ParentId);
        Assert.Equal("call_9", loaded.ToolCallId);
        Assert.True(loaded.IsError);
        Assert.Equal(1715000000000, loaded.Timestamp);
        Assert.Equal("v", loaded.Metadata!["k"]);

        var blocks = loaded.Content;
        Assert.Equal(new TextContent("plain") is { }, blocks[0] is TextContent { Text: "plain" });
        Assert.IsType<ThinkingContent>(blocks[1]);
        var img = Assert.IsType<ImageContent>(blocks[2]);
        Assert.Equal("image/png", img.MimeType);
        var tc = Assert.IsType<ToolCallContent>(blocks[3]);
        Assert.Equal("call_9", tc.Id);
        Assert.Equal("a/b.txt", tc.Arguments.GetProperty("path").GetString());
        Assert.Equal(3, tc.Arguments.GetProperty("retries").GetInt32());
    }

    [Fact]
    public void GetActiveBranch_RootToHead()
    {
        var manager = NewManager();
        var session = manager.CreateSession(_cwd,
            [Txt(ChatRole.User, "u1"), Txt(ChatRole.Assistant, "a1"), Txt(ChatRole.User, "u2")]);

        var branch = session.GetActiveBranch();
        Assert.Equal(["u1", "a1", "u2"],
            branch.Select(e => ((TextContent)e.Content[0]).Text));
        Assert.Equal(session.Entries.Count, branch.Count);

        var tree = session.GetTree();
        Assert.Equal(session.Entries.Count, tree.Count);
        Assert.Single(session.ChildrenOf(null));
        Assert.Single(session.ChildrenOf(session.Entries[0].Id));
    }

    [Fact]
    public void JumpTo_TruncatesTail_HeadIsRewound()
    {
        var manager = NewManager();
        var session = manager.CreateSession(_cwd,
            [Txt(ChatRole.User, "u1"), Txt(ChatRole.Assistant, "a1"), Txt(ChatRole.User, "u2")]);

        session.JumpTo(session.Entries[1].Id);

        Assert.Equal("a1", ((TextContent)session.Head!.Content[0]).Text);
        Assert.Equal(2, session.Entries.Count);
    }

    [Fact]
    public async Task Fork_CopiesBranchTruncatedAtForkPoint_IndependentHistory()
    {
        var manager = NewManager();
        var source = manager.CreateSession(_cwd,
            [Txt(ChatRole.User, "u1"), Txt(ChatRole.Assistant, "a1"), Txt(ChatRole.User, "u2")]);

        var fork = await manager.ForkAsync(_cwd, source.Id, atEntryId: source.Entries[1].Id);
        Assert.NotEqual(source.Id, fork.Id);
        Assert.Equal(["u1", "a1"], fork.GetActiveBranch().Select(e => ((TextContent)e.Content[0]).Text));
        Assert.True(File.Exists(manager.SessionFilePath(_cwd, fork.Id)));

        fork.AppendMessage(Txt(ChatRole.User, "fork-q"));
        await manager.AppendAsync(fork, _cwd);
        source.AppendMessage(Txt(ChatRole.Assistant, "orig-a3"));
        await manager.AppendAsync(source, _cwd);

        var reloadedFork = await manager.LoadSessionAsync(_cwd, fork.Id);
        var reloadedSource = await manager.LoadSessionAsync(_cwd, source.Id);
        Assert.Equal(["u1", "a1", "fork-q"],
            reloadedFork.GetActiveBranch().Select(e => ((TextContent)e.Content[0]).Text));
        Assert.Equal(["u1", "a1", "u2", "orig-a3"],
            reloadedSource.GetActiveBranch().Select(e => ((TextContent)e.Content[0]).Text));
    }

    [Fact]
    public async Task Clone_DuplicatesEntireTree_IndependentHistory()
    {
        var manager = NewManager();
        var source = manager.CreateSession(_cwd,
            [Txt(ChatRole.User, "u1"), Txt(ChatRole.Assistant, "a1")]);

        var clone = await manager.CloneAsync(_cwd, source.Id);

        Assert.NotEqual(source.Id, clone.Id);
        Assert.Equal(source.Entries.Count, clone.Entries.Count);
        Assert.True(File.Exists(manager.SessionFilePath(_cwd, clone.Id)));

        clone.AppendMessage(Txt(ChatRole.User, "clone-q"));
        await manager.AppendAsync(clone, _cwd);

        var reloadedSource = await manager.LoadSessionAsync(_cwd, source.Id);
        Assert.Equal(2, reloadedSource.Entries.Count);
        Assert.Equal(3, (await manager.LoadSessionAsync(_cwd, clone.Id)).Entries.Count);
    }

    [Fact]
    public async Task File_IsSingleLineObjects_AppendOnly_Diffable()
    {
        var manager = NewManager();
        var session = manager.CreateSession(_cwd, [Txt(ChatRole.User, "first")]);
        var path = manager.SessionFilePath(_cwd, session.Id);

        var lines = File.ReadAllLines(path, Encoding.UTF8);
        Assert.Single(lines);
        Assert.StartsWith("{\"id\":", lines[0]);
        Assert.Equal(1, CountLines(path));

        session.AppendMessage(Txt(ChatRole.Assistant, "second"));
        await manager.AppendAsync(session, _cwd);

        lines = File.ReadAllLines(path, Encoding.UTF8);
        Assert.Equal(2, lines.Length);
        Assert.Equal(2, CountLines(path));
        using var doc = JsonDocument.Parse(lines[1]);
        Assert.Equal("assistant", doc.RootElement.GetProperty("role").GetString());
    }

    private static int CountLines(string path)
    {
        var count = 0;
        using var reader = new StreamReader(path);
        while (reader.ReadLine() is not null) count++;
        return count;
    }
}