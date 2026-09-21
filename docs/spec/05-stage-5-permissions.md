# Specification 05 — Stage 5: Permissions & Tool Security

- **Spec status:** Verified
- **Author:** Pi.NET
- **Written before implementation:** yes (SDD)
- **Consumes:** SR-02 `BeforeToolCall` hook, built-in tools (`read_file`, `write_file`, `edit_file`, `bash`), SR-03 project-directory sessions
- **Provides:** `PiNet.Permissions` project (rules, resolver, config, trust) + CLI permission gate (prompt, `/trust`, `--approve`/`--no-approve`)

## 1. Purpose

Gate destructive tool calls behind configurable, layered permission rules so that
a user can control what the agent may do in a given project, without baking a
hard permission system into the agent core. The gate plugs into the existing
`BeforeToolCall` hook (SR-02.10); the agent core itself is unchanged.

Security posture:

- **Reads never prompt** (`read_file` is always allowed).
- **Shell execution prompts by default** (`bash` asks before every run).
- **Writes outside the working directory are always denied** (safety floor, not
  configurable away).
- **Denied calls are reported back to the LLM** as an error tool result so the
  model can adapt; the agent run does not terminate.

## 2. Scope

In scope:

- `PiNet.Permissions` project (references **no** other PiNet project — pure BCL + `System.Text.Json`).
- Permission model: `ToolPermission`, `PermissionRule`, `PermissionDecision`, `IPermissionResolver`.
- `RulePermissionResolver` with three rule layers: **session** (in-memory) → **project** (`.pi/permissions.json`) → **global** (`~/.pinet/permissions.json`).
- Built-in default policy: `AllowReads`, `AskForBash`, `DenyDangerousPaths` (safety floor).
- Config file format, loading, saving; `PI_NET_HOME` override for the global location.
- `TrustStore` (project trust decision persisted by `/trust`).
- CLI: `PermissionGate` (adapts resolver → `BeforeToolCallDelegate`), `ConsolePermissionPrompt`,
  `/trust` slash command, `--approve` / `--no-approve` flags, non-interactive fallback.
- `docs/permissions.md` with config reference and a containerization (Dockerfile) template.

Out of scope:

- Per-argument inspection of bash commands (no command parsing/heuristics).
- Per-path "allow for session" scoping (session grants are tool-scoped).
- Extension-defined tools (Stage 6) — only the four built-in tools are path-aware.
- UI polish (pickers, colors beyond the existing console style).

## 3. Contracts

### 3.1 Core model (`PiNet.Permissions`)

```csharp
public enum ToolPermission { Allow, Ask, Deny }

public sealed record PermissionRule(string ToolPattern, string? PathPattern, ToolPermission Permission)
{
    // ToolPattern: exact tool name or glob ('*' matches any sequence, e.g. "bash", "*_file").
    // PathPattern: glob matched against the cwd-relative path (forward slashes);
    //              null = applies regardless of path.
    public bool Matches(string toolName, string? relativePath);
}

public sealed record PermissionDecision(ToolPermission Permission, string Source, string? Reason)
// Source ∈ "safety" | "session" | "project" | "global" | "default"

public interface IPermissionResolver
{
    PermissionDecision Resolve(string toolName, string? absolutePath, string cwd);
    void AddSessionRule(PermissionRule rule);
    void SetProjectRules(IReadOnlyList<PermissionRule> rules);
}
```

`Resolve` is synchronous (pure in-memory evaluation; no per-call IO).

### 3.2 `RulePermissionResolver`

```csharp
public sealed class RulePermissionResolver : IPermissionResolver
{
    public RulePermissionResolver(IReadOnlyList<PermissionRule> global, IReadOnlyList<PermissionRule> project);
    public static Task<RulePermissionResolver> LoadAsync(string cwd, CancellationToken ct); // reads global + project files (missing = empty)
    public IReadOnlyList<PermissionRule> SessionRules { get; }
    public IReadOnlyList<PermissionRule> ProjectRules { get; }
    public IReadOnlyList<PermissionRule> GlobalRules { get; }
}
```

**Resolution algorithm** (the contract all tests check):

1. If `absolutePath` is given, compute `relativePath` = path relative to `cwd`
   (forward slashes; `..` segments preserved when the path escapes the cwd).
2. **Safety floor** — if the tool is `write_file` or `edit_file` and the resolved
   path is outside `cwd` → `Deny(source: "safety")`, unconditionally (rules,
   session grants, and `--approve` cannot override it).
