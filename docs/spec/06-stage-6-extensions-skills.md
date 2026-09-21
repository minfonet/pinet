# Specification 06 — Stage 6: Extension System & Skills

- **Spec status:** Verified
- **Author:** Pi.NET
- **Written before implementation:** yes (SDD)
- **Consumes:** built-in tools + `AgentTool` (SR-02), `BeforeToolCall` permission gate (SR-05), interactive loop slash commands (SR-04), `ChatRequest.SystemPrompt`
- **Provides:** `PiNet.Extensions` project (extensions, skills, prompt templates, context files, package manager) + CLI wiring (subcommands, flags, `/skill:`, template expansion, extension commands, system-prompt assembly)

## 1. Purpose

Make PiNet extensible without breaking the zero-NuGet, raw-protocol posture
(ADR-0001): users can add **C# extensions** (tools + slash commands),
**skills** (Agent Skills standard `SKILL.md` files that teach the agent
procedures), **prompt templates** (expandable `/command` snippets), and
**context files** (`AGENTS.md`/`SYSTEM.md`/`APPEND_SYSTEM.md`) that shape the
system prompt. A small package manager (`pinet install/list/remove/update`)
distributes bundles of extensions/skills/prompts from local paths, npm, or git.

Design constraints:

- `PiNet.Extensions` references **no other PiNet project** (pure BCL +
  `System.Text.Json`). Extension tools are declared with a neutral
  `ExtensionToolSpec` and converted to `AgentTool` in the CLI.
- Extensions are plain C# source files compiled at startup with the SDK's
  Roslyn compiler (`csc.dll`) — no NuGet restore, no build system.
- Skills follow the **Agent Skills standard** (agentskills.io): a
  `SKILL.md` with YAML frontmatter (`name`, `description`, …).
- Everything is **best-effort and non-fatal**: a broken extension, skill, or
  template produces a warning line and is skipped; the CLI always starts.

## 2. Scope

In scope:

- `PiNet.Extensions` project:
  - Extension model: `IExtension`, `IExtensionContext`, `ExtensionToolSpec`,
    `ExtensionToolResult`, `ExtensionCommandHandler`, `IExtensionCommandContext`.
  - `ExtensionHost`: discovery, csc compilation (content-hash cache),
    loading, registration, lifecycle.
  - Skill system: `Skill`, `SkillFrontmatter`, `SkillRegistry`,
    `SkillCatalog` (system-prompt XML).
  - Prompt templates: `PromptTemplate`, `TemplateRegistry`, `TemplateExpander`.
  - Context files: `ContextFiles` + `SystemPromptBuilder`.
  - Package manager: `PackageManifest`, `PackageManager`, `TarExtractor`
    (minimal gzip+tar reader, no external deps).
- CLI:
  - Subcommands `pinet install|list|remove|update`.
  - Flags `--no-extensions`, `--no-skills`, `--no-prompt-templates`.
  - `InteractiveLoop`: extension tools in the agent, extension slash
    commands, `/skill:name [args]`, template expansion into the editor buffer,
    `/help` listing, system-prompt assembly.
  - Agent mode: same extension/skill/context-file integration.
- `docs/extensions.md` guide.
- `PiNet.Extensions.Tests` + new `PiNet.Cli.Tests`.

Out of scope (deferred to Stage 7/8):

- Extension keyboard shortcuts, UI widgets, generic event-handler hooks
  (only tools + commands in this stage).
- Enforcing `allowed-tools` frontmatter (parsed but not enforced; the
  permission system already gates tools).
- Themes bundled in packages (Stage 8 polish).
- Package signing / trust prompts for npm/git sources (documented warning
  instead: users install trusted sources, same posture as pi).

## 3. Contracts

### 3.1 Extension model (`PiNet.Extensions`)

```csharp
public interface IExtension
{
    void Register(IExtensionContext ctx);
}

public interface IExtensionContext
{
    string Cwd { get; }
    void RegisterTool(ExtensionToolSpec tool);
    void RegisterCommand(string name, ExtensionCommandHandler handler);
}

public sealed record ExtensionToolSpec(
    string Name,
    string Description,
    string? ParametersJson,   // JSON schema string or null (no parameters)
    Func<string, JsonElement, CancellationToken, Task<ExtensionToolResult>> Execute);

public sealed record ExtensionToolResult(string Content, bool IsError = false, bool Terminate = false);

public delegate Task<bool> ExtensionCommandHandler(string args, IExtensionCommandContext ctx);

public interface IExtensionCommandContext
{
    void WriteLine(string text);
    Task SendToAgentAsync(string prompt);   // queues a turn to run after the command
}
```

