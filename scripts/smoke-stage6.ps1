<#
.SYNOPSIS
    Stage 6 manual smoke test: extension/skill/package wiring end-to-end.

.DESCRIPTION
    Runs, in order:

      0. Builds the solution (unless -SkipBuild).
      1. `pinet install npm:<pkg>`  -> a real npm tarball (network required).
      2. `pinet install git:<url>`  -> a local throwaway git bundle (no network),
         proving the npm-style tar + git clone + strip+install path.
      3. A real LLM invoking the sample `hello` extension tool, in agent mode
         (needs OPENAI_API_KEY or ANTHROPIC_API_KEY; for a local/self-hosted
         provider such as llama.cpp set PI_NET_PROVIDER and PI_NET_MODEL instead).
         Creates the sample extension in ~/.pinet/extensions/ if it is missing.

    Note: steps 1 and 3 need network/credentials; failures there are reported
    as WARNs and the rest of the smoke still runs. Step 3 also depends on the
    model actually choosing to call the tool, so it is informational.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\smoke-stage6.ps1

.EXAMPLE
    $env:OPENAI_API_KEY = "sk-..."
    .\scripts\smoke-stage6.ps1 -OpenAiModel gpt-4o

.EXAMPLE
    # Local llama.cpp server via models.json (see docs/testing-with-a-real-llm.md)
    $env:PI_NET_PROVIDER = "llama-cpp-"
    $env:PI_NET_MODEL = "Qwen3.8-27B-GSQ-RCO-IQ3_S-mtp"
    .\scripts\smoke-stage6.ps1
