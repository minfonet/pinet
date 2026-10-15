# Packaging & Release

Pi.NET ships three ways:

1. **Global .NET tool** (`dotnet tool install -g PiNet`) — the default install.
2. **Self-contained single-file binaries** per RID (no .NET runtime needed).
3. **From source** (`dotnet build` / `dotnet run`).

## Build

```bash
dotnet build PiNet.slnx -c Release   # 0 warnings, 0 errors
dotnet test PiNet.slnx -c Release    # 370 tests
```

## Package (global tool)

```bash
dotnet pack src/PiNet.Cli/PiNet.Cli.csproj -c Release -o artifacts/packages
# → artifacts/packages/PiNet.<version>.nupkg
```

Package metadata lives in `src/PiNet.Cli/PiNet.Cli.csproj` (`PackageId=PiNet`,
`ToolCommandName=pinet`, `PackAsTool=true`).

### Install / update

```powershell
.\install.ps1                       # Windows
```
```bash
./install.sh                        # Linux / macOS
```

or directly:

```bash
dotnet tool install -g PiNet
dotnet tool update  -g PiNet        # what `pinet update --self` runs
```

`pinet update --self` delegates to `dotnet tool update -g PiNet` and prints
guidance when PiNet is not installed as a global tool. (`pinet update [name]`
still updates *extension packages*.)

## Self-contained binaries

```powershell
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
# → artifacts/publish/<rid>/pinet[.exe]

powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Rids win-x64 -Version 0.2.0
```

or directly:

```bash
dotnet publish src/PiNet.Cli/PiNet.Cli.csproj -c Release \
  -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:PublishTrimmed=false \
  -o artifacts/publish/win-x64
```

## CI / Release

- `.github/workflows/ci.yml` — build + test on push/PR (ubuntu).
- `.github/workflows/release.yml` — on `v*` tags (or manual dispatch):
  1. runs the test suite and packs the global tool (version from the tag),
  2. publishes self-contained binaries for `win-x64`, `linux-x64`, `osx-arm64`
     and zips them,
  3. pushes to NuGet.org **when `NUGET_API_KEY` is set** (skipped otherwise),
  4. creates the GitHub release with the `.nupkg` and per-RID `.zip` assets
     (`gh release create`, release notes auto-generated).

### Cutting a release

```bash
git tag v0.2.0
git push origin v0.2.0
```

No version is committed in advance: the workflow stamps the tag into the pack
and publish commands.

## Configuration layout (runtime)

| Path | Purpose |
|------|---------|
| `~/.pinet/settings.json` (`$PI_NET_HOME`) | Global settings |
| `<cwd>/.pi/settings.json` | Project settings (override global) |
| `~/.pinet/sessions/` | Session JSONL files (`PI_NET_HOME/sessions`) |
| `~/.pinet/models.json` | Custom/local LLM providers (see `local-llms.md`) |
| `~/.pinet/packages/` | Installed extension packages |
| `~/.pinet/permissions.json`, `~/.pinet/trust.json` | Permission rules and trusted directories |
