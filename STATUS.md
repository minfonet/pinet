# PiNet — Project Status

> Last updated: 2026-09-21
> Companion to [`IMPLEMENTATION_PLAN.md`](IMPLEMENTATION_PLAN.md). Per-stage detail lives in
> [`docs/spec/`](docs/spec/) (spec-driven development: spec written before code, then verified).

## Snapshot

| Item | State |
|------|-------|
| Solution | `PiNet.slnx` — builds clean (0 warnings, 0 errors) |
| Tests | **279 passing** (18 `PiNet.Providers.Tests` + 47 `PiNet.Permissions.Tests` + 27 `PiNet.AgentCore.Tests` + 13 `PiNet.Sessions.Tests` + 110 `PiNet.Cli.Tests` + 64 `PiNet.Extensions.Tests`), 0 failing |
| Current stage | **Stage 7 — Advanced Features** (next) |
| Stages complete | 1, 2, 3, 4, 5, 6 + spec 07 (specs verified against the running CLI) |
| Packaging | Git repo initialized with `.gitignore`; CI/release not started (Stage 8) |

## Projects

| Project | Role | State |
|---------|------|-------|
| `PiNet.Abstractions` | `ChatMessage`/content blocks, `StreamEvent` union, `IChatProvider`, `IModelRunner`, `ModelRegistry`, credential resolution (env + store) | Done (Stage 1) |
| `PiNet.Providers` | `ChatService` facade; OpenAI + Anthropic providers (raw HTTP + SSE, ADR-0001); `Faux` provider for deterministic tests; per-provider model catalogs; Pi-compatible custom providers via `~/.pinet/models.json` (`ModelsConfig`, `ConfigCredentialResolver`, spec 07) | Done (Stages 1, 6) |
| `PiNet.AgentCore` | `Agent` loop (`PromptAsync`/`ContinueAsync`/`Abort`/`Subscribe`), `AgentEvent` lifecycle, parallel/sequential tool execution, `BeforeToolCall`/`AfterToolCall` hooks, `TransformContext`/`ConvertToLlm`, built-in tools (`read`, `write`, `edit`, `bash`) | Done (Stage 2) |
| `PiNet.Sessions` | JSONL tree sessions (`Session`, `SessionEntry`, `SessionStore`, `SessionJson`), `SessionManager` (create/load/latest/list/fork, append watermark), `SessionCompactor` (LLM summarization) | Done (Stage 3) |
| `PiNet.Cli` | Entry point: `-p/--print`, `--agent` one-shot, `--interactive/-i` loop, session flags (`--session`, `-c`, `--fork`, `--no-session`), `--list-models`, `--approve`/`--no-approve`, `install/list/remove/update` package subcommands, `--no-extensions`/`--no-skills`/`--no-prompt-templates`; interactive layer: `LineEditor`, `LineHistory`, `SlashCommand` (incl. `/trust`, `/skill:`), `AgentConsoleRenderer` (shared folding), `TurnRunner` (shared turn/persist), `InteractiveLoop`, `PermissionGate` + `ConsolePermissionPrompt`; `Extensions/` adapter + `CliEnvironment` setup | Done (Stages 4–6) |
| `PiNet.Permissions` | Zero-project-reference permission layer: `ToolPermission`/`PermissionRule` + `Glob`, `RulePermissionResolver` (safety floor → deny-sticky → session→project→global → default), `PermissionConfig` (JSON load/save, `PI_NET_HOME`), `ToolPaths`, `IPermissionPrompt` (`AutoApprovePrompt`, `DenyAllPrompt`), `TrustStore` | Done (Stage 5) |
| `PiNet.Extensions` | Extension host (source compiled with SDK Roslyn, cached by content hash) + `IExtension` model; skill registry (`~/.pinet/skills`, `~/.agents/skills`, `.pi/skills`, packages; frontmatter + `SkillCatalog`); prompt templates (+ `TemplateExpander`); context files (`SYSTEM.md`/`AGENTS.md`/`CLAUDE.md` → `SystemPromptBuilder`); tar extractor (zip-slip safe); `PackageManager` (`install/list/remove/update`, path/npm/git) | Done (Stage 6) |
| `PiNet.AgentCore.Tests` | Loop, hooks, tool execution, built-in tools (scripted `IModelRunner`) | 27 passing |
| `PiNet.Sessions.Tests` | Session tree, JSONL store, compactor | 13 passing |
| `PiNet.Permissions.Tests` | Rule matching, resolution algorithm, config load/save/malformed, `PI_NET_HOME`, `TrustStore`, `ToolPaths` | 47 passing |
| `PiNet.Cli.Tests` | Line editor, history, slash parser (incl. `/skill:`), renderer folding, full `InteractiveLoop` tests (scripted keys, `ScriptedProvider`, `HangingProvider`), permission-gate AC1–AC4 tests (`PermissionGateTests`, `ToolCallScript`), plus `SlashSkillTests`, `ExtensionCommandTests`, `TemplateLoopTests`, `CliEnvironmentTests`, `PackageManagerCliTests` (Spec 06) | 110 passing |
| `PiNet.Extensions.Tests` | Skill frontmatter/registry/catalog, template expander/registry, context files + system prompt builder, tar extractor, package manager, extension host (incl. real SDK compile + cache reuse) | 64 passing |
| `PiNet.Providers.Tests` | `models.json` parsing + warnings, `ConfigCredentialResolver` (`$ENV`/literal/fallback), custom provider registration (OpenAI/Anthropic), built-in merge by model id, end-to-end `PI_NET_HOME` (spec 07) | 18 passing |