#>
[CmdletBinding()]
param(
    [string]$Project = "src/PiNet.Cli/PiNet.Cli.csproj",

    [switch]$SkipBuild,

    [string]$NpmPackage = "left-pad",

    [string]$GitRepoUrl = "",

    [string]$OpenAiModel = "gpt-4o",

    [string]$AnthropicModel = "claude-sonnet-4-20250514"
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$projectPath = Join-Path $repoRoot $Project
$dotnet = Get-Command dotnet -ErrorAction Stop | Select-Object -ExpandProperty Source
$results = @()

function Write-Step([string]$label) {
    Write-Host ""
    Write-Host ("=== " + $label + " ===") -ForegroundColor Cyan
}

function Invoke-Pinet([string[]]$subArgs) {
    # Native stderr must not trip $ErrorActionPreference = "Stop"; steps inspect
    # $LASTEXITCODE and downgrade to WARN/SKIP on their own (e.g. npm missing).
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        & $dotnet "run" "--project" $projectPath "--" @subArgs 2>&1 | Out-String
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

function Assert-Contains([string]$text, [string]$needle, [string]$step) {
    if ($text -match [regex]::Escape($needle)) {
        # $script: is required: += inside a function would only touch a local copy.
        $script:results += , @($step, "PASS")
        Write-Host ("[PASS] " + $step + "  (" + $needle + ")") -ForegroundColor Green
    }
    else {
        $script:results += , @($step, "FAIL")
        Write-Host ("[FAIL] " + $step + "  expected substring was not found: " + $needle) -ForegroundColor Red
    }
}

Write-Host "Pi.Net Stage 6 smoke test" -ForegroundColor Magenta
Write-Host ("repo: " + $repoRoot)

# ---------------------------------------------------------------- 0. build
if (-not $SkipBuild) {
    Write-Step "0. Build solution"
    & $dotnet "build" (Join-Path $repoRoot "PiNet.slnx") 2>&1 | Out-String | Write-Verbose
    if ($LASTEXITCODE -ne 0) { throw "build failed (exit $LASTEXITCODE)" }
    Write-Host "build OK" -ForegroundColor Green
}

# -------------------------------------------------------------- 1. npm pack
Write-Step ("1. pinet install npm:" + $NpmPackage)
$npmInstalled = $false
$npmOut = Invoke-Pinet @("install", ("npm:" + $NpmPackage))
if ($LASTEXITCODE -ne 0) {
    Write-Warning ("npm install failed (exit " + $LASTEXITCODE + "):" + $npmOut + " - network or registry issue? Skipping, continuing.")
    $results += , @("npm install", "SKIP")
}
else {
    Assert-Contains $npmOut "installed" "npm install"
    $npmInstalled = $true
}

# list shows the newly recorded package. Note: for a bare npm package the
# recorded name comes from the extracted folder ("pkg", v0.0.0) unless the
# tarball ships a pinet.json, so we only assert the list is non-empty here.
$listOut = Invoke-Pinet @("list")
if (-not $npmInstalled) {
    $results += , @("npm install visible in list", "SKIP")
}
elseif ($listOut -match "\(no packages installed\)") {
    $results += , @("npm install visible in list", "FAIL")
    Write-Host "[FAIL] npm install visible in list  (list stayed empty)" -ForegroundColor Red
}
else {
    $results += , @("npm install visible in list", "PASS")
    Write-Host "[PASS] npm install visible in list" -ForegroundColor Green
}

# ----------------------------------------------------------------- 2. git
Write-Step "2. pinet install git:<local bundle>"
$git = Get-Command git -ErrorAction SilentlyContinue
if ($null -eq $git) {
    Write-Warning "git not found - skipping git smoke."
    $results += , @("git install", "SKIP")
}
else {
    $tmp = Join-Path $env:TEMP ("pinet-smoke-git-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
    try {
        New-Item -ItemType Directory -Path (Join-Path $tmp "extensions") | Out-Null
        Set-Content -LiteralPath (Join-Path $tmp "pinet.json") -Value '{"name":"smoke-git-bundle","version":"1.2.3"}'
        Set-Content -LiteralPath (Join-Path $tmp "extensions\sample.cs") -Value '// smoke bundle: nothing meaningful to compile, just structure'
        & $git "init" "-b" "main" $tmp 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "git init failed" }
        & $git "-C" $tmp "config" "user.email" "smoke@pinet.dev" 2>&1 | Out-Null
        & $git "-C" $tmp "config" "user.name" "Smoke" 2>&1 | Out-Null
        & $git "-C" $tmp "add" "-A" 2>&1 | Out-Null
        & $git "-C" $tmp "commit" "-m" "bundle" 2>&1 | Out-Null

        $source = if ($GitRepoUrl) { $GitRepoUrl } else { $tmp }
        $gitOut = Invoke-Pinet @("install", ("git:" + $source))
        if ($LASTEXITCODE -eq 0) {
            Assert-Contains $gitOut "installed smoke-git-bundle" "git install"
        }
        else {
            Write-Warning ("git install failed: " + $gitOut)
            $results += , @("git install", "FAIL")
        }
    }
    catch {
        Write-Warning ("local git bundle failed: " + $_.Exception.Message)
        $results += , @("git install", "FAIL")
    }
    finally {
        try { Remove-Item -LiteralPath $tmp -Recurse -Force } catch { }
    }
}

# ----------------------------------------------------------------- 3. LLM
Write-Step "3. Real LLM invokes the hello extension tool in agent mode"
$apiKey = if ($env:OPENAI_API_KEY) { $env:OPENAI_API_KEY } else { $env:ANTHROPIC_API_KEY }
if (-not $apiKey -and -not $env:PI_NET_PROVIDER) {
    Write-Host "No OPENAI_API_KEY / ANTHROPIC_API_KEY (or PI_NET_PROVIDER) set - skipping the LLM step (set one and re-run to include it)." -ForegroundColor Yellow
    $results += , @("LLM extension tool", "SKIP")
}
else {
    $homeDir = if ($env:PI_NET_HOME) { $env:PI_NET_HOME } else { Join-Path $env:USERPROFILE ".pinet" }
    $extDir = Join-Path $homeDir "extensions"
    $sample  = Join-Path $extDir "hello.cs"
    if (-not (Test-Path -LiteralPath $sample)) {
        New-Item -ItemType Directory -Path $extDir -Force | Out-Null
        Set-Content -LiteralPath $sample -Value @'
using System.Threading;
using System.Threading.Tasks;
using PiNet.Extensions;

public sealed class HelloExtension : IExtension
{
    public void Register(IExtensionContext ctx)
    {
        ctx.RegisterTool(new ExtensionToolSpec(
            "hello",
            "Say hello",
            null,
            (callId, args, ct) => Task.FromResult(new ExtensionToolResult("Hello from extension!"))));
    }
}
'@
        Write-Host ("Created sample extension: " + $sample) -ForegroundColor Yellow
    }

    $provider = if ($env:PI_NET_PROVIDER) { $env:PI_NET_PROVIDER } elseif ($env:OPENAI_API_KEY) { "openai" } else { "anthropic" }
    $model    = if ($env:PI_NET_MODEL) { $env:PI_NET_MODEL } elseif ($env:OPENAI_API_KEY) { $OpenAiModel } else { $AnthropicModel }
    $prompt   = "Please call the hello tool and report exactly what it returns."

    Write-Host ("provider/model: " + $provider + "/" + $model)
    # Agent mode (prompt as a positional argument, not -p): the plain print path sends no
    # tools, so the model could never call the extension. --approve skips the permission
    # prompt for the run.
    $llmOut = Invoke-Pinet @("--agent", "--approve", "--provider", $provider, "--model", $model, $prompt)
    $llmExit = $LASTEXITCODE
    $llmTail = ($llmOut -split "`r?`n" | Select-Object -Last 15) -join "`n"

    if ($llmExit -ne 0) {
        # Configuration problem (e.g. unknown model / provider missing from models.json).
        Write-Host ("[FAIL] the agent run failed (exit " + $llmExit + "):") -ForegroundColor Red
        Write-Host $llmTail
        $results += , @("LLM extension tool", "FAIL")
    }
    elseif ($llmOut -match [regex]::Escape("Hello from extension!")) {
        $results += , @("LLM extension tool", "PASS")
        Write-Host "[PASS] the model called hello and reported 'Hello from extension!'" -ForegroundColor Green
    }
    else {
        # Informational: the agent ran, but the model may not have chosen to call the tool.
        Write-Warning "The agent ran, but 'Hello from extension!' was not found in its reply. Last lines of the run:"
        Write-Host $llmTail
        $results += , @("LLM extension tool", "INFO")
    }
}

# ------------------------------------------------------------- summary
Write-Host ""
Write-Step "Summary"
foreach ($r in $results) {
    $color = switch ($r[1]) {
        "PASS" { "Green" }
        "SKIP" { "Yellow" }
        "INFO" { "Cyan" }
        default { "Red" }
    }
    Write-Host ("  [{0,-6}] {1}" -f $r[1], $r[0]) -ForegroundColor $color
}

if ($results | Where-Object { $_[1] -eq "FAIL" }) {
    Write-Host ""
    Write-Host "One or more steps FAILED." -ForegroundColor Red
    exit 1
}
exit 0