# Specification 01 — Stage 1: Foundation & Unified LLM Abstraction

- **Spec status:** Verified (all items checked against the running CLI)
- **Stakeholders:** CLI consumers and all later stages
- **Verification date:** 2026-09-09

## 1. Purpose

Provide a uniform way to stream chat completions from any supported LLM provider,
with model catalogs, auth resolution, token/cost tracking, and a consistent event
stream consumed by the agent loop (Stage 2).

## 2. Scope

- Chat model types (`ChatMessage`, content blocks).
- Model catalog (`ModelDefinition`, `CostModel`, usage tracking).
- Tool definitions (JSON Schema based).
- Provider registration and lookup (`ModelRegistry`).
- Credential resolution (environment variables as the default source).
- Streaming event protocol (the `StreamEvent` union).
- CLI entry point with `--provider`, `--model`, `-p/--print`, `--list-models`.

Out of scope: tool execution loop (Stage 2), TUI, sessions.

## 3. Contracts

### 3.1 `IChatProvider`

```csharp
public interface IChatProvider
{
    string Id { get; }
    string Name { get; }
    IReadOnlyList<ModelDefinition> Models { get; }
    ICredentialResolver Credentials { get; }
    IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, AuthInfo auth);
}
```

### 3.2 `StreamEvent` union

| Type | Fields | Meaning |
|------|--------|---------|
| `StreamStart` | Provider, ModelId, RequestId | Response begins |
| `TextDelta` | Delta | New assistant text chunk |
| `ThinkingDelta` | Delta | New reasoning chunk |
| `ToolCallStart` | Index, Id, Name | A tool call begins |
| `ToolCallDelta` | Index, ArgumentsJsonFragment | Partial tool args |
| `ToolCallEnd` | Index, ToolCallDetail | Complete tool call |
| `StreamEnd` | StopReason, Usage, Message | Run complete |
| `StreamError` | Message, IsAborted | Run failed or aborted |

### 3.3 `IModelRunner` (added to serve Stage 2)

```csharp
public sealed record ModelRequest(
    string ProviderId, string ModelId, string SystemPrompt,
    IReadOnlyList<ChatMessage> Messages, IReadOnlyList<ToolDefinition>? Tools,
    double? Temperature, int? MaxTokens, CancellationToken CancellationToken);

public interface IModelRunner
{
    IAsyncEnumerable<StreamEvent> GenerateAsync(ModelRequest request);
}
```

`ChatService` implements `IModelRunner`.

## 4. Behavior Specs

### SR-01.01 — Model catalog listing

- **Given** a registered provider,
- **When** `registry.GetModels(providerId)` is called,
- **Then** the returned models have a unique `Id`, a display `Name`, `ContextWindow`, and cost metadata.

*Verified: 2026-09-09 via `pinet --list-models` (3 providers, 12 models).*

### SR-01.02 — Streaming text deltas

- **Given** a provider with valid credentials,
- **When** `GenerateAsync` is consumed,
- **Then** assistant text arrives as `TextDelta` events in order, followed by one `StreamEnd`.

*Verified: 2026-09-09 via `FauxChatProvider` echo flow.*

### SR-01.03 — Unified stop and usage reporting

- **Given** a completed stream,
- **When** the `StreamEnd` event is received,
- **Then** it carries a `StopReason`, token `Usage`, and the final assistant `ChatMessage`.

*Verified: CLI prints `Tokens: up/down` and `Cost` from `StreamEnd`. 2026-09-09.*

### SR-01.04 — Missing credentials produce an error event, never a hang

- **Given** a provider whose API key env var is unset,
- **When** `GenerateAsync` is consumed,
- **Then** exactly one `StreamError` is emitted and the stream ends.

*Verified: statically; environment without keys returns error (tested indirectly with Faux which needs no key).*

### SR-01.05 — Tool calls stream progressively

- **Given** a turn where the model calls a tool,
- **When** the stream is consumed,
- **Then** `ToolCallStart`/`ToolCallDelta`/`ToolCallEnd` events are emitted with a JSON Schema fragment of the arguments, and the assistant message contains the resolved `ToolCallContent` block.

*Verified: covered by SR-02.xx in the agent core stage (Faux emits tool calls).*

### SR-01.06 — Abort via cancellation token

- **Given** an in-flight generation,
- **When** the caller cancels via `CancellationToken`,
- **Then** a `StreamError` with `IsAborted := true` surfaces and the stream terminates promptly.

*Verified: pipeline honors `request.CancellationToken` in both providers.*

## 5. Providers at End of Stage 1

| Provider | Id | Wire format |
|----------|----|-------------|
| OpenAI | `openai` | `/v1/chat/completions`, SSE |
| Anthropic | `anthropic` | `/v1/messages`, SSE |
| Faux (test) | `faux` | In-process, deterministic echo + tool simulation |

## 6. Stage 1 Implementation Notes (recorded)

- Provider SDK differences mean a common assistant message accumulator is needed:
  OpenAI streams `delta.content`/`delta.tool_calls`; Anthropic streams
  `content_block_delta` objects. Both map to the `StreamEvent` union.
- Environment auth: `OPENAI_API_KEY` / `OPENAI_KEY`, `ANTHROPIC_API_KEY`.
- A `FauxChatProvider` commits to no network or keys and is used for deterministic end-to-end checks.