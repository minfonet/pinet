# Publishes self-contained single-file PiNet binaries per RID.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Rids win-x64 -Version 0.2.0
param(
    [string[]]$Rids = @("win-x64", "linux-x64", "osx-arm64"),
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "artifacts/publish"
$project = Join-Path $root "src/PiNet.Cli/PiNet.Cli.csproj"

foreach ($rid in $Rids) {
    $dest = Join-Path $out $rid
    Write-Host "publishing $rid -> $dest"
    $publishArgs = @(
        "publish", $project, "-c", "Release", "-r", $rid,
        "--self-contained", "true",
        "-p:PublishSingleFile=true", "-p:PublishTrimmed=false",
        "-o", $dest
    )
    if ($Version) { $publishArgs += "-p:Version=$Version" }

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid (exit $LASTEXITCODE)" }
}

Write-Host "done: $out"
