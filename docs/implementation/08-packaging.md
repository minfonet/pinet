# Stage 8 - Packaging / CI / Release (Implementation Notes)

Delivered:

- **Global tool packaging** (`src/PiNet.Cli/PiNet.Cli.csproj`):
  `PackAsTool` + `ToolCommandName=pinet`, `PackageId=PiNet`, `Version=0.1.0`,
  `AssemblyName=pinet` (self-contained binaries are called `pinet`/`pinet.exe`),
  description/tags and the repo README packed as `PackageReadmeFile`.
- **Install scripts**: `install.ps1` / `install.sh` (pack from source when
  needed, then `dotnet tool install -g PiNet --add-source <feed>`; an existing
  feed directory can be passed as the first argument).
- **Self-update**: `pinet update --self` delegates to `dotnet tool update -g PiNet`
  (`PackageCommands.SelfUpdateAsync`), streaming dotnet's output and printing
  guidance when PiNet is not a global tool. `pinet update [name]` still updates
  extension packages.
- **CI** (`.github/workflows/ci.yml`): restore + build + test on push/PR (ubuntu).
- **Release** (`.github/workflows/release.yml`): on `v*` tags (or manual):
  tests, packs the tool with the tag as version, publishes self-contained
  single-file binaries for `win-x64`/`linux-x64`/`osx-arm64` (zipped), and
  creates the GitHub release with the `.nupkg` + per-RID `.zip` assets via
  `gh release create`. No NuGet.org push (decision 2026-10-16).
- **Local publish helper**: `scripts/publish.ps1` (`-Rids`, `-Version`).
- **Docs**: `docs/packaging.md` (build/pack/install/update/self-contained/
  release + runtime config layout).

Verified locally (Windows, .NET 10):

- `dotnet pack` → `artifacts/packages/PiNet.0.1.0.nupkg` (README included).
- `dotnet tool install PiNet --tool-path <tmp> --add-source artifacts/packages`
  → `pinet list` and `pinet --provider faux --model faux-model -p "hi"` work.
- `pinet update --self` without a global install → dotnet's error plus the
  "update from source / re-run the install script" guidance, exit 1.
- `dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true` →
  `artifacts/publish/win-x64/pinet.exe` runs the faux smoke test.

Bug fixed along the way: `Program` sliced the args before dispatching to
`PackageCommands`, so every `pinet install|list|remove|update` printed usage.
Regression covered by `PackageManagerCliTests.Program_DispatchesPackageSubcommands_WithFullArgs`
(`InternalsVisibleTo PiNet.Cli.Tests` added; `Program.RunAsync` is now internal).

Decided (2026-10-16): **no NuGet.org publication** — distribution is via
GitHub Release assets (per-RID zips + tool `.nupkg`) and local packs.
Still open (future): SBOM generation, telemetry (decided against for now:
zero-dependency posture), signing.
