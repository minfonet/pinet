# Testing Pi.NET with a real LLM

This guide shows how to verify Pi.NET end-to-end against a real model:
streaming, tool calling, permissions, sessions, context compaction and cost
reporting — plus the manual smoke script and how to debug the usual failures.

The automated test suite never calls a real provider (it uses the deterministic
`faux` provider, see [ADR-0001](adr/0001-raw-http-over-sdks.md)); everything in
this document is a **manual** check you run with your own credentials.

## 1. Prerequisites

- .NET SDK 10 (`dotnet --list-sdks`).
- One of these credentials:
  - OpenAI — `OPENAI_API_KEY` (fallback: `OPENAI_KEY`);
  - Anthropic — `ANTHROPIC_API_KEY`;
  - any OpenAI/Anthropic-compatible endpoint (Ollama, LM Studio, llama.cpp,
    vLLM, a gateway) declared in `models.json` — see
    [local-llms.md](local-llms.md); local endpoints usually need no key.

```powershell
# PowerShell
$env:OPENAI_API_KEY = "sk-..."
```

```bash
# bash / zsh
export OPENAI_API_KEY="sk-..."
```

Prefer environment variables over committed files: `.env` and `*.local` are
gitignored, but keys are still best kept outside the repository.

To keep test state away from your real Pi home, point `PI_NET_HOME` at a
throwaway directory for the whole session:

```powershell
$env:PI_NET_HOME = "$env:TEMP\pinet-test-home"
```

```bash
export PI_NET_HOME="$TMPDIR/pinet-test-home"
```

All examples below use `dotnet run --project src/PiNet.Cli -- …`; replace it
with `pinet` if you installed the global tool.

## 2. Offline sanity check (no tokens spent)

Confirm the wiring before talking to a real model:

```bash
dotnet build PiNet.slnx

# Every provider/model Pi.NET knows about
dotnet run --project src/PiNet.Cli -- --list-models

# Deterministic offline reply + token/cost line
dotnet run --project src/PiNet.Cli -- --provider faux --model faux-model -p "hi"
```

`--list-models` should show `openai` and/or `anthropic` (with your key present)
and any provider you declared in `~/.pinet/models.json`. The faux run should
print a reply and a `Tokens: ↑… ↓… | Cost: $…` line.

## 3. One-shot prompt (print mode)

```bash
dotnet run --project src/PiNet.Cli -- --provider openai --model gpt-4o -p "Reply with exactly: pong"
```

```bash
dotnet run --project src/PiNet.Cli -- --provider anthropic --model claude-sonnet-4-20250514 -p "Reply with exactly: pong"
```

What to verify:

- the reply streams to the terminal as it is generated;
- the run closes with a `Tokens: ↑… ↓… | Cost: $…` summary line (when the
  provider reports usage);
- exit code 0.

Stdin piping also works (useful for testing prompts from files):

```bash
cat README.md | dotnet run --project src/PiNet.Cli -- --provider openai --model gpt-4o -p "Summarize this"
```

`--thinking <Off|Minimal|Low|Medium|High|XHigh|Max>` maps to OpenAI
`reasoning_effort` / Anthropic `thinking` budgets where the model supports it.

## 4. Agent mode with tools

`--agent` attaches the built-in tools (`read_file`, `write_file`, `edit_file`,
`bash`) and runs the full tool-calling loop:

```bash
dotnet run --project src/PiNet.Cli -- --agent --provider openai --model gpt-4o -p "List the files in the current directory, then stop."
```

Permission behavior to keep in mind:

- in a real terminal, `bash` asks for confirmation before running;
- in a non-interactive run (piped/redirected stdin) the fallback is
  **deny-all**, so the model's `bash` call is denied unless you pass
  `--approve`:

```bash
dotnet run --project src/PiNet.Cli -- --agent --approve --provider openai --model gpt-4o -p "Run `dotnet --version` and report it."
```

- `--no-approve` forces prompts even when the config would allow the command
  (see [permissions.md](permissions.md) for rules and `/trust`).

What to verify: the tool call is rendered, the result is fed back to the model
and the final answer references it. A fun end-to-end check with an extension:

```bash
# Creates ~/.pinet/extensions/hello.cs if missing (see smoke script below)
dotnet run --project src/PiNet.Cli -- --provider openai --model gpt-4o \
  -p "Please call the hello tool and report exactly what it returns."
```

## 5. Interactive loop

```bash
dotnet run --project src/PiNet.Cli -- -i
```

Worth exercising by hand:

