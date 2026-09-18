# ADR-0002 — Unified StreamEvent Discriminated Union

- **Status:** Accepted
- **Date:** 2026-09-09
- **Applies to:** `PiNet.Abstractions.Streaming`

## Context

OpenAI, Anthropic, and future providers stream radically different chunks:
`delta.content`, `content_block_delta { text }`, `input_json_delta`, etc.
Consumers (agent loop, TUI) must not care which provider is behind the wire.

## Decision

All providers `yield` a single sealed record hierarchy rooted at `StreamEvent`
(`StreamStart`, `TextDelta`, `ThinkingDelta`, `ToolCallStart/Delta/End`,
`StreamEnd`, `StreamError`). Match order is deterministic (`switch` on type).

## Consequences

- Provider knowledge stays isolated in `Providers`.
- Streaming tool-call JSON fragments scale to a shared `Newtonsoft`-free JSON
  accumulation path.
- The union grows with features (e.g., image blocks) as new sealed records.

## Revisit when

- A provider emits a content shape that does not fit any event; add a new record
  rather than widening an existing one's payload.