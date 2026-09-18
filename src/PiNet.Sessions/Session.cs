using PiNet.Abstractions.Chat;

namespace PiNet.Sessions;

public sealed class Session
{
    private readonly List<SessionEntry> _entries = [];

    public required string Id { get; init; }
    public IReadOnlyList<SessionEntry> Entries => _entries;
    public SessionEntry? Head => _entries.Count == 0 ? null : _entries[^1];
    public int PersistedEntryCount { get; internal set; }

    public void Append(SessionEntry entry)
    {
        if (entry.Id is null || entry.Id.Length == 0)
            throw new ArgumentException("Entry id must not be empty.", nameof(entry));
        if (entry.ParentId is null && _entries.Count > 0 && Head is not null)
            throw new ArgumentException("Entry must carry a ParentId when the session already has entries.", nameof(entry));
        if (entry.ParentId is not null && _entries.All(e => e.Id != entry.ParentId))
            throw new ArgumentException($"ParentId '{entry.ParentId}' does not exist in the session.", nameof(entry));

        _entries.Add(entry);
    }

    public void JumpTo(string entryId)
    {
        var index = _entries.FindIndex(e => e.Id == entryId);
        if (index < 0)
            throw new KeyNotFoundException($"No entry with id '{entryId}'.");
        _entries.RemoveRange(index + 1, _entries.Count - index - 1);
    }

    public IReadOnlyList<SessionEntry> GetActiveBranch()
    {
        var branch = new List<SessionEntry>();
        var current = Head;
        while (current is not null)
        {
            branch.Add(current);
            current = _entries.FirstOrDefault(e => e.Id == current.ParentId);
        }
        branch.Reverse();
        return branch;
    }

    public IReadOnlyList<SessionEntry> GetTree() => _entries;

    public IReadOnlyList<SessionEntry> ChildrenOf(string? parentId) =>
        _entries.Where(e => e.ParentId == parentId).ToArray();

    public void Restore(List<SessionEntry> entries)
    {
        _entries.Clear();
        _entries.AddRange(entries);
    }
}

public static class SessionMessageExtensions
{
    public static IReadOnlyList<ChatMessage> ToMessages(this Session session) =>
        session.GetActiveBranch().Select(e => e.ToMessage()).ToArray();

    public static SessionEntry AppendMessage(this Session session, ChatMessage message) =>
        AppendMessage(session, message, parentId: null);

    public static SessionEntry AppendMessage(this Session session, ChatMessage message, string? parentId)
    {
        var entry = SessionEntry.FromMessage(message, parentId ?? session.Head?.Id);
        session.Append(entry);
        return entry;
    }
}