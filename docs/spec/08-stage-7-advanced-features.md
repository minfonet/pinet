# Specification 08 — Stage 7: Advanced Features

- **Spec status:** Verified (2026-10-15); 7a.2 (CLI capture during a turn) deferred
- **Author:** Pi.NET
- **Written before implementation:** yes (SDD)
- **Consumes:** `Agent` / `AgentState` / `AgentEvent` (SR-02), `SessionManager` / `SessionCompactor` / `SessionJson` (SR-03), `IModelRunner` / `ChatRequest` / `StreamEvent` / `ModelDefinition` (SR-01), interactive loop + slash commands (SR-04), `~/.pinet` config resolution (SR-05/06/07)
- **Provides:** interactive steering/follow-up, wired context compaction, reasoning/thinking levels, cross-provider handoff, image input, session export/import, user settings

## 1. Purpose

Bring Pi.NET to feature parity with Pi's advanced capabilities while keeping the
zero-NuGet, raw-protocol posture (ADR-0001) and the SDD process. The stage is
split into seven sub-stages that can be specified, implemented and verified
independently:

| Sub-stage | Feature | Status |
|-----------|---------|--------|
| 7a | Steering & follow-up | Verified (agent core); CLI capture 7a.2 deferred |
| 7b | Context compaction (auto + `/compact`) | Verified |
| 7c | Reasoning / thinking levels | Verified (2026-10-15) |
| 7d | Cross-provider handoff | Verified (2026-10-15) |
| 7e | Image input | Verified (2026-10-15) |
| 7f | Export / import | Verified (2026-10-15) |
| 7g | Settings | Verified (2026-10-15) |

## 2. Scope

In scope: the seven sub-stages above, plus two consistency fixes discovered while
researching the stage:

- `SessionManager` default root must honor `PI_NET_HOME` (it currently hard-codes
  `~/.pinet/sessions`, unlike `ModelsConfig`, `PermissionPaths` and
  `ExtensionsPaths`). Tracked under 7g (config resolution).
- `Agent.TransformContext` / `ConvertToLlm` are exposed but never wired by the
  CLI; 7b uses `TransformContext` as the compaction seam.

Out of scope (explicit divergences / later stages):

- Themes, mouse support, multi-pane full-screen TUI (Stage 4 out-of-scope).
- `--mode json` / `--mode rpc` machine modes (not planned for this stage).
- Telemetry, packaging, CI/release (Stage 8).
- Multi-agent orchestration (project non-goal).

## 3. Sub-stage 7a — Steering & follow-up

- **Status:** Verified (agent core, 2026-09-21); CLI capture during a turn (7a.2)
  deferred.

**Goal.** Let a user (or a host) inject messages into a running agent without
aborting it, matching Pi's steering/follow-up model.

- **Steering** — a message delivered to the model at the next **turn boundary**
  (i.e. the next LLM request of the current run). It influences the very next
  decision.
- **Follow-up** — a message queued for **after the run would otherwise finish**
  (when the model stops calling tools). It starts another turn instead of ending
  the run.

### 3.1 Contracts (`PiNet.AgentCore`)

```csharp
public enum QueueMode
{
    OneAtATime, // deliver at most one queued message per turn boundary (default)
    All         // deliver every queued message at the same turn boundary, FIFO
}

// AgentState
public QueueMode SteeringMode { get; set; } = QueueMode.OneAtATime;
public QueueMode FollowUpMode { get; set; } = QueueMode.OneAtATime;

// Agent
public void Steer(string text);
public void Steer(ChatMessage message);
public void FollowUp(string text);
public void FollowUp(ChatMessage message);
public int PendingSteering { get; }
public int PendingFollowUp { get; }
```

### 3.2 Behavior specs

- **SR-07.01** — Steering messages are never injected mid-stream; they are
  delivered at the next turn boundary (after the current assistant/tool turn
  completes, before the next LLM request).
- **SR-07.02** — With `QueueMode.OneAtATime`, at most one queued message is
  injected per turn boundary; with `QueueMode.All`, all currently queued messages
  are injected at that boundary in FIFO order.
