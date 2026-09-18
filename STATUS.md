# PiNet — Project Status

> Last updated: 2026-07-09
> Companion to [`IMPLEMENTATION_PLAN.md`](IMPLEMENTATION_PLAN.md). Per-stage detail lives in
> [`docs/spec/`](docs/spec/) (spec-driven development: spec written before code, then verified).

## Snapshot

| Item | State |
|------|-------|
| Solution | `PiNet.slnx` — builds clean (0 warnings, 0 errors) |
| Tests | **111 passing** (27 `PiNet.AgentCore.Tests` + 13 `PiNet.Sessions.Tests` + 71 `PiNet.Cli.Tests`), 0 failing |
| Current stage | **Stage 5 — Permissions** (spec `05`, not started) |
| Stages complete | 1, 2, 3, 4 (specs verified against the running CLI) |
| Packaging | Git repo initialized with `.gitignore`; CI/release not started (Stage 8) |

## Projects

| Project | Role | State |
|---------|------|-------|
| `PiNet.Abstractions` | `ChatMessage`/content blocks, `StreamEvent` union, `IChatProvider`, `IModelRunner`, `ModelRegistry`, credential resolution (env + store) | Done (Stage 1) |
| `PiNet.Providers` | `ChatService` facade; OpenAI + Anthropic providers (raw HTTP + SSE, ADR-0001); `Faux` provider for deterministic tests; per-provider model catalogs | Done (Stage 1) |
| `PiNet.AgentCore` | `Agent` loop (`PromptAsync`/`ContinueAsync`/`Abort`/`Subscribe`), `AgentEvent` lifecycle, parallel/sequential tool execution, `BeforeToolCall`/`AfterToolCall` hooks, `TransformContext`/`ConvertToLlm`, built-in tools (`read`, `write`, `edit`, `bash`) | Done (Stage 2) |
| `PiNet.Sessions` | JSONL tree sessions (`Session`, `SessionEntry`, `SessionStore`, `SessionJson`), `SessionManager` (create/load/latest/list/fork, append watermark), `SessionCompactor` (LLM summarization) | Done (Stage 3) |
| `PiNet.Cli` | Entry point: `-p/--print`, `--agent` one-shot, `--interactive/-i` loop, session flags (`--session`, `-c`, `--fork`, `--no-session`), `--list-models`; interactive layer: `LineEditor`, `LineHistory`, `SlashCommand`, `AgentConsoleRenderer` (shared folding), `TurnRunner` (shared turn/persist), `InteractiveLoop` | Done (Stage 4) |
| `PiNet.AgentCore.Tests` | Loop, hooks, tool execution, built-in tools (scripted `IModelRunner`) | 27 passing |
| `PiNet.Sessions.Tests` | Session tree, JSONL store, compactor | 13 passing |
| `PiNet.Cli.Tests` | Line editor, history, slash parser, renderer folding, and full `InteractiveLoop` tests (scripted keys, `ScriptedProvider`, `HangingProvider`) | 71 passing |

Not yet created (later stages): `PiNet.Permissions` (5), `PiNet.Extensions` (6),
steering/follow-up + advanced features (7), packaging/CI (8).

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

### Stages 5–8 — ⬜ not started
Permissions (5), extensions/skills (6), steering/follow-up + compaction polish (7),
packaging/CI/release (8). See `IMPLEMENTATION_PLAN.md`.

## Known issues / next steps

1. **Stage 5 — Permissions** (entry point now unblocked): write spec `05` before coding
   (SDD), then implement the `PiNet.Permissions` project. See `IMPLEMENTATION_PLAN.md`.
2. **Stage 5 prep done** (2026-07-09): Stage 4 tests written (71 passing), dead code in
   `Program.cs` removed (bare prompt now defaults to print mode), `Class1.cs` deleted,
   git repo initialized with `.gitignore`.
3. Later: packaging/CI/release (Stage 8), steering/follow-up + compaction polish (Stage 7).

## Verification commands

```bash
dotnet build PiNet.slnx        # 0 warnings, 0 errors
dotnet test PiNet.slnx         # 111/111 passing
dotnet run --project src/PiNet.Cli -- --list-models
dotnet run --project src/PiNet.Cli -- --provider faux --model faux-model -p "hi"
```