- `ParametersJson` is parsed by `ToolSchemaParser` into the existing
  `ToolParameterSchema` (recursive `type`/`properties`/`required` shape).
  Malformed JSON → warning, tool registered without a schema.
- Tool names must be unique across built-ins + extensions; a collision with a
  built-in name → warning, extension tool is skipped.
- Command names are lowercased on registration; a collision with a built-in
  slash command → warning, extension command is skipped.

### 3.2 `ExtensionHost`

```csharp
public sealed class ExtensionHost : IDisposable
{
    public IReadOnlyList<ExtensionToolSpec> Tools { get; }
    public IReadOnlyDictionary<string, ExtensionCommandHandler> Commands { get; }
    public IReadOnlyList<string> Warnings { get; }

    // Discovers, compiles (cached), loads and registers extensions.
    public static async Task<ExtensionHost> LoadAsync(string cwd, CancellationToken ct);

    // Test hook: register an in-memory extension without compilation.
    public static ExtensionHost LoadInMemory(string cwd, params IExtension[] extensions);

    public void Dispose();   // disposes extensions implementing IDisposable
}
```

Discovery locations (source files, non-recursive, in this order):

1. `~/.pinet/extensions/*.cs` (global home; `PI_NET_HOME` override)
2. `<cwd>/.pi/extensions/*.cs` (project)
3. `~/.pinet/packages/<pkg>/extensions/*.cs` (packages, sorted by name)

Compilation (per directory = per compilation unit):

- Content hash = SHA256 of sorted `(relativePath, fileContent)` pairs.
- Cache dir: `~/.pinet/cache/extensions/<hash>.dll`. Hit → load directly.
- Miss → invoke the SDK compiler:
  `dotnet <sdkRoot>/Roslyn/bincore/csc.dll /nologo /target:library /optimize+
   /out:<dll> /reference:<PiNet.Extensions.dll> /reference:<PiNet.Abstractions.dll> <sources>`
  where `sdkRoot` comes from `dotnet --list-sdks` (highest SDK).
- Compilation failure → all compiler error lines become warnings; the unit is
  skipped. The CLI never crashes on extension errors.
- Load: `Assembly.LoadFrom(dll)` (default load context), find public,
  non-abstract, non-static types implementing `IExtension` with a public
  parameterless constructor, instantiate each, call `Register(ctx)`.
  Exceptions during construction/registration → warning, type skipped.
- If `dotnet` is not found → warning "extensions disabled (dotnet SDK not found)".

### 3.3 Skill system

```csharp
public sealed record Skill(
    string Name,          // validated frontmatter name
    string Description,   // validated frontmatter description
    string Path,          // absolute path to SKILL.md
    string Directory,     // directory containing SKILL.md (or the .md file's dir)
    bool DisableModelInvocation);

public static class SkillFrontmatter
{
    public static (Skill skill, string warning?) Parse(string path, string content);
}
```

Frontmatter rules (minimal YAML, line-based — no YAML library):

- File must start with `---` line; frontmatter ends at the next `---` line.
- Keys parsed: `name`, `description`, `disable-model-invocation`
  (case-insensitive; `true`/`false`). Other keys (e.g. `metadata`,
  `allowed-tools`) are ignored in this stage.
- Values may be single/double-quoted (quotes stripped).
- `name`: required; 1–64 chars; only `[a-z0-9-]`; no leading/trailing/
  consecutive hyphens. Invalid → warning, skill not loaded.
- `description`: required; non-empty; ≤ 1024 chars. Missing/invalid → warning,
  skill not loaded.
- Body = everything after the closing `---` (trimmed of leading newline).

`SkillRegistry`:

```csharp
public sealed class SkillRegistry
{
    public IReadOnlyList<Skill> Skills { get; }
    public IReadOnlyList<string> Warnings { get; }
    public static Task<SkillRegistry> LoadAsync(string cwd, CancellationToken ct);
    public Skill? Find(string name);
}
```

Discovery locations (in this order):