- **SR-07.03** — An injected message is appended to `State.Messages` with role
  `User` and emits `MessageStartEvent` then `MessageEndEvent` for that message,
  so subscribers render it like any user turn.
- **SR-07.04** — When the loop would finish (assistant produced no tool calls),
  if the follow-up queue is non-empty the agent performs another turn instead of
  ending; `FollowUpMode` selects how many follow-ups are injected per boundary.
- **SR-07.05** — `AgentEndEvent` is emitted only once both queues are empty and
  the loop has no work left.
- **SR-07.06** — `Steer` / `FollowUp` are non-throwing and thread-safe: they may
  be called from any thread at any time. If no run is active, the queued messages
  are delivered at the next run (`PromptAsync` / `ContinueAsync`); otherwise they
  take effect during the active run.
- **SR-07.07** — Steering is drained at the top of every turn; follow-ups are
  drained only when the loop is otherwise done. A follow-up run re-drains
  steering at its first turn boundary.
- **SR-07.08** — Existing behavior is unchanged when no messages are queued: the
  turn sequence and emitted events for a plain run are identical to Stage 2.

### 3.3 Deferred within 7a

- **7a.2 — CLI capture during a turn.** Reading keystrokes while a turn is in
  flight (Enter → steer, Alt+Enter → follow-up) requires the interactive loop to
  pump input concurrently with `TurnRunner.RunAsync`. This is deferred to a
  follow-up increment and will be specified as its own acceptance criteria when
  implemented; the agent-level capability above is the prerequisite.

### 3.4 Acceptance criteria (7a)

- **AC7a.1** — A steering message enqueued while the agent is streaming is
  present in the **next** `ModelRequest.Messages` (asserted via a scripted
  runner that enqueues on its first call and inspects the second).
- **AC7a.2** — `QueueMode.All` injects two steering messages at the same
  boundary; `QueueMode.OneAtATime` injects them one per boundary (two turns).
- **AC7a.3** — A follow-up enqueued during a run causes a second turn after the
  first would have ended, and appears in the final request.
- **AC7a.4** — `MessageStartEvent` + `MessageEndEvent` are emitted for each
  injected message.
- **AC7a.5** — `PendingSteering` / `PendingFollowUp` reflect the queue sizes.
- **AC7a.6** — A run with no queued messages produces the same event sequence as
  before the change (regression).

## 4. Sub-stage 7b — Context compaction (auto + `/compact`)

- **Status:** DONE (2026-09-21).

**Goal.** Keep long sessions within the model's context window without losing
persisted history.

### 4.1 Contracts

`PiNet.Abstractions` — character-based token accounting helper:

```csharp
public static class TokenEstimator
{
    public static int Estimate(IReadOnlyList<ChatMessage> messages);
    public static int Estimate(ChatMessage message);
}
```

`PiNet.AgentCore` — usage tracking (previously discarded):

```csharp
// AgentState
public Usage? LastUsage { get; internal set; }   // from the most recent StreamEnd
public Usage TotalUsage { get; internal set; }   // accumulated across runs
```

`PiNet.Sessions` — compaction policy (uses the existing `SessionCompactor`):

```csharp
public sealed record CompactionSettings
{
    public long ContextWindow { get; init; }              // 0 = auto-compaction disabled
    public double Threshold { get; init; } = 0.75;        // fraction of the window
    public int PreserveRecentMessages { get; init; } = 4;
    public string DefaultInstruction { get; init; } = "...";
}

public sealed record CompactionOutcome(IReadOnlyList<ChatMessage> Messages, bool Compacted, int Summarized);

public sealed class ContextCompactor
{
    public ContextCompactor(SessionCompactor summarizer, CompactionSettings settings);
    public bool AutoEnabled { get; }
    public bool HasSummary { get; }
    public int SummarizedCount { get; }
    public int? LastPromptTokens { get; set; }

    public Task<IReadOnlyList<ChatMessage>> TransformAsync(
        IReadOnlyList<ChatMessage> messages, string systemPrompt, CancellationToken ct = default);
    public Task<CompactionOutcome> CompactAsync(
        IReadOnlyList<ChatMessage> messages, string systemPrompt, string? instructions,
        bool force = false, CancellationToken ct = default);
}
```