| Feature | How |
|---------|-----|
| Streaming + tool loop | Ask it to read a file, then edit it |
| Permission prompt | First `bash`/`write_file` call in a project asks allow/deny |
| Steering | While a turn runs, type a message + **Enter** → queued for the next turn boundary (`(steering: …)`) |
| Follow-up | **Alt+Enter** while a turn runs → queues a message that starts another turn |
| Cancel | **Ctrl+C** cancels the running turn; empty prompt + Ctrl+C exits |
| Model switch | `/model` to list, `/model anthropic/claude-sonnet-4-20250514` to hand off mid-session |
| Thinking | `/thinking High` |
| Compaction | `/compact` or `--compact` (auto) |
| Images | `@photo.png` (vision models) |
| Export / import | `/export html out.html`, `/import session.jsonl` |
| Skills / templates | `/skill:<name>`, plus any `/template-name` from `.pi/prompts/` |
| Extensions | `/help` lists extension commands; ask the model to use extension tools |

## 6. Sessions

Runs persist a JSONL session by default under `~/.pinet/sessions/`
(`PI_NET_HOME/sessions`):

```bash
dotnet run --project src/PiNet.Cli -- --agent -p "Remember the number 42."
dotnet run --project src/PiNet.Cli -- -c -p "What number did I ask you to remember?"
```

- `-c` / `--continue` resumes the most recent session for this directory;
- `--session <id|path>` resumes a specific one; `--fork <id|path>` forks it;
- `--no-session` disables persistence for a run.

Verify the file was written (`Get-ChildItem $env:PI_NET_HOME\sessions` or
`ls ~/.pinet/sessions`) and that the resumed run answers from history.

## 7. The Stage 6 smoke script

[`scripts/smoke-stage6.ps1`](../scripts/smoke-stage6.ps1) is the scripted
version of the checks above for extensions and packages:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\smoke-stage6.ps1
$env:OPENAI_API_KEY = "sk-..."; .\scripts\smoke-stage6.ps1 -OpenAiModel gpt-4o
```

It (1) builds the solution, (2) installs an npm package (`pinet install
npm:<pkg>`) against a real registry, (3) installs a local throwaway git bundle
(`pinet install git:<path>`, no network), and (4) asks a real model to invoke a
sample `hello` extension tool, creating `~/.pinet/extensions/hello.cs` if it is
missing.

Steps that need network/keys report `WARN`/`SKIP` instead of failing, and the
LLM step is informational (it depends on the model choosing to call the tool).
The script exits non-zero only on hard `FAIL`s.

## 8. Checklist — what a good run looks like

- [ ] `--list-models` lists the provider(s) you configured.
- [ ] Print mode streams text and prints the `Tokens:`/`Cost:` summary.
- [ ] `--agent` performs at least one tool call and produces a final answer.
- [ ] Non-interactive runs deny `bash` unless `--approve` is passed.
- [ ] A JSONL session appears under `$PI_NET_HOME/sessions` and `-c` resumes it.
- [ ] Interactive: steering (Enter), follow-up (Alt+Enter) and Ctrl+C work as
      described in [spec 08 §3.3](spec/08-stage-7-advanced-features.md).
- [ ] `/compact` replaces old history with a summary; the JSONL stays complete.
- [ ] A custom provider from `models.json` (e.g. Ollama) answers via
      `--provider <key> --model <id>`.

## 9. Troubleshooting

| Symptom | Likely cause | Fix |
|---------|--------------|-----|
| `No OpenAI API key found…` / 401 | key missing or wrong | set `OPENAI_API_KEY` / `ANTHROPIC_API_KEY` for the same shell, or `--api-key` |
| `Unknown provider` | typo or provider not configured | check `--list-models` and `~/.pinet/models.json` |
| `model not found` (provider 404) | wrong model id for your account | pick an id from `--list-models` / provider docs |
| Connection refused / timeout | local server down, wrong `baseUrl` | start Ollama/LM Studio, verify the URL with `curl` |
| 429 | rate limit | wait, retry, or use a smaller model |
| `bash` denied without a prompt | non-interactive fallback is deny-all | add `--approve`, or `/trust` the directory interactively |
| No `Tokens:`/`Cost:` line | provider did not report usage | still fine; usage is best-effort per provider |
| Extension tool never called | model chose not to (or tool shadowed) | check startup warnings; ask explicitly “call the `hello` tool” |

Cost notes: use the smallest model that exercises the feature, keep prompts
short, and remember the summary line is exact per run. For repeated checks,
prefer the `faux` provider: it is deterministic, offline and free.

## Related documents

- [local-llms.md](local-llms.md) — `models.json`: Ollama, LM Studio, vLLM,
  gateways, custom keys.
- [permissions.md](permissions.md) — rules, `/trust`, `--approve`, sandboxing.
- [extensions.md](extensions.md) — build your own tools/commands to test with.
- [packaging.md](packaging.md) — install `pinet` globally before testing.
