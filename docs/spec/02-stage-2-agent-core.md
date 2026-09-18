# Specification 02 — Stage 2: Agent Core (Tool-Calling Loop)

- **Spec status:** Verified
- **Author:** Pi.NET
- **Written before implementation:** yes (SDD)
- **Consumes:** SR-01 contracts (`IModelRunner`, `StreamEvent`, `ChatMessage`)
- **Provides:** `Agent`, `AgentTool`, `AgentEvent` surface consumed by Stage 4 (TUI)

## 1. Purpose

Introduce a stateful agent runtime in `PiNet.AgentCore` that keeps a conversation
context, streams assistant responses, executes tool calls, and loops until the model
stops — all while emitting a deterministic event stream for UIs.

## 2. Scope

- `Agent` class: state, `PromptAsync`, `ContinueAsync`, `Abort`, `Subscribe`.
- `AgentTool` model with JSON Schema parameters and an execute delegate.
- Event lifecycle mirroring agent turns: `agent_start`, `turn_start`,
  `message_*`, `tool_execution_*`, `turn_end`, `agent_end`.
- Tool execution modes: parallel (default) and sequential.
- Hooks: `TransformContext`, `ConvertToLlm`, `BeforeToolCall`, `AfterToolCall`.
- Termination hint propagation across a tool batch.
- Built-in tools: `read`, `write`, `edit`, `bash`.

Out of scope: sessions (Stage 3), TUI (Stage 4), permissions (Stage 5), steering/follow-up queues (Stage 7).

## 3. Contracts

### 3.1 `AgentTool`

```csharp
public sealed class AgentTool
{
    public required string Name { get; init; }
    public string? Label { get; init; }
    public required string Description { get; init; }
    public ToolParameterSchema? Parameters { get; init; }
    public ToolExecutionMode ExecutionMode { get; init; }   // Parallel | Sequential
    public required Func<string, JsonElement, CancellationToken,
        IProgress<StreamEvent>?, Task<ToolResult>> Execute { get; init; }
}

public sealed record ToolResult(string Content, bool IsError = false, bool Terminate = false);
```

### 3.2 `AgentEvent` union

| Type | Payload | Emission point |
|------|---------|----------------|
| `AgentStartEvent` | — | Run begins |
| `TurnStartEvent` | — | Each LLM turn begins |
| `MessageStartEvent` | partial assistant `ChatMessage` | Before streaming a message |
| `MessageUpdateEvent` | `Delta` (text or thinking) | During streaming |
| `MessageEndEvent` | final assistant `ChatMessage` | Message completed |
| `ToolExecutionStartEvent` | `ToolCallContent` | Preflight decided, tool begins |
| `ToolExecutionUpdateEvent` | `Partial` | Tool streams progress (optional) |
| `ToolExecutionEndEvent` | tool call + `ToolResult` | Tool finished |
| `TurnEndEvent` | assistant message + `ToolExecutionResult[]` | Turn finished |
| `AgentEndEvent` | — | Run finished (subscribers still awaited) |

### 3.3 `AgentOptions` (primary surface)

```csharp
public sealed class AgentOptions
{
    public required IModelRunner Runner { get; init; }
    public AgentState? InitialState { get; init; }
    public Func<IReadOnlyList<ChatMessage>, ValueTask<IReadOnlyList<ChatMessage>>, ...>? TransformContext;
    public Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ChatMessage>>? ConvertToLlm;
    public Func<ToolCallContent, JsonElement, IReadOnlyList<ChatMessage>,
        CancellationToken, Task<ToolCallDecision>>? BeforeToolCall;
    public Func<ToolCallContent, ToolResult, IReadOnlyList<ChatMessage>,
        CancellationToken, Task<ToolResult?>>? AfterToolCall;
    public bool ParallelToolExecution;  // default true
}
```

### 3.4 `AgentState`

