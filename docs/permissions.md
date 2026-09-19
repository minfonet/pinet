# PiNet Permissions

PiNet gates every tool call through a permission resolver before the tool
executes. The model always sees the outcome — allowed calls run normally,
denied calls return an error result (`Blocked: <reason>`) so the LLM can adapt.

## How a decision is made

For each tool call, rules are checked in this order:

1. **Safety floor** — `write_file`/`edit_file` targeting a path *outside the
   working directory* is always **denied**. Not configurable, not overridable
   by any rule, `--approve`, or session grant.
2. **Deny-sticky** — if *any* matching rule in *any* layer is `deny`, the call
   is denied. Deny always wins, regardless of layer.
3. **First match wins** — otherwise the first matching non-deny rule in
   `session → project → global` order decides (`allow` or `ask`).
4. **Default policy** — with no matching rule: `bash` → **ask**, everything
   else → **allow**.

A rule matches when its `tool` glob matches the tool name **and** (if it has a
`path`) its path glob matches the affected path, resolved relative to the
working directory. `*` matches any sequence of characters, including `/`.

## Config files

| Layer | Location |
|-------|----------|
| Global | `$PI_NET_HOME/permissions.json` (default `~/.pinet/permissions.json`) |
| Project | `<cwd>/.pi/permissions.json` |
| Session | in-memory only (from the prompt's "allow for session") |

Both files share the same JSON shape:

```json
{
  "rules": [
    { "tool": "bash", "permission": "ask" },
    { "tool": "write_file", "path": "secrets/*", "permission": "deny" },
    { "tool": "read_file", "permission": "allow" }
  ]
}
```

- `tool` (required): tool name or glob (`bash`, `read_file`, `*`, …).
- `path` (optional): glob, relative to the working directory (`secrets/*`,
  `*.env`, `docs/**`-style patterns also work since `*` spans `/`).
- `permission` (required): `allow` | `ask` | `deny` (case-insensitive).

A missing file means zero rules — not an error. Malformed JSON or invalid
values fail startup with a message naming the file.

Set `PI_NET_HOME` to relocate the global config (used by tests and containers):

```powershell
$env:PI_NET_HOME = "D:\pinet-home"   # Windows
export PI_NET_HOME=/home/pi/.pinet   # Linux
```

## Interactive prompt

When a call resolves to **ask**, the CLI prints:

```
⚠ Permission required — bash [default]
  $ rm -rf build
  [a] allow once   [s] allow for session   [d] deny (default)
> 
```

(one keystroke; file tools show `  path: <absolute path>` instead of the
command line):

| Key | Effect |
|-----|--------|
| `a` | allow this call once |
| `s` | allow this tool for the rest of the session |
| `d` / Enter / anything else | deny (safe default) |

The `[source]` tag says why the prompt appeared: `default`, `project`,
`global`, or `session`.

## CLI flags and commands

| Flag / command | Effect |
|----------------|--------|
| (none) | interactive: `bash` prompts per call; file tools allowed by default |
| `--approve` | never prompt: every **ask** becomes allow (denies and the safety floor still apply). For unattended runs. |
| `--no-approve` | re-enable `bash` prompting even if project/global rules allow it (adds a session `ask` rule for `bash`). |
| `/trust` | merge `{ "tool": "bash", "permission": "allow" }` into `<cwd>/.pi/permissions.json`; bash then runs without prompting in this project. |
| `/trust off` | remove that rule from the project config. |

Non-interactive fallback: if stdin is redirected (piped input, CI) and
`--approve` is not given, ask decisions are denied automatically with a
notice:

```
(non-interactive: cannot prompt for 'bash'; denied. Use --approve to allow.)
```

## Examples

Deny all writes to secret material and prompt for every shell command:

```json
{
  "rules": [
    { "tool": "bash", "permission": "ask" },
    { "tool": "write_file", "path": "secrets/*", "permission": "deny" },
    { "tool": "edit_file", "path": "*.env", "permission": "deny" }
  ]
}
```

A permissive CI project config (still bounded by the safety floor):

```json
{
  "rules": [
    { "tool": "*", "permission": "allow" }
  ]
}
```

## Sandboxed container (Dockerfile template)

Run PiNet in a throwaway container so the agent's working directory *is* the
sandbox boundary. The safety floor already blocks writes outside the cwd; the
container makes that boundary a real OS boundary.

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0

ENV PI_NET_HOME=/home/pinet/.pinet
WORKDIR /work

# Build PiNet (adjust to your checkout location)
COPY pinet /opt/pinet
RUN cd /opt/pinet && dotnet build PiNet.slnx -c Release

# Optional: bake in a global policy
RUN mkdir -p /home/pinet/.pinet
COPY permissions.json /home/pinet/.pinet/permissions.json

ENTRYPOINT ["/bin/bash", "-c", "cd /opt/pinet && dotnet run --project src/PiNet.Cli -- $*"]
```

```bash
docker run --rm -it -v "$PWD:/work" pinet -i
```

Notes:

- Mount the project you want to work on at `/work` and `cd /work` (or pass
  the working directory) so the safety floor matches the mount.
- No `--cap-add`/privileged flags needed; the agent has no more rights than
  the `pinet` user inside the container.
- Use `--approve` inside the container if you want fully unattended runs;
  the container is your safety net, not the permission system.
