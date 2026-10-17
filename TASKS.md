# Pi.NET — Task List

> Generated from the 2026-10-16 project analysis (build clean, 396/396 tests
> passing).
> Companion to [`STATUS.md`](STATUS.md) and [`IMPLEMENTATION_PLAN.md`](IMPLEMENTATION_PLAN.md).

## Deferred features

- [ ] **7e — clipboard/paste image input** (platform-dependent; optional per spec).
- [ ] **7g — settings hot-reload** during a running session (restart picks changes up).
- [ ] **Stage 6 network smoke tests**: `scripts/smoke-stage6.ps1` against a real
  registry (`pinet install npm:`/`git:`) and a real LLM invoking an extension tool
  (needs `OPENAI_API_KEY`/`ANTHROPIC_API_KEY`; see
  [`docs/testing-with-a-real-llm.md`](docs/testing-with-a-real-llm.md)).

## Quality & documentation

- [x] **Print-mode token/cost summary test** (`PrintModeCliTests`).
- [x] **Malformed permission config startup error test**
  (`ConfigTests.Load_MalformedProjectConfig_Throws`).
- [x] **Re-audit the "Pending Tests Summary"** in `IMPLEMENTATION_PLAN.md` — the
  table was stale; most rows were already covered (fork/clone, sequential mode,
  `AfterToolCall` mutation, abort, credential fallback, `disable-model-invocation`,
  template slices, extension cache reuse, Ctrl+C exit code, `/new` ephemeral,
  `PI_NET_HOME` for permissions).
- [ ] **Coverage measurement** for AgentCore/Sessions/Providers (Stage 8 target >80%);
  either measure with a collector or adjust the criterion in the plan.

## Done (2026-10-16)

- [x] **7a.2 implemented** — interactive-only keystroke pump during turns
  (Enter → steer, Alt+Enter → follow-up, Ctrl+C cancels the turn, unsubmitted
  text carries over to the next prompt); coordinated with the permission prompt
  via `InputArbiter`; spec 08 §3.3 acceptance criteria + 5 tests
  (`SteeringCaptureTests`).
- [x] Pending test gaps closed: print-mode tokens/cost
  (`PrintModeCliTests`), malformed permission config startup error
  (`ConfigTests` ×2), above-threshold auto-compaction
  (`ContextCompactorTests.AboveThreshold_AutoCompacts`).
- [x] `IMPLEMENTATION_PLAN.md` test-table re-audit (now a coverage summary with
  evidence per row).
- [x] Doc/test-count consistency pass (README, STATUS, CHANGELOG, spec 08,
  plan): 396 tests passing (after the real-LLM tool-calling and Windows
  packaging fixes).
- [x] **Distribution decision applied**: no NuGet.org publication — README,
  STATUS, CHANGELOG, plan, spec 00, `docs/packaging.md`, implementation notes
  and the install scripts (local feed) updated; the tool `.nupkg` is produced
  by `dotnet pack` and installed from a local feed.
- [x] **New/rewritten guides (English)**:
  [`docs/extensions.md`](docs/extensions.md) — complete *creating Pi-style
  extensions* guide (host pipeline, API reference, examples, testing,
  packaging, skills/templates/context files, Pi parity);
  [`docs/testing-with-a-real-llm.md`](docs/testing-with-a-real-llm.md) —
  provider setup, print/agent/interactive checks, sessions, smoke script,
  checklist and troubleshooting.
- [x] **Graphify graph regenerated** for the current HEAD (`graph/`, gitignored
  local artifact; counts in `graph/GRAPH_REPORT.md`).

## Done (2026-10-17)

- [x] **`/model` picker accepts a selection** (completes spec 08 §7d) — `/model`
  (or `/models`) with no argument lists numbered models and accepts a pick by
  number or `provider/id`; `q` cancels, invalid numbers are rejected. 4 new
  `InteractiveLoopTests` + parser alias test (401 tests total).
- [x] **`run.bat` hardened after the "black window" report** — builds with
  `-tl:off` (plain output; no screen-clearing live terminal logger), launches
  the built executable directly, warns when a previous `pinet.exe` keeps
  `bin\...\pinet.exe` locked, and pauses on failure. Two stuck launches were
  diagnosed: one apphost suspended at 2 MB (terminal handoff never resumed it)
  and one app running fine inside a hidden console with no terminal window.