```csharp
public sealed class AgentState
{
    public string SystemPrompt { get; set; }
    public string ProviderId { get; set; }
    public string ModelId { get; set; }
    public IReadOnlyList<AgentTool> Tools { get; set; }
    public IReadOnlyList<ChatMessage> Messages { get; set; }
    public bool IsStreaming { get; internal set; }
    public string? ErrorMessage { get; internal set; }
}
```

## 4. Behavior Specs

### SR-02.01 — Prompt input becomes the first message

- **Given** an idle agent,
- **When** `PromptAsync("hello")` is called,
- **Then** a `ChatRole.User` message is appended to `State.Messages`,
  `AgentStartEvent` is emitted, and processing proceeds.

### SR-02.02 — `prompt()` event sequence without tools

- **Given** a run that produces plain text,
- **When** the prompt completes,
- **Then** events are emitted in exactly this order:
  `AgentStartEvent`, `TurnStartEvent`, `MessageStartEvent`,
  zero or more `MessageUpdateEvent`, `MessageEndEvent`, `TurnEndEvent`, `AgentEndEvent`.

### SR-02.03 — Streaming accumulates the assistant message

- **Given** streamed text deltas,
- **When** a turn ends,
- **Then** the assistant `ChatMessage` in `MessageEndEvent` contains the concatenated text as a `TextContent` block and `State.Messages` holds it.

### SR-02.04 — Tool call deltas become tool calls

- **Given** a stream that emits `ToolCallStart`/`ToolCallDelta`/`ToolCallEnd`,
- **When** the turn finalizes,
- **Then** each completed tool call appears as a `ToolCallContent` block in the assistant message.

### SR-02.05 — Tool execution event order (sequential, one tool)

- **Given** an assistant message with exactly one tool call,
- **When** tool execution runs,
- **Then** the sequence is:
  `ToolExecutionStartEvent`, optional `ToolExecutionUpdateEvent`(s),
  `ToolExecutionEndEvent`, then a `ChatRole.Tool` message appended to context,
  then `TurnEndEvent`, then the next `TurnStartEvent` (automatic continuation).

### SR-02.06 — Unknown tool call is reported as an error result

- **Given** the model calls a tool not present in `State.Tools`,
- **When** the tool executes,
- **Then** a `ToolResult` with `IsError := true` is returned and the LLM is told the tool is unknown, allowing it to recover.

### SR-02.07 — The loop continues while tools are called

- **Given** a turn that returns tool calls,
- **When** the tool results are appended to context,
- **Then** the agent starts another turn automatically with the updated context, and stops only when a turn produces zero tool calls.

### SR-02.08 — Tool failure does not crash the loop

- **Given** a tool that throws inside `Execute`,
- **When** the loop runs it,
- **Then** the exception is captured into a `ToolResult` with `IsError := true` and the loop continues with the next turn.

### SR-02.09 — Parallel execution mode

- **Given** an assistant message with two or more tool calls and `ParallelToolExecution := true`,
- **When** the tools run,
- **Then** tools execute concurrently; `ToolExecutionEndEvent` emission may finish out of tool-call order but `ToolResult` messages are appended to context in the assistant's original order.

### SR-02.10 — Sequential execution mode

- **Given** `ParallelToolExecution := false` (or a tool with `ExecutionMode := Sequential`),
- **When** the tools run,
- **Then** each tool executes one at a time, strictly in assistant order.

### SR-02.11 — `BeforeToolCall` gate can block

- **Given** `BeforeToolCall` returns a blocked decision,
- **When** the tool would run,
- **Then** execution is skipped, a `ToolResult` with `IsError := true` and content `<reason>` is produced, and `ToolExecutionEndEvent` is still emitted.

### SR-02.12 — `BeforeToolCall` return value downcasts to blocked tool

- **Given** a blocked preflight,
- **When** a reason is attached (`Block: true`, `BlockReason: "bash is disabled"`),
- **Then** the tool result content seen by the LLM and in `ToolExecutionEndEvent` contains the reason text.

### SR-02.13 — `AfterToolCall` can replace a result

