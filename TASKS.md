# Pi.NET — Task List

> Generated from the 2026-10-16 project analysis (build clean, 371/371 tests
> passing at analysis time; 380/380 after the follow-up work below).
> Companion to [`STATUS.md`](STATUS.md) and [`IMPLEMENTATION_PLAN.md`](IMPLEMENTATION_PLAN.md).

## P0 — Release (requires the maintainer's accounts/actions)

- [ ] **Configure the GitHub remote and cut the first release**
  - `git remote add origin <repo-url>`, push `main`, then push the `v0.1.0` tag
    so `.github/workflows/release.yml` runs (tests + pack + self-contained
    `win-x64`/`linux-x64`/`osx-arm64` binaries + GitHub Release assets: tool
    `.nupkg` + per-RID zips). No `NUGET_API_KEY` needed: **NuGet.org
    publication is intentionally skipped**.
  - Neither workflow has ever run: the repo is local-only (no remote, no tags).
- [ ] **Verify the release artifacts**: `dotnet tool install -g PiNet
  --add-source <dir-with-nupkg>` (or `install.ps1` / `install.sh`), `pinet --help`,
  `pinet -p "hello"`, `pinet update --self`; download one per-RID zip and run it.
- [ ] **Decide SBOM/signing** (document "none" per the zero-dependency posture, or add
  SBOM generation to the release workflow).

## P1 — Deferred features

- [ ] **7e — clipboard/paste image input** (platform-dependent; optional per spec).
- [ ] **7g — settings hot-reload** during a running session (restart picks changes up).
- [ ] **Stage 6 network smoke tests**: `scripts/smoke-stage6.ps1` against a real
  registry (`pinet install npm:`/`git:`) and a real LLM invoking an extension tool
  (needs `OPENAI_API_KEY`/`ANTHROPIC_API_KEY`; see
  [`docs/testing-with-a-real-llm.md`](docs/testing-with-a-real-llm.md)).

## P2 — Quality & documentation

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
- [ ] **Post-release doc pass**: once the GitHub remote/release exists, link the
  release page from README/STATUS and confirm the install paths there.

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
- [x] **NuGet.org publication decision applied**: no publication — README,
  STATUS, CHANGELOG, plan, spec 00, `docs/packaging.md`, implementation notes,
  `release.yml` (NuGet push step removed) and the install scripts (local feed)
  updated; the tool `.nupkg` ships as a GitHub Release asset.
- [x] **New/rewritten guides (English)**:
  [`docs/extensions.md`](docs/extensions.md) — complete *creating Pi-style
  extensions* guide (host pipeline, API reference, examples, testing,
  packaging, skills/templates/context files, Pi parity);
  [`docs/testing-with-a-real-llm.md`](docs/testing-with-a-real-llm.md) —
  provider setup, print/agent/interactive checks, sessions, smoke script,
  checklist and troubleshooting.
- [x] **Graphify graph regenerated** for the current HEAD (`graph/`, gitignored
  local artifact; counts in `graph/GRAPH_REPORT.md`).
