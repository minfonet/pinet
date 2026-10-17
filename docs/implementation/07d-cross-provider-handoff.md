# Stage 7d - Cross-provider handoff (Implementation Notes)

- `PiNet.AgentCore/MessageNormalizer.cs`: adapts the LLM-bound history to the
  target provider.
  - Providers whose id contains `anthropic` accept assistant thinking blocks in
    history (returned unchanged).
  - Every other target gets `ThinkingContent` stripped from a *copy*; text, tool
    calls and tool results are kept; thinking-only messages become an empty text
    block.
  - The original message list is never mutated, so the persisted session keeps
    the full history (spec: normalize without data loss).
- Wired via `ConvertToLlm` in `InteractiveLoop.RebuildAgent` and
  `Program.AgentModeAsync`; `/model` switches (and the model picker: numbered
  list, selection by number or `provider/id`) rebuild the agent against the new
  provider.
- Tests: `PiNet.AgentCore.Tests/MessageNormalizerTests`,
  `InteractiveLoopTests` handoff tests (drop for OpenAI targets, keep for
  Anthropic-style ids, session file still contains the thinking entry) and
  picker tests (list + select by number/`provider/id`, cancel, invalid number,
  `/models` alias).
- Note: Anthropic requests do not re-send thinking blocks on the wire yet
  (no signature support in `ThinkingContent`); keeping them in the list is
  lossless but inert until signatures land.