- **Given** `AfterToolCall` returns a non-null `ToolResult`,
- **When** a tool finishes,
- **Then** the returned result replaces the original in the emitted `ToolExecutionEndEvent` and in context.

### SR-02.14 — Termination hint semantics

- **Given** `ToolResult.Terminate := true` on the **only** finalized tool of a batch,
- **When** the turn ends,
- **Then** no automatic follow-up LLM call is made; the run emits `AgentEndEvent` and stops.
- **Given** a batch with two or more tool results where only some have `Terminate := true`,
- **Then** the loop continues (termination requires every finalized result to signal it).

### SR-02.15 — `TransformContext` runs before each LLM call

- **Given** an agent with a `TransformContext` hook,
- **When** a turn starts,
- **Then** the hook receives the current messages and its output is used for the LLM request.

### SR-02.16 — `ConvertToLlm` filters messages before the wire call

- **Given** an agent with a `ConvertToLlm` hook,
- **When** a turn starts,
- **Then** the hook maps/filters `State.Messages` into the wire message list; default behavior passes all messages through unchanged.

### SR-02.17 — Abort

- **Given** an in-flight prompt,
- **When** `Abort()` is called,
- **Then** the provider stream is cancelled, `State.IsStreaming` becomes `false`, `State.ErrorMessage` is set, and `AgentEndEvent` is emitted exactly once.

### SR-02.18 — `ContinueAsync` resumes from current context

- **Given** a settled agent,
- **When** `ContinueAsync()` is called,
- **Then** a new turn runs from the existing `State.Messages` without appending a new user message.

### SR-02.19 — Subscribers are awaited in registration order

- **Given** subscribers registered with async handlers,
- **When** events are emitted,
- **Then** each handler is awaited (in registration order) before the next event proceeds.

### SR-02.20 — Built-in tools exist and respond

- **Given** the built-in tool set,
- **When** each tool is invoked,
- **Then** `read` returns a file's UTF-8 content (error if missing),
  `write` creates parent directories and writes content,
  `edit` replaces a single unique occurrence (error if zero or >1 matches),
  `bash` runs the command via the platform shell with a timeout and returns captured stdout/stderr (truncated).

## 5. Acceptance Criteria (entry to Stage 3)

- [x] AC1: Agent can read a file then write another based on it — full multi-turn loop.
  *(verified: `Ac1_AgentReadsFile_ThenWritesAnotherBasedOnIt_FullMultiTurnLoop`)*
- [x] AC2: Parallel tool execution verified (two independent reads in one turn).
  *(verified: `ParallelMode_RunsToolsConcurrently_ResultsAppendInAssistantOrder`)*
- [x] AC3: `BeforeToolCall` blocks a tool and the reason reaches the context.
  *(verified: `BeforeToolCall_BlocksTool_ReasonReachesContext`)*
- [x] AC4: Event ordering from SR-02.02 holds under test.
  *(verified: `PlainTextTurn_EmitsExactEventOrder`, `Subscribers_AreAwaitedInRegistrationOrder`)*
- [x] AC5: Faux provider drives a complete tool loop without network.

## 6. Testing Strategy

- Unit tests: `PiNet.AgentCore.Tests` (xunit) — event ordering, hooks, terminate semantics,
  error handling, parallel vs sequential using a scripted `IModelRunner`, and built-in tool behaviors.
  Run: `dotnet test PiNet.slnx`.
- End-to-end: CLI `--agent` mode against `FauxChatProvider`, which simulates a
  `read_file` tool call on turn 1 and answers contextually on turn 2.

## 7. Verification Log

