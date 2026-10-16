# Specification 00 — Project Overview

- **Spec status:** Verified (base document, updated continuously)
- **Related docs:** [01 Unifed LLM](01-stage-1-unified-llm.md), [02 Agent Core](02-stage-2-agent-core.md)

## 1. Vision

Pi.NET is a minimal terminal coding harness for .NET 10, inspired by the open-source
[Pi Agent Harness](https://github.com/earendil-works/pi). It provides:

- A unified multi-provider LLM API (OpenAI, Anthropic, and more to come).
- A stateful agent runtime with tool calling and event streaming.
- An interactive terminal coding agent: read/write/edit files and run shell commands.
- Sessions, extensions, skills, and themes, evolving over later stages.

The operating philosophy mirrors Pi: keep the core small and make everything else
extensible. No bake-in of sub-agents, plan mode, or permission popups as hard
features — those are extension points.

## 2. Goals

- G1: Provider-agnostic streaming chat with a single ergonomic API.
- G2: Deterministic agent loop with parallel tool execution.
- G3: Addressable behavior specs (SDD) that can be checked per stage.
- G4: Zero runtime dependencies beyond the .NET base class library where feasible.

## 3. Non-Goals (bounded scope)

- Multi-agent orchestration / graph workflows.
- Built-in sub-agents and permission prompts (postponed or built as extensions).
- Production telemetry pipelines.

## 4. Architecture

```
                         ┌─────────────────────────────┐
                         │        PiNet.Cli (UI)        │
                         └──────────────┬──────────────┘
                                        │ AgentEvents
                         ┌──────────────▼──────────────┐
                         │        PiNet.AgentCore       │      Agent loop, tools, hooks
                         └──────────────┬──────────────┘
                                        │ IModelRunner
                         ┌──────────────▼──────────────┐
                         │       PiNet.Providers        │      OpenAI / Anthropic / Faux
                         └──────────────┬──────────────┘
                                        │ IChatProvider
                         ┌──────────────▼──────────────┐
                         │    PiNet.Abstractions        │      contracts, models, events
                         └─────────────────────────────┘
```

### 4.1 Dependency rule (SR-00.01)

- `PiNet.Cli` may reference `Abstractions`, `Providers`, `AgentCore`, and later UI/session packages.
- `PiNet.AgentCore` references **only** `PiNet.Abstractions` (through `IModelRunner`).
- `PiNet.Providers` references only `PiNet.Abstractions`.

### 4.2 Module map

| Project | Responsibility |
|---------|----------------|
| `PiNet.Abstractions` | Chat model, model catalog, stream events, model registry, credential resolution, `IModelRunner` |
| `PiNet.Providers` | Wire protocol implementations (OpenAI, Anthropic), unified `ChatService`, SseParser, Faux provider |
| `PiNet.AgentCore` | Agent state, agent loop, tool execution, hooks, events, built-in tools |
| `PiNet.AgentCore.Tests` | xunit suite: loop/event ordering, hooks, execution modes, terminate, built-in tool behavior |
| `PiNet.Sessions` | Session tree (JSONL), `SessionManager`, store, id/prefix resolution, compactor |
| `PiNet.Sessions.Tests` | xunit suite: round-trip, append watermark, fork/clone, resolution, tree/branch, compactor, multi-invocation ACs |
| `PiNet.Cli` | Argument parsing, print mode, agent mode, session flags, (future) interactive TUI |

## 5. Milestones

| Stage | Name | Status |
|-------|------|--------|
| 1 | Foundation & Unified LLM Abstraction | Verified (see [01-stage-1-unified-llm.md](01-stage-1-unified-llm.md)) |
| 2 | Agent Core: Tool-Calling Loop | Verified (see [02-stage-2-agent-core.md](02-stage-2-agent-core.md)) |
| 3 | Session Management (JSONL tree) | Verified (see [03-stage-3-sessions.md](03-stage-3-sessions.md)) |
| 4 | Interactive CLI & Terminal UI | Verified (see [04-stage-4-interactive-loop.md](04-stage-4-interactive-loop.md)) |
| 5 | Permissions & Tool Security | Verified (see [05-stage-5-permissions.md](05-stage-5-permissions.md)) |
| 6 | Extension System & Skills | Verified (see [06-stage-6-extensions-skills.md](06-stage-6-extensions-skills.md)) |
| — | Local & Custom LLM Providers (spec 07) | Verified (see [07-local-llm-providers.md](07-local-llm-providers.md)) |
| 7 | Advanced Features | Verified (see [08-stage-7-advanced-features.md](08-stage-7-advanced-features.md)) |
| 8 | Polish, Packaging & Release | Done — packaging/release verified locally; distribution via GitHub Release assets (NuGet.org publication intentionally skipped) |

## 6. Conventions

- C# `net10.0`, nullable reference types enabled, `System.Text.Json` source generation preferred.
- Async-first API surface (`IAsyncEnumerable`, `ValueTask`, cancellation tokens).
- Public types are `sealed` unless designed for inheritance.
- Third-party packages only when they earn their weight (see ADR-0001).