1. `~/.pinet/skills/` (global)
2. `~/.agents/skills/` (global, shared across agents)
3. Project: `.pi/skills/` then `.agents/skills/` in `<cwd>`, then ancestors up
   to the filesystem root (nearest first)
4. `~/.pinet/packages/<pkg>/skills/` (packages, sorted by name)

Per-location discovery rules (mirrors pi):

- In `~/.pinet/skills/` and `.pi/skills/`: root-level `*.md` files with valid
  frontmatter are skills; **any** directory (recursively) containing a
  `SKILL.md` is a skill (the directory may group many skills).
- In `~/.agents/skills/` and `.agents/skills/`: root-level `*.md` files are
  **ignored** (no grouping folder); nested `*.md` files with valid
  frontmatter (at any depth) are skills; directories with `SKILL.md` also
  count.
- Files without valid frontmatter (README, notes) are silently ignored.
- Name collision across locations → first found wins; warning recorded.

`SkillCatalog` (system-prompt section):

```csharp
public static class SkillCatalog
{
    public static string? ToSystemPromptSection(IReadOnlyList<Skill> skills);
}
```

Emits (only when at least one skill is loadable):

```
# Skills
The following skills are available. When a task matches a skill, read the
skill file at its location with the read_file tool and follow its instructions.

<available_skills>
  <skill>
    <name>…</name>
    <description>…</description>
    <location>…/SKILL.md</location>
  </skill>
</available_skills>
```

Skills with `disable-model-invocation: true` are omitted from the XML but
remain invokable via `/skill:name`.

### 3.4 Prompt templates

```csharp
public sealed record PromptTemplate(
    string Name,             // file name without .md, lowercased
    string Description,      // frontmatter description or first non-empty body line
    string? ArgumentHint,    // frontmatter argument-hint (optional)
    string Body);

public static class TemplateRegistry
{
    public static Task<IReadOnlyList<PromptTemplate>> LoadAsync(string cwd, CancellationToken ct);
}

public static class TemplateExpander
{
    public static string Expand(string template, IReadOnlyList<string> args);
}
```

Locations (non-recursive, in this order):

1. `~/.pinet/prompts/*.md` (global)
2. `<cwd>/.pi/prompts/*.md` (project)
3. `~/.pinet/packages/<pkg>/prompts/*.md` (packages, sorted by name)

- Frontmatter keys: `description`, `argument-hint` (same minimal YAML parser
  as skills). `description` falls back to the first non-empty body line
  (trimmed, truncated to 80 chars).
- Name collision → first wins; warning.
- Body = content after the closing `---` (or the whole file if no
  frontmatter), with the trailing newline trimmed.

Expansion syntax (applied to the body, left to right):

| Token | Meaning |
|---|---|
| `$1`, `$2`, … | positional argument (1-based); missing → empty string |
| `$@` / `$ARGUMENTS` | all arguments joined with single spaces |
| `${1:-default}`, `${@:-default}`, `${ARGUMENTS:-default}` | positional/all, or `default` when empty |
| `${@:N}` | arguments from 0-based index `N` to end, space-joined |
| `${@:N:L}` | arguments from index `N`, at most `L` items, space-joined |

Unknown `$` sequences (e.g. `$FOO`, `$0`, `$12` when only 2 args exist) are
left as-is, except `$0` which expands to the template name.

### 3.5 Context files & system prompt

```csharp
public sealed record ContextFiles(string? SystemMd, string? AppendSystemMd, string? ProjectInstructions);

public static class ContextFiles
{
    public static ContextFiles Load(string cwd);
}

public static class SystemPromptBuilder
{
    public static string Build(string basePrompt, ContextFiles ctx, IReadOnlyList<Skill> skills);
}
```

Rules:

- `SYSTEM.md` in `<cwd>` (only cwd, not ancestors) → replaces the base prompt.
- `APPEND_SYSTEM.md` in `<cwd>` → appended after the base/system prompt.
- `AGENTS.md` / `CLAUDE.md`: walk from `<cwd>` up to the filesystem root; the
  first directory containing either wins (prefer `AGENTS.md` when both are
  present in the same directory). Its content is appended as a
  `# Project instructions` section.
- Assembly order: base (or `SYSTEM.md`) → `APPEND_SYSTEM.md` → project
  instructions → skills section (`SkillCatalog.ToSystemPromptSection`).
  Sections are joined with blank lines; empty sections are skipped.

