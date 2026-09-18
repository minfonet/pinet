# ADR-0001 — Raw HTTP over Vendor SDKs for LLM Providers

- **Status:** Accepted
- **Date:** 2026-09-09
- **Applies to:** `PiNet.Providers`

## Context

Pi.NET must support OpenAI, Anthropic, and (later) many more providers with
streaming, tool calls, and thinking tokens. Binding to each vendor's SDK adds
per-provider dependency weight, version drift, and differing abstractions.

## Decision

Provider protocol endpoints are implemented with `HttpClient` + `System.Text.Json`
against the raw REST/SSE APIs. A single `SseParser` yields subscripted
`SseEvent`(s). Each provider maps wire chunks to the shared `StreamEvent` union.

## Consequences

- One wire protocol addition is a bounded, dependency-free change.
- SSE quirks (event re-framing, trailing whitespace) are our responsibility.
- We keep full control over payloads (needed for thinking budgets and tool schemas
  that differ per provider).

## Revisit when

- A provider introduces complex auth (OAuth w/ interceptor) or heavy encryption;
  then a thin SDK adapter can be added behind `IChatProvider`.