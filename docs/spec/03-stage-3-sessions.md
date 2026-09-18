# Specification 03 — Stage 3: Session Management (JSONL Tree)

- **Spec status:** Verified
- **Author:** Pi.NET
- **Written before implementation:** yes (SDD)
- **Consumes:** SR-01 (`ChatMessage`, `ContentBlock`), SR-02 (`Agent` + `State.Messages`, `IModelRunner`)
- **Provides:** persistent sessions consumed by Stage 4 (TUI) and the CLI `--session` family

## 1. Purpose

Persist and resume agent conversations as append-only JSONL tree files so that
conversations survive process exits, support branching, and remain
human-readable and diffable. Also provide context compaction that summarizes
old messages via an LLM call without losing the full history.

## 2. Scope

- `PiNet.Sessions` project.
- `SessionEntry`: one message node in the tree (`Id`, `ParentId`, `Role`, `Content`, `Timestamp`, `Metadata`).
- `Session`: in-memory tree with a head, append, jump, branch linearization, tree grouping.
- `SessionStore`: JSONL read/write using one JSON object per line with stable field order.
- `SessionManager`: locations, create, load, latest, list, fork, clone, persist (append watermark).
- `SessionJson`: faithful `ChatMessage` <-> `SessionEntry` conversion (text, thinking, image, tool-call blocks).
- `SessionCompactor`: summarizes a message prefix via `IModelRunner`.
- CLI: `--session <path|id>`, `-c/--continue`, `--fork <id>`, `--no-session`.

Out of scope: TUI (Stage 4), permissions (Stage 5), skills (Stage 6), steering/follow-up queues (Stage 7), GC of old session files.

## 3. Contracts

### 3.1 `SessionEntry`

```csharp
public sealed record SessionEntry(
    string Id,
    ChatRole Role,
    IReadOnlyList<ContentBlock> Content,
    string? ParentId = null,
    string? ToolCallId = null,
    bool IsError = false,
    long Timestamp = 0,
    IReadOnlyDictionary<string, string>? Metadata = null);
```

### 3.2 `Session`

```csharp
public sealed class Session
{
    public required string Id { get; init; }
    public IReadOnlyList<SessionEntry> Entries { get; }
    public SessionEntry? Head { get; }                 // leaf of the active branch
    public int PersistedEntryCount { get; internal set; }
    public void Append(SessionEntry entry);            // ParentId defaults to current Head id
    public void JumpTo(string entryId);
    public IReadOnlyList<SessionEntry> GetActiveBranch();   // root -> head
    public IReadOnlyList<SessionEntry> GetTree();            // insertion order
    public IReadOnlyList<SessionEntry> ChildrenOf(string? parentId);
}
```

### 3.3 `SessionManager`

```csharp
public sealed class SessionManager
{
    public SessionManager(string sessionsRoot);          // default ~/.pinet/sessions
    public string WorkingDirKey(string cwd);             // sha256(cwd).Substring(0,12)
    public string SessionFilePath(string cwd, string sessionId);
    public Session CreateSession(string cwd, IReadOnlyList<ChatMessage>? seed = null);
    public async Task<Session> LoadSessionAsync(string cwd, string idOrPath);
    public async Task<Session?> LoadLatestAsync(string cwd);
    public Task SaveAsync(Session session, string cwd);
    public async Task AppendAsync(Session session, string cwd);   // persists entries beyond watermark
    public IReadOnlyList<SessionFileInfo> ListSessions(string cwd);
    public Task<Session> ForkAsync(string cwd, string idOrPath, string? atEntryId = null);
    public Task<Session> CloneAsync(string cwd, string idOrPath);
}
```

Branching model: a session file is an append-only linear chain. To branch, `ForkAsync`
copies the active branch (optionally truncated at a fork point) into a **new** session
file with fresh entry ids; future appends to the fork do not touch the source. `JumpTo`
rewinds the current session to an earlier entry (its stale tail is discarded), which is
how a session re-targets before forking.

### 3.4 JSONL line format (one object per line, stable order)

```
{"id":"...","parentId":"..."|null,"role":"user|assistant|tool|system",
 "timestamp":123,"isError":false,"content":[...],"toolCallId":"..."|null,"metadata":{...}}
```