### 3.6 Package manager

```csharp
public sealed record PackageInfo(string Name, string Version, string Source, string Path);

public static class PackageManager
{
    public static string PackagesRoot();                 // ~/.pinet/packages
    public static Task<PackageInfo> InstallFromPathAsync(string sourceDir, CancellationToken ct);
    public static Task<PackageInfo> InstallNpmAsync(string package, CancellationToken ct);
    public static Task<PackageInfo> InstallGitAsync(string url, CancellationToken ct);
    public static Task<IReadOnlyList<PackageInfo>> ListAsync(CancellationToken ct);
    public static Task RemoveAsync(string name, CancellationToken ct);
    public static Task<PackageInfo> UpdateAsync(string name, CancellationToken ct);
}
```

- A **package** is a directory with an optional `pinet.json`:
  `{ "name": "...", "version": "..." }` (name lowercased; falls back to the
  directory name; version defaults to `"0.0.0"`). Bundled content:
  `extensions/*.cs`, `skills/…`, `prompts/*.md` — each discovered by the
  respective registry (see locations above).
- Install destination: `~/.pinet/packages/<name>/`. An index file
  `~/.pinet/packages.json` records
  `[{ "name", "version", "source", "path", "installedUtc" }]`.
- Sources:
  - `path:<dir>` or a bare path → recursive copy (existing destination
    replaced).
  - `npm:<pkg>` → `npm pack <pkg>` in a temp dir → extract the resulting
    `*.tgz` (strip leading `package/` prefix) → install.
  - `git:<url>` → `git clone --depth 1 <url> <temp>` → install.
  - `npm:`/`git:` shell out to the respective CLI; missing binary → clear
    error message.
- `list` prints name, version, source. `remove` deletes the directory + index
  entry. `update` re-runs the recorded source (no name → all packages).
- `TarExtractor`: minimal gzip+tar reader (512-byte headers; handles regular
  files `0` and directories `5`; skips pax `g`/`x` entries, symlinks and
  other types). Rejects absolute paths and `..` traversal (zip-slip guard).
  No external dependencies (BCL `GZipStream` only).

### 3.7 CLI wiring

`Program.cs`:

- New subcommands (first positional arg, no dash): `install`, `list`,
  `remove`, `update`.
  - `pinet install <path|npm:<pkg>|git:<url>>`
  - `pinet list`
  - `pinet remove <name>`
  - `pinet update [name]`
- New flags: `--no-extensions`, `--no-skills`, `--no-prompt-templates`.
- `InteractiveModeAsync` / `AgentModeAsync` build, in order:
  1. `ExtensionHost.LoadAsync(cwd)` (unless `--no-extensions`)
  2. `SkillRegistry.LoadAsync(cwd)` (unless `--no-skills`)
  3. `TemplateRegistry.LoadAsync(cwd)` (unless `--no-prompt-templates`)
  4. `ContextFiles.Load(cwd)`
  5. `SystemPromptBuilder.Build(CliConfig.DefaultSystemPrompt, ctx, skills)`
  Warnings from all loaders are printed once, dim, before the first prompt.
- Agent mode: `SystemPrompt` = assembled prompt; `Tools` =
  `BuiltInTools.All()` + converted extension tools (via
  `ExtensionToolAdapter.ToAgentTool(spec)`).
- Print mode: assembled system prompt only (no tools/extensions).

`InteractiveLoop` (new optional parameters, default `null`):

```csharp
public InteractiveLoop(...,
    ExtensionHost? extensions = null,
    SkillRegistry? skills = null,
    IReadOnlyList<PromptTemplate>? templates = null,
    string? systemPrompt = null)
```

- `RebuildAgent`: `SystemPrompt = systemPrompt ?? CliConfig.DefaultSystemPrompt`;
  tools include extension tools.
- Slash dispatch order in `HandleAsync` (after built-ins):
  1. `/skill:<name> [args]` → skill dispatch (below).
  2. Extension command with that name → run handler with
     `IExtensionCommandContext` (writes to output; `SendToAgentAsync` queues a
     turn that runs immediately after the command returns).
  3. Template with that name → `TemplateExpander.Expand(body, args)` is set as
     the **editor buffer** (caret at end); the line is **not** submitted —
     the user edits/presses Enter to send.
  4. Otherwise → `Unknown command: /name (see /help)`.
