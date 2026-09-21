# Extensions, skills & prompt templates

Stage 6 adds three discovery layers that let a project extend Pi without
touching the CLI source, plus a small package manager to ship them together.

All paths below are resolved relative to the Pi home (`~/.pinet`, or `PI_NET_HOME`)
and the working directory.

## Extension host

Extensions are **plain C# files** compiled at startup with the Roslyn compiler
that ships inside the .NET SDK (`csc.dll`). They reference only the BCL plus
`PiNet.Extensions` and `PiNet.Abstractions`.

### Locations (all are scanned, in order)

| Location | Path |
| --- | --- |
| Global | `~/.pinet/extensions/*.cs` |
| Project | `./.pi/extensions/*.cs` |
| Packages | `<pkg>/extensions/*.cs` (every installed package) |

### Writing an extension

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

- Tools are exposed to the agent alongside the built-ins. Their JSON parameter
  schema is converted to the agent's `ToolParameterSchema` at startup
  (`ExtensionToolAdapter`).
- Commands become `/name` slash commands. `SendToAgentAsync(prompt)` queues a
  follow-up agent turn.
- Names that collide with built-ins are skipped (built-ins win) and reported as
  startup warnings.

### Compilation

- Source files in one directory are compiled together and cached in
  `~/.pinet/cache/extensions/<sha256>.dll`; the cache key is the source content
  plus the `PiNet.Extensions` version, so editing an extension recompiles it.
- If the `.cs` files fail to compile, the failure is printed as warnings and
  Pi continues without them.
- `--no-extensions` disables the whole layer.

## Skills

Skills are small, self-describing Markdown instructions the model can be told
about. They live under `skills/` directories.

| Location | Path | Discovery rule |
| --- | --- | --- |
| Global | `~/.pinet/skills/*.md`, `*/SKILL.md` | standard |
| Shared | `~/.agents/skills/` | agents convention (root `.md` ignored, nested picked) |
| Project (ancestors) | `./.pi/skills/` and `./.agents/skills/` | standard / agents |
| Packages | `<pkg>/skills/` | standard |

### Skill file format

```markdown
---
name: ensure-pr
description: Run the PR checklist after implementing a feature
---
1. Read the diff.
2. ...
```

`name` must be lowercase `[a-z0-9]` with `-` separators (max 64); `description`
is required (max 1024) and is shown to the model. `disable-model-invocation: true`
hides the skill from the model while keeping it usable via `/skill:`.

- Invocation: `/skill:<name> [args]` sends the skill body as a turn prompt.
- The system prompt gets an `<available_skills>` XML block listing loadable
  skills (the model can read the file and follow it).
- Duplicate names are skipped with a warning (first one wins).
- `--no-skills` disables discovery.

## Prompt templates

Reusable prompt fragments, expanded inline when you type `/name args`.

| Location | Path |
| --- | --- |
| Global | `~/.pinet/prompts/*.md` |
| Project | `./.pi/prompts/*.md` |
| Packages | `<pkg>/prompts/*.md` |

The template body is the file content after the frontmatter (`name` comes from
the filename; `description` falls back to the first line; `argument-hint` is
optional):

```markdown
---
description: Review a changeset
argument-hint: files...
---
Review $1 with focus on $@.
```

### Expansion

| Token | Meaning |
| --- | --- |
| `$1`, `$2`, … | 1-based positional arguments (missing → empty) |
| `$@`, `$ARGUMENTS` | all arguments, space-joined |
| `$0` | the template name |
| `$FOO` (unknown) | left as-is |
| `${1:-default}` | value or default when missing |
| `${@:-default}` | all args or default when empty |
| `${@:N}` | all args from index N |
| `${@:N:L}` | all args from index N, at most L items |

`--no-prompt-templates` disables discovery.

## Context files

`SYSTEM.md`, `APPEND_SYSTEM.md`, `AGENTS.md` and `CLAUDE.md` are loaded from the
working directory (AGENTS.md is also resolved from the nearest ancestor) and
assembled into the system prompt: base → `APPEND_SYSTEM.md` → project
instructions → skills XML.

## Packages

`pinet install <path|npm:<pkg>|git:<url>>` installs a bundle into
`~/.pinet/packages/<name>` and records it in `~/.pinet/packages.json`. A bundle
is a directory (or archive/tarball) with an optional `pinet.json`
(`name`/`version`) and any of `extensions/`, `skills/`, `prompts/`.

```text
pinet install ./my-bundle        # local directory
pinet install ./my-bundle.tgz    # gzipped tar (npm-style)
pinet install npm:my-pkg         # runs `npm pack` in a temp dir
pinet install git:https://…      # shallow-clones the repo
pinet list
pinet remove <name>
pinet update [name]
```

- `pinet.json` is optional (defaults: name = folder name, version `0.0.0`).
- tar extraction strips a leading `package/` prefix, rejects absolute paths and
  `..` traversal (zip-slip), and skips pax entries.
- Reinstalling the same name replaces it; `update` re-applies the recorded
  source.