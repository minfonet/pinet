# Packaging

Pi.NET ships three ways (there is **no NuGet.org publication**):

1. **Global .NET tool** installed from a local package feed (`dotnet pack` +
   `dotnet tool install -g PiNet --add-source …`) — the default install.
2. **Self-contained single-file binaries** per RID (no .NET runtime needed),
   produced locally (`scripts/publish.ps1`).
3. **From source** (`dotnet build` / `dotnet run`).

## Build

```bash
dotnet build PiNet.slnx -c Release   # 0 warnings, 0 errors
dotnet test PiNet.slnx -c Release    # 401 tests
```

## Package (global tool)

```bash
dotnet pack src/PiNet.Cli/PiNet.Cli.csproj -c Release -o artifacts/packages
# → artifacts/packages/PiNet.<version>.nupkg
```

Package metadata lives in `src/PiNet.Cli/PiNet.Cli.csproj` (`PackageId=PiNet`,
`ToolCommandName=pinet`, `PackAsTool=true`).

### Install / update

The install scripts pack Pi.NET from source when needed and install from a
local feed. Pass an existing feed directory (for example one holding a
`.nupkg` produced by `dotnet pack`) to skip the pack:

```powershell
.\install.ps1                       # Windows (packs from source)
.\install.ps1 C:\path\to\feed       # Windows (existing .nupkg directory)
```
```bash
./install.sh                        # Linux / macOS (packs from source)
./install.sh /path/to/feed          # Linux / macOS (existing .nupkg directory)
```

or directly:

```bash
dotnet pack src/PiNet.Cli/PiNet.Cli.csproj -c Release -o artifacts/packages
dotnet tool install -g PiNet --add-source artifacts/packages
dotnet tool update  -g PiNet --add-source artifacts/packages
```

`pinet update --self` delegates to `dotnet tool update -g PiNet` and prints
guidance when PiNet is not installed as a global tool. When you installed from
a local feed, run the update with `--add-source <feed>` yourself or re-run the
install script after the new package is in the feed. (`pinet update [name]`
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

## CI

- `.github/workflows/ci.yml` — build + test on push/PR (ubuntu).

### Versions

The package version lives in `src/PiNet.Cli/PiNet.Cli.csproj` (`Version`);
override it per pack/publish with `-p:Version=x.y.z`.

## Configuration layout (runtime)

| Path | Purpose |
|------|---------|
| `~/.pinet/settings.json` (`$PI_NET_HOME`) | Global settings |
| `<cwd>/.pi/settings.json` | Project settings (override global) |
| `~/.pinet/sessions/` | Session JSONL files (`PI_NET_HOME/sessions`) |
| `~/.pinet/models.json` | Custom/local LLM providers (see `local-llms.md`) |
| `~/.pinet/packages/` | Installed extension packages |
| `~/.pinet/permissions.json`, `~/.pinet/trust.json` | Permission rules and trusted directories |