- `/skill:<name>`:
  - Unknown skill → `Unknown skill: <name> (see /help)`.
  - Otherwise the agent turn prompt is:
    ```
    [Skill: <name> — <path>]
    <SKILL.md body without frontmatter>

    User: <args>          ← only when args are non-empty
    ```
- `/help` additionally lists: extension commands (name + description = first
  line), skills (name + description), templates (name + description +
  argument hint).
- `SlashCommandParser`: names starting with `skill:` parse to a new
  `SlashCommandKind.Skill` with `Name` = the part after `skill:` (lowercased).

## 4. Resolution & precedence summary

| Item | Order (first wins) |
|---|---|
| Skills | `~/.pinet/skills` → `~/.agents/skills` → project (cwd→root: `.pi/skills`, `.agents/skills`) → packages |
| Templates | `~/.pinet/prompts` → `.pi/prompts` (cwd only) → packages |
| Extensions | `~/.pinet/extensions` → `.pi/extensions` (cwd only) → packages |
| Context | `SYSTEM.md`/`APPEND_SYSTEM.md`: cwd only; `AGENTS.md`/`CLAUDE.md`: nearest ancestor |
| Tool names | built-ins beat extension tools (collision → skip extension) |
| Command names | built-in slash commands beat extension commands (collision → skip) |

## 5. Error handling

- Every loader is total: missing directories → empty result; unreadable
  files → warning + skip; bad JSON/frontmatter → warning + skip.
- Extension compilation errors → warnings with the compiler's error lines.
- Package install: non-zero exit from npm/git → error message, exit code 1;
  partial install is rolled back (destination dir removed on failure).
- The CLI never crashes because of user content.

## 6. Acceptance criteria

- **AC1 (skills):** A `SKILL.md` with valid frontmatter in
  `<cwd>/.pi/skills/my-skill/SKILL.md` is discovered; its XML entry appears in
  `ChatRequest.SystemPrompt`; `/skill:my-skill do the thing` sends a user
  message containing the skill body and `User: do the thing`.
- **AC2 (templates):** A template `review.md` in `<cwd>/.pi/prompts/` with
  body `Review $1 with focus on $@` and args `[foo, bar baz]` expands to
  `Review foo with focus on bar baz`; in the loop, `/review foo bar baz`
  fills the editor buffer (not submitted); Enter submits it.
- **AC3 (extensions):** A `hello.cs` extension in `<cwd>/.pi/extensions/`
  registering tool `hello` and command `hello` compiles at startup;
  `/hello` runs the command handler; the agent sees `hello` in its tool list
  and can call it. A broken extension file produces warnings, not a crash.
- **AC4 (context files):** `SYSTEM.md` replaces the base prompt;
  `APPEND_SYSTEM.md` is appended; the nearest `AGENTS.md` ancestor is
  included as `# Project instructions`.
- **AC5 (packages):** `pinet install <dir>` installs a bundle (extensions +
  skills + prompts) into `~/.pinet/packages/`; `pinet list` shows it;
  `pinet remove` removes it; npm `.tgz` extraction works (tested with a
  generated `.tgz`).
- **AC6 (flags):** `--no-skills` / `--no-prompt-templates` / `--no-extensions`
  disable the respective discovery; the CLI still works.
- **AC7 (validation):** invalid skill names/descriptions are rejected with
  warnings; zip-slip tar entries are rejected.

## 7. Test plan

`PiNet.Extensions.Tests`:

- `SkillFrontmatterTests`: valid parse; missing `name`/`description`; invalid
  name (uppercase, leading hyphen, `--`, >64 chars); quoted values;
  `disable-model-invocation`; no frontmatter.
- `SkillRegistryTests`: root `.md` discovery; nested `SKILL.md` dirs; `.agents`
  root-md-ignored rule; ancestor walk (nearest wins); collision first-wins +
  warning; packages dir.
- `SkillCatalogTests`: XML shape; `disable-model-invocation` omitted; empty
  list → null.
- `TemplateExpanderTests`: `$1`/`$2`; `$@`; `$ARGUMENTS`; `${1:-default}`;
  `${@:-default}`; `${@:1}`; `${@:1:2}`; missing args → empty; `$0` → name;
  unknown `$X` untouched.
- `TemplateRegistryTests`: discovery order; description fallback;
  argument-hint; collision.
