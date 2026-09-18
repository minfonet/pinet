# ADR-0003 — IModelRunner in Abstractions

- **Status:** Accepted
- **Date:** 2026-09-09
- **Applies to:** `PiNet.Abstractions`, `PiNet.Providers`, `PiNet.AgentCore`

## Context

Stage 2 adds `PiNet.AgentCore`. The agent loop needs to call an LLM, but should
not depend on provider details. Coupling `AgentCore -> Providers` would drag
`HttpClient`, wire formats, and catalogs into every consumer of the agent.

## Decision

`Abstractions` defines `IModelRunner`:

```csharp
public interface IModelRunner
{
    IAsyncEnumerable<StreamEvent> GenerateAsync(ModelRequest request);
}
```

`ChatService` (in `Providers`) implements it. `AgentCore` references only
`Abstractions`; the CLI composes `ChatService -> Agent`.

## Consequences

- `AgentCore` remains testable with a scripted in-memory runner (no network).
- A remote or proxied runner can be swapped in later without touching `AgentCore`.
- `ModelRequest` centralizes per-call options (provider, model, system prompt,
  messages, tools, temperature, max tokens, cancellation).

## Revisit when

- Session-level options (thinking budgets, cache control) become first-class;
  extend `ModelRequest` and the pipeline in one change.