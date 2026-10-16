# Creating Pi-style extensions

Pi.NET follows the Pi philosophy: **keep the core small and make everything
else extensible**. The CLI itself only ships the agent loop, sessions,
permissions and a handful of built-in tools — everything else is discovered at
startup from the Pi home (`~/.pinet`, or `PI_NET_HOME`), the project tree and
installed packages:

| Layer | What it adds | How you invoke it |
|-------|--------------|-------------------|
| **Extensions** (C#) | agent tools + `/slash` commands | the model calls the tool; you type the command |
| **Skills** (Markdown) | model-facing procedures | `/skill:<name> [args]` |
| **Prompt templates** (Markdown) | reusable prompt snippets | `/name args` |
| **Context files** | extra system-prompt text | automatic |
| **Packages** | bundles of the above | `pinet install <source>` |

This guide is the full reference. The condensed version lives in the
[README](../README.md); for running against a real model while you develop an
extension, see [testing-with-a-real-llm.md](testing-with-a-real-llm.md).

## Quickstart — your first extension

Extensions are **plain C# files** compiled at startup with the Roslyn compiler
(`csc.dll`) that ships inside the .NET SDK. No project file, no NuGet restore,
no build step — save a `.cs` file and restart Pi.

Create `.pi/extensions/hello.cs` in your project (or
`~/.pinet/extensions/hello.cs` for all projects):

```csharp
using System.Threading;
using System.Threading.Tasks;
using PiNet.Extensions;

public sealed class HelloExtension : IExtension
{
    public void Register(IExtensionContext ctx)
    {
        // A tool the model can call, with a JSON parameter schema.
        ctx.RegisterTool(new ExtensionToolSpec(
            "hello",
            "Say hello to someone",
            """
            {"type":"object",
             "properties":{"name":{"type":"string","description":"Who to greet"}},
             "required":["name"]}
            """,
            (callId, args, ct) =>
            {
                var name = args.TryGetProperty("name", out var n) ? n.GetString() : "world";
                return Task.FromResult(new ExtensionToolResult($"Hello, {name}!"));
            }));

        // A /hello slash command for the interactive loop.
        ctx.RegisterCommand("hello", (args, c) =>
        {
            c.WriteLine("Hello from a slash command!");
            return Task.FromResult(true);
        });
    }
}
```

Run it:

```bash
dotnet run --project src/PiNet.Cli -- -i     # or: pinet -i
```

- `/help` now lists `/hello` under “Extension commands”.
- Ask the model: *“Use the hello tool to greet Ada.”* — it should call `hello`
  and answer with `Hello, Ada!`.

## 1. How the extension host works

At startup (`ExtensionHost.LoadAsync`) Pi.NET:

1. Locates the newest .NET SDK (`dotnet --list-sdks`) and its Roslyn compiler.
   If no SDK is found, extensions are disabled with the warning
   `extensions disabled (dotnet SDK not found)` and the CLI still starts.
2. Scans these directories (non-recursive, `*.cs` only, in this order):

   | Location | Path |
   |----------|------|
   | Global | `~/.pinet/extensions/` (`PI_NET_HOME`) |
   | Project | `<cwd>/.pi/extensions/` |
   | Packages | `<package>/extensions/` for every installed package |

3. Compiles **all files in one directory together** into a single cached
   assembly — the files share a compilation, so helpers in one file can be used
   from another. Each directory produces its own assembly.
4. Loads every public, non-abstract class that implements `IExtension` and has
   a public parameterless constructor, and calls `Register(ctx)` on it.

Anything that fails is **non-fatal**: a compile error prints the compiler
output as startup warnings and that directory is skipped; an exception thrown
in `Register` becomes a warning too. The CLI always starts.

Extensions that implement `IDisposable` get `Dispose()` called when the host is
disposed (CLI shutdown), which is useful for background resources.

### Compilation & cache

| Aspect | Detail |
|--------|--------|
| Compiler | `csc.dll` from the SDK (`/nologo /target:library /optimize+`) |
| References | .NET 10 reference assemblies + `PiNet.Extensions.dll` + `PiNet.Abstractions.dll` (the BCL, nothing else) |
| Cache | `~/.pinet/cache/extensions/<hash>.dll` — SHA-256 over file names + contents + the `PiNet.Extensions` version |
| Rebuild | editing any `.cs` in the directory changes the hash → automatic recompile |

To force a clean rebuild, delete the cache directory
(`Remove-Item -Recurse ~/.pinet/cache/extensions`).

Use `--no-extensions` to disable discovery and compilation entirely (useful
when debugging unrelated issues).

## 2. API reference

The whole extension model lives in the `PiNet.Extensions` project (zero
dependencies):

### `IExtension`

```csharp
public interface IExtension
{
    void Register(IExtensionContext ctx);
}
```

### `IExtensionContext`

| Member | Description |
|--------|-------------|
| `string Cwd` | The CLI working directory (resolve relative paths against it) |
| `void RegisterTool(ExtensionToolSpec tool)` | Adds a tool the agent can call |
| `void RegisterCommand(string name, ExtensionCommandHandler handler)` | Adds `/name` to the interactive loop |

### `ExtensionToolSpec`

```csharp
public sealed record ExtensionToolSpec(
    string Name,
    string Description,
    string? ParametersJson,   // JSON schema string, or null for no parameters
    Func<string, JsonElement, CancellationToken, Task<ExtensionToolResult>> Execute);
```

- `Name` — unique, case-insensitive; must not collide with a built-in tool
  (`read_file`, `write_file`, `edit_file`, `bash`) or another extension tool.
  Collisions are reported as startup warnings and the tool is skipped
  (built-ins win).
- `Description` — shown to the model; write it like a tool docstring.
- `ParametersJson` — recursive `{ "type", "description", "properties",
  "required" }` JSON schema; `null` or malformed JSON means “no parameters”.
- `Execute(callId, argsJson, ct)` — runs when the model calls the tool.
  `argsJson` is a `System.Text.Json.JsonElement` matching your schema; return
  a task so long work can be awaited and cancelled.

### `ExtensionToolResult`

```csharp
public sealed record ExtensionToolResult(string Content, bool IsError = false, bool Terminate = false);
```

- `Content` — the text handed back to the model (plain text or JSON).
- `IsError: true` — marks the result as a failed tool call (the model is told
  the tool failed so it can recover).
- `Terminate: true` — asks the agent loop to stop. It takes effect when every
  tool result in the same turn sets it (mirrors the built-in `done` pattern).

### Commands

```csharp
public delegate Task<bool> ExtensionCommandHandler(string args, IExtensionCommandContext ctx);

public interface IExtensionCommandContext
{
    void WriteLine(string text);
    Task SendToAgentAsync(string prompt);
}
```

- `args` — everything typed after `/name` (possibly empty).
- `ctx.WriteLine` — prints a line to the loop output.
- `ctx.SendToAgentAsync(prompt)` — queues a prompt; every queued prompt runs as
  a normal agent turn **after** the handler returns. This is how a command
  delegates work to the model.
- Return `true` when the command was handled (the Pi extension contract).
- Command names are lowercased on registration; duplicates are skipped with a
  warning. Built-in slash commands (`/help`, `/model`, `/compact`, …) are
  resolved first, so don’t reuse their names.

## 3. Examples

### Tool with validation and error results

```csharp
using System.IO;
using System.Text.Json;
using PiNet.Extensions;

public sealed class WordCountExtension : IExtension
{
    public void Register(IExtensionContext ctx)
    {
        ctx.RegisterTool(new ExtensionToolSpec(
            "word_count",
            "Count the words in a file (relative to the working directory)",
            """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""",
            (callId, args, ct) =>
            {
                var path = args.TryGetProperty("path", out var p) ? p.GetString() : null;
                if (string.IsNullOrWhiteSpace(path))
                    return Task.FromResult(new ExtensionToolResult("missing 'path'", IsError: true));

                var full = Path.GetFullPath(Path.Combine(ctx.Cwd, path));
                if (!File.Exists(full))
                    return Task.FromResult(new ExtensionToolResult($"file not found: {path}", IsError: true));

                var words = File.ReadAllText(full)
                    .Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries).Length;
                return Task.FromResult(new ExtensionToolResult($"{words} words"));
            }));
    }
}
```

### Command that drives the agent

```csharp
ctx.RegisterCommand("review", async (args, c) =>
{
    c.WriteLine("Asking the agent to review the current changes…");
    await c.SendToAgentAsync(
        $"Review the current git diff{(string.IsNullOrWhiteSpace(args) ? "" : $" focusing on {args}")} " +
        "and list the top three risks.");
    return true;
});
```

Typing `/review` prints the acknowledgment and then a full agent turn runs with
your prompt.

### Stateful extension with `IDisposable`

```csharp
public sealed class TimerExtension : IExtension, IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    public void Register(IExtensionContext ctx)
    {
        ctx.RegisterCommand("elapsed", (_, c) =>
        {
            c.WriteLine($"session started at {DateTimeOffset.Now:HH:mm:ss}");
            return Task.FromResult(true);
        });
    }

    public void Dispose() => _cts.Dispose();
}
```

One instance per registration; `Dispose` runs at CLI shutdown.

## 4. Testing and debugging your extension

1. **Run it locally** — keep the file in your project’s `.pi/extensions/`, run
   `dotnet run --project src/PiNet.Cli -- -i`, and watch the startup warnings.
   `/help` lists registered commands; ask the model to call the tool.
2. **Compile errors** — they appear as warnings at startup (the raw Roslyn
   output). Fix the file and rerun; the content hash triggers a recompile.
3. **Tool not visible** — check for collision warnings (`skipped: name
   conflicts with a built-in tool`, `duplicate name`).
4. **Command not visible** — remember names are lowercased and built-ins win;
   check `Unknown command: /…` messages.
5. **Unit tests** — `ExtensionHost.LoadInMemory(cwd, new MyExtension())`
   registers extensions without compilation, so tests can assert tools,
   commands and warnings directly (this is how `PiNet.Extensions.Tests` covers
   the model):

   ```csharp
   var host = ExtensionHost.LoadInMemory("/tmp/proj", new HelloExtension());
   Assert.Single(host.Tools);
   Assert.True(host.Commands.ContainsKey("hello"));
   ```

   Keep extension logic in methods/classes that don’t depend on the host, and
   test those directly.

For end-to-end runs against a real provider while developing, see
[testing-with-a-real-llm.md](testing-with-a-real-llm.md).

## 5. Packaging & sharing (`pinet install`)

Bundle extension files, skills and prompts into a directory and distribute it
with the built-in package manager. A bundle is:

```text
my-bundle/
  pinet.json          # optional: { "name": "my-bundle", "version": "1.2.0" }
  extensions/*.cs     # compiled at startup like any other extension
  skills/*.md         # discovered as global skills
  prompts/*.md        # discovered as prompt templates
```

```bash
pinet install ./my-bundle        # local directory
pinet install ./my-bundle.tgz    # gzipped tar (npm-style, strips a leading package/)
pinet install npm:my-pkg         # runs `npm pack` in a temp dir
pinet install git:https://…      # shallow-clones the repo
pinet list
pinet remove <name>
pinet update [name]              # re-applies the recorded source
```

- Packages install into `~/.pinet/packages/<name>` and are recorded in
  `~/.pinet/packages.json` (`PI_NET_HOME` moves both).
- `pinet.json` is optional: defaults are name = folder name, version `0.0.0`.
- Reinstalling the same name replaces it; `update` re-fetches from the
  recorded source.
- Extraction is hardened: a leading `package/` prefix is stripped, absolute
  paths and `..` traversal are rejected, pax entries are skipped.
- Same trust posture as Pi: install packages from sources you trust — npm/git
  sources are not signed and there is no trust prompt (see spec 06).

`pinet update --self` is unrelated: it updates the Pi.NET CLI itself.

## 6. Skills reference

Skills are small, self-describing Markdown instructions the model can be told
about. They live under `skills/` directories:

| Location | Path | Discovery rule |
|----------|------|----------------|
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

- `name` must be lowercase `[a-z0-9]` with `-` separators (max 64);
  `description` is required (max 1024) and is shown to the model.
- `disable-model-invocation: true` hides the skill from the model while keeping
  it usable via `/skill:`.
- Invocation: `/skill:<name> [args]` sends the skill body as a turn prompt.
- The system prompt gets an `<available_skills>` XML block listing loadable
  skills (the model can read the file and follow it).
- Duplicate names are skipped with a warning (first one wins);
  `--no-skills` disables discovery.

## 7. Prompt templates reference

Reusable prompt fragments, expanded inline when you type `/name args`.

| Location | Path |
|----------|------|
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

| Token | Meaning |
|-------|---------|
| `$1`, `$2`, … | 1-based positional arguments (missing → empty) |
| `$@`, `$ARGUMENTS` | all arguments, space-joined |
| `$0` | the template name |
| `$FOO` (unknown) | left as-is |
| `${1:-default}` | value or default when missing |
| `${@:-default}` | all args or default when empty |
| `${@:N}` | all args from index N |
| `${@:N:L}` | all args from index N, at most L items |

Typing `/name args` loads the expansion into the editor buffer (press Enter to
send). `--no-prompt-templates` disables discovery.

## 8. Context files

`SYSTEM.md`, `APPEND_SYSTEM.md`, `AGENTS.md` and `CLAUDE.md` are loaded from the
working directory (`AGENTS.md` is also resolved from the nearest ancestor) and
assembled into the system prompt: base → `APPEND_SYSTEM.md` → project
instructions → skills XML. `SYSTEM.md` replaces the base prompt entirely.

## 9. Design notes & Pi parity

- **Parity with Pi:** the discovery layout mirrors Pi’s conventions — a global
  home directory (`~/.pinet` ↔ `~/.pi`), a project `.pi/` tree, bundles
  installed by a small package manager, the Agent Skills standard
  (agentskills.io) for skills, and the same prompt-template ergonomics.
- **Deliberate difference:** Pi extensions are TypeScript modules; Pi.NET
  compiles plain C# with the SDK’s Roslyn compiler so users get type checking
  and IDE support without any build system or NuGet dependency (ADR-0001).
- **Best-effort by design:** every layer (extensions, skills, templates,
  packages) fails soft — a warning line, the rest keeps working. Disable any
  layer with `--no-extensions`, `--no-skills`, `--no-prompt-templates`.
- **Out of scope (documented):** extension keyboard shortcuts, UI widgets and
  generic event hooks; `allowed-tools` frontmatter is parsed but not enforced
  (the permission system already gates tool execution).
