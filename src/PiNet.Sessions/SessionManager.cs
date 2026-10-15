using System.Text.Json;
using PiNet.Abstractions.Chat;

namespace PiNet.Sessions;

public sealed record SessionFileInfo(string SessionId, string Path, DateTime LastWriteUtc, int EntryCount);

public sealed class SessionManager
{
    /// <summary>Environment variable that relocates the global session home (spec 07 §3, 7g).
    /// Defaults to <c>~/.pinet</c> when unset.</summary>
    public const string HomeEnvVar = "PI_NET_HOME";

    public string SessionsRoot { get; }

    /// <summary>Global home (<c>PI_NET_HOME</c> override, else <c>~/.pinet</c>). Used to default the
    /// session root to <c>~/.pinet/sessions</c> (or <c>$PI_NET_HOME/sessions</c>).)</summary>
    public static string HomeDir =>
        Environment.GetEnvironmentVariable(HomeEnvVar)
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pinet");

    public SessionManager(string? sessionsRoot = null)
    {
        SessionsRoot = sessionsRoot
            ?? Path.Combine(HomeDir, "sessions");
    }

    public string WorkingDirKey(string cwd) => SessionId.WorkingDirKey(cwd);

    private string DirFor(string cwd) => Path.Combine(SessionsRoot, WorkingDirKey(cwd));

    public string SessionFilePath(string cwd, string sessionId) =>
        Path.Combine(DirFor(cwd), $"{sessionId}.jsonl");

    public Session CreateSession(string cwd, IReadOnlyList<ChatMessage>? seed = null)
    {
        var session = new Session { Id = SessionId.New() };
        string? parent = null;
        foreach (var message in seed ?? [])
        {
            var entry = SessionEntry.FromMessage(message, parent);
            session.Append(entry);
            parent = entry.Id;
        }
        Save(cwd, session);
        return session;
    }

    public async Task<Session> LoadSessionAsync(string cwd, string idOrPath, CancellationToken ct = default)
    {
        var path = ResolvePath(cwd, idOrPath);
        var id = Path.GetFileNameWithoutExtension(path);
        var entries = await SessionStore.ReadAllAsync(path, ct);
        var session = new Session { Id = id };
        session.Restore(entries);
        session.PersistedEntryCount = session.Entries.Count;
        return session;
    }

    public async Task<Session?> LoadLatestAsync(string cwd, CancellationToken ct = default)
    {
        var latest = ListSessions(cwd).OrderByDescending(f => f.LastWriteUtc).ThenByDescending(f => f.SessionId).FirstOrDefault();
        return latest is null ? null : await LoadSessionAsync(cwd, latest.SessionId, ct);
    }

    public IReadOnlyList<SessionFileInfo> ListSessions(string cwd)
    {
        var dir = DirFor(cwd);
        if (!Directory.Exists(dir))
            return [];

        return Directory.EnumerateFiles(dir, "*.jsonl")
            .Select(path => new SessionFileInfo(
                Path.GetFileNameWithoutExtension(path),
                path,
                File.GetLastWriteTimeUtc(path),
                CountLines(path)))
            .OrderBy(f => f.LastWriteUtc)
            .ThenBy(f => f.SessionId) // deterministic tie-break when timestamps coincide
            .ToArray();
    }

    private static int CountLines(string path)
    {
        var count = 0;
        using var reader = new StreamReader(path);
        while (reader.ReadLine() is not null) count++;
        return count;
    }

    public Task SaveAsync(Session session, string cwd, CancellationToken ct = default)
    {
        Save(cwd, session);
        return Task.CompletedTask;
    }

    public async Task AppendAsync(Session session, string cwd, CancellationToken ct = default)
    {
        var toWrite = session.Entries.Skip(session.PersistedEntryCount).ToArray();
        if (toWrite.Length == 0)
            return;
        await SessionStore.AppendAsync(SessionFilePath(cwd, session.Id), toWrite, ct);
        session.PersistedEntryCount = session.Entries.Count;
    }

    private void Save(string cwd, Session session)
    {
        var path = SessionFilePath(cwd, session.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lines = session.Entries.Select(Ser).ToArray();
        File.WriteAllLines(path, lines);
        session.PersistedEntryCount = session.Entries.Count;
    }

    private static string Ser(SessionEntry entry)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, SessionJson.WriterOptions))
            SessionJson.WriteEntry(writer, entry);
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    public async Task<Session> ForkAsync(string cwd, string idOrPath, string? atEntryId = null, CancellationToken ct = default)
    {
        var source = await LoadSessionAsync(cwd, idOrPath, ct);
        var branch = source.GetActiveBranch();
        if (atEntryId is not null)
        {
            if (branch.All(e => e.Id != atEntryId))
                throw new SessionResolutionException($"No entry '{atEntryId}' on the active branch.");
            branch = branch.TakeWhile(e => e.Id != atEntryId).Append(branch.First(e => e.Id == atEntryId)).ToArray();
        }

        var fork = new Session { Id = SessionId.New() };
        CopyInto(fork, branch);
        Save(cwd, fork);
        return fork;
    }

    public async Task<Session> CloneAsync(string cwd, string idOrPath, CancellationToken ct = default)
    {
        var source = await LoadSessionAsync(cwd, idOrPath, ct);
        var clone = new Session { Id = SessionId.New() };
        CopyInto(clone, source.Entries);
        Save(cwd, clone);
        return clone;
    }

    private static void CopyInto(Session target, IReadOnlyList<SessionEntry> entries)
    {
        var idMap = entries.ToDictionary(e => e.Id, _ => SessionId.New());
        foreach (var entry in entries)
        {
            var parentId = entry.ParentId is null ? null : idMap[entry.ParentId];
            target.Append(entry with { Id = idMap[entry.Id], ParentId = parentId });
        }
        target.PersistedEntryCount = target.Entries.Count;
    }

    private string ResolvePath(string cwd, string idOrPath)
    {
        var looksLikePath = idOrPath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
            || idOrPath.Contains(Path.DirectorySeparatorChar)
            || idOrPath.Contains('/')
            || File.Exists(idOrPath);
        if (looksLikePath)
            return Path.GetFullPath(idOrPath);

        var dir = DirFor(cwd);
        if (!Directory.Exists(dir))
            throw new SessionResolutionException($"No session matching '{idOrPath}'.");

        var matches = Directory.EnumerateFiles(dir, "*.jsonl")
            .Where(f => {
                var name = Path.GetFileNameWithoutExtension(f);
                return name.StartsWith(idOrPath, StringComparison.Ordinal);
            })
            .ToArray();

        if (matches.Length == 1)
            return matches[0];
        if (matches.Length == 0)
            throw new SessionResolutionException($"No session matching '{idOrPath}'.");
        throw new SessionResolutionException($"Session id '{idOrPath}' is ambiguous ({matches.Length} matches).");
    }
}