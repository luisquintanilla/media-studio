# Configuration

Media Studio keeps one settings file per user:

```text
%LOCALAPPDATA%\MediaStudio\settings\model-providers.json
```

Set `MEDIASTUDIO_HOME` to move the whole per-user folder (settings, `models`, `copilot`) elsewhere, for
example to a test folder. Every `configure`, `assets` and `bench` command also accepts `--settings <file>`
and `--models <dir>`.

You rarely edit the file by hand. These write it for you:

| Tool | Writes |
| --- | --- |
| Setup wizard (first run, or **Settings → Setup**) | Everything below |
| `configure local [--tier S\|M\|L\|XL]` | Local connection, fast/balanced tiers, vision, embeddings, speech |
| `configure azd` | Adds the `azure` Foundry connection, cloud tiers and documents; allows cloud |
| `configure copilot …` | GitHub Copilot connection, tier and runtimes (see [Copilot](copilot.md)) |

`configure show` prints the path and contents. `configure test` checks every role (see [CLI](cli.md#setup-commands)).

## Example

This is the file `configure local --tier S` followed by `configure azd` writes (endpoint shortened):

```json
{
  "schemaVersion": 2,
  "privacy": { "allowCloud": true },
  "connections": [
    { "id": "local", "kind": "openai-compatible", "endpoint": "http://localhost:11434/v1", "auth": "none" },
    { "id": "azure", "kind": "foundry", "endpoint": "https://<your-resource>.services.ai.azure.com/",
      "auth": "entra", "tenantId": "<tenant-id>", "region": "swedencentral", "resourceId": "<resource-id>" }
  ],
  "chat": {
    "tiers": [
      { "name": "fast",     "connection": "local", "model": "qwen3.5:2b" },
      { "name": "balanced", "connection": "local", "model": "qwen3.5:4b" },
      { "name": "cloud",    "connection": "azure", "model": "chat",      "api": "responses" },
      { "name": "strong",   "connection": "azure", "model": "authoring", "api": "responses" }
    ],
    "routing": { "decider": "auto", "confidenceFloor": 0.6, "authoringFloor": "balanced", "failover": true }
  },
  "agents": {
    "authoring": { "runtime": "maf" },
    "illustration": { "runtime": "maf" },
    "copilot": { "backend": "subscription", "model": null, "byokConnection": null }
  },
  "vision": { "connection": "local", "model": "qwen3.5:4b" },
  "embeddings": { "connection": "local", "model": "embeddinggemma" },
  "documents": {
    "candidates": ["pdfpig", "vlm", "document-intelligence", "content-understanding", "mistral"],
    "decider": "auto", "mistralDeployment": "ocr"
  },
  "speech": {
    "tts": { "provider": "kokoro", "voice": "af_heart", "speed": 1, "quality": "quantized" },
    "stt": { "provider": "whisper", "model": "base" },
    "live": { "provider": "whisper-ptt" }
  },
  "images": {
    "mode": "svg", "deployment": "image", "grounding": true, "exemplars": true,
    "critique": { "enabled": true, "threshold": 0.7, "maxRetries": 1 }
  },
  "decisions": { "julia": "auto", "cloudDeployment": "chat" }
}
```

## Reference

### `privacy`

`allowCloud` (default `false`) is the single switch for anything that leaves your machine: Foundry, GitHub
Copilot (subscription) and any `openai-compatible` connection whose endpoint isn't on this machine. When it is
off, cloud roles are skipped or fail with a clear `*.cloud-disabled` error; nothing is sent.

### `connections[]`

| Field | Values |
| --- | --- |
| `id` | Name used by tiers and roles (`local`, `azure`, `copilot` by default) |
| `kind` | `openai-compatible` (Ollama, LM Studio, vLLM, …), `foundry`, `github-copilot` |
| `endpoint` | Base URL. Foundry: `https://<your-resource>.services.ai.azure.com/` |
| `auth` | `none`, `entra` (Microsoft Entra ID via `DefaultAzureCredential`), `api-key`, `logged-in-user`, `env-token` |
| `tenantId`, `region`, `resourceId` | Foundry details used for sign-in and Azure Speech |
| `allowRemote` | Allow a non-loopback `openai-compatible` endpoint (it then counts as cloud) |

### `chat`

`tiers[]` are ordered weakest to strongest. Each has a `name` (`fast`, `balanced`, `cloud`, `copilot`,
`strong`), a `connection`, a `model` (Ollama tag or Foundry deployment name), an optional `api`
(`chat-completions`, `responses`, `anthropic`) and optional `thinking` (default off).
`routing` controls the router; see [routing](routing.md#settings).

### `agents`

`authoring.runtime` and `illustration.runtime` are `maf` (Microsoft Agent Framework over the chat router,
default) or `copilot`. `copilot` holds `backend` (`subscription` or `byok`), `model` and `byokConnection`.
See [Copilot](copilot.md).

### `vision`, `embeddings`

A `connection` and `model`. Vision powers the `vlm` document reader and image checks; embeddings power
illustration grounding.

### `documents`

`candidates` lists the extractors Auto may use: `pdfpig`, `vlm`, `document-intelligence`,
`content-understanding`, `mistral`. `mistralDeployment` names the OCR deployment. See [documents](documents.md).

### `speech`

| Section | Providers | Other fields |
| --- | --- | --- |
| `tts` | `kokoro` (default), `azure-speech` | `voice`, `speed`, `quality` (`quantized` or `fp32`), `style` |
| `stt` | `whisper` (default), `azure-speech` | `model` (`base` or `large-v3-turbo`), `diarization` |
| `live` | `whisper-ptt` (default), `foundry-openai` | `connection`, `deployment` (`transcribe-live`) |

`foundry-openai` is a push-to-talk dictation (`live`) provider only. For cloud narration and transcription use
`azure-speech`. See [speech](speech.md).

### `images`

`mode` is `svg` (default) or `png` (gpt-image; needs cloud). `deployment` names the image deployment.
`grounding`, `exemplars` and `critique` control the pipeline; see [illustrations](illustrations.md).

### `decisions`

`julia` is `auto` (use it when installed), `on` or `off`. `cloudDeployment` names the deployment for
OpenAI Decisions, which are compiled out today ([why](troubleshooting.md#openai-sdk-version)).

## Secrets

Settings never contain secrets. Prefer Microsoft Entra ID (`az login` / `azd auth login`). For
`api-key` connections the key is read only from:

- the environment variable `MEDIASTUDIO__CONNECTIONS__<ID>__APIKEY` (for example
  `MEDIASTUDIO__CONNECTIONS__AZURE__APIKEY`), or
- .NET user secrets with the id `MediaStudio.Sample.Development`:

  ```powershell
  dotnet user-secrets set "MediaStudio:Connections:azure:ApiKey" "<key>" --id MediaStudio.Sample.Development
  ```

GitHub Copilot sign-in is kept in the app's private Copilot folder, or read from `COPILOT_GITHUB_TOKEN` /
`GH_TOKEN` with `auth: env-token`.

## Older settings files

Files from earlier versions (schema 1) are migrated to schema 2 when loaded. The original is kept next to
it as `model-providers.v1.bak.json`. Run `configure show` to see the result and `configure test` to check it.