### 4.2 Behavior specs

- **SR-07b.01** — The estimator is character-based (~4 chars/token) plus a
  per-message overhead; it counts text, thinking and tool-call JSON, and charges
  a fixed 1000 tokens for an image block.
- **SR-07b.02** — The agent records each `StreamEnd.Usage` into
  `AgentState.LastUsage` and accumulates it into `TotalUsage`; usage is no longer
  discarded.
- **SR-07b.03** — Auto-compaction is opt-in (`--compact`) and active only when
  the model declares a non-zero `ContextWindow`.
- **SR-07b.04** — When the estimated size of the *current* context (summary +
  unsummarized tail) exceeds `Threshold * ContextWindow`, the prefix up to the
  last `PreserveRecentMessages` messages is summarized and replaced by a single
  summary message for the next request; the recent tail is kept verbatim.
- **SR-07b.05** — Auto-compaction is lossless on disk: `AgentState.Messages` and
  the JSONL history are never truncated. `TransformContext` only changes the view
  sent to the model.
- **SR-07b.06** — The summary is cached: consecutive turns do not re-summarize
  while the compacted view stays under the threshold. Further compaction folds
  the previous summary into the new one.
- **SR-07b.07** — `/compact [instructions]` forces compaction of the live
  context, persists the summary as a new session entry, and rewinds the
  persistence watermark so only new messages are appended afterwards. Custom
  instructions replace the default summary prompt.
- **SR-07b.08** — A failed summarization never aborts the session: the failure is
  reported (red line) and the context is left unchanged.
- **SR-07b.09** — When real prompt-token counts are available
  (`LastPromptTokens`), the threshold uses `max(estimate, LastPromptTokens)`;
  the character estimator is the fallback.

### 4.3 Acceptance criteria (7b)

- **AC7b.1** — `TokenEstimator` grows with text and counts tool-call JSON and
  images.
- **AC7b.2** — After a run, `State.LastUsage` equals the last `StreamEnd` usage
  and `TotalUsage` accumulates across turns.
- **AC7b.3** — A context below the threshold is returned unchanged and triggers
  no summarizer call.
- **AC7b.4** — A context above the threshold is compacted to `[summary, ...last
  N]`; the original list is untouched.
- **AC7b.5** — After compaction, a subsequent turn under the threshold performs
  no further summarizer call.
- **AC7b.6** — `/compact` forces compaction below the threshold, emits
  `(compacted N message(s) into a summary)`, persists the summary, and the JSONL
  keeps the full history.
- **AC7b.7** — Custom `/compact <instructions>` reaches the summarizer request.
- **AC7b.8** — With `ContextWindow == 0` auto-compaction is a no-op; `/compact`
  still works.

## 5. Sub-stage 7c — Reasoning / thinking levels

- **Status:** Verified (2026-10-15); see §11.

**Goal.** Expose the (already scaffolded) `ThinkingLevel` end-to-end.

Scope:

- Add a provider-agnostic `ThinkingLevel?` to `ModelRequest` / `ChatRequest`;
  `Agent` passes `State.ThinkingLevel`.
- Providers map it: OpenAI reasoning models → `reasoning_effort`
  (`minimal`/`low`/`medium`/`high`); Anthropic → `thinking.enabled` with a
  `budget_tokens` derived from the level (requires `max_tokens > budget`).
  Non-reasoning models ignore it (`ModelDefinition.SupportsReasoning` gates the
  UI, not the wire).
- Persist `ThinkingContent` from the assistant stream into `AgentState.Messages`
  (currently dropped) so multi-turn Anthropic thinking blocks round-trip.
- `/thinking <level>` slash command + `--thinking <level>` flag; surface reasoning
  deltas in the renderer (already emitted as `MessageUpdateEvent(IsThinking)`).

Acceptance criteria (draft): each level produces the expected provider payload;
`/thinking high` changes the next request; thinking content round-trips in a
session; `Off` sends no thinking fields.

## 6. Sub-stage 7d — Cross-provider handoff

- **Status:** Verified (2026-10-15); see §11.

**Goal.** Switch provider/model mid-session without losing context.

Scope:

