# Changelog

All notable changes to Pi.NET are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
Progress per stage (with verification logs) lives in
[`STATUS.md`](STATUS.md) and [`docs/spec/`](docs/spec/); this file is the
release-notes style view.

## [Unreleased]

Nothing yet. The first release is `0.1.0` below; the package is packed and
installed locally (tool + self-contained binaries verified), the NuGet.org
publication is still pending. See “Deferred” at the bottom for documented items
that will land in later releases.

## [0.1.0] — 2026-10-16

Initial complete implementation: stages 1–8 of
[`IMPLEMENTATION_PLAN.md`](IMPLEMENTATION_PLAN.md), product features
[spec 01–08](docs/spec/) verified against the running CLI.

**Verified state:** `dotnet build PiNet.slnx` clean (0 warnings, 0 errors);
`dotnet test PiNet.slnx` — 371 passing, 0 failing (34 Providers + 47 Permissions
+ 52 AgentCore + 26 Sessions + 148 Cli + 64 Extensions).

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
  modes and turn-boundary draining. (CLI keystroke capture deferred, see below.)
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
  binaries, NuGet push when `NUGET_API_KEY` is set, GitHub release assets);
  `scripts/publish.ps1` for local per-RID publishes.

### Fixed

- Every `pinet install|list|remove|update` invocation printed usage because the
  top-level dispatch sliced the args before `PackageCommands`; regression
  covered by `PackageManagerCliTests.Program_DispatchesPackageSubcommands_WithFullArgs`.
- `SessionJson` gained a shared `WriteEntryText` serializer (session files,
  exports and tests round-trip identically).
- Settings manager: replaced the obsolete `JsonIgnoreCondition`/`IgnoreNullValues`
  API to keep the build warning-free.

### Deferred

Documented and intentionally not part of 0.1.0:

- 7a.2 — in-flight keystroke capture during a running turn (Enter → steer,
  Alt+Enter → follow-up).
- 7e — clipboard/paste image input (platform-dependent, optional per spec).
- 7g — settings hot-reload during a running session (restart picks changes up).
- Stage 6 network smoke tests (`npm:`/`git:` installs, extension tool invoked by
  a real LLM).
- First NuGet.org publication; SBOM/signing decisions (telemetry: none, per the
  zero-dependency posture).
