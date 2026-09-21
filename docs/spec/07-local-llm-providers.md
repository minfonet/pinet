# Specification 07 — Local & Custom LLM Providers (Pi-compatible `models.json`)

- **Spec status:** Verified
- **Author:** Pi.NET
- **Written before implementation:** yes (SDD)
- **Consumes:** `IModelRegistry` / `ModelDefinition` / `IChatProvider` (SR-01), `DefaultModelRegistry.Create()` (SR-01)
- **Provides:** `PiNet.Providers` config types + loader, `ConfigCredentialResolver`, provider factory/merge, `~/.pinet/models.json` wiring in `DefaultModelRegistry`

## 1. Purpose

Mirror Pi's custom-provider configuration so users can register **local,
OpenAI-compatible LLM servers** (Ollama, LM Studio, llama.cpp, vLLM, …) and
custom endpoints **without touching code**, exactly like
`~/.pi/agent/models.json` does for Pi:

```json
{
  "providers": {
    "ollama": {
      "baseUrl": "http://localhost:11434/v1",
      "api": "openai-completions",
      "apiKey": "ollama",
      "models": [
        { "id": "llama3.1:8b" },
        { "id": "qwen2.5-coder:7b", "reasoning": true }
      ]
    }
  }
}
```

This unblocks the Stage 6 manual smoke (a real LLM invoking an extension tool)
on machines with a local model but no remote API keys, and is the first
feature-parity step with Pi's provider model.

Design constraints:

- `PiNet.Providers` keeps its zero-NuGet, raw-HTTP posture (ADR-0001);
  config loading uses BCL `System.Text.Json` only.
- Built-in providers (OpenAI, Anthropic, Faux) are unaffected when the file is
  missing, empty or malformed.
- Everything is **best-effort**: an invalid provider/api produces a warning and
  is skipped; the CLI still starts with the built-ins.

## 2. Scope

In scope:

- `ModelsConfig` (types + loader) in `PiNet.Providers`:
  - `CustomModelConfig` — `id` (required), `name`, `reasoning`, `input`,
    `contextWindow`.
  - `CustomProviderConfig` — `baseUrl`, `api`, `apiKey`, `models[]`.
  - `ModelsConfig.Load(homeDir)` — reads `<homeDir>/models.json`; missing file
    → empty config; malformed JSON → warning + empty config.
- `ConfigCredentialResolver` (`ICredentialResolver` impl in `PiNet.Providers`):
  resolves the configured `apiKey` as an environment variable reference
  (`$VAR`), a literal, or a fallback to the provider's default env names.
- Provider construction / merge (in `DefaultModelRegistry.Create()`):
  - `api: "openai-completions"` → `OpenAIChatProvider` with `baseUrl`.
  - `api: "anthropic-messages"` → `AnthropicChatProvider` with `baseUrl`.
  - any other `api` → warning + provider skipped.
  - Configured provider key equal to a built-in id (`openai`/`anthropic`)
    **merges** by model `id`: built-in models are kept; custom models upsert
    (same id replaces, new ids appended), matching Pi's merge semantics.
- `--list-models`, `--provider <custom>` and model validation all go through
  the same registry, so custom models light up everywhere automatically.
- `PiNet.Providers.Tests` project.
- `docs/local-llms.md` guide + smoke-script support (local provider params).

Out of scope (deferred / explicit divergences):

- Model hot-reload (Pi reloads `models.json` each time `/model` opens; we load
  at startup only).
- Project-level config (Pi also supports `.pi/agent/models.json`; we read
  `~/.pinet/models.json` only, respecting `PI_NET_HOME`).
- `maxTokens` / `cost` / `compat` / per-model `api` (our `ModelDefinition`
  lacks `MaxTokens`; parsed fields are `id/name/reasoning/input/contextWindow`).
- `openai-responses` and `google-generative-ai` API types (provider not
  implemented; stated with a clear warning).

## 3. Contracts

### 3.1 Config model (`PiNet.Providers`)

```csharp
public sealed record CustomModelConfig
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public bool? Reasoning { get; init; }
    public string[]? Input { get; init; }
    public long? ContextWindow { get; init; }
}

public sealed record CustomProviderConfig
{
    public string? BaseUrl { get; init; }
    public string? Api { get; init; }
    public string? ApiKey { get; init; }
    public List<CustomModelConfig>? Models { get; init; }
}

public sealed record ModelsConfig
{
    public Dictionary<string, CustomProviderConfig>? Providers { get; init; }

    public static ModelsConfig Load(string homeDir); // missing → empty; malformed → empty + warning
}
```