- `/model <provider>/<id>` already rebuilds the agent with the current messages;
  make the handoff faithful by normalizing message content per target provider in
  `ConvertToLlm` (e.g. drop/replace `ThinkingContent` for a provider that does not
  support it, keep text + tool calls + tool results).
- Add a model picker for `/model` with no argument (list providers/models, accept
  a selection), replacing exact-string entry.

Acceptance criteria (draft): switching Claude→GPT mid-session keeps the
conversation coherent; provider-specific blocks that the target cannot accept are
normalized without data loss in the persisted session; the picker selects a
valid model.

## 7. Sub-stage 7e — Image input

- **Status:** Verified (2026-10-15); see §11. Clipboard/paste images remain
  deferred (optional per scope).

**Goal.** Let the user attach images to a prompt as vision content.

Scope:

- Ergonomic factories (`ChatMessage.User(params ContentBlock[])` or a builder) so
  image + text content can be constructed without dropping to the record ctor.
- `@image.png` file reference expansion in the editor: resolve relative to cwd,
  read bytes, base64-encode, attach as `ImageContent` with the inferred MIME type.
- Guard by `ModelDefinition.SupportsVision`; a non-vision model rejects the
  attachment with a clear message instead of sending it.
- Clipboard/paste image support is optional and platform-dependent; if not
  feasible with raw console APIs it is documented as deferred.

Acceptance criteria (draft): `@image.png` attaches an image block visible in the
request; a non-vision model refuses with a message; text-only prompts unchanged.

## 8. Sub-stage 7f — Export / import

- **Status:** Verified (2026-10-15); see §11.

**Goal.** Portable sessions.

Scope:

- Export the active branch to standalone styled HTML.
- Export to JSONL (reuse `SessionJson` / `SessionStore`).
- Import from JSONL into a new session file.
- CLI surface: `/export html <path>`, `/export jsonl <path>`, `/import <path>`,
  or equivalent flags.

Acceptance criteria (draft): HTML opens standalone and contains every message;
exported JSONL re-imports to an equivalent session; import of a malformed file
fails cleanly without corrupting existing sessions.

## 9. Sub-stage 7g — Settings

- **Status:** Verified (2026-10-15); see §11. Hot-reload of settings files during
  a session remains out of scope (restart picks changes up).

**Goal.** User/project configuration.

Scope:

- `~/.pinet/settings.json` (global, `PI_NET_HOME` override) and
  `<cwd>/.pi/settings.json` (project); JSON via `System.Text.Json`, missing =
  defaults, malformed = startup warning (non-fatal).
- Settings cover defaults: provider/model, thinking level, auto-compaction
  threshold, and interactive preferences; CLI flags and `/` commands override.
- `/settings` command to view/edit the effective settings.
- Fix `SessionManager` to resolve its root through `PI_NET_HOME`.

Acceptance criteria (draft): global + project settings merge with project
winning; `/settings` shows effective values; malformed file warns and falls back;
`PI_NET_HOME` relocates sessions.

## 10. Test plan

- **7a:** `PiNet.AgentCore.Tests` — steering at turn boundaries, `OneAtATime` vs
  `All`, follow-up extra turn, injected-message events, queue counters, thread
  safety, no-queue regression. Uses a scripted `IModelRunner` (existing pattern).
- **7b:** `PiNet.AgentCore.Tests` + `PiNet.Sessions.Tests` — threshold trigger,
  summary replacement, JSONL history preserved, `/compact` dispatch
  (`PiNet.Cli.Tests`).
- **7c:** `PiNet.Providers.Tests` — payload mapping per level; `PiNet.Cli.Tests`
  — `/thinking` and persistence.
- **7d:** `PiNet.Cli.Tests` — mid-session model switch + picker.
- **7e:** `PiNet.Cli.Tests` — `@image.png` expansion, vision guard.
- **7f:** `PiNet.Sessions.Tests` / `PiNet.Cli.Tests` — HTML + JSONL round-trip.
- **7g:** `PiNet.Cli.Tests` — settings merge/precedence/malformed.

## 11. Verification log

- **Status:** COMPLETE (2026-10-15) — 7a–7g verified; only the deferred items
  listed under 7a.2 and 7e (clipboard images) remain.