Not yet created (later stages): steering/follow-up + advanced features (7),
packaging/CI (8).

## Stage-by-stage

### Stage 1 — Foundation & Unified LLM — ✅ verified
Spec [`docs/spec/01`](docs/spec/01-stage-1-unified-llm.md) marked *Verified*.
Providers (OpenAI, Anthropic, Faux), streaming event protocol, auth via env vars
(`--api-key` override supported), token/cost tracking printed at `StreamEnd`.

### Stage 2 — Agent Core — ✅ verified
Spec [`docs/spec/02`](docs/spec/02-stage-2-agent-core.md) marked *Verified*.
Full tool-calling loop with hooks and parallel execution; 27 unit tests.

### Stage 3 — Sessions (JSONL tree) — ✅ verified
Spec [`docs/spec/03`](docs/spec/03-stage-3-sessions.md) marked *Verified*.
Append-only JSONL, fork/resume/continue wired into the CLI (`--agent` mode); compactor implemented.

### Stage 4 — Interactive loop — ✅ verified
Spec [`docs/spec/04`](docs/spec/04-stage-4-interactive-loop.md) marked *Verified* (2026-07-09).
Zero-dependency TUI (ADR decision: no Spectre.Console — scrolling loop with raw `ReadKey`).

Implemented:
- `--interactive`/`-i` loop with multi-prompt context accumulation and per-turn persistence
- `LineEditor` (caret editing, Ctrl+U/K/A/E) + `LineHistory` (Up/Down, dedupe)
- Slash commands: `/help`, `/quit`/`/exit`, `/new`, `/resume [id|path]` (with picker), `/fork`, `/model`
- `AgentConsoleRenderer` — single source of truth for streaming output, shared by `--agent` one-shot and the loop (SR-04.10)
- `TurnRunner` — shared turn + watermark persistence; aborted turns not persisted (SR-04.09)
- Ctrl+C: cancels running turn → back to prompt; on empty prompt → exits (code 130 on abort)

All acceptance criteria checked off (evidence in spec §7 verification log):
- [x] AC1: multi-prompt session persists across turns (JSONL line count + context assertions)
- [x] AC2: line editor + history verified headlessly with scripted key sequences
- [x] AC3: slash command coverage tests (parse + loop dispatch, incl. picker, fork, model switch)
- [x] AC4: Ctrl+C cancel/exit semantics tested (aborted turn not persisted; empty-prompt exit code 0)

### Stage 5 — Permissions & Tool Security — ✅ verified
Spec [`docs/spec/05`](docs/spec/05-stage-5-permissions.md) marked *Verified* (2026-07-09).
New zero-project-reference `PiNet.Permissions` project (pure BCL + `System.Text.Json`);
the gate lives in the CLI (`PermissionGate` adapts resolver + prompt to `BeforeToolCall`).

Implemented:
- Resolution algorithm: safety floor (out-of-cwd writes always denied) → deny-sticky
  (any deny in any layer wins) → first non-deny match in session→project→global order →
  default (`bash` asks, everything else allows)
- Config files: `~/.pinet/permissions.json` (global, `PI_NET_HOME` override) and
  `<cwd>/.pi/permissions.json` (project); missing = empty, malformed = startup error
- Interactive prompt: `a` allow once / `s` allow for session / `d`/Enter deny (safe default)
- `/trust` and `/trust off` — merge/remove the bash-allow project rule
- `--approve` (auto-approve asks; denies still apply), `--no-approve` (re-ask bash),
  non-interactive fallback (`DenyAllPrompt` with one-line notice)
- Denied calls return `Blocked: <reason>` as an error tool result — the LLM sees it and adapts
- `docs/permissions.md` — config reference + Dockerfile sandbox template

All acceptance criteria checked off (evidence in spec §7 verification log):
- [x] AC1: first `bash` in a new project prompts; `a` runs the tool, `d` blocks it, agent continues
- [x] AC2: `/trust` persists project trust; subsequent `bash` no longer prompts; `/trust off` reverts
- [x] AC3: `--approve` bypasses prompts (denies still apply); non-interactive stdin denies with notice
- [x] AC4: path-scoped deny (`secrets/*`) blocks matching writes only; out-of-cwd writes always blocked

