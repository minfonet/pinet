# PowerShell installer for PiNet.
# Pi.NET is not published to NuGet.org: this packs the tool (unless an existing
# package feed is supplied) and installs the global tool from that local feed.
#
# Usage:
#   .\install.ps1                   # pack from source, then install
#   .\install.ps1 C:\path\to\feed   # install from a directory containing PiNet.*.nupkg
param(
    [string]$Source = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not $Source) {
    $Source = Join-Path $root "artifacts/packages"
    if (-not (Get-ChildItem $Source -Filter "PiNet.*.nupkg" -ErrorAction SilentlyContinue)) {
        dotnet pack (Join-Path $root "src/PiNet.Cli/PiNet.Cli.csproj") -c Release -o $Source
        if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed (exit $LASTEXITCODE)" }
    }
}

dotnet tool install -g PiNet --version 0.1.0 --add-source $Source