`content` is an array of self-describing blocks:
`{"type":"text","text":"..."}`,
`{"type":"thinking","thinking":"..."}`,
`{"type":"image","data":"...","mimeType":"..."}`,
`{"type":"tool_call","id":"...","name":"...","arguments":{...}}`.

## 4. Behavior Specs

### SR-03.01 — Session location

- **Given** a `SessionManager` with root `R`,
- **When** a session for working dir `W` is created/loaded,
- **Then** files live under `R/<sha256(W).Substring(0,12)>/<session-id>.jsonl`.

### SR-03.02 — Create persists an empty seedable session

- **Given** `CreateSession(W, seed)` with seed messages,
- **When** the session is saved,
- **Then** the JSONL file exists with one line per entry, chained by `ParentId`.

### SR-03.03 — Append persists only new entries

- **Given** a loaded session with `PersistedEntryCount = N`,
- **When** two new entries are appended and `AppendAsync` runs,
- **Then** exactly 2 lines are added to the file and `PersistedEntryCount = N + 2`.

### SR-03.04 — JSONL round-trip preserves every field

- **Given** entries containing text, thinking, image, and tool-call blocks,
- **When** they are written then loaded,
- **Then** each entry round-trips identically (Id, ParentId, Role, Content blocks, ToolCallId, IsError, Timestamp, Metadata).

### SR-03.05 — Load resolves by path or partial id

- **Given** a session id `abcdef123456`,
- **When** `LoadSessionAsync(W, "abc")` (unique prefix) or an absolute/relative `.jsonl` path is passed,
- **Then** the session is loaded; a non-unique prefix raises `SessionResolutionException`.

### SR-03.06 — Latest session

- **Given** several session files in a working dir,
- **When** `LoadLatestAsync(W)` runs,
- **Then** the file with the newest last-write time is returned (or `null` if none).

### SR-03.07 — Tree grouping

- **Given** entries with a fork (an entry whose `ParentId` is not the previous head),
- **When** `GetTree()` / `ChildrenOf(parentId)` run,
- **Then** every child is grouped under its parent and the total count equals `Entries.Count`.

### SR-03.08 — Active branch linearization

- **Given** a head entry,
- **When** `GetActiveBranch()` runs,
- **Then** it returns the chain from the root entry to the head in order.

### SR-03.09 — JumpTo rewinds future appends

- **Given** a session whose head is `H`,
- **When** `JumpTo(E)` runs,
- **Then** `Head == E`, entries after `E` are discarded, and the next `Append` attaches under
  `E` (branching into a separate history happens via `ForkAsync`).

### SR-03.10 — Fork copies the active branch

- **Given** a session with a fork point `E`,
- **When** `ForkAsync(W, session, E)` runs,
- **Then** a new session id + file is created whose entries are exactly the active branch up to `E`, and independent appends no longer touch the source.

### SR-03.11 — Clone duplicates the whole tree

- **Given** a session with multiple branches,
- **When** `CloneAsync` runs,
- **Then** a new session id + file is created containing all entries of the original tree, and new appends are independent.

### SR-03.12 — Message/entry conversion is faithful

- **Given** any `ChatMessage`,
- **When** converted to `SessionEntry` and back,
- **Then** `Role`, `ToolCallId`, `IsError`, and every content block (including `ToolCallContent` arguments JSON) are preserved.

### SR-03.13 — File is human-readable and diffable

- **Given** a persisted session,
- **When** the file is inspected,
- **Then** each line is a single self-contained JSON object and the line sequence is append-only and deterministic (stable field order).

### SR-03.14 — Compactor summarizes a prefix losslessly

- **Given** an `IModelRunner` and messages exceeding a threshold,
- **When** `Compactor.CompactAsync(prefix, runner)` runs,
- **Then** the LLM summary is returned as a single summary message, the full history is left untouched on disk, and re-running is idempotent.

### SR-03.15 — CLI session flags load then persist

- **Given** `--session <id|path>` (or `-c` for latest, `--fork <id>` for a fork),
- **When** agent mode runs with a prompt,
- **Then** prior entries are loaded into `AgentState.Messages`, the turn runs, and all messages (old + new) are saved back under the session file.

## 5. Acceptance Criteria (entry to Stage 4)

- [x] AC1: Create a session, ask 3 questions across invocations, resume — all context preserved.
  *(verified: CLI E2E with `faux` + `Ac1_ThreeInvocations_PreserveAllContext`)*