### 7a — Steering & follow-up (agent core)

- **Status:** DONE (2026-09-21)

Implemented as specified. Verification summary:

- **Build:** `dotnet build PiNet.slnx` → 0 warnings, 0 errors (net10.0).
- **Tests:** `dotnet test PiNet.slnx` → 287 passed, 0 failed (AgentCore 35, up
  from 27; the other projects unchanged: 47 + 18 + 13 + 110 + 64).
- **AC coverage (all in `PiNet.AgentCore.Tests/SteeringTests.cs`):**
  - AC7a.1 — `Steering_IsDeliveredAtNextTurnBoundary`: a steering message
    enqueued by the runner on its first call is absent from
    `Requests[0].Messages` and present in `Requests[1].Messages`.
  - AC7a.2 — `Steering_All_InjectsEveryMessageAtSameBoundary` (both in
    `Requests[1]`) and `Steering_OneAtATime_InjectsOnePerBoundary` (`s1` in
    `Requests[1]`, `s2` only in `Requests[2]`).
  - AC7a.3 — `FollowUp_StartsAnotherTurn_AfterRunWouldEnd`: two
    `GenerateCalls`; the follow-up appears in `Requests[1]`; the final
    assistant message is the second turn's.
  - AC7a.4 — `InjectedMessage_EmitsStartAndEndEvents`: a `MessageStartEvent`
    and a `MessageEndEvent` with role `User` and the injected text.
  - AC7a.5 — `PendingCounters_ReflectQueues` (2 steering / 1 follow-up).
  - SR-07.05 — `Steering_QueuedDuringFinalTurn_ContinuesRun`: a steering
    message enqueued during the model's final (no-tool) turn keeps the run
    alive for another turn instead of ending it.
  - AC7a.6 — regression preserved: `AgentLoopTests.PlainTextTurn_EmitsExactEventOrder`
    still asserts the exact Stage 2 event sequence (no extra events when both
    queues are empty).
  - Thread safety — `Steer_IsThreadSafe` (`Parallel.For` 200 enqueues →
    `PendingSteering == 200`).
- **Divergences from spec (intentional):** none. The contracts match §3.1
  (`QueueMode`, `AgentState.SteeringMode`/`FollowUpMode`,
  `Agent.Steer`/`FollowUp`/`PendingSteering`/`PendingFollowUp`). Injected
  messages use `ChatMessage.User(text)` and are appended through the existing
  internal `AgentState.AppendMessage`.
- **Files added:** `src/PiNet.AgentCore.Tests/SteeringTests.cs`.
- **Files modified:** `src/PiNet.AgentCore/Agent.cs` (queues, public API,
  `InjectQueuedAsync`, turn-boundary drain in `RunAsync`),
  `src/PiNet.AgentCore/AgentState.cs` (`QueueMode`, `SteeringMode`,
  `FollowUpMode`).
- **Deferred (7a.2):** CLI keystroke capture during an in-flight turn
  (Enter → steer, Alt+Enter → follow-up) still needs the interactive loop to
  pump input concurrently with `TurnRunner.RunAsync`; tracked in §3.3.

### 7b — Context compaction (auto + `/compact`)

- **Status:** DONE (2026-09-21)

Implemented and verified per §4. Verification summary:

- **Build:** `dotnet build PiNet.slnx` → 0 warnings, 0 errors (net10.0).
- **Tests:** `dotnet test PiNet.slnx` → 307 passed, 0 failed (AgentCore 44, up
  from 35; Sessions 20, up from 13; Cli 114, up from 110; Providers 18;
  Permissions 47; Extensions 64).