3. Collect the rules that match `(toolName, relativePath)` in each layer.
4. If **any** matching rule in any layer is `Deny` → `Deny` (deny is sticky;
   the source is the highest-priority layer holding the deny).
5. Otherwise the first matching rule in layer order **session → project → global**
   (file order within a layer) decides (`Allow` or `Ask`).
6. Otherwise the **default policy** applies:
   - `bash` → `Ask(source: "default")`
   - `read_file` → `Allow(source: "default")`
   - anything else → `Allow(source: "default")`

**Matching semantics:**

- Tool pattern: ordinal case-sensitive equality, or a glob where `*` matches any
  character sequence (a single `*` matches everything).
- Path pattern: glob (`*` crosses `/`) against the cwd-relative path;
  case-insensitive on Windows, ordinal otherwise.
- A rule with a non-null `PathPattern` never matches a call with no extractable path.
- Path extraction (`ToolPaths.ExtractPath(toolName, args)`): `path` argument for
  `read_file` / `write_file` / `edit_file`; `null` for `bash` and unknown tools.

### 3.3 Config files

- Global: `<PI_NET_HOME>/permissions.json`, where `PI_NET_HOME` defaults to `~/.pinet`.
- Project: `<cwd>/.pi/permissions.json`.

```json
{
  "rules": [
    { "tool": "bash", "permission": "ask" },
    { "tool": "write_file", "path": "secrets/*", "permission": "deny" }
  ]
}
```

- `permission` ∈ `allow` | `ask` | `deny` (case-insensitive).
- Missing file → zero rules (not an error). Malformed JSON / bad values →
  `PermissionConfigException` with the file path in the message.
- `PermissionConfig.LoadAsync(path, ct)` / `SaveAsync(config, path, ct)`;
  `SaveAsync` creates parent directories.

### 3.4 Trust

```csharp
public static class TrustStore
{
    // Merges {tool:"bash", permission:"allow"} into the project config (idempotent).
    public static Task<bool> TrustAsync(string cwd, CancellationToken ct);
    // Removes that rule from the project config (no-op if absent).
    public static Task<bool> UntrustAsync(string cwd, CancellationToken ct);
    public static bool IsTrusted(string cwd);
}
```

### 3.5 Prompt surface

```csharp
public enum PermissionChoice { Allow, AllowSession, Deny }

public sealed record PermissionRequest(
    string ToolName,
    string? Path,      // absolute path for file tools
    string? Command,   // command string for bash
    string Source,     // why prompting is required (rule source)
    string? Reason);

public interface IPermissionPrompt
{
    Task<PermissionChoice> PromptAsync(PermissionRequest request, CancellationToken ct);
}
```

Implementations:

- `ConsolePermissionPrompt(IConsoleInput, IConsoleOutput)` — CLI/interactive.
- `AutoApprovePrompt` — always `Allow` (used by `--approve`).
- `DenyAllPrompt(TextWriter)` — always `Deny`, prints a one-line notice
  (non-interactive fallback).

**Prompt rendering** (contract for `ConsolePermissionPrompt`):

```
⚠ Permission required — bash [default]
  $ rm -rf build
  [a] allow once   [s] allow for session   [d] deny (default)
> 
```

For file tools the second line is `  path: <absolute path>`.
Input: one keystroke — `a`/`A` → `Allow`, `s`/`S` → `AllowSession`,
`d`/`D`/Enter → `Deny`, EOF → `Deny`.

### 3.6 CLI gate (`PiNet.Cli`)

```csharp
public static class PermissionGate
{
    // Adapts resolver + prompt to the agent's BeforeToolCall hook.
    public static BeforeToolCallDelegate Create(IPermissionResolver resolver, IPermissionPrompt prompt, string cwd);
}
```

Behavior per call:

1. `path = ToolPaths.ExtractPath(toolCall.Name, toolCall.Arguments)` (absolute).
2. `decision = resolver.Resolve(name, path, cwd)`.
3. `Allow` → `ToolCallDecision.Allow()`.
4. `Deny` → `ToolCallDecision.Block($"Permission denied: {reason}")` (no terminate).
5. `Ask` → prompt:
   - `Allow` → allow once.
   - `AllowSession` → `resolver.AddSessionRule(new PermissionRule(name, null, Allow))`, then allow.
   - `Deny` → `ToolCallDecision.Block("Denied by user")`.