- `ContextFilesTests`: SYSTEM.md override; APPEND; nearest AGENTS.md;
  CLAUDE.md fallback; both in same dir → AGENTS.md.
- `SystemPromptBuilderTests`: assembly order; empty sections skipped.
- `TarExtractorTests`: extract generated gzip+tar (files + dirs); pax skip;
  `..` traversal rejected; absolute path rejected.
- `PackageManagerTests`: install from path (with `pinet.json`); list; remove;
  update; index file round-trip; npm .tgz extraction via generated archive.
- `ExtensionHostTests`: in-memory registration (tools + commands +
  collision warnings); full compile+load of a sample `.cs` extension
  (skipped if no dotnet SDK); compile failure → warnings, no crash;
  content-hash cache reuse.

`PiNet.Cli.Tests`:

- `SkillDispatchTests`: `/skill:name` sends skill body as a turn
  (ScriptedProvider asserts `ChatRequest.Messages`); unknown skill message.
- `TemplateTests`: `/review foo` expands into editor buffer; Enter submits the
  expanded text (asserted via provider request); unknown template message.
- `ExtensionCommandTests`: in-memory extension command runs; output written;
  `SendToAgentAsync` queues a turn.
- `SystemPromptTests`: assembled prompt contains SYSTEM.md + AGENTS.md +
  skills XML (asserted via `ChatRequest.SystemPrompt`); `--no-skills` equivalent
  (no skills passed).
- `PackageManagerCliTests`: `pinet install/list/remove` subcommand flow
  (temp home via `PI_NET_HOME`).

Manual smoke (documented in verification log):

- `pinet install npm:<pkg>` and `git:<url>` on a real machine (network).
- Extension tool invoked by the LLM end-to-end.

## 8. Verification log

- **Status:** DONE (2026-09-21)

Implemented as specified. Verification summary:

- **Build:** `dotnet build PiNet.slnx` → 0 warnings, 0 errors (net10.0).
- **Tests:** `dotnet test PiNet.slnx` → 261 passed, 0 failed
  (`PiNet.Extensions.Tests` 64, `PiNet.Cli.Tests` 110 incl. new extension/skill
  tests, plus the existing 47+27+13 from stages 1–5).
- **Divergences from spec (intentional):**
  - §3.1 `ToolSchemaParser` produces the neutral `ExtensionParameterSchema`
    (not the agent's `ToolParameterSchema`) because `PiNet.Extensions` must not
    reference `PiNet.Abstractions`; the CLI maps one to the other in
    `ExtensionToolAdapter.MapSchema` (`src/PiNet.Cli/Extensions/CliSetup.cs`).
  - SDK discovery targets .NET 10 (latest SDK selected, ref assemblies from
    `Microsoft.NETCore.App.Ref`); csc is used from the versioned SDK folder
    (`sdk/<ver>/Roslyn/bincore/csc.dll`) with a fallback to the legacy
    `sdk/Roslyn` layout.
  - `~/.agents/skills` follows the "agents" convention (root `.md` files are
    location files and are ignored; nested `.md` are skills), matching the
    actual `claude-code`/`agents` ecosystem rather than the root-.md-as-skill
    reading of the draft.
- **Files added:** `src/PiNet.Extensions/` (8 files: Paths, ExtensionModel,
  ExtensionHost, Skills, Templates, ContextFiles, TarExtractor,
  PackageManager); `src/PiNet.Extensions.Tests/` (9 files incl. TarBuilder +
  TestDirs helpers); `src/PiNet.Cli/Extensions/CliSetup.cs`,
  `src/PiNet.Cli/PackageCommands.cs`; new CLI test files (`SlashSkillTests`,
  `ExtensionCommandTests`, `TemplateLoopTests`, `CliEnvironmentTests`,
  `PackageManagerCliTests`, `Extensions/ExtTestEnv.cs`) plus slash-parser skill
  cases. `Program.cs`, `SlashCommand.cs`, `InteractiveLoop.cs`, `CliConfig.cs`
  and `PiNet.slnx` updated.
- **Manual smoke (deferred to a networked machine):** `pinet install npm:<pkg>`
  and `git:<url>` (network), and an extension tool invoked by a real LLM
  end-to-end. Unit coverage of the local parts (`InstallAsync` path source,
  tar extraction, `ExtensionToolAdapter` → `AgentTool`) is green.