| Date | Item | Evidence |
|------|------|----------|
| 2026-09-09 | AC5 — Faux drives a full tool loop | `dotnet run --project src\PiNet.Cli -- --agent --provider faux --model faux-model "read_file docs/README.md"` — turn 1: Faux emitted `ToolCallStart/Deltas/End` for `read_file`; agent executed the real `ReadFile` tool and printed contents; turn 2: Faux answered contextually from the `ChatRole.Tool` message; loop terminated (`AgentEndEvent`). Exit code 0. |
| 2026-09-09 | SR-02.03, SR-02.04, SR-02.05, SR-02.07 (plain-text path) | Same run's turn 2: streamed deltas accumulated into the assistant message, no tool calls → single-turn termination. Also confirmed `pinet --provider faux --model faux-model -p "ping"` still echoes (Stage 1 regression). |
| 2026-09-09 | SR-02.20 — `read_file` | Real file `docs/README.md` content returned in E2E. Missing-file error path covered by `ReadFile_MissingFile_ReturnsError`. |
| 2026-09-09 | SR-02.09 / AC2 — parallel | `ParallelMode_RunsToolsConcurrently_ResultsAppendInAssistantOrder`: two tools gated on a shared `TaskCompletionSource` both ran concurrently; results appended to context in assistant call order. |
| 2026-09-09 | SR-02.10 — sequential | `SequentialMode_ExecutesStrictlyInAssistantOrder` recorded strict A→B order with `ParallelToolExecution = false`. |
| 2026-09-09 | SR-02.11, SR-02.12 / AC3 — preflight block | `BeforeToolCall_BlocksTool_ReasonReachesContext`: tool not executed, `ToolExecutionEndEvent` + context `Tool` message carry the block reason. |
| 2026-09-09 | SR-02.13 — `AfterToolCall` replace | `AfterToolCall_ReplacesResult_InEventAndContext` → `"REPLACED"` in event and context. |
| 2026-09-09 | SR-02.14 — terminate semantics | `TerminateHint_OnOnlyResult_StopsLoopWithoutFollowUpTurn` (`GenerateCalls == 1`) and `TerminateHint_PartialInBatch_KeepsLooping` (`GenerateCalls == 2`). |
| 2026-09-09 | SR-02.15, SR-02.16 — context hooks | `TransformContext_RunsBeforeEachLlmCall_AndShapesRequestMessages` and `ConvertToLlm_FiltersMessagesBeforeWireCall`. |
| 2026-09-09 | SR-02.17 — abort | `Abort_CancelsProvider_SetsError_EmitsAgentEndOnce`: stream cancelled after `AgentStart`, `ErrorMessage` = "Aborted by user.", exactly one `AgentEndEvent`. |
| 2026-09-09 | SR-02.18 — ContinueAsync | `ContinueAsync_ResumesFromCurrentContext_WithoutNewUserMessage` → roles `[User, Assistant, Assistant]`, 2 generate calls. |
| 2026-09-09 | SR-02.19 — subscriber ordering | `Subscribers_AreAwaitedInRegistrationOrder`: for every event, handler 1 completes before handler 2 of the same event in the recorded log. |
| 2026-09-09 | SR-02.20 — `write_file`, `edit_file`, `bash` | `WriteFile_CreatesParentDirectories_AndWritesContent`, `EditFile_ReplacesSingleUniqueOccurrence`, `Bash_EchoCommand_ReturnsCapturedOutput`, `Bash_NonZeroExit_ReturnsErrorWithExitCode`. |
| 2026-09-09 | SR-02.01–02.08 | Covered by `AgentLoopTests` (user message append, exact event order, delta accumulation, tool-call blocks, execution sequence, unknown tool error, loop continuation, exception capture). |
| 2026-09-09 | AC1 | `Ac1_AgentReadsFile_ThenWritesAnotherBasedOnIt_FullMultiTurnLoop`: `read_file` → `write_file` (content derived from the read result) → final text turn; output file content asserted. |
| 2026-09-09 | Full suite | `dotnet test PiNet.slnx` → **27 passed, 0 failed**; plus `dotnet build PiNet.slnx` → 0 warnings, 0 errors. Regression: `pinet --provider faux --model faux-model -p "ping"` and `--agent ... "read_file docs/README.md"` still green. |