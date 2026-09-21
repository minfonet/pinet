# Local & Custom LLM Providers

Pi.NET can talk to any **OpenAI-compatible** or **Anthropic-compatible** HTTP
endpoint — Ollama, LM Studio, llama.cpp, vLLM, a self-hosted gateway, or a
remote API — without touching code. You declare providers and models in a single
`models.json` file, mirroring Pi's `~/.pi/agent/models.json`.

This is the feature delivered by [spec 07](spec/07-local-llm-providers.md).

## File location

| Location | Path |
|----------|------|
| Default | `~/.pinet/models.json` |
| Override | `$PI_NET_HOME/models.json` |

`PI_NET_HOME` replaces the whole Pi home (the same rule used by permissions,
sessions, skills and packages). The file is optional: when it is missing,
malformed, or empty, Pi.NET starts with the three built-in providers
(`openai`, `anthropic`, `faux`).

## Format

```json
{
  "providers": {
    "<provider-key>": {
      "baseUrl": "http://localhost:11434/v1",
      "api": "openai-completions",
      "apiKey": "ollama",
      "models": [
        { "id": "llama3.1:8b", "name": "Llama 3.1 8B (Local)" }
      ]
    }
  }
}
```

The `<provider-key>` is the provider id you pass to `--provider` (and the
prefix shown by `--list-models`).

### Provider fields

| Field | Required | Meaning |
|-------|----------|---------|
| `baseUrl` | yes | Endpoint base URL. Missing → the provider is skipped with a warning. |
| `api` | yes | Wire protocol. See [API types](#api-types). Missing/unknown → skipped with a warning. |
| `apiKey` | no | Literal key, or `$ENV_VAR` to read an environment variable. See [API keys](#api-keys). |
| `models` | no | Catalog of models exposed by this provider. Omit for a provider with no models. |

### Model fields

| Field | Default | Meaning |
|-------|---------|---------|
| `id` | — (required) | Model id sent to the endpoint and used with `--model`. |
| `name` | the `id` | Display name shown by `--list-models`. |
| `reasoning` | `false` | Marks the model as a reasoning model. |
| `input` | text only | Array of accepted inputs; include `"image"` to mark it vision-capable. |
| `contextWindow` | `0` | Context window in tokens (informational). |

### API types

| `api` value | Maps to | Notes |
|-------------|---------|-------|
| `openai-completions` | `OpenAIChatProvider` | `POST {baseUrl}/chat/completions` — Ollama, LM Studio, llama.cpp, vLLM, OpenAI, … |
| `anthropic-messages` | `AnthropicChatProvider` | `POST {baseUrl}/v1/messages` — Anthropic or any compatible gateway |

Other values (for example `openai-responses` or `google-generative-ai`) are
**not implemented**; they produce a warning and the provider is skipped.

## API keys

`apiKey` is resolved in this order (first match wins):

1. **`$ENV_VAR`** — a value starting with `$` names an environment variable
   whose value is used (`"apiKey": "$OLLAMA_API_KEY"`). Unset → no key.
2. **Literal** — any other value is used as-is (`"apiKey": "ollama"`).
3. **Fallback** — if `apiKey` is omitted, the provider's default environment
   variables are consulted: `OPENAI_API_KEY` / `OPENAI_KEY` for
   `openai-completions`, `ANTHROPIC_API_KEY` for `anthropic-messages`.

A provider with no resolvable key still registers and its models are still
listed; requests then fail with the usual "No API key" stream error. Local
servers that ignore auth (Ollama, LM Studio) are happy with any placeholder.

## Merge semantics

Built-in providers are always registered first. A configured key that matches a
built-in (`openai`, `anthropic`) **merges by model `id`**:

- a custom model whose `id` matches a built-in **replaces** that entry;
- a new `id` is **appended** alongside the built-ins;
- untouched built-in models remain available.

A key that is not a built-in (for example `ollama`) is added as a new provider
with exactly the models you list.

## Examples

### Ollama

```json
{
  "providers": {
    "ollama": {
      "baseUrl": "http://localhost:11434/v1",
      "api": "openai-completions",
      "apiKey": "ollama",
      "models": [
        { "id": "llama3.1:8b", "name": "Llama 3.1 8B (Local)" },
        { "id": "qwen2.5-coder:7b", "reasoning": true, "contextWindow": 32768 }
      ]
    }
  }
}
```

```bash
dotnet run --project src/PiNet.Cli -- --provider ollama --model llama3.1:8b -p "hello"
```

### LM Studio

LM Studio serves an OpenAI-compatible API on port 1234 by default:

```json
{
  "providers": {
    "lmstudio": {
      "baseUrl": "http://localhost:1234/v1",
      "api": "openai-completions",
      "apiKey": "lm-studio",
      "models": [ { "id": "qwen2.5-coder-7b-instruct" } ]
    }
  }
}
```

### llama.cpp / vLLM

Both expose an OpenAI-compatible server (`llama-server`, `vllm serve`):

```json
{
  "providers": {
    "local": {
      "baseUrl": "http://localhost:8000/v1",
      "api": "openai-completions",
      "apiKey": "$LOCAL_API_KEY",
      "models": [
        { "id": "qwen2.5", "name": "Qwen Coder", "input": ["text", "image"], "contextWindow": 32768 }
      ]
    }
  }
}
```

### Anthropic-compatible gateway

```json
{
  "providers": {
    "claude-local": {
      "baseUrl": "http://localhost:8080",
      "api": "anthropic-messages",
      "apiKey": "$ANTHROPIC_API_KEY",
      "models": [ { "id": "claude-sonnet" } ]
    }
  }
}
```

## Using configured models

Everything that reads the model catalog picks up `models.json` automatically:

```bash
# List every provider and model (built-ins + custom)
dotnet run --project src/PiNet.Cli -- --list-models

# Run a one-shot prompt against a custom provider/model
dotnet run --project src/PiNet.Cli -- --provider ollama --model llama3.1:8b -p "hello"

# Interactive loop, then switch with /model
dotnet run --project src/PiNet.Cli -- -i
```

## Troubleshooting

Warnings are printed to stderr at startup and never abort the CLI:

| Warning | Cause |
|---------|-------|
| `missing baseUrl, skipped` | The provider has no `baseUrl`. |
| `missing api, skipped` | The provider has no `api`. |
| `unsupported api '…'` | `api` is not `openai-completions` or `anthropic-messages`. |
| `Could not load <path>: …` | `models.json` is malformed JSON (or a model entry is missing `id`). |

If the CLI starts but a request fails with "No API key", set the configured
`apiKey` / environment variable (or use a placeholder for local servers).

## Differences from Pi

- Pi reloads `models.json` whenever `/model` opens; Pi.NET loads it once at
  startup.
- Pi also reads a project-level `.pi/agent/models.json`; Pi.NET reads
  `~/.pinet/models.json` (respecting `PI_NET_HOME`) only.
- Per-model `maxTokens`, `cost`, `compat` and per-model `api` overrides are not
  parsed; the supported model fields are listed above.
- `openai-responses` and `google-generative-ai` are not implemented yet.

See also: [spec 07](spec/07-local-llm-providers.md) ·
[`docs/extensions.md`](extensions.md) · [Pi's models.json](https://github.com/earendil-works/pi).
