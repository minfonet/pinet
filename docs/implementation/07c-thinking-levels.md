# Stage 7c - Thinking levels (Implementation Notes)

- `PiNet.Abstractions/Providers/ThinkingMapper.cs`: provider-agnostic `ThinkingLevel`
  → wire mapping.
  - `ToOpenAiReasoningEffort`: Off/null → none; Minimal/Low/Medium → same;
    High/XHigh/Max → `high` (OpenAI's highest supported effort).
  - `ToAnthropicBudgetTokens(level, maxTokens)`: `budget_tokens` = 1024 · 2^(level-1)
    clamped to `maxTokens - 1`; unavailable when `maxTokens <= 1024`.
- `OpenAIChatProvider` payload gains `reasoning_effort` (omitted when off);
  null-ignoring serializer options; `BuildPayloadJson` is `internal` for wire tests
  (`InternalsVisibleTo PiNet.Providers.Tests`).
- `AnthropicChatProvider` payload gains `thinking: { type: "enabled", budget_tokens }`
  (omitted when off/unavailable).
- Capture: `Agent` appends a `ThinkingContent` block built from `ThinkingDelta`
  events to the assistant message; persisted in the session JSONL.
- CLI: `/thinking [level]` (shows current level, rejects unknown levels) and
  `--thinking <level>`; the level lives in `AgentState.ThinkingLevel` and survives
  `/model` switches.
- Tests: `PiNet.Providers.Tests/ThinkingPayloadTests`,
  `PiNet.AgentCore.Tests/ThinkingTests`, `InteractiveLoopTests` thinking section.
