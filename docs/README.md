# Pi.NET Documentation

This folder contains all project documentation, written using the **Spec-Driven Development (SDD)** method.

## SDD Workflow

1. Write the **specification** for a feature *before* writing its implementation
   (behavior specs, contracts, and acceptance criteria).
2. Implement the code so that it satisfies the spec.
3. Verify each spec item against the running implementation.
4. Mark the spec item `Verified` and record how it was verified.

## Status Legend

| Status | Meaning |
|--------|---------|
| `Proposed` | Spec written, implementation not started |
| `In Progress` | Implementation underway |
| `Implemented` | Code exists, not yet fully verified |
| `Verified` | Behavior confirmed against running code |

## Index

### Specifications (spec/)

| Doc | Stage | Status |
|-----|-------|--------|
| [00-project-overview.md](spec/00-project-overview.md) | All | Overall vision, architecture, milestones |
| [01-stage-1-unified-llm.md](spec/01-stage-1-unified-llm.md) | 1 — Foundation & Unified LLM | Verified |
| [02-stage-2-agent-core.md](spec/02-stage-2-agent-core.md) | 2 — Agent Core | Verified |
| [03-stage-3-sessions.md](spec/03-stage-3-sessions.md) | 3 — Sessions (JSONL tree) | Verified |
| [04-stage-4-interactive-loop.md](spec/04-stage-4-interactive-loop.md) | 4 — Interactive CLI Loop | Verified |
| [05-stage-5-permissions.md](spec/05-stage-5-permissions.md) | 5 — Permissions & Tool Security | Verified |
| [06-stage-6-extensions-skills.md](spec/06-stage-6-extensions-skills.md) | 6 — Extensions & Skills | Verified |
| [07-local-llm-providers.md](spec/07-local-llm-providers.md) | 6 — Local & Custom LLM Providers | Verified |
| [08-stage-7-advanced-features.md](spec/08-stage-7-advanced-features.md) | 7 — Advanced Features | Verified |

### Architecture Decision Records (adr/)

| Doc | Decision |
|-----|----------|
| [0001-raw-http-over-sdks.md](adr/0001-raw-http-over-sdks.md) | Implement LLM providers with raw HTTP + SSE rather than vendor SDKs |
| [0002-stream-event-model.md](adr/0002-stream-event-model.md) | Unified `StreamEvent` discriminated union across providers |
| [0003-imodelrunner-in-abstractions.md](adr/0003-imodelrunner-in-abstractions.md) | Agent core depends only on `Abstractions` via `IModelRunner` |

### Guides

| Doc | Topic |
|-----|-------|
| [permissions.md](permissions.md) | Permission rules, config files, `/trust`, `--approve`, container template |
| [extensions.md](extensions.md) | Extensions, skills, prompt templates, context files, packages (`pinet install`) |
| [local-llms.md](local-llms.md) | Local & custom LLM providers via `~/.pinet/models.json` (Ollama, LM Studio, vLLM, …) |
| [packaging.md](packaging.md) | Packaging & distribution (tool install, install scripts, CI) |

### Project records

| Doc | Topic |
|-----|-------|
| [`../STATUS.md`](../STATUS.md) | Current status, test counts and stage-by-stage record |
| [`../IMPLEMENTATION_PLAN.md`](../IMPLEMENTATION_PLAN.md) | Full stage plan and remaining/deferred work |
| [`../CHANGELOG.md`](../CHANGELOG.md) | Release-notes style history of every stage |
| [implementation/](implementation/) | Implementation notes per sub-stage (07c–08) |

### Scripts

| Doc | Topic |
|-----|-------|
| [`../scripts/smoke-stage6.ps1`](../scripts/smoke-stage6.ps1) | Manual Stage 6 smoke test: `npm:`/`git:` installs and a real LLM invoking an extension tool |

## Conventions

- All docs are written in **English**.
- Spec item IDs follow the scheme `SR-<stage>.<sequence>` (Spec Requirement).
- Behavior specs use Given / When / Then phrasing.
- Code samples in specs are illustrative; the implementing code may use
  internal helpers as long as the observable behavior matches.