- **AC coverage:**
  - AC7b.1 — `TokenEstimatorTests` (character-based estimate; overhead, tool-call
    JSON, thinking, image): `EmptyList_IsZero`, `EmptyTextMessage_IsMessageOverheadOnly`,
    `GrowsWithLongerText`, `ImageBlock_IsLargeFixedCost`, `ToolCallContent_CountsArgumentJson`,
    `ThinkingContent_CountsText`.
  - AC7b.2 — `UsageTrackingTests`: `LastUsage_MatchesLastStreamEnd`,
    `TotalUsage_AccumulatesAcrossTurns`, `Usage_Feedback_IntoCompactorViaLastPromptTokens`.
  - AC7b.3 — `ContextCompactorTests.BelowThreshold_NoSummarizerCall_NoCompaction`.
  - AC7b.4 — `ContextCompactorTests.ForceCompaction_RewritesPrefix_KeepsTail_OriginalUntouched`.
  - AC7b.5 — `ContextCompactorTests.AfterCompaction_SubsequentUnderThreshold_NoFurtherSummary`.
  - AC7b.6 — `InteractiveLoopTests.Compact_ForcesCompaction_BelowThreshold_PersistsSummary_AndKeepsHistory`.
  - AC7b.7 — `InteractiveLoopTests.CompactWithCustomInstruction_InstructionReachesSummarizer`.
  - AC7b.8 — `InteractiveLoopTests.Compact_TooLittleContent_ReportsAlreadyCompact`
    (CLI dispatch) + `ContextCompactorTests.ContextWindowZero_AutoNoOp_ForceStillCompacts`
    (auto no-op when `ContextWindow == 0`; force still works) +
    `InteractiveLoopTests.CompactThenModelSwitch_SummaryFoldsAcrossSwitch`
    (context/summary preserved across a model switch and folded into a new summary).
- **Code added (new test files):**
  - `src/PiNet.AgentCore.Tests/TokenEstimatorTests.cs`
  - `src/PiNet.AgentCore.Tests/UsageTrackingTests.cs`
  - `src/PiNet.Sessions.Tests/ContextCompactorTests.cs`
  - `src/PiNet.Cli.Tests/InteractiveLoopTests.cs` (AC7b.6–7b.8 `/compact` tests).
- **Code modified (7b):**
  - `src/PiNet.Sessions/ContextCompactor.cs` — public `Summary` property, public
    4-arg constructor for state carry-over, `Reset()`.
  - `src/PiNet.Cli/Interact/InteractiveLoop.cs` — `RebuildAgent` compactor carry-over
    + watermark rewind; `CompactAsync` persists the summary and rewinds the watermark.
- **Divergences from spec (intentional):** none. Character-based estimator
  (~4 chars/token); threshold uses `max(estimate, LastPromptTokens)` when real
  prompt-token counts are available.
- **Open items:** none for 7b; 7c–7g verified (per §11).

### 7c — Reasoning / thinking levels

- **Status:** DONE (2026-10-15)

Implemented per §5. Verification summary:

- **Build:** `dotnet build PiNet.slnx` → 0 warnings, 0 errors.
- **Tests:** `dotnet test PiNet.slnx` → 370 passed, 0 failed (Providers 34,
  up from 18; AgentCore 52, up from 44; Cli 147, up from 133; Sessions 26;
  Permissions 47; Extensions 64).
- **AC coverage:**
  - AC7c.1 (payload per level) — `PiNet.Providers.Tests/ThinkingPayloadTests`:
    `OpenAi_ThinkingOff_SendsNoReasoningEffort` (null + `Off`),
    `OpenAi_ThinkingLevel_MapsToReasoningEffort` (minimal/low/medium/high ×3),
    `Anthropic_ThinkingOff_SendsNoThinking`,
    `Anthropic_ThinkingOn_SendsEnabledWithBudgetBelowMaxTokens`,
    `Anthropic_MediumWithLargeMaxTokens_UsesLevelBudget`,
    `Anthropic_HighLevel_ClampsBudgetBelowMaxTokens`,
    `Anthropic_TinyMaxTokens_DisablesThinking`,
    `Mapper_ClampsBudgetsToTheAllowedRange`, `Mapper_OpenAiEffort_IsNullOnlyWhenOff`.
  - AC7c.2 (`/thinking high` changes the next request) —
    `InteractiveLoopTests.Thinking_Command_SetsLevel_ForNextRequest` (asserts
    `ChatRequest.ThinkingLevel == High`); no-arg shows the current level;
    invalid level is rejected and keeps the previous one; the level survives a
    `/model` switch.
  - AC7c.3 (thinking round-trips in a session) —
    `AgentCore.Tests/ThinkingTests.ThinkingDeltas_AreCapturedAsThinkingContent`
    (assistant message carries `ThinkingContent`);
    `Sessions.Tests/SessionExportTests.Jsonl_RoundTrips_AllEntries_AndBlockKinds`
    (thinking/image/tool blocks survive the JSONL round-trip).
  - AC7c.4 (`Off` sends no thinking fields) — covered by the Off/unset payload
    tests above; `State_ThinkingLevel_IsPassedToTheRunner` pins the plumbing.
