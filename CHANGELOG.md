# Changelog

All notable changes to Pi.NET are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
Progress per stage (with verification logs) lives in
[`STATUS.md`](STATUS.md) and [`docs/spec/`](docs/spec/); this file is the
release-notes style view.

## [Unreleased]

Nothing yet. The first release is `0.1.0` below; the package is packed and
installed locally (tool + self-contained binaries verified). Pi.NET is
**not published to NuGet.org** — releases are distributed as GitHub Release
assets (per-RID binaries + the packed global-tool `.nupkg`). See “Deferred” at
the bottom for documented items that will land in later releases.

## [0.1.0] — 2026-10-16

Initial complete implementation: stages 1–8 of
[`IMPLEMENTATION_PLAN.md`](IMPLEMENTATION_PLAN.md), product features
[spec 01–08](docs/spec/) verified against the running CLI.

**Verified state:** `dotnet build PiNet.slnx` clean (0 warnings, 0 errors);
`dotnet test PiNet.slnx` — 396 passing, 0 failing (40 Providers + 49 Permissions
+ 52 AgentCore + 27 Sessions + 159 Cli + 69 Extensions).

### Stage 1 — Foundation & Unified LLM abstraction

- Zero-dependency raw-HTTP providers with SSE streaming: OpenAI (chat
  completions) and Anthropic (messages), plus the deterministic `Faux` provider.
- Unified `StreamEvent` discriminated union (text/thinking deltas, tool calls,
  usage, stream end) shared by all providers ([ADR-0002](docs/adr/0002-stream-event-model.md)).
- `ModelRegistry` with per-provider catalogs; credential resolution from the
  environment; `ChatService` facade over `IModelRunner` ([ADR-0003](docs/adr/0003-imodelrunner-in-abstractions.md)).

### Stage 2 — Agent Core

- Deterministic tool-calling loop (`Agent`) with events (`AgentStartEvent`,
  message start/update/end, tool start/end, `AgentEndEvent`), abort, and error
  propagation.
- Parallel/sequential tool execution; `BeforeToolCall`/`AfterToolCall` hooks;
  `TransformContext`/`ConvertToLlm` seams.
- Built-in tools: `read`, `write`, `edit`, `bash`; token estimation and usage
  tracking (`LastUsage`/`TotalUsage`).

### Stage 3 — Sessions

- Append-only JSONL session tree (`Session`, `SessionEntry`, `SessionStore`,
  `SessionJson`); `SessionManager` for create/load/latest/list/fork with an
  append watermark; id/prefix resolution.
- LLM compaction (`SessionCompactor`) and lossless on-disk history (branches
  preserved).

### Stage 4 — Interactive CLI loop

- Zero-dependency terminal UI: line editor (cursor, history, Ctrl+C semantics)
  and slash commands (`/help`, `/model`, `/sessions`, `/resume`, `/fork`, …).
- Print mode (`-p`), one-shot agent mode (`--agent`), interactive mode (`-i`),
  session flags (`--session`, `--fork`, `-c`, `--no-session`), `--list-models`.

### Stage 5 — Permissions & tool security

- Rule resolver with safety floor and deny-sticky semantics; path globs; config
  files (`~/.pinet/permissions.json`, project `.pi/permissions.json`).
- Interactive permission prompt, `--approve`/`--no-approve`, `/trust`,
  `TrustStore`.

### Stage 6 — Extensions, skills, templates & packages

- Extension host compiling plain C# files at startup with the SDK’s Roslyn
  (`csc.dll`), no NuGet dependencies; extension tools and commands.
- Skills (`/skill:…`), prompt templates, context files, system-prompt builder.
- Package manager and CLI: `pinet install <path|npm:|git:>`, `list`, `remove`,
  `update`; manual smoke script (`scripts/smoke-stage6.ps1`).
- [Spec 07](docs/spec/07-local-llm-providers.md): local/custom LLM providers via
  `~/.pinet/models.json` (`openai-completions` / `anthropic-messages`, `$ENV`
  credentials, merge-by-model-id with built-ins).

### Stage 7 — Advanced features ([spec 08](docs/spec/08-stage-7-advanced-features.md))

- **7a Steering & follow-up** — `Steer`/`FollowUp` queues with `OneAtATime`/`All`
  modes and turn-boundary draining; the interactive loop captures keystrokes
  while a turn is in flight (Enter → steer, Alt+Enter → follow-up, Ctrl+C
  cancels the turn, unsubmitted text carries over to the next prompt); the
  permission prompt pauses the capture via `InputArbiter`.
- **7b Context compaction** — auto-compaction by token threshold
  (`--compact`, `--compact-threshold`, `--compact-preserve`) and manual
  `/compact [instructions]`; the summary is persisted and the JSONL history
  stays lossless.
- **7c Thinking levels** — `/thinking <Off…Max>` and `--thinking`; OpenAI
  `reasoning_effort` and Anthropic `thinking.enabled` + `budget_tokens`
  (clamped below `max_tokens`); thinking deltas render dimmed and are persisted
  with the session.
