# Pi.NET — Implementation Plan

> A .NET 10 port of the [Pi Agent Harness](https://github.com/earendil-works/pi), providing a minimal terminal coding agent with a unified LLM API, tool-calling loop, session management, and extensibility.

---

## Repository Analysis Summary

The original Pi project (TypeScript/Node.js) is structured as a monorepo with the following core packages:

| Package | Purpose |
|---------|---------|
| `@earendil-works/pi-ai` | Unified multi-provider LLM API with streaming, tool definitions (TypeBox), auth resolution, token/cost tracking |
| `@earendil-works/pi-agent-core` | Stateful agent runtime: tool execution loop, event streaming, steering/follow-up messages, context compaction |
| `@earendil-works/pi-coding-agent` | Interactive coding agent CLI: TUI, sessions (JSONL with branching), extensions, skills, prompt templates, themes |
| `@earendil-works/chord` | Application-composition runtime: plugins/facets, services, replicated state, delta tracking |
| `@earendil-works/pi-tui` | Terminal UI library with differential rendering |

**Key design principles from Pi:**
- Aggressively extensible — no sub-agents, no permission popups, no plan mode baked in. Everything is an extension.
- Provider-agnostic: 30+ LLM providers behind a unified streaming interface.
- Tool calling is first-class: TypeBox schemas with validation, parallel/sequential execution modes.
- Sessions are JSONL trees (branching, compaction, fork/clone).
- Extensions, skills, prompts, and themes are discovered from filesystem conventions and npm/git packages.

---

## Current Progress

> Live status lives in [`STATUS.md`](STATUS.md). This section summarizes what is
> done as of the latest commit; each stage heading below carries its own marker.

| Stage | Status |
|-------|--------|
| 1 — Foundation & Unified LLM | [DONE] verified (spec `docs/spec/01`) |
| 2 — Agent Core | [DONE] verified (spec `docs/spec/02`) |
| 3 — Sessions | [DONE] verified (spec `docs/spec/03`) |
| 4 — Interactive CLI & TUI | [DONE] verified (spec `docs/spec/04`) |
| 5 — Permissions & Tool Security | [DONE] verified (spec `docs/spec/05`) |
| 6 — Extensions & Skills | [DONE] verified (spec `docs/spec/06`) |
| 7 — Advanced Features | [IN PROGRESS] 7a steering/follow-up + 7b context compaction verified (spec `docs/spec/08`); 7c–7g skeleton |
| 8 — Polish, Packaging & Release | [PENDING] |

Verified baseline: `dotnet build PiNet.slnx` clean (0 warnings / 0 errors);
`dotnet test PiNet.slnx` — 307 passing (18 Providers + 47 Permissions + 44
AgentCore + 20 Sessions + 114 Cli + 64 Extensions).

### Recommended implementation order (remaining work)

1. **7g — Settings** — `~/.pinet/settings.json` + `<cwd>/.pi/settings.json`,
   `/settings` command; merge with project winning. Also fixes the `SessionManager`
   session root to honor `PI_NET_HOME` (currently hard-coded to `~/.pinet/sessions`).
2. **7c — Thinking levels** — add `ThinkingLevel?` to `ModelRequest`/`ChatRequest`
   + `Agent` passes `State.ThinkingLevel`; provider mapping (OpenAI `reasoning_effort`,
   Anthropic `thinking.enabled` + `budget_tokens`); persist `ThinkingContent`; `/thinking <level>` + `--thinking`.
3. **7d — Cross-provider handoff** — faithful `ConvertToLlm` (normalize/drop
   `ThinkingContent` per target provider) + `/model` picker with no argument.
4. **7e — Image input** — `@image.png` editor expansion (resolve, base64, `ImageContent`
   + inferred MIME), guarded by `ModelDefinition.SupportsVision`.
5. **7f — Export / import** — standalone styled HTML export; JSONL export/import.
6. **8 — Packaging / CI / Release** — CI, `dotnet tool` packaging, self-contained
   publish, README quick-start, self-update.
7. **Deferred** — 7a.2 in-flight keystroke capture (Enter → steer, Alt+Enter →
   follow-up); network smoke tests (Stage 6 `install npm:`/`git:` + extension tool
   invoked by a real LLM).

---

## Technology Stack (.NET 10)

| Concern | Library / Approach |
|---------|--------------------|
| LLM clients | Raw REST/SSE via `HttpClient` + `SseParser` mapping to the `StreamEvent` union (ADR-0001; no vendor SDKs) |
| Agent harness | Custom `AgentLoop` in `PiNet.AgentCore` (inspired by pi's agent-core); no `Microsoft.Agents.AI` |
| Tool definitions | JSON Schema via custom `ToolParameterSchema` (`System.Text.Json` source generated) |
| Serialization | `System.Text.Json` with source generators |
| Terminal UI | Zero-dependency raw `System.Console` scrolling loop + `ReadKey` (ADR decision in Stage 4; no Spectre.Console) |
| Plugin/extension system | C# source files compiled at startup with the SDK Roslyn compiler (`csc.dll`) — spec `docs/spec/06`; no MEF2 |
| Session persistence | JSONL files via `System.Text.Json` streaming (Utf8JsonWriter/Utf8JsonReader) |
| Testing | xUnit + `Microsoft.NET.Test.Sdk` |
| Build | .NET 10 solution (`PiNet.slnx`) with separate class library / test projects per concern |

---

## Stage 1 — Foundation & Unified LLM Abstraction — [DONE] verified

**Goal:** A working console app that can talk to at least two LLM providers through a unified interface.

### Deliverables

- Solution structure (`PiNet.sln`) with projects:
  - `PiNet.Abstractions` — shared interfaces and models
  - `PiNet.Providers` — LLM provider implementations
  - `PiNet.Cli` — console entry point
- Core abstractions:
  - `IChatProvider` — streaming chat completions
  - `IModelRegistry` — provider registration + model catalog
  - `ChatMessage`, `ToolDefinition`, `ToolCall`, `ToolResult` models
  - `StreamEvent` discriminated union (`TextDelta`, `ToolCallStart`, `ToolCallDelta`, `ToolCallEnd`, `Done`, `Error`)
- Provider implementations (initial):
  - `OpenAIProvider` (via `OpenAI` NuGet package or raw HTTP)
  - `AnthropicProvider` (via raw HTTP with SSE parsing)
- Auth resolution: environment variables per provider (`OPENAI_API_KEY`, `ANTHROPIC_API_KEY`), stored-credential fallback
- Token usage + cost tracking on every response

### Acceptance Criteria

- `dotnet run -- --provider openai --model gpt-4o "Hello"` returns a streamed response
- `dotnet run -- --provider anthropic --model claude-sonnet-4-20250514 "Hello"` returns a streamed response
- Streaming prints text deltas to the console in real-time
- Usage/cost summary printed at end

---

## Stage 2 — Agent Core: Tool-Calling Loop — [DONE] verified

**Goal:** An autonomous agent loop that can call tools and iterate until the model stops.

### Deliverables

- `PiNet.AgentCore` project
- `Agent` class with:
  - `SystemPrompt`, `Model`, `Messages`, `Tools` state
  - `PromptAsync(string)` and `ContinueAsync()` methods
  - `Subscribe(Action<AgentEvent>)` for event streaming
  - `Abort()` / `AbortToken`
- `AgentEvent` types: `AgentStart`, `TurnStart`, `MessageStart`, `MessageUpdate`, `MessageEnd`, `ToolExecutionStart`, `ToolExecutionEnd`, `TurnEnd`, `AgentEnd`
- Tool execution pipeline:
  - Parallel (default) and sequential modes
  - `BeforeToolCall` hook (can block with reason)
  - `AfterToolCall` hook (can mutate result, signal termination)
  - `Terminate` hint propagation
- `AgentTool` interface: `Name`, `Description`, `Parameters` (JSON Schema), `ExecuteAsync()`
- Built-in tools: `ReadFileTool`, `WriteFileTool`, `EditFileTool`, `BashTool`
- Context transformation pipeline: `TransformContext` (for pruning/compaction), `ConvertToLlm` (for message filtering)

### Acceptance Criteria

- Agent can read a file, then write a new file based on contents — full loop with 2+ tool calls
- Parallel tool execution works (two independent reads in one turn)
- `BeforeToolCall` can block a tool and return a reason to the LLM
- All events are emitted in correct order (verified by test subscriber)

---

## Stage 3 — Session Management (JSONL Tree) — [DONE] verified

**Goal:** Persistent sessions with branching, compaction, fork, and resume.

### Deliverables

- `PiNet.Sessions` project
- `Session` model: tree of `SessionEntry` nodes (`Id`, `ParentId`, `Role`, `Content`, `Timestamp`, `Metadata`)
- `SessionManager`:
  - Save/load JSONL files (streaming read/write with `Utf8JsonReader`/`Utf8JsonWriter`)
  - Auto-save to `~/.pinet/sessions/<working-dir-hash>/`
  - Resume from file path or partial UUID
  - Fork: copy active branch into new file
  - Clone: duplicate current branch into new session file
  - Tree navigation: `GetTree()`, `JumpTo(entryId)`
- Context compaction:
  - `Compactor` that summarizes old messages via LLM call
  - Auto-compact on context overflow (configurable threshold)
  - Manual `/compact` trigger
  - Lossy but full history preserved in JSONL
- Session options in CLI:
  - `-c` / `--continue` (most recent)
  - `-r` / `--resume` (browse and select)
  - `--session <path|id>`
  - `--fork <path|id>`
  - `--no-session` (ephemeral)

### Acceptance Criteria

- Create a session, ask 3 questions, quit, resume — all context preserved
- Fork a session at message 2, continue both branches independently
- Auto-compact triggers when context window approaches limit
- JSONL file is human-readable and diffable

---

## Stage 4 — Interactive CLI & Terminal UI — [DONE] verified

**Goal:** A polished interactive terminal experience.

### Deliverables

- `PiNet.Tui` project (wrapping Spectre.Console)
- Interactive mode:
  - Editor prompt with syntax-aware multi-line input (Shift+Enter)
  - Streaming response rendering with markdown formatting
  - Collapsible tool output (toggle with Ctrl+O)
  - Collapsible thinking blocks (toggle with Ctrl+T)
  - Status bar: working directory, model, tokens (↑↓R W CH), cost, context usage
  - `@file` reference autocomplete (fuzzy file search)
  - Path tab-completion
  - Clipboard paste (text + images)
  - `!command` to run shell commands inline
- Slash commands:
  - `/login`, `/logout` — credential management
  - `/model` — switch model with picker UI
  - `/thinking` — switch thinking level
  - `/settings` — interactive settings editor
  - `/resume`, `/new`, `/session`, `/tree`, `/fork`, `/clone`
  - `/compact`, `/copy`, `/export`, `/import`
  - `/quit`
- Keyboard shortcuts:
  - Ctrl+C (clear), Ctrl+C twice (quit)
  - Escape (cancel/abort), Escape twice (open tree)
  - Ctrl+L (model selector)
  - Ctrl+P / Shift+Ctrl+P (cycle scoped models)
  - Shift+Tab (cycle thinking level)
- Message queue: Enter = steering, Alt+Enter = follow-up
- Non-interactive modes:
  - `-p` / `--print` (one-shot, print and exit)
  - `--mode json` (JSONL event stream)
  - `--mode rpc` (JSONL RPC over stdin/stdout)

### Acceptance Criteria

- Interactive session feels responsive with real-time streaming
- `pi -p "Summarize this codebase"` works end-to-end
- `echo "hello" | pi -p "Echo this back"` merges stdin
- Model picker UI renders available models grouped by provider
- `/tree` shows session history as navigable tree

---

## Stage 5 — Permissions & Tool Security — [DONE] verified

**Goal:** Configurable permission gates for destructive tools.

### Deliverables

- `PiNet.Permissions` project
- Permission model:
  - `ToolPermission` enum: `Allow`, `Ask`, `Deny`
  - `PermissionRule`: tool name pattern + path pattern + permission
  - `IPermissionResolver` interface
- Built-in permission policies:
  - `AskForBash` — ask before every `bash` / `powershell` execution
  - `DenyDangerousPaths` — deny writes outside working directory
  - `AllowReads` — never ask for read-only tools
- Permission resolution chain: global → project → session
- User prompts during tool execution:
  - Show tool name, arguments, affected paths
  - Options: Allow / Allow for session / Deny
- Settings integration:
  - `permissions.json` (global `~/.pinet/` and project `.pi/`)
  - `/trust` command to save project trust decision
  - `--approve` / `--no-approve` CLI flags for one-shot override
- Containerization support (documentation + Dockerfile template)

### Acceptance Criteria

- First `bash` call in a new project triggers a permission prompt
- `/trust` saves the project as trusted, no more prompts for that project
- `--approve` flag bypasses prompts for one run
- Permission rules can deny writes to specific paths

---

## Stage 6 — Extension System & Skills — [DONE] verified

**Goal:** A plugin architecture for community extensions, skills, and prompt templates.

Delivered (spec `docs/spec/06`, verified 2026-09-21; see `docs/extensions.md`):

- `PiNet.Extensions` project (zero NuGet, no references to other Pi projects)
- Extension system:
  - `IExtension` interface: `Register(IExtensionContext ctx)` (+ tools as
    `ExtensionToolSpec`, slash commands, `IExtensionCommandContext`)
  - Runtime discovery from `~/.pinet/extensions/`, `.pi/extensions/`, packages
  - Extensions register tools + slash commands (`--no-extensions` disables)
  - Extensions compiled at startup with the SDK's `csc.dll`; content-hash cache
    in `~/.pinet/cache/extensions/`; lifecycle: load → register → dispose
- Skill system:
  - `Skill` model: `Name`, `Description`, body (frontmatter Markdown)
  - Discovery from `~/.pinet/skills/`, `~/.agents/skills/`, `.pi/skills/` +
    ancestor walk, packages (`--no-skills` disables)
  - Invoked via `/skill:<name>`; loadable skills listed in the system prompt
    (agentskills-style `<available_skills>` XML; `disable-model-invocation` supported)
- Prompt templates:
  - Markdown files with `$1`/`$@`/`${var:-default}` placeholders
  - Discovery from `~/.pinet/prompts/`, `.pi/prompts/`, packages
    (`--no-prompt-templates` disables)
  - Expanded via `/templatename [args]` then Enter in the editor
- Package management:
  - `pinet install <path|npm:<pkg>|git:<url>>`, `remove`, `list`, `update`
  - Packages bundle extensions + skills + prompts; `~/.pinet/packages.json` index
- Context files:
  - Load `AGENTS.md` / `CLAUDE.md` from cwd and ancestors
  - `SYSTEM.md` to override the default system prompt; `APPEND_SYSTEM.md` to append

### Acceptance Criteria (verified)

- [x] Custom tool extension in `~/.pinet/extensions/` is picked up on restart
      (compile + load covered by `ExtensionHostTests`; tool reaches
      `ChatRequest.Tools` via `ExtensionToolAdapter`)
- [x] Skill markdown file invoked with `/skill:my-skill` sends its steps as a
      turn (`SlashSkillTests`)
- [x] Prompt template expanded with `/review`; variables filled from user input
      (`TemplateLoopTests`, `TemplateExpanderTests`)
- [x] `pinet install` for a path/`npm:`-style source installs and activates a
      package (`PackageManagerTests`, `PackageManagerCliTests`)

Manual smoke (network) pending: `install npm:<pkg>`/`git:<url>` on a real
machine and an extension tool invoked end-to-end by a real LLM.

Delivered alongside Stage 6 — **spec 07** (`docs/spec/07-local-llm-providers.md`,
verified 2026-09-21): Pi-compatible local & custom LLM providers via
`~/.pinet/models.json` (Ollama, LM Studio, llama.cpp, vLLM, Anthropic-compatible
gateways); config guide in `docs/local-llms.md`.

---

## Stage 7 — Advanced Features — [IN PROGRESS]

**Goal:** Feature parity with Pi's advanced capabilities.

### Deliverables

- Steering & follow-up: ✅ verified
  - `SteerAsync(message)` — inject message while agent is running tools
  - `FollowUpAsync(message)` — queue message for after agent finishes
  - `SteeringMode` / `FollowUpMode`: `OneAtATime` (default) or `All`
- Context compaction (auto + `/compact`): ✅ verified (7b)
  - `TokenEstimator` (character-based, ~4 chars/token); auto-trigger when the
    estimated context exceeds `Threshold * ContextWindow` (`--compact`,
    `--compact-threshold`, `--compact-preserve`)
  - `ContextCompactor` + `SessionCompactor`: summary cached, folded on re-compaction,
    lossless on disk (JSONL preserves the full history)
  - `/compact [instructions]` slash command: forces compaction, persists the summary
    as a new entry, and rewinds the persistence watermark
  - Usage tracking (`LastUsage`/`TotalUsage`) feeds the threshold via
    `max(estimate, LastPromptTokens)`
- Cross-provider handoff (7d): 🔄 skeleton
  - `/model <provider>/<id>` already rebuilds the agent with current messages
  - `ConvertToLlm` is an identity stub; must normalize content per target provider
    (drop/replace `ThinkingContent` for providers that don't support it)
  - `/model` picker with no argument (list providers/models, select) — pending
- Reasoning/thinking levels (7c): 🔄 skeleton
  - `ThinkingLevel` enum + `AgentState.ThinkingLevel` present; `ThinkingMapper` stub
  - Provider mapping + `ThinkingContent` persistence pending
- Image input support (7e): 🔄 skeleton
  - `ImageContent` supported; `@image` editor expansion + vision guard not fully wired
- Export/import (7f): 🔄 skeleton
  - `SessionExport` stubs for JSONL/HTML; CLI commands pending
- Settings (7g): 🔄 skeleton
  - `SettingsManager` loads global/project JSON; `/settings` + hot-reload pending; `SessionManager` root via `PI_NET_HOME` pending

### Acceptance Criteria

- Send a steering message while agent is executing a tool — agent receives it after current turn
- Switch from Claude to GPT-4o mid-session — conversation continues seamlessly
- Toggle thinking from `off` to `high` — reasoning tokens appear in UI
- Paste a screenshot, agent describes what it sees

---

## Pending Tests Summary

The following acceptance criteria / features still lack dedicated unit/integration
integration tests. (Stage 7b is fully covered — its rows below are marked ✅ Done;
16 new tests were added in the 7b implementation.)

| Stage | Feature / AC | Test Status | Notes |
|-------|--------------|-------------|-------|
| 1 | Provider credential fallback (`$ENV` → literal → default env) | Missing | `ConfigCredentialResolver` has basic tests; fallback chain not covered |
| 1 | Token/cost tracking printed at `StreamEnd` | Missing | Integration test with `ScriptedProvider` needed |
| 2 | `Agent.Abort()` cancels in-flight run and does not persist | Missing | Abort path tested partially, no abort-during-tool test |
| 2 | Sequential tool execution mode | Missing | Only parallel mode exercised |
| 2 | `AfterToolCall` mutation / termination hint | Missing | Hook invoked but mutation not asserted |
| 3 | Session fork / clone and tree navigation | Partial | `SessionTests` covers CRUD, fork/clone not unit-tested |
| 3 | Auto-compact trigger on context overflow | Missing | `CompactorTests` covers LLM summarization, not threshold auto-trigger |
| 4 | `/model` switch persists across turns | Missing | Model switch tested in loop, persistence not asserted |
| 4 | `/new` ephemeral session disables persistence | Missing | No test for `NoSession` path |
| 4 | Ctrl+C on empty prompt exits with code 0 | Missing | Exit code not verified in tests |
| 5 | Malformed permission config produces startup error | Missing | `ConfigTests` covers good/bad JSON, startup error path not tested |
| 5 | `PI_NET_HOME` override for permissions | Missing | Only global load tested |
| 6 | Extension host compilation cache reuse | Missing | `ExtensionHostTests` covers compile, not cache hit |
| 6 | Skill `disable-model-invocation` frontmatter | Missing | Frontmatter parsed, flag not exercised |
| 6 | Prompt template `${1:-default}` slice and unknown `$X` keep | Partial | `TemplateTests` covers basic, edge slices missing |
| 7b | `TokenEstimator` accuracy and image/token counting | ✅ Done | `TokenEstimatorTests` (6 tests) |
| 7b | `ContextCompactor` auto-compact threshold & caching | ✅ Done | `ContextCompactorTests` (7 tests) |
| 7b | Manual `/compact` persists summary and rewinds watermark | ✅ Done | `InteractiveLoopTests` `/compact` (4 tests) |
| 7b | Usage tracking `LastUsage/TotalUsage` accumulation | ✅ Done | `UsageTrackingTests` (3 tests) |
| 7c-7g | Thinking levels, cross-provider handoff, image input, export/import, settings | Missing | Not started (recommended order: 7g → 7c → 7d → 7e → 7f) |

## Stage 8 — Polish, Packaging & Release — [PENDING]

**Goal:** Production-ready release with documentation and CI/CD.

### Deliverables

- CLI packaging: `PackAsTool`, `ToolCommandName`, `install.ps1`/`install.sh`
- Build & CI: `.github/workflows/ci.yml` builds and tests
- Documentation: `docs/packaging.md`
- Remaining: self-contained publish, NuGet publish, SBOM, telemetry opt-in

### Deliverables

- CLI packaging:
  - `dotnet tool` packaging (`dotnet pack` + NuGet publishing)
  - Self-contained single-file executables per RID (`dotnet publish -r win-x64`, `linux-x64`, `osx-arm64`)
  - Install script (`install.sh` / `install.ps1`)
- Build & CI:
  - GitHub Actions: build, test, lint, pack, publish
  - Unit tests: >80% coverage on `AgentCore`, `Sessions`, `Providers`
  - Integration tests: mock LLM provider for deterministic agent loop testing
  - E2E tests: real provider calls (gated by API key availability)
- Documentation:
  - `README.md` with quick start, installation, configuration
  - `docs/` directory: providers, extensions, skills, sessions, settings
  - Inline XML doc comments on all public APIs
- Telemetry (opt-in):
  - Anonymous usage ping (model used, session length, tool counts)
  - Disabled by default, `PI_TELEMETRY=1` to enable
- Supply-chain hardening:
  - Pinned dependency versions in `.csproj`
  - NuGet audit enabled
  - SBOM generation
- Release process:
  - Semantic versioning
  - GitHub Releases with binaries per platform
  - `pinet update --self` for self-update

### Acceptance Criteria

- `dotnet tool install -g PiNet` works and `pinet` is available on PATH
- `pinet -p "Hello"` works on Windows, Linux, and macOS
- All tests pass in CI
- README has working quick-start instructions

---

## Project Structure (Final)

```
pinet/
├── src/
│   ├── PiNet.Abstractions/          # Interfaces, models, enums
│   ├── PiNet.Providers/             # LLM provider implementations
│   ├── PiNet.AgentCore/             # Agent loop, tool execution, events
│   ├── PiNet.Sessions/              # JSONL session management
│   ├── PiNet.Tui/                   # [deferred] terminal UI lives in PiNet.Cli/Interact
│   ├── PiNet.Permissions/           # Permission gates and policies
│   ├── PiNet.Extensions/            # [Stage 6] extension/skill/prompt discovery
│   ├── PiNet.Cli/                   # Console app entry point (incl. interactive layer)
│   ├── PiNet.AgentCore.Tests/       # 44 tests
│   ├── PiNet.Sessions.Tests/        # 20 tests
│   ├── PiNet.Permissions.Tests/     # 47 tests
│   ├── PiNet.Providers.Tests/       # 18 tests [spec 07]
│   ├── PiNet.Extensions.Tests/      # 64 tests [Stage 6]
│   └── PiNet.Cli.Tests/             # 114 tests
├── docs/                            # specs (01–08), ADRs, guides
├── .github/workflows/               # [Stage 8]
├── PiNet.slnx
└── README.md                        # [Stage 8]
```

---

## Dependencies (NuGet)

| Package | Purpose |
|---------|---------|
| (none) — raw `HttpClient` | All provider protocol endpoints (ADR-0001); no vendor SDKs |
| `xunit` / `xunit.runner.visualstudio` | Testing framework (test projects only) |
| `Microsoft.NET.Test.Sdk` | Test host (test projects only) |
| `System.Text.Json` | Serialization (built-in) |
| `Microsoft.Extensions.Logging` | Structured logging (planned) |
| `Microsoft.Extensions.Configuration` | Settings management (planned, Stage 7) |

All runtime projects (`Abstractions`, `Providers`, `AgentCore`, `Sessions`,
`Permissions`, `Cli`) currently use zero NuGet packages; only the test projects
reference `xunit` + `Microsoft.NET.Test.Sdk`.

---

## Risk Mitigation

| Risk | Mitigation |
|------|------------|
| .NET 10 SDK stability | Target `net10.0` (GA); no multi-targeting needed |
| Provider API rate limits / breaking changes | Abstract behind `IChatProvider`; raw HTTP keeps wire changes bounded (ADR-0001) |
| Terminal portability (raw console) | Zero-dependency `ReadKey` loop; test on Windows Terminal, iTerm2, GNOME Terminal |
| Cross-platform terminal differences | Test on Windows Terminal, iTerm2, GNOME Terminal; platform detection where needed |
| Extension compilation weight at startup | Content-hash caching (`~/.pinet/cache/extensions/`) + best-effort non-fatal loading (spec 06) |

---

*Plan created: 2026-09-09*
*Last updated: 2026-09-21 — 7a + 7b verified, 307 tests passing. Next per recommended order: 7g → 7c → 7d → 7e → 7f → Stage 8; see `STATUS.md`.*
*Target: .NET 10 (preview → GA)*
*Reference: https://github.com/earendil-works/pi*