- **Code added:** `src/PiNet.Abstractions/Providers/ThinkingMapper.cs`
  (moved from AgentCore and rewritten with typed helpers:
  `ToOpenAiReasoningEffort`, `ToAnthropicBudgetTokens`).
- **Code modified:** `OpenAIChatProvider` (`reasoning_effort` in the payload,
  null-ignoring serializer options, `BuildPayloadJson` internal for wire tests),
  `AnthropicChatProvider` (`thinking: {type: enabled, budget_tokens}` +
  null-ignoring options), `PiNet.Providers.csproj` (`InternalsVisibleTo`),
  `SlashCommand`/`InteractiveLoop` (`/thinking [level]`), `Program.cs`
  (`--thinking <level>` for print/agent/interactive modes).
- **Divergences from spec (intentional):** OpenAI effort for reasoning models
  is sent whenever a level is set (the spec gates UI, not the wire); `XHigh`
  and `Max` clamp to `high` (no higher OpenAI value); Anthropic budgets are the
  documented minimum `1024`, doubling per level, clamped below `max_tokens`
  (thinking is silently unavailable when `max_tokens <= 1024`).

### 7d — Cross-provider handoff

- **Status:** DONE (2026-10-15)

Implemented per §6. Verification summary:

- **Build/tests:** same run as 7c (370 passed, 0 failed).
- **AC coverage:**
  - AC7d.1 (switch keeps the conversation coherent) —
    `InteractiveLoopTests.HandoffToAnthropicStyleProvider_KeepsThinking` and
    `HandoffToOpenAi_DropsThinking_FromRequest_ButKeepsSession` (text, tool
    calls and results always survive; the last turn's messages reach the new
    provider).
  - AC7d.2 (normalize without data loss in the persisted session) —
    the OpenAi handoff test re-reads the session entries and asserts the
    original `ThinkingContent` entry is still persisted.
  - AC7d.3 (picker selects a valid model) —
    `InteractiveLoopTests` `/model` listing + switch tests
    (`CompactThenModelSwitch_SummaryFoldsAcrossSwitch`,
    `Thinking_Level_Survives_ModelSwitch`).
  - Unit coverage — `AgentCore.Tests/MessageNormalizerTests` (same-instance
    pass-through for Anthropic and for histories without thinking; thinking
    stripping keeps text/tool blocks; thinking-only messages become an empty
    text block; original list untouched).
- **Code added:** `src/PiNet.AgentCore/MessageNormalizer.cs`
  (`ForProvider(messages, providerId)`).
- **Code modified:** `InteractiveLoop.RebuildAgent` and
  `Program.AgentModeAsync` wire `ConvertToLlm = msgs =>
  MessageNormalizer.ForProvider(msgs, providerId)`.
- **Divergences from spec (intentional):** the target "accepts thinking
  history" test is the provider id containing `anthropic` (built-ins and
  anthropic-compatible custom providers); Anthropic requests currently do not
  re-send thinking blocks on the wire (no signature support), so keeping them
  in the message list is lossless but inert until that lands.

### 7e — Image input

- **Status:** DONE (2026-10-15)

Implemented per §7. Verification summary:

- **Build/tests:** same run as 7c (370 passed, 0 failed).
- **AC coverage:**
  - AC7e.1 (`@image.png` visible in the request) —
    `InteractiveLoopTests.ImageToken_BecomesImageBlock_WhenModelSupportsVision`
    (base64 bytes + inferred `image/png` in the user message; text keeps the
    rest of the prompt); `ImageToken_MissingFile_StaysAsText` (unresolvable
    tokens are left in the text verbatim).
  - AC7e.2 (non-vision model refuses with a message) —
    `ImageToken_NonVisionModel_SkipsAttachment_WithMessage` (attachment
    dropped, yellow `[image] … does not accept image input` notice, text-only
    request).
  - AC7e.3 (text-only prompts unchanged) — the whole suite (prompt text is
    preserved byte-for-byte when no image token is extracted; regression
    caught and fixed for trailing whitespace during implementation).