- **7d Cross-provider handoff** — `/model` switch (with picker listing) keeps the
  conversation coherent; `MessageNormalizer` (wired via `ConvertToLlm`) drops
  thinking blocks for target providers that cannot accept them while the
  persisted session keeps them.
- **7e Image input** — `@photo.png` tokens expand to base64 image blocks
  (png/jpg/jpeg/gif/webp/bmp/svg, resolved against the working directory);
  non-vision models skip the attachment with a clear notice.
- **7f Export / import** — `/export jsonl <path>` (all entries) and
  `/export html <path>` (standalone styled active branch); `/import <path.jsonl>`
  continues the imported conversation in a new persisted session; malformed
  files fail cleanly.
- **7g Settings** — `~/.pinet/settings.json` (`PI_NET_HOME`) merged with
  `<cwd>/.pi/settings.json` (project wins), `/settings` view/edit, CLI flag
  precedence.

### Stage 8 — Packaging, CI & release

- `dotnet tool` packaging: `PackageId=PiNet`, `ToolCommandName=pinet`,
  `AssemblyName=pinet`, README packed; install scripts (`install.ps1`,
  `install.sh`).
- `pinet update --self` (delegates to `dotnet tool update -g PiNet` with clear
  guidance when not installed as a global tool).
- CI workflow (build + test) and release workflow (tag `v*` → tests, pack with
  the tag version, self-contained `win-x64`/`linux-x64`/`osx-arm64` single-file
  binaries, GitHub release assets: tool `.nupkg` + per-RID zips);
  `scripts/publish.ps1` for local per-RID publishes.
- Distribution decision: **no NuGet.org publication** — install from the
  packed `.nupkg` (local feed or GitHub Release asset) or use a per-RID
  binary; `install.ps1`/`install.sh` now install from a local package feed.

### Documentation

- `docs/extensions.md` rewritten as a complete **creating Pi-style
  extensions** guide: quickstart, host pipeline, API reference, examples,
  testing, packaging, skills/templates/context files, Pi parity notes.
- New `docs/testing-with-a-real-llm.md`: provider setup (OpenAI, Anthropic,
  custom `models.json`), print/agent/interactive checks, sessions, the Stage 6
  smoke script, a verification checklist and troubleshooting.

### Fixed

- **Tool calling against real providers.** `OpenAIChatProvider` (SSE) and
  `AnthropicChatProvider` only emitted `ToolCallDelta`, never
  `ToolCallStart`/`ToolCallEnd`; the agent loop assembles tool calls from the
  full SR-01.05 sequence, so every real tool call was silently dropped (the
  model's call rendered as plain text or the turn ended with no answer). Both
  providers now emit the complete sequence, covered by `StreamingToolCallTests`
  (canned OpenAI-compatible and Anthropic wire streams) and a real llama.cpp
  agent run that executes `read_file`.
- Live agent rendering repeated streamed content (thinking and text were
  printed once while streaming and again when the line was flushed). New
  `AgentStreamPrinter` terminates already-streamed lines with a newline instead
  of re-printing them; used by one-shot `--agent` and the interactive loop.
- `--agent` combined with `-p/--print` silently ran print mode (no tools,
  no agent loop) because print won the dispatch; `--agent` now takes
  precedence and `-p` only selects print mode when `--agent` is absent.
- Every `pinet install|list|remove|update` invocation printed usage because the
  top-level dispatch sliced the args before `PackageCommands`; regression
  covered by `PackageManagerCliTests.Program_DispatchesPackageSubcommands_WithFullArgs`.
- `pinet install npm:<pkg>` failed on Windows with “cannot find the file”: npm
  ships as `npm.cmd` and `ProcessStartInfo` does not apply PATHEXT. CLI tools
  are now resolved through PATH + PATHEXT (`PackageManager.ResolveExecutable`).
- Package reinstall/removal failed on Windows with access denied when the
  installed tree contained read-only files (git objects):
  `PackageManager.DeleteDirectory` clears the attribute first, `git:` clones no
  longer copy `.git` metadata into the package, and the post-clone temp
  directory is cleaned up the same way.
- `scripts/smoke-stage6.ps1`: the real-LLM step now runs in agent mode (the old
  `-p` call never sent tools), honors `PI_NET_PROVIDER`/`PI_NET_MODEL` for local
  providers (SKIP when neither keys nor a provider are set), fails on a
  non-zero agent exit, survives a missing npm binary (SKIP instead of aborting),
  and records every check in the summary (`$script:` scope bug).
- `SessionJson` gained a shared `WriteEntryText` serializer (session files,
  exports and tests round-trip identically).
- Settings manager: replaced the obsolete `JsonIgnoreCondition`/`IgnoreNullValues`
  API to keep the build warning-free.

### Deferred

Documented and intentionally not part of 0.1.0:

- 7e — clipboard/paste image input (platform-dependent, optional per spec).
- 7g — settings hot-reload during a running session (restart picks changes up).
- Stage 6 network smoke tests (`npm:`/`git:` installs, extension tool invoked by
  a real LLM).
- SBOM/signing decisions (telemetry: none, per the zero-dependency posture).