File location: `<home>/models.json` where `home` = `PI_NET_HOME` env var, else
`~/.pinet` (same resolution rule as the other Pi home consumers).

### 3.2 Config API key resolution

`ConfigCredentialResolver(ICredentialStore _, string baseUrl, string? apiKey,
string[] fallbackEnvNames)` resolves `apiKey` as follows (first match wins):

1. `null`/whitespace → try `fallbackEnvNames` in order (`OPENAI_API_KEY`,
   `OPENAI_KEY` / `ANTHROPIC_API_KEY`); none set → `null` key (the provider's
   existing "no API key" `StreamError` path, unchanged).
2. starts with `$` → the value of the env var named by the rest of the string
   (unset → `null` key).
3. otherwise → the literal string.

`AuthInfo.BaseUrl` is `baseUrl` in all cases (this is how a configured endpoint
reaches the HTTP client, since the existing constructors already accept it).

### 3.3 Provider construction & merge

`DefaultModelRegistry.Create()`:

```csharp
var registry = new ModelRegistry();
registry.RegisterProvider(new OpenAIChatProvider());
registry.RegisterProvider(new AnthropicChatProvider());
registry.RegisterProvider(new FauxChatProvider());
ModelsConfig.Load(HomeDir).ApplyTo(registry);   // may add / merge / warn
return registry;
```

`ApplyTo` per configured provider `(key, cfg)`:

- `BaseUrl` empty → warning, skip.
- `Api` not `openai-completions`/`anthropic-messages` → warning, skip.
- Build the provider with `baseUrl` and the resolved `apiKey`.
- Models: `name` fallback = `id`; `SupportsVision` = `input` contains `"image"`;
  `SupportsReasoning` = `reasoning`; `ContextWindow` = `contextWindow`; `Cost` = zero.
  If the key aliases a built-in, start from that built-in's existing models and
  upsert each custom model by `id` (case-insensitive).
- `registry.RegisterProvider(provider, mergedModels)`.

## 4. Resolution & precedence

| Item | Rule |
|---|---|
| Config file | `<home>/models.json` only; `PI_NET_HOME` overrides `~/.pinet` |
| Built-in providers | Always registered first; replaced **by provider key** when configured |
| Model merge | By model `id` (case-insensitive): built-in models kept, custom upserted; new provider keys get custom models only |
| Auth | `apiKey` (literal or `$ENV`) → default provider env vars → none |

## 5. Error handling

- Missing file → built-ins only (no warnings).
- Malformed JSON → warning on stderr, built-ins only.
- Unknown `api` / missing `baseUrl` → warning with the provider key, skipped.
- A custom provider without any resolvable key still registers; its models stay
  listed but requests fail with the existing "No API key" stream error (Pi's
  "loads but unavailable without auth" behavior).
- Nothing above ever aborts CLI startup.

## 6. Acceptance criteria

- **AC1:** A `~/.pinet/models.json` (temp `PI_NET_HOME`) with an `ollama`
  provider (`openai-completions`, `baseUrl`, `models[{id: llama3.1:8b}]`)
  results in provider `ollama` in the registry with that model; `GetModel`
  resolves it; `name` defaults to the id.
- **AC2:** `apiKey: "ollama"` resolves as the literal key; `apiKey:
  "$MY_KEY"` resolves to the env var value; no `apiKey` falls back to
  `OPENAI_API_KEY`/`OPENAI_KEY`.
- **AC3:** `api: "anthropic-messages"` maps to the Anthropic provider with the
  configured `baseUrl`.
- **AC4:** An unknown `api` and a missing `baseUrl` produce warnings and are
  skipped; malformed JSON produces a warning; built-ins remain intact and the
  Faux provider still works.
- **AC5:** A configured `openai`-key provider merges: a custom model id matching
  a built-in replaces it; a new id is appended alongside the built-ins.
- **AC6:** With no file, `DefaultModelRegistry.Create()` is unchanged
  (3 providers, `--list-models` output identical).

## 7. Test plan

`PiNet.Providers.Tests` (new xunit project):

- `ModelsConfigTests`: missing file → empty; valid parse (literal + `$ENV`
  keys); malformed JSON → empty + warning; unknown api / missing baseUrl →
  skipped with warnings.
- `ProviderRegistrationTests`: `openai-completions` maps to OpenAI with custom
  baseUrl + apiKey (assert via `Credentials.ResolveAsync` → `AuthInfo`);
  model `name` fallback, vision/reasoning derived from `input`/`reasoning`;
  `anthropic-messages` maps to Anthropic.