- **Code modified:** `InteractiveLoop.RunTurnAsync` (expansion + vision guard +
  `ChatMessage` with image blocks), `ExpandImagesAsync` (regex-based token
  extraction, relative to session cwd, warns on unreadable files),
  `TurnRunner.RunAsync(ChatMessage, …)` overload.
- **Divergences from spec (intentional):** a non-vision model skips the
  attachments and still sends the prompt text (clear notice instead of failing
  the turn); clipboard/paste images remain deferred as documented in §7.

### 7f — Export / import

- **Status:** DONE (2026-10-15)

Implemented per §8. Verification summary:

- **Build/tests:** same run as 7c (370 passed, 0 failed).
- **AC coverage:**
  - AC7f.1 (HTML opens standalone and contains every message) —
    `SessionExportTests.Html_Export_WritesStandaloneFile_WithActiveBranch`
    (doctype → `</html>`, thinking/tool/image blocks rendered) and
    `Html_EscapesMarkup`; CLI: `Export_Html_WritesStandaloneFile`.
  - AC7f.2 (JSONL re-imports to an equivalent session) —
    `SessionExportTests.Jsonl_RoundTrips_AllEntries_AndBlockKinds` (entry-by-
    entry equality via `SessionJson.WriteEntryText`), `Jsonl_String_RoundTrips`,
    `Import_ImportedSession_IsIndependent_WithFreshId`; CLI:
    `Export_Jsonl_ThenImport_RoundTrips_AndContinuesSession` (export → import
    in a second loop → next turn sees the imported messages).
  - AC7f.3 (malformed file fails cleanly) —
    `SessionExportTests.Import_MalformedLine_Throws`; CLI:
    `Import_MalformedFile_FailsCleanly_WithoutCorruptingCurrentSession`
    (red `[import]` message, session keeps working) and
    `Import_MissingFile_ReportsNotFound`; `Export_MissingFormat_PrintsUsage`.
- **Code added:** `SessionJson.WriteEntryText` (shared entry serialization).
- **Code modified:** `SlashCommand`/`InteractiveLoop` (`/export jsonl|html
  <path>`, `/import <path.jsonl>`; CLI import persists a fresh session id and
  rebuilds the agent), `SessionStore`/`SessionExport` use the shared serializer.
- **Divergences from spec (intentional):** JSONL exports *all* entries
  (history + branches) while HTML renders the active branch — matches the
  "portable/round-trip" intent; `/export` without a path writes
  `pinet-<session-id>.<format>` in the session cwd.

### 7g — Settings

- **Status:** DONE (2026-10-15)

Implemented per §9. Verification summary:

- **Build/tests:** same run as 7c (370 passed, 0 failed; 19 of the Cli tests
  are `SettingsTests`).
- **AC coverage:**
  - Global + project merge with project winning —
    `SettingsTests` load/merge cases; malformed file warns and falls back
    (`Load_InvalidThinking_Ignored_WithWarning`).
  - `/settings` shows/sets effective values (`(global thinkingLevel = High …)`
    output assertions).
  - `PI_NET_HOME` relocates sessions (end-to-end `SessionManager` test).
  - Settings defaults surface in the loop (provider/model/thinking/verbose).
- **Code added:** `src/PiNet.Cli/Settings/SettingsManager.cs`,
  `src/PiNet.Cli.Tests/SettingsTests.cs`.
- **Code modified:** `SessionManager` (root under `PI_NET_HOME`),
  `Program.cs` (settings defaults + flag precedence), `InteractiveLoop`
  (settings banner, defaults), obsolete `IgnoreNullValues` replaced with
  `DefaultIgnoreCondition`.
- **Open items:** hot-reload during a running session is out of scope
  (restart picks changes up).
