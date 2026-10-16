# Pi.NET — Task List

> Generated from the 2026-10-16 project analysis (build clean, 371/371 tests
> passing at analysis time; 380/380 after the follow-up work below).
> Companion to [`STATUS.md`](STATUS.md) and [`IMPLEMENTATION_PLAN.md`](IMPLEMENTATION_PLAN.md).

## P0 — Release (requires the maintainer's accounts/credentials)

- [ ] **Publish the first package to NuGet.org**
  - `dotnet pack src/PiNet.Cli/PiNet.Cli.csproj -c Release -o artifacts/packages`
  - `dotnet nuget push artifacts/packages/PiNet.0.1.0.nupkg --source https://api.nuget.org/v3/index.json --api-key $NUGET_API_KEY`
- [ ] **Configure the GitHub remote** (`git remote add origin …`), push `main`,
  set the `NUGET_API_KEY` secret, and push the `v0.1.0` tag so `.github/workflows/release.yml`
  runs (tests + pack + self-contained `win-x64`/`linux-x64`/`osx-arm64` binaries +
  NuGet push + GitHub Release). Neither workflow has ever run: the repo is local-only
  (no remote, no tags).
- [ ] **Verify the published tool**: `dotnet tool install -g PiNet`, `pinet --help`,
  `pinet -p "hello"`, `pinet update --self`.
- [ ] **Decide SBOM/signing** (document "none" per the zero-dependency posture, or add
  SBOM generation to the release workflow).

## P1 — Deferred features

- [ ] **7a.2 — in-flight keystroke capture** (Enter → steer, Alt+Enter → follow-up
  during a running turn). Implemented in `InteractiveLoop` with an interactive-only
  input pump; spec 08 §3.3 carries the acceptance criteria and tests.
- [ ] **7e — clipboard/paste image input** (platform-dependent; optional per spec).
- [ ] **7g — settings hot-reload** during a running session (restart picks changes up).
- [ ] **Stage 6 network smoke tests**: `scripts/smoke-stage6.ps1` against a real
  registry (`pinet install npm:`/`git:`) and a real LLM invoking an extension tool
  (needs `OPENAI_API_KEY`/`ANTHROPIC_API_KEY`).

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
- [ ] **Post-release doc pass**: README/STATUS/CHANGELOG/spec 00 "published" state +
  NuGet link.

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
  plan): 380 tests passing.
