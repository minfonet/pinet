# Pi.NET

A minimal terminal coding harness for .NET 10, inspired by the
[Pi Agent Harness](https://github.com/earendil-works/pi): keep the core small and
make everything else extensible. Zero runtime dependencies beyond the BCL, built
with **Spec-Driven Development** (spec first, then verify against the running CLI).

## Status

| Item | State |
|------|-------|
| Stages | **1–6 implemented and verified** against the running CLI |
| Build | `dotnet build PiNet.slnx` — clean (0 warnings, 0 errors) |
| Tests | **279 passing** (see verification commands below) |
| Next | Stage 7 — Advanced Features |

See [`STATUS.md`](STATUS.md) and [`IMPLEMENTATION_PLAN.md`](IMPLEMENTATION_PLAN.md)
for the full stage-by-stage record.

## Quick start

```bash
dotnet build PiNet.slnx

# Deterministic offline run with the built-in Faux provider
dotnet run --project src/PiNet.Cli -- --provider faux --model faux-model -p "hi"

# List available models
dotnet run --project src/PiNet.Cli -- --list-models

# Interactive loop
dotnet run --project src/PiNet.Cli -- -i
```

Set `OPENAI_API_KEY` (or `ANTHROPIC_API_KEY`) to talk to a real model:

```bash
dotnet run --project src/PiNet.Cli -- --provider openai --model gpt-4o -p "hello"
```

## Feature map

| Stage | Name | Highlights |
|-------|------|------------|
| 1 | Foundation & Unified LLM | Raw-HTTP + SSE providers (OpenAI, Anthropic, Faux), unified `StreamEvent` protocol |
| 2 | Agent Core | Deterministic tool-calling loop, hooks, parallel tool execution, built-in tools |
| 3 | Sessions | Append-only JSONL session tree, fork/resume/continue, LLM compaction |
| 4 | Interactive loop | Zero-dependency TUI, line editor + history, slash commands, Ctrl+C semantics |
| 5 | Permissions & tool security | Rule resolver (safety floor, deny-sticky), `/trust`, `--approve`, path globs |
| 6 | **Extensions & Skills** | Extension host, skills, prompt templates, context files, package manager |
| 7 | Advanced Features | not started |
| 8 | Packaging & Release | not started |

Beyond the staged work, [spec 07](docs/spec/07-local-llm-providers.md) adds
Pi-compatible local/custom LLM providers (`~/.pinet/models.json`).

## Stage 6 — Extensions & Skills (new)

Extensions, skills, prompt templates and context files are three discovery layers
that extend Pi from the user's home directory or the project tree — no CLI source
changes required — plus a small package manager to ship them together. All paths
resolve against the Pi home (`~/.pinet`, or `PI_NET_HOME`) and the working directory.

### Extension host

Extensions are **plain C# files** compiled at startup with the Roslyn compiler
(`csc.dll`) that ships inside the .NET SDK. They reference only the BCL plus
`PiNet.Extensions` and `PiNet.Abstractions`.

| Location | Path |
|----------|------|
| Global | `~/.pinet/extensions/*.cs` |
| Project | `./.pi/extensions/*.cs` |
| Packages | `<pkg>/extensions/*.cs` |

```csharp
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
            """{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}""",
            (callId, args, ct) => Task.FromResult(new ExtensionToolResult("Hello from extension!"))));

        ctx.RegisterCommand("hello", (args, c) =>
        {
            c.WriteLine("hello from command");
            return Task.FromResult(true);
        });
    }
}
```

- Tools are exposed to the agent alongside the built-ins (JSON parameter schema
  converted to the agent's `ToolParameterSchema`); commands become `/name` slash
  commands.
- Names colliding with built-ins are skipped (built-ins win) and reported as
  startup warnings.
- Sources are compiled together and cached in `~/.pinet/cache/extensions/` by
  content hash; compile failures print as warnings and Pi continues.
- `--no-extensions` disables the whole layer.

### Skills

Skills are small, self-describing Markdown instructions the model can be told
about, discovered under `skills/` directories:

| Location | Path | Rule |
|----------|------|------|
| Global | `~/.pinet/skills/*.md`, `*/SKILL.md` | standard |
| Shared | `~/.agents/skills/` | agents convention (root `.md` ignored, nested picked) |
| Project (ancestors) | `./.pi/skills/` and `./.agents/skills/` | standard / agents |
| Packages | `<pkg>/skills/` | standard |

```markdown
---
name: ensure-pr
description: Run the PR checklist after implementing a feature
---
1. Read the diff.
2. ...
```

- `name` must be lowercase `[a-z0-9-]` (max 64); `description` is required and
  shown to the model; `disable-model-invocation: true` hides it from the model.
- Invoke with `/skill:<name> [args]`; the system prompt gets an `<available_skills>`
  XML block. Duplicates are skipped with a warning. `--no-skills` disables discovery.

### Prompt templates

Reusable prompt fragments expanded inline when you type `/name args`. Found in
global + project `.pi/prompts/*.md` and package `prompts/`.

```markdown
---
description: Review a changeset
argument-hint: files...
---
Review $1 with focus on $@.
```

| Token | Meaning |
|-------|---------|
| `$1`, `$2`, … | positional arguments (missing → empty) |
| `$@`, `$ARGUMENTS` | all arguments, space-joined |
| `$0` | the template name |
| `${1:-default}` / `${@:-default}` | value, or default when empty |
| `${@:N}` / `${@:N:L}` | slice of the argument list |

Unknown `$X` tokens are left as-is. `--no-prompt-templates` disables discovery.

### Context files

`SYSTEM.md` (replaces the base system prompt), `APPEND_SYSTEM.md`, `AGENTS.md`
(resolved from the nearest ancestor) and `CLAUDE.md` (fallback) are loaded from
the working directory and assembled into the system prompt.

### Packages

```bash
pinet install ./my-bundle        # local directory
pinet install ./my-bundle.tgz    # gzipped tar (npm-style)
pinet install npm:my-pkg         # runs `npm pack` in a temp dir
pinet install git:https://…      # shallow-clones the repo
pinet list
pinet remove <name>
pinet update [name]
```

Bundles install into `~/.pinet/packages/<name>` and are recorded in
`~/.pinet/packages.json`. A bundle is a directory or archive with an optional
`pinet.json` (`name`/`version`) and any of `extensions/`, `skills/`, `prompts/`.

- `pinet.json` is optional (defaults: name = folder name, version `0.0.0`).
- Tar extraction strips a leading `package/` prefix, rejects absolute paths and
  `..` traversal (zip-slip), and skips pax entries.
- Reinstalling the same name replaces it; `update` re-applies the recorded source.

Full reference: [`docs/extensions.md`](docs/extensions.md).

## Local & custom models

Pi.NET talks to any OpenAI-compatible or Anthropic-compatible endpoint (Ollama,
LM Studio, llama.cpp, vLLM, self-hosted gateways) declared in
`~/.pinet/models.json` (`PI_NET_HOME` override) — no code changes:

```json
{
  "providers": {
    "ollama": {
      "baseUrl": "http://localhost:11434/v1",
      "api": "openai-completions",
      "apiKey": "ollama",
      "models": [ { "id": "llama3.1:8b", "name": "Llama 3.1 8B (Local)" } ]
    }
  }
}
```

```bash
dotnet run --project src/PiNet.Cli -- --list-models
dotnet run --project src/PiNet.Cli -- --provider ollama --model llama3.1:8b -p "hello"
```

Supported `api` values are `openai-completions` and `anthropic-messages`;
`apiKey` is a literal or a `$ENV_VAR` reference. Built-in providers are always
kept, and a configured `openai`/`anthropic` key merges by model `id`. A missing
or malformed file falls back to the built-ins with a warning. Full reference:
[`docs/local-llms.md`](docs/local-llms.md).

## Manual smoke tests

[`scripts/smoke-stage6.ps1`](scripts/smoke-stage6.ps1) runs the Stage 6 wiring
end-to-end on a networked machine:

1. builds the solution (`-SkipBuild` to skip);
2. `pinet install npm:<pkg>` against a real registry;
3. `pinet install git:<local bundle>` through a throwaway git repo (no network);
4. a real LLM invoking the sample `hello` extension tool (requires
   `OPENAI_API_KEY` or `ANTHROPIC_API_KEY`; creates `~/.pinet/extensions/hello.cs`
   if missing).

Network/key-dependent steps report `WARN`/`SKIP` instead of failing the run; the
final step is informational (depends on the model choosing to call the tool).

```powershell
powershell -ExecutionPolicy Bypass -File scripts\smoke-stage6.ps1
$env:OPENAI_API_KEY = "sk-..."; .\scripts\smoke-stage6.ps1 -OpenAiModel gpt-4o
```

## Verification commands

```bash
dotnet build PiNet.slnx        # 0 warnings, 0 errors
dotnet test PiNet.slnx         # 279/279 passing
dotnet run --project src/PiNet.Cli -- --list-models
dotnet run --project src/PiNet.Cli -- --provider faux --model faux-model -p "hi"
```

## Documentation

- [`docs/README.md`](docs/README.md) — index of specs and guides (Spec-Driven Development)
- [`docs/extensions.md`](docs/extensions.md) — extensions, skills, templates, packages reference
- [`docs/local-llms.md`](docs/local-llms.md) — local & custom LLM providers (`~/.pinet/models.json`)
- [`docs/permissions.md`](docs/permissions.md) — permission rules, `/trust`, `--approve`, sandbox template
- [`docs/spec/`](docs/spec/) — per-stage specifications with verification logs
- [`STATUS.md`](STATUS.md) — project status and stage-by-stage record
- [`IMPLEMENTATION_PLAN.md`](IMPLEMENTATION_PLAN.md) — roadmap