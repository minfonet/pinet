# Specification 08 — Stage 7: Advanced Features

- **Spec status:** In Progress
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
| 7b | Context compaction (auto + `/compact`) | Planned |
| 7c | Reasoning / thinking levels | Planned |
| 7d | Cross-provider handoff | Planned |
| 7e | Image input | Planned |
| 7f | Export / import | Planned |
| 7g | Settings | Planned |

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

**Goal.** Keep long sessions within the model's context window.

Scope:

- Wire `SessionCompactor` into the agent through `Agent.TransformContext`: before
  each LLM request, if the estimated prompt size exceeds a threshold derived from
  `ModelDefinition.ContextWindow`, summarize the oldest messages and replace them
  with a single summary message.
- Track token usage from `StreamEnd.Usage` at the agent layer (currently
  discarded) so the threshold can use real counts, with a character-based
  estimator as fallback.
- `/compact [instructions]` slash command: manual compaction of the current
  session; custom instructions replace the default summary prompt.
- Auto-compaction is opt-in/configurable; lossy compaction must never delete
  persisted JSONL history (the summary is appended as a new entry).

Acceptance criteria (draft): overflow triggers compaction and the request fits;
`/compact` shrinks the live context while JSONL keeps full history; custom
instructions reach the summarizer; a session under the threshold is unchanged.

## 5. Sub-stage 7c — Reasoning / thinking levels

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

- **Status:** IN PROGRESS

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
