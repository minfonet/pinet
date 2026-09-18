# Specification 04 — Stage 4: Interactive Prompt Loop (zero-dependency TUI)

- **Spec status:** Verified
- **Author:** Pi.NET
- **Written before implementation:** yes (SDD)
- **Consumes:** SR-01 (`ChatMessage`, events), SR-02 (`Agent`, `IModelRunner`, event stream), SR-03 (`SessionManager`, `--session/-c/--fork`)
- **Provides:** a persistent, scriptable interactive terminal loop inside `PiNet.Cli`; shared turn/render logic reused by one-shot `--agent` mode

## 1. Purpose

Turn `PiNet.Cli --agent` into a usable REPL-style coding assistant: the user stays in one
process, types prompts, watches the agent stream (thinking, tool calls, results), and can
resume/fork sessions with slash-commands — all with **zero NuGet dependencies**
(the project's ADR-0001 rule). Everything interactive is layered so its logic is a pure
input→state→output function, making the loop testable headlessly.

## 2. Scope

In scope (all inside `PiNet.Cli`):

- `--interactive`/`-i` flag entering the loop (agent mode columns).
- `LineEditor` + `LineHistory`: minimal raw line editing (insert at caret, backspace,
  delete, arrows, home/end, kill-line) with in-memory command history (Up/Down).
- `SlashCommand` parsing and dispatch: `/help`, `/quit`, `/exit`, `/new`, `/resume`,
  `/fork`, `/model`.
- `IConsoleInput` / `IConsoleOutput` seams so the loop is driven by scripted keys in tests.
- `AgentConsoleRenderer`: pure folding of `AgentEvent`s into display lines, shared by
  one-shot agent mode and the loop (single source of truth for streaming output).
- `TurnRunner`: shared "run one prompt against a session, then persist" logic, reused by
  `--agent` (one-shot) and the interactive loop, preserving SessionManager watermark append.
- Ctrl+C semantics: during a prompt it clears the line (and on an empty line exits);
  during a turn it cancels the turn and returns to the prompt.

Out of scope: multi-pane screen layouts, themes, mouse support, completion menus,
permissions (Stage 5), skills (Stage 6), TUI as a separate package. The loop renders
scrolling output, not a full-screen alternative to the terminal.

## 3. Contracts

### 3.1 I/O seams

```csharp
public interface IConsoleInput  { ConsoleKeyInfo? ReadKey(); }        // null = EOF
public interface IConsoleOutput { void Write(string s); void WriteLine(string s); void WriteLine(string s, ConsoleColor color); }
```

Real implementations wrap `System.Console`; tests feed `ConsoleKeyInfo` sequences.

### 3.2 `LineEditor`

```csharp
public sealed class LineEditor
{
    public string Buffer { get; }   // current line text
    public int Caret { get; }       // 0..Buffer.Length
    public bool Complete { get; }   // true after Enter
    public string Submit();

    public void Apply(ConsoleKeyInfo key);   // handles keys below; unknown keys insert their char
    public void Reset();
}
```

Handled keys: printable chars (insert at caret), `Enter` (complete), `Backspace`/`Ctrl+H`
(delete left), `Delete`, `Left`/`Right`, `Home`/`End`, `Ctrl+A`, `Ctrl+E`, `Ctrl+U`
(delete to start), `Ctrl+K` (delete to end), `Ctrl+C` (clear line only — termination is loop
policy). Cursor always stays in `[0, Buffer.Length]`.

### 3.3 `LineHistory`

```csharp
public sealed class LineHistory
{
    public LineHistory(int maxEntries = 100);
    public void Add(string line);       // ignores blank and duplicate-last
    public bool MoveBack();             // toward older
    public bool MoveForward();          // toward newer
    public string Current { get; }      // entry at the pointer or the in-progress draft
    public int Count { get; }
}
```

### 3.4 `SlashCommand`

```csharp
public enum SlashCommandKind { Unknown, Help, Quit, NewSession, Resume, Fork, Model }

public sealed record SlashCommand(SlashCommandKind Kind, string? Arg, string Raw);
public static class SlashCommandParser { public static SlashCommand Parse(string line); }
```

Rules: a line whose first non-space char is `/` is a command. Word = rest after the name,
trimmed. Names: `help` (arg optional: command name), `quit`/`exit`, `new`, `resume` (arg:
session id/path or blank for interactive picker), `fork` (arg required), `model` (arg
required, `provider/model`). Anything else → `Unknown`.

### 3.5 `AgentConsoleRenderer`

```csharp
public static class AgentConsoleRenderer
{
    public static IReadOnlyList<RenderLine> Fold(IReadOnlyList<AgentEvent> events);
}
public sealed record RenderLine(string Text, ConsoleColor? Color);
```

Folding rules (single source of truth, shared by one-shot and loop):
- `MessageUpdateEvent { IsThinking: true }` → dark gray text appended to the current thinking line.
- Other `MessageUpdateEvent` → plain text.
- `ToolExecutionStartEvent` → new line `> Tool: <name>`.
- `ToolExecutionUpdateEvent` → appended to the current tool line.
- `ToolExecutionEndEvent` → new line `> ok: <truncated result>` (green) / `> error: <...>` (red).
- `TurnEndEvent` and other events → blank/ignored for display.
- Long tool results truncated to 500 chars.

### 3.6 `TurnRunner`

```csharp
public sealed class TurnRunner
{
    public Agent Agent { get; }
    public string Cwd { get; }
    public Session? Session { get; private set; }   // null when --no-session
    public int PriorMessageCount { get; private set; }  // messages loaded when session attached
    public event AgentEventSink? EventSink;             // the loop/CLI attach its renderer here

    public void AttachSession(Session session);
    public void DettachSession();
    public Task<(bool Ok, string? Error, bool Aborted)> RunAsync(string prompt, CancellationToken ct);
}
```

`RunAsync` seeds no messages (they were baked into the `Agent` at construction), invokes
`Agent.PromptAsync`, and on success appends only the messages beyond `PriorMessageCount` to
the attached session and persists via `SessionManager.AppendAsync`. It returns
`Aborted = true` when the token was cancelled mid-turn; on abort the session is **not**
persisted (partial turns are not committed).

### 3.7 `InteractiveLoop`

```csharp
public sealed class InteractiveLoop
{
    public InteractiveLoop(ChatService chat, SessionManager manager, string cwd,
        string providerId, string modelId, IConsoleOutput output, Func<ConsoleKeyInfo?> readKey);

    public Task<int> RunAsync(IConsoleInput input, CancellationToken ct);
}
```

`RunAsync` is the pump: prompt → key editing → slash-command or turn → repeat.
Return codes: 0 normal exit (`/quit`, Ctrl+D, Ctrl+C on empty prompt), 130 aborted turn.

## 4. Behavior Specs

### SR-04.01 — Loop lifecycle

- **Given** `--agent -i` (or `--agent --interactive`),
- **When** the loop starts,
- **Then** the user can submit prompts repeatedly; `/quit`/`/exit`, Ctrl+D, or Ctrl+C on an
  empty input line exit with code 0; a blank line is ignored (re-prompts).

### SR-04.02 — Line editor editing keys (pure)

- **Given** a `LineEditor` with buffer `abc` and caret `2`,
- **When** each edit key is applied,
- **Then** typing `X` yields `abXc`/caret 3; Backspace yields `ac`/caret 1; Delete yields
  `ab`; Left/Right move caret by one (clamped at 0/3); Home/End jump to 0/3; Ctrl+U clears
  the buffer; Ctrl+K deletes to end; Enter completes and `Submit()` returns the line.

### SR-04.03 — Command history

- **Given** lines `foo`, `bar` submitted,
- **When** Up is pressed three times,
- **Then** the buffer shows `bar`, `foo`, then stays `foo` (older-boundary clamped);
  Down after navigating back returns to the in-progress draft; blank lines and repeats are
  not recorded; `Count == 2`.

### SR-04.04 — Slash-command parsing

- **Given** lines `/quit`, `exit`, `/resume ywbri5s32t5gd`, `/fork <id>`, `/model faux/faux-model`, `/wat`,
- **When** parsed,
- **Then** each maps to the typed kind + arg; `exit` (no slash) is a plain prompt, not a
  command; `/wat` → `Unknown`. Unknown commands print an error line and return to the prompt.

### SR-04.05 — `/help` output

- **Given** `/help` (or `/help <name>`),
- **Then** a usage summary listing all commands is printed; `/help quit` prints the quit entry.

### SR-04.06 — Sessions in the loop

- **Given** `--session <id|path>` / `-c` / `--fork <id>` supplied on the command line,
- **When** the loop runs turns,
- **Then** prior entries are loaded into the agent and every successful turn is appended and
  persisted (SR-03.15 semantics); `/new` abandons the current session and starts ephemeral
  (no persistence); `--no-session` disables persistence entirely.

### SR-04.07 — `/resume` picker

- **Given** `/resume` with no argument and one or more saved sessions for the cwd,
- **When** executed,
- **Then** sessions are listed (id, entry count, last-write age) and the user selects by
  number or id; `/resume <id|path>` loads directly; invalid selection prints an error and
  keeps the current session.

### SR-04.08 — `/model` switch

- **Given** `/model <provider>/<id>`,
- **When** the model is valid per the registry,
- **Then** subsequent turns use it; an invalid model prints an error and keeps the current one.

### SR-04.09 — Ctrl+C during a turn cancels and returns to the prompt

- **Given** a turn in progress,
- **When** Ctrl+C is raised,
- **Then** the turn is cancelled (aborted marker printed), the partial turn is **not**
  persisted, prior context is intact, and the loop returns to the prompt; the next prompt
  continues the session.

### SR-04.10 — Streaming rendering shared by one-shot and loop

- **Given** an identical `AgentEvent` sequence,
- **When** folded by `AgentConsoleRenderer.Fold`,
- **Then** the same display line sequence is produced whether the events come from
  `--agent` (one-shot) or the interactive loop; thinking lines are colorized and tool
  results are labeled `ok:`/`error:`.

## 5. Acceptance Criteria (entry to Stage 5)

- [x] AC1: `--agent -i` accepts several prompts in one process; context accumulates and is
  persisted across turns (visible in the JSONL file afterward).
- [x] AC2: Line editing + history behave per SR-04.02/03, verified headlessly with scripted
  key sequences.
- [x] AC3: Slash commands (`/help`, `/quit`, `/new`, `/resume`, `/fork`, `/model`) work;
  unknown commands print an error and the loop continues.
- [x] AC4: Ctrl+C cancels a running turn (Aborted, not persisted) and returns to the prompt;
  Ctrl+C on an empty prompt exits the loop.

## 6. Testing Strategy

- Unit tests: `PiNet.Cli.Tests` (xunit) with scripted `ConsoleKeyInfo` sequences for the
  line editor, history, and slash parser; pure folding assertions for the renderer; loop
  tests drive `InteractiveLoop` with scripted input + `ScriptedRunner` (from
  `PiNet.AgentCore.Tests`) and assert on the captured `IConsoleOutput`.
- E2E: run `dotnet run --project src\PiNet.Cli -- --agent -i ...` with piped keys and verify
  multi-turn persistence in the JSONL file.

## 7. Verification Log

| Date | Item | Evidence |
|------|------|----------|
| 2026-07-09 | AC1 multi-turn persistence | `InteractiveLoopTests.MultiPrompt_Session_AccumulatesContext_AndPersists` (2 turns → 4 JSONL lines, second request carries prior context) + `NewSession_DisablesPersistence_ForSubsequentTurns` + `NoSessionFlag_DisablesPersistence`. Fixed `TurnRunner.PriorMessageCount` watermark (was re-appending the whole history each turn). |
| 2026-07-09 | AC2 line editor + history, scripted keys | `LineEditorTests` (13 tests: insert/caret/backspace/delete/arrows/home-end/Ctrl+U/Ctrl+K/Enter/unknown keys) + `LineHistoryTests` (8 tests: add/Up/Down clamping/draft restore/blank+repeat dedup/Count). |
| 2026-07-09 | AC3 slash commands | `SlashCommandParserTests` (11 tests) + loop tests: `Help_PrintsCommandList`, `HelpForCommand_PrintsSingleEntry`, `UnknownCommand_PrintsError_AndContinues`, `NewSession_DisablesPersistence...`, `ResumePicker_ListsSessions_AndSelectsByNumber`, `ResumeWithArg_LoadsDirectly`, `ForkWithArg_CreatesNewSession`, `ModelSwitch_ChangesModel_ForNextTurn`, `ModelSwitch_InvalidModel_KeepsCurrent`. |
| 2026-07-09 | AC4 Ctrl+C semantics | `CtrlC_DuringTurn_Aborts_AndDoesNotPersist` (aborted marker, 0 persisted lines, loop returns to prompt) + `CtrlC_OnEmptyPrompt_Exits` (exit code 0). |
| 2026-07-09 | AC10 shared rendering | `AgentConsoleRendererTests` (10 tests) fold identical event sequences for one-shot and loop; `--agent` one-shot mode reuses `AgentConsoleRenderer` + `TurnRunner` (single streaming path). |
| 2026-07-09 | Full suite | `dotnet test PiNet.slnx`: 111/111 passing (27 AgentCore + 13 Sessions + 71 Cli). Note: piped-keys E2E from §6 is covered headlessly — `InteractiveLoop` is driven by scripted `ConsoleKeyInfo` sequences through the same `IConsoleInput`/`IConsoleOutput` seams the real console uses; `-i` intentionally rejects redirected stdin because raw `ReadKey` needs a TTY. |

## 8. Decisions

- Zero-dependency rule honored: no Spectre.Console — the "TUI" is a scrolling prompt loop
  with raw `ReadKey` editing. Keeps the core small and testable.
- Rendering and turn orchestration are extracted out of one-shot `--agent` and shared, so
  there is exactly one streaming behavior.
- Partial turns are not persisted on abort: crash-consistency and no half-messages.
- Ctrl+C is pure loop policy (clear line), never baked into the line editor.