### Stage 6 — Extensions & Skills — ✅ verified
Spec [`docs/spec/06`](docs/spec/06-stage-6-extensions-skills.md) marked *Verified*
(2026-09-21). New zero-NuGet `PiNet.Extensions` project (pure BCL +
`System.Text.Json`) that references no other Pi project.

Implemented:
- Extension host: plain C# sources compiled at startup with the SDK's
  `csc.dll` (versioned SDK path, refs from `Microsoft.NETCore.App.Ref` +
  `PiNet.Extensions`/`PiNet.Abstractions`), cached by content hash in
  `~/.pinet/cache/extensions/`; `IExtension` model (tools + slash commands);
  startup `LoadInMemory` for tests; `--no-extensions`
- Skills: `~/.pinet/skills`, shared `~/.agents/skills` (agents convention:
  root `.md` ignored, nested picked), `.pi/skills` + `.agents/skills` ancestor
  walk, package `skills/`; frontmatter (`name`, `description`,
  `disable-model-invocation`); `/skill:<name> [args]` dispatch;
  `SkillCatalog` XML block injected into the system prompt; `--no-skills`
- Prompt templates: global + project `.pi/prompts`; `TemplateExpander`
  (`$1`/`$@`/`$ARGUMENTS`/`$0`, `${1:-default}`, slices, unknown `$X` kept);
  `/name args` expands into the editor buffer; `--no-prompt-templates`
- Context files: `SYSTEM.md` (replaces base), `APPEND_SYSTEM.md`, `AGENTS.md`
  (nearest ancestor), `CLAUDE.md` fallback → composed system prompt
- Package manager: `pinet install <path|npm:<pkg>|git:<url>>`, `list`, `remove`,
  `update`; `~/.pinet/packages.json` index; safe tar extraction (zip-slip,
  empty-name, absolute-path and `C:`-drive rejection)
- `docs/extensions.md` — extensions/skills/templates/packages reference

All acceptance criteria checked off (evidence in spec §7/§8 verification log);
manual smoke (`npm:`/`git:` installs, extension tool invoked by a real LLM) is
deferred to a networked machine.

### Local & Custom LLM Providers (spec 07) — ✅ verified
Spec [`docs/spec/07`](docs/spec/07-local-llm-providers.md) marked *Verified*
(2026-09-21). Adds Pi-compatible custom providers to `PiNet.Providers` — no new
project, no NuGet.

Implemented:
- `ModelsConfig.TryLoad` / `ApplyTo` — reads `~/.pinet/models.json`
  (`PI_NET_HOME` override); missing/malformed file → built-ins only + warning
- `ConfigCredentialResolver` — `apiKey` as `$ENV` reference, literal, or
  fallback to the provider's default env vars; `baseUrl` flows through as
  `AuthInfo.BaseUrl`
- Provider factory: `openai-completions` → `OpenAIChatProvider`,
  `anthropic-messages` → `AnthropicChatProvider`; unknown `api` skipped with a
  warning
- Built-in merge by model `id` (replace + append); custom keys (e.g. `ollama`)
  added as new providers
- `OpenAI`/`Anthropic` constructors gained `providerId`/`name` so custom keys
  do not collide with the built-ins
- `docs/local-llms.md` — configuration guide (Ollama, LM Studio, llama.cpp,
  vLLM, Anthropic-compatible)

Verified: `PiNet.Providers.Tests` 18 passing; CLI `--list-models` with a temp
`PI_NET_HOME` + `models.json` shows `[ollama] llama3.1:8b` alongside the
built-ins (exit 0).

### Stages 7–8 — ⬜ not started
Steering/follow-up + compaction polish (7), packaging/CI/release (8).
See `IMPLEMENTATION_PLAN.md`.

## Known issues / next steps

1. **Stage 7 — Advanced Features** (next): steering/follow-up + compaction
   polish. See `IMPLEMENTATION_PLAN.md`.
2. **Stage 6 done** (2026-09-21): `PiNet.Extensions` (extensions, skills,
   templates, context files, packages) wired into the CLI and verified.
   **Spec 07** (local & custom LLM providers via `~/.pinet/models.json`) is
   also done and verified (279 tests passing). Manual smoke tests
   (`pinet install npm:/git:`, real LLM invoking an extension tool) still to
   run on a networked machine; the smoke script's optional `-Provider`/`-Model`
   local-provider parameters are not added yet.
3. Later: packaging/CI/release (Stage 8).

## Verification commands

```bash
dotnet build PiNet.slnx        # 0 warnings, 0 errors
dotnet test PiNet.slnx         # 279/279 passing
dotnet run --project src/PiNet.Cli -- --list-models
dotnet run --project src/PiNet.Cli -- --provider faux --model faux-model -p "hi"
```