- `RegistryMergeTests`: built-in `openai` key merges by id (replace + append);
  no-file registry unchanged (enumeration of provider ids).
- `DefaultModelRegistryTests`: temp `PI_NET_HOME` end-to-end — register via
  `~/.pinet/models.json`, then `GetModels()`/`GetModel()` see custom providers
  alongside built-ins.

Manual smoke (documented in verification log): run `scripts/smoke-stage6.ps1`
step 4 against a local Ollama (`-Provider ollama -Model llama3.2`) to prove an
extension tool is invoked end-to-end; falls back to a remote key if configured.

## 8. Verification log

- **Status:** DONE (2026-09-21)

Implemented as specified. Verification summary:

- **Build:** `dotnet build PiNet.slnx` → 0 warnings, 0 errors (net10.0).
- **Tests:** `dotnet test PiNet.slnx` → 279 passed, 0 failed (new
  `PiNet.Providers.Tests` 18, plus the existing 47+27+13+110+64 = 261 from
  stages 1–6).
- **CLI end-to-end (AC1–AC3, AC6):** with `PI_NET_HOME` pointed at a temp dir
  containing a `models.json` with an `ollama` provider (`api:
  openai-completions`, `baseUrl: http://localhost:11434/v1`, `apiKey:
  "ollama"`, model `llama3.1:8b`), `dotnet run --project src/PiNet.Cli --
  --list-models` printed (exit code 0):

  ```
  [openai]    gpt-4o-mini — GPT-4o mini (context: 128000, reasoning: no)
  ...
  [anthropic] claude-sonnet-4-20250514 — Claude Sonnet 4 (context: 200000, reasoning: yes)
  ...
  [faux]      faux-model — Faux model (context: 32000, reasoning: yes)
  [ollama]    llama3.1:8b — Llama 3.1 8B (Local) (context: 0, reasoning: no)
  ```

  The custom provider is registered alongside the built-ins and its model
  resolves through the same registry used by `--provider` and model validation.
- **AC coverage:** AC1 (registration + `GetModel` + `name` fallback) and AC3
  (Anthropic mapping) by `ProviderRegistrationTests`; AC2 (`$ENV`/literal/
  fallback key resolution) by `ModelsConfigTests`; AC4 (bad api / missing
  baseUrl / malformed JSON warnings, built-ins intact) by
  `ProviderRegistrationTests` + `RegistryMergeTests`; AC5 (built-in merge by id:
  replace + append) and AC6 (no file → unchanged) by `RegistryMergeTests`.
  `RegistryMergeTests.EndToEnd_CustomProviderAlongsideBuiltins` covers the
  `DefaultModelRegistry` temp-`PI_NET_HOME` end-to-end case from §7.
- **Divergences from spec (intentional):**
  - The loader is `ModelsConfig.TryLoad(out string? warning)` (spec §3.1 said
    `Load`), returning an empty config plus an optional warning string instead
    of throwing; `DefaultModelRegistry.Create()` prints the warning to stderr.
  - `OpenAIChatProvider` / `AnthropicChatProvider` constructors gained
    `providerId` and `name` parameters (defaults `"openai"` / `"anthropic"`)
    because `ModelRegistry` keys providers by `provider.Id`; custom keys would
    otherwise collide with the built-ins.
  - The config types live in `PiNet.Providers` (`ModelsConfig`,
    `CustomProviderConfig`, `CustomModelConfig`), with
    `ConfigCredentialResolver` under `PiNet.Providers.Auth`.
  - `openai-responses` and `google-generative-ai` are rejected with a warning
    (as scoped) rather than implemented.
- **Files added:** `src/PiNet.Providers/Config/ModelsConfig.cs`,
  `src/PiNet.Providers/Auth/ConfigCredentialResolver.cs`,
  `src/PiNet.Providers.Tests/` (`PiNet.Providers.Tests.csproj`, `TestDirs.cs`,
  `ModelsConfigTests.cs`, `ProviderRegistrationTests.cs`,
  `RegistryMergeTests.cs`), `docs/local-llms.md`.
- **Files modified:** `src/PiNet.Providers/OpenAI/OpenAIChatProvider.cs`,
  `src/PiNet.Providers/Anthropic/AnthropicChatProvider.cs`,
  `src/PiNet.Providers/DefaultModelRegistry.cs`, `PiNet.slnx`.
- **Manual smoke (deferred):** running `scripts/smoke-stage6.ps1` step 4 against
  a live local Ollama (plus the optional `-Provider` / `-Model` smoke
  parameters) is deferred to a machine with a running local model; the
  provider/model resolution it depends on is covered by the CLI check above.