### 3.7 CLI wiring

- `Program.cs` parses `--approve` and `--no-approve` (mutually exclusive; last wins).
- Agent mode and interactive mode both construct
  `resolver = await RulePermissionResolver.LoadAsync(cwd, ct)`;
  `--no-approve` adds session rule `{bash, null, Ask}`;
  prompt = `--approve ? AutoApprovePrompt : (stdin redirected ? DenyAllPrompt(Console.Error) : ConsolePermissionPrompt)`.
- `InteractiveLoop` accepts optional `IPermissionResolver` / `IPermissionPrompt`
  (null → no gate, i.e. today's behavior) and wires the gate into
  `AgentOptions.BeforeToolCall` on every `RebuildAgent`.
- `/trust` (no arg) → `TrustStore.TrustAsync` + reload project rules +
  `(project trusted: bash allowed without prompt)`;
  `/trust off` → `UntrustAsync` + reload + `(project no longer trusted)`.
- Help text lists `/trust` and the new flags.

## 4. Behavior Specs

### SR-05.01 — Default policy

- **Given** no rules in any layer,
- **When** the agent calls `bash`, `read_file`, or `write_file`/`edit_file` with a path inside the cwd,
- **Then** the decisions are `Ask`, `Allow`, and `Allow` respectively, all with source `default`.

### SR-05.02 — Safety floor for out-of-cwd writes

- **Given** any rules, session grants, or `--approve`,
- **When** `write_file` or `edit_file` targets a path that resolves outside the cwd (e.g. `../etc/passwd`, `/etc/passwd`),
- **Then** the decision is `Deny` with source `safety` and a reason naming the path.

### SR-05.03 — Rule matching

- **Given** a rule `{tool, path?, permission}`,
- **When** a tool call is resolved,
- **Then** the rule matches iff the tool pattern matches the tool name **and**
  (path pattern is null **or** the relative path matches the pattern);
  a rule with a path pattern never matches `bash` (no extractable path).

### SR-05.04 — Layer precedence and deny-stickiness

- **Given** matching rules in multiple layers,
- **When** the resolver resolves,
- **Then** any `Deny` in any layer wins (source = highest-priority layer holding it);
  otherwise the first matching rule in order session → project → global decides.

### SR-05.05 — Config files and `PI_NET_HOME`

- **Given** `PI_NET_HOME` set (or defaulting to `~/.pinet`) and a project dir,
- **When** `RulePermissionResolver.LoadAsync(cwd)` runs,
- **Then** global rules come from `<PI_NET_HOME>/permissions.json`, project rules
  from `<cwd>/.pi/permissions.json`; missing files contribute zero rules;
  malformed content raises `PermissionConfigException` naming the file.

### SR-05.06 — Ask prompt

- **Given** a decision of `Ask`,
- **When** `ConsolePermissionPrompt.PromptAsync` runs,
- **Then** it renders the tool name, source, command/path line, and the
  `[a]/[s]/[d]` options; `a` → `Allow`, `s` → `AllowSession`, `d`/Enter/EOF → `Deny`.

### SR-05.07 — Deny blocks and informs the LLM

- **Given** a `Deny` decision,
- **When** the gate's `BeforeToolCall` runs,
- **Then** the tool call is blocked (not executed), the agent continues, and the
  LLM receives an error tool result containing `Permission denied:` and the reason.

### SR-05.08 — "Allow for session"

- **Given** the user answers `s` to an `Ask` prompt for tool `T`,
- **When** tool `T` is called again in the same run,
- **Then** no prompt appears and the call is allowed (session rule `{T, null, Allow}`).

### SR-05.09 — `/trust`

- **Given** an interactive session in project `P` without `.pi/permissions.json`,
- **When** the user runs `/trust`,
- **Then** `P/.pi/permissions.json` contains `{tool:"bash", permission:"allow"}`,
  subsequent `bash` calls in the session do not prompt, and `/trust off`
  removes the rule and restores prompting.

### SR-05.10 — `--approve` / `--no-approve`

- **Given** `--approve`,
- **When** any `Ask` decision occurs,
- **Then** the prompt is skipped and the call is allowed; `Deny` decisions
  (rules or safety floor) still block.
- **Given** `--no-approve` with a trusted project,
- **When** `bash` is called,
- **Then** the prompt appears again (session rule overrides the project rule).

### SR-05.11 — Non-interactive fallback

- **Given** agent mode with redirected stdin and no `--approve`,
- **When** an `Ask` decision occurs,
- **Then** the call is denied, a notice is printed to stderr, and the agent
  continues with the error tool result.

### SR-05.12 — Documentation & containerization

- **Given** the delivered `docs/permissions.md`,
- **When** a user reads it,
- **Then** they can configure global/project rules, use `/trust`,
  `--approve`/`--no-approve`, and build a sandboxed container from the included
  Dockerfile template.

## 5. Acceptance Criteria

- **AC1:** First `bash` call in a new project (no `.pi/` config) triggers a
  permission prompt in interactive mode.
- **AC2:** `/trust` saves the project as trusted; subsequent `bash` calls in
  that project no longer prompt.
- **AC3:** `--approve` bypasses prompts for one run (denies still apply).
- **AC4:** A project rule can deny writes to specific paths
  (`{"tool":"write_file","path":"secrets/*","permission":"deny"}`), and the
  LLM receives the denial reason.

## 6. Test Plan

- `PiNet.Permissions.Tests` (new project): rule matching, resolution algorithm
  (defaults, safety floor, precedence, deny-stickiness), config load/save/malformed,
  `PI_NET_HOME` override, `TrustStore` trust/untrust/idempotence, `ToolPaths` extraction.
- `PiNet.Cli.Tests` (extended): scripted provider that emits a `bash` tool call;
  interactive loop tests for AC1–AC4 (prompt rendering, `a`/`s`/`d` answers,
  `/trust`, `/trust off`, `--approve` via `AutoApprovePrompt`, deny-still-blocks),
  plus the non-interactive fallback notice.

## 7. Verification Log

| Item | Verified | Date | Evidence |
|------|----------|------|----------|
| Build (whole solution) | ✅ 0 errors, 0 warnings | 2026-07-09 | `dotnet build PiNet.slnx` |
| Permissions unit tests | ✅ 47/47 | 2026-07-09 | `dotnet test src/PiNet.Permissions.Tests` — rule matching (glob, path scope), resolution (defaults, safety floor, precedence, deny-stickiness), config load/save/malformed, `PI_NET_HOME` override, `TrustStore`, `ToolPaths` |
| CLI permission tests (AC1) | ✅ prompt on first bash; `a` runs tool, `d` blocks with `Blocked: Denied by user`, agent continues | 2026-07-09 | `PermissionGateTests.FirstBash_Prompts_AllowOnce_RunsTool`, `..._Deny_BlocksTool_AgentContinues` |
| CLI permission tests (AC1, session) | ✅ `s` allows for session; second bash in same turn does not prompt again | 2026-07-09 | `PermissionGateTests.AllowSession_SecondBashInSameTurn_DoesNotPromptAgain` |
| CLI permission tests (AC2) | ✅ `/trust` writes `.pi/permissions.json` (bash/allow), subsequent bash runs without prompt; `/trust off` removes it | 2026-07-09 | `PermissionGateTests.Trust_WritesProjectConfig_SubsequentBashDoesNotPrompt`, `TrustOff_RemovesTrustRule` |
| CLI permission tests (AC3) | ✅ `AutoApprovePrompt` runs bash with no prompt; `DenyAllPrompt` blocks with non-interactive notice | 2026-07-09 | `PermissionGateTests.AutoApprove_RunsBashWithoutPrompt`, `DenyAllPrompt_NonInteractive_BlocksBash` |
| CLI permission tests (AC4) | ✅ out-of-cwd write denied even with auto-approve (safety floor, file never created); path-scoped project deny blocks `secrets/*` only, other paths allowed | 2026-07-09 | `PermissionGateTests.WriteOutsideCwd_AlwaysBlocked_EvenWithAutoApprove`, `PathScopedProjectDeny_BlocksMatchingPath_Only` |
| Full regression | ✅ 167/167 (47 Permissions + 27 AgentCore + 80 CLI + 13 Sessions) | 2026-07-09 | `dotnet test PiNet.slnx` |
| Docs (SR-05.12) | ✅ `docs/permissions.md` delivered: config reference, flags, prompt, Dockerfile template | 2026-07-09 | `docs/permissions.md` |

**Status: VERIFIED.** All acceptance criteria (AC1–AC4) and behavior specs
(SR-05.01–SR-05.12) pass. Stage 5 complete.