- [x] AC2: Fork a session at entry 2; both branches continue independently.
  *(verified: `Fork_CopiesBranchTruncatedAtForkPoint_IndependentHistory` + CLI `--fork`)*
- [x] AC3: Compactor summarises an oversize prefix via the LLM (context-limit-driven trigger verified at the API level).
  *(verified: `Compactor_SummarizesPrefix_AndDoesNotTouchDisk`)*
- [x] AC4: The JSONL file is human-readable and diffable (verified by content assertion).
  *(verified: `File_IsSingleLineObjects_AppendOnly_Diffable`)*

## 6. Testing Strategy

- Unit tests: `PiNet.Sessions.Tests` (xunit) with temp dirs; round-trip, append watermark,
  resolution, fork/clone, tree/branch, conversion, compactor via `ScriptedRunner`.
  Run: `dotnet test PiNet.slnx`.
- E2E: CLI `--session` flow across separate process invocations against `faux` provider.

## 7. Verification Log

| Date | Item | Evidence |
|------|------|----------|
| 2026-09-09 | SR-03.01, 03.02 | `Create_PersistsSeed_UnderWorkingDirHash_WithParentChain`: file under `<root>/<sha256(cwd)[..12]>/<id>.jsonl`, parent chain intact. |
| 2026-09-09 | SR-03.03 | `AppendAsync_PersistsOnlyNewEntries`: watermark increments by appended count only. |
| 2026-09-09 | SR-03.04, 03.12 | `RichEntry_RoundTrips_Exactly`: text/thinking/image/tool-call blocks, `ParentId`, `ToolCallId`, `IsError`, `Timestamp`, `Metadata`, tool-call arguments JSON all preserved. |
| 2026-09-09 | SR-03.05 | `Load_ResolvesByPartialId_AndByPath`: exact prefix + path resolution; unknown and ambiguous prefixes throw `SessionResolutionException`. |
| 2026-09-09 | SR-03.06 | `LoadLatest_ReturnsNewestWrite` (timestamp ordering controlled explicitly). |
| 2026-09-09 | SR-03.07, 03.08 | `GetActiveBranch_RootToHead`: branch + tree + `ChildrenOf` counts. |
| 2026-09-09 | SR-03.09 | `JumpTo_TruncatesTail_HeadIsRewound`. |
| 2026-09-09 | SR-03.10 / AC2 | `Fork_CopiesBranchTruncatedAtForkPoint_IndependentHistory` + CLI: two `--fork ywbri5s32t5gd` runs produced independent branches that kept their own tails. |
| 2026-09-09 | SR-03.11 | `Clone_DuplicatesEntireTree_IndependentHistory`. |
| 2026-09-09 | SR-03.13 / AC4 | `File_IsSingleLineObjects_AppendOnly_Diffable`: one JSON object per line, append-only, `{"id":...}` leading; sample file `C:\Users\migue\.pinet\sessions\3d68c4e9997c\ywbri5s32t5gd.jsonl` (10 lines after 4 invocations + tool turn). |
| 2026-09-09 | SR-03.14 / AC3 | `Compactor_SummarizesPrefix_AndDoesNotTouchDisk`: summary produced via one `IModelRunner` call; on-disk bytes unchanged; empty prefix short-circuits without a call. |
| 2026-09-09 | SR-03.15 / AC1 | CLI E2E (`faux`): `-c "first question"` created session; three `--session ywbri5s32t5gd` invocations each echoed the full growing history (`You said: first question second question third question`); a 4th invocation ran the `read_file` tool loop in-context; final `-c` resumed the newest fork. Each turn persisted (`[saved N new message(s)]`). |
| 2026-09-09 | Full suite | `dotnet test PiNet.slnx` → 40 passed (27 AgentCore + 13 Sessions), 0 failed; `dotnet build PiNet.slnx` → 0 warnings, 0 errors. |

## 8. Decisions

- JSONL over a single JSON document: appends are O(1) and the file is diffable (per-file diff,
  not lines-of-file).
- Session id is a base32 short token; user may reference any unique prefix.
- Fork defaults to copying the active branch only; Clone copies the full tree.
- Auto-compaction wiring into the agent loop is deferred to Stage 7; the compactor is a verified
  standalone component consumed by the CLI/explicit triggers.