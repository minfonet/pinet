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

## Technology Stack (.NET 10)

| Concern | Library / Approach |
|---------|--------------------|
| LLM clients | `Microsoft.Agents.AI` + provider-specific SDKs (`OpenAI`, `Anthropic.SDK`, `Google.Apis.GenerativeLanguage.V1`), or raw HTTP via `HttpClient` for custom providers |
| Agent harness | `Microsoft.Agents.AI.AIAgent` + custom `AgentLoop` (inspired by Pi's agent-core) |
| Tool definitions | JSON Schema via `JsonSchemaExporter` / `System.Text.Json.Serialization.Metadata` |
| Serialization | `System.Text.Json` with source generators |
| Terminal UI | [Spectre.Console](https://spectreconsole.net/) (rich TUI: tables, progress, prompts, markdown rendering) |
| Plugin/extension system | `System.Composition` (MEF2) or custom `IExtension` interface with runtime discovery |
| Session persistence | JSONL files via `System.Text.Json` streaming (Utf8JsonWriter/Utf8JsonReader) |
| Testing | xUnit + FluentAssertions + Moq |
| Build | .NET 10 solution with separate class library projects per concern |

---

## Stage 1 — Foundation & Unified LLM Abstraction

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

## Stage 2 — Agent Core: Tool-Calling Loop

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

## Stage 3 — Session Management (JSONL Tree)

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

## Stage 4 — Interactive CLI & Terminal UI

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

## Stage 5 — Permissions & Tool Security

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

## Stage 6 — Extension System & Skills

**Goal:** A plugin architecture for community extensions, skills, and prompt templates.

### Deliverables

- `PiNet.Extensions` project
- Extension system:
  - `IExtension` interface: `Register(Agent agent, ExtensionContext ctx)`
  - Runtime discovery from `~/.pinet/extensions/`, `.pi/extensions/`
  - Extensions can register: tools, commands, keyboard shortcuts, event handlers, UI widgets
  - Extension lifecycle: load → register → activate → dispose
- Skill system:
  - `Skill` model: `Name`, `Description`, `Steps[]` (Markdown-based)
  - Discovery from `~/.pinet/skills/`, `.pi/skills/`, `~/.agents/skills/`
  - Invoked via `/skill:name` or auto-loaded by agent
  - Follows [Agent Skills standard](https://agentskills.io)
- Prompt templates:
  - Markdown files with `{{variable}}` placeholders
  - Discovery from `~/.pinet/prompts/`, `.pi/prompts/`
  - Expanded via `/templatename` in editor
- Package management:
  - `pinet install npm:<package>` / `pinet install git:<url>`
  - `pinet remove`, `pinet list`, `pinet update`
  - Packages bundle extensions + skills + prompts + themes
- Context files:
  - Load `AGENTS.md` / `CLAUDE.md` from cwd and parent directories
  - `SYSTEM.md` to override default system prompt
  - `APPEND_SYSTEM.md` to append

### Acceptance Criteria

- Write a custom tool extension, place in `~/.pinet/extensions/`, agent picks it up on restart
- Create a skill markdown file, invoke with `/skill:my-skill`, agent follows its steps
- Create a prompt template, expand with `/review`, variables are filled from user input
- `pinet install npm:@example/my-tools` installs and activates the package

---

## Stage 7 — Advanced Features

**Goal:** Feature parity with Pi's advanced capabilities.

### Deliverables

- Steering & follow-up:
  - `SteerAsync(message)` — inject message while agent is running tools
  - `FollowUpAsync(message)` — queue message for after agent finishes
  - `SteeringMode` / `FollowUpMode`: `OneAtATime` (default) or `All`
- Context compaction (advanced):
  - Auto-trigger when approaching context window limit
  - Custom compaction instructions
  - Compaction summary preserves key decisions and file references
- Cross-provider handoff:
  - Switch model mid-session without losing context
  - `convertToLlm` normalizes message format across providers
- Reasoning/thinking levels:
  - Unified `ThinkingLevel` enum: `Off`, `Minimal`, `Low`, `Medium`, `High`, `XHigh`, `Max`
  - Per-provider mapping (Anthropic thinking, OpenAI reasoning_effort, Google thinking config)
- Image input support:
  - Paste images into editor, included as vision content
  - `@image.png` file references
- Export/import:
  - Export session to HTML (standalone, styled)
  - Export session to JSONL (for sharing)
  - Import session from JSONL
- Settings:
  - `~/.pinet/settings.json` (global) + `.pi/settings.json` (project)
  - `/settings` interactive editor
  - Hot-reload on file change

### Acceptance Criteria

- Send a steering message while agent is executing a tool — agent receives it after current turn
- Switch from Claude to GPT-4o mid-session — conversation continues seamlessly
- Toggle thinking from `off` to `high` — reasoning tokens appear in UI
- Paste a screenshot, agent describes what it sees

---

## Stage 8 — Polish, Packaging & Release

**Goal:** Production-ready release with documentation and CI/CD.

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
│   ├── PiNet.Tui/                   # Spectre.Console terminal UI
│   ├── PiNet.Permissions/           # Permission gates and policies
│   ├── PiNet.Extensions/            # Extension/skill/prompt discovery
│   └── PiNet.Cli/                   # Console app entry point
├── tests/
│   ├── PiNet.Abstractions.Tests/
│   ├── PiNet.Providers.Tests/
│   ├── PiNet.AgentCore.Tests/
│   ├── PiNet.Sessions.Tests/
│   └── PiNet.Cli.Tests/
├── docs/
├── .github/workflows/
├── PiNet.sln
└── README.md
```

---

## Dependencies (NuGet)

| Package | Purpose |
|---------|---------|
| `Microsoft.Agents.AI` | Agent Framework abstractions |
| `OpenAI` | OpenAI official SDK |
| `Anthropic.SDK` | Anthropic API client |
| `Google.Apis.GenerativeLanguage.V1` | Gemini API |
| `Spectre.Console` | Rich terminal UI |
| `Spectre.Console.Cli` | CLI command parsing |
| `System.Text.Json` | Serialization (built-in) |
| `System.Composition` | MEF2 for extension discovery |
| `Microsoft.Extensions.Logging` | Structured logging |
| `Microsoft.Extensions.Configuration` | Settings management |
| `xUnit` | Testing framework |
| `FluentAssertions` | Test assertions |
| `Moq` | Mocking |

---

## Risk Mitigation

| Risk | Mitigation |
|------|------------|
| .NET 10 SDK not stable yet | Target `net10.0` but keep compatibility with `net9.0` via multi-targeting |
| Provider API rate limits / breaking changes | Abstract behind `IChatProvider`; pin SDK versions |
| Spectre.Console limitations for complex TUI | Prototype interactive mode early (Stage 4); fallback to raw `System.Console` |
| Cross-platform terminal differences | Test on Windows Terminal, iTerm2, GNOME Terminal; use Spectre's platform detection |
| MEF2 performance for extension loading | Lazy loading + cached discovery; benchmark early |

---

*Plan created: 2026-09-09*
*Target: .NET 10 (preview → GA)*
*Reference: https://github.com/earendil-works/pi*
