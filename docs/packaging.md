# Packaging & Release

## Build
```bash
dotnet build PiNet.slnx -c Release
dotnet test PiNet.slnx -c Release
```

## Package
```bash
dotnet pack src/PiNet.Cli/PiNet.Cli.csproj -c Release -o artifacts/packages
```

## Install
```powershell
.\install.ps1
```
or
```bash
./install.sh
```

## CI
GitHub Actions workflow in `.github/workflows/ci.yml` builds and tests on push/PR.
