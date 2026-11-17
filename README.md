# Media Studio

> **Preview (v0.1.0-preview).** Local paths are tested offline in CI; several cloud paths have not been
> verified against live services yet. See [known limitations](#known-limitations).

Media Studio is a **local-first AI explainer-video studio** for Windows. You bring notes,
a PDF or a topic, and it drafts a storyboard, narration and illustrations, then renders
a captioned MP4. It runs on your own machine with Ollama, Kokoro and Whisper. Microsoft
Foundry and GitHub Copilot are optional and only used when you turn on cloud.

It is a .NET 11 sample built on [Microsoft.Extensions.AI](https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai)
(`IChatClient`, `ITextToSpeechClient`, `ISpeechToTextClient`, `IImageGenerator`), the
[Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/) and a Blazor Hybrid (WPF) desktop app.

## Features

- **Storyboard authoring.** An agent drafts and edits scenes; every change is a typed, reviewable edit.
- **Smart chat routing.** A small local decision model (Julia) picks the cheapest capable model tier
  for each request and escalates only when needed. See [routing](docs/routing.md).
- **Narration and captions.** Kokoro text-to-speech and Whisper speech-to-text run locally;
  Azure Speech MAI voices are an opt-in alternative. See [speech](docs/speech.md).
- **Illustrations.** Grounded SVG diagrams with a critique-and-repair loop; optional PNG via gpt-image.
  See [illustrations](docs/illustrations.md).
- **Documents.** PDFs are read locally with PdfPig or a vision model; cloud extractors ask before
  uploading. See [documents](docs/documents.md).
- **Deterministic rendering.** A pinned headless browser and FFmpeg render frame-exact,
  loudness-checked H.264/AAC video. See [contracts](docs/contracts.md).
- **Optional GitHub Copilot** as an extra chat tier or agent runtime. See [Copilot](docs/copilot.md).
- **Coding-agent ready.** `AGENTS.md` and twelve task skills let GitHub Copilot, Claude Code or Codex set up,
  run, fix and extend the studio for you. See [coding agents](docs/coding-agents.md).

## Quickstart with a coding agent (recommended)

The easiest way in is to let a coding agent do the setup while you approve each step.

1. Clone the repository and start your agent in it (GitHub Copilot CLI or VS Code agent mode, Claude Code, Codex, ...):
   ```powershell
   git clone https://github.com/luisquintanilla/media-studio.git
   cd media-studio
   copilot          # or: claude, codex
   ```
   Claude Code and Codex users: run `.\scripts\Link-AgentSkills.ps1` once first so they find the skills.
2. Ask:
   > Set up Media Studio locally on this machine. Show me what you'd install and download, and wait for my go-ahead.
3. Then:
   > Render the demo video and give me the path to the MP4.

The agent follows the repository's skills and asks before it installs software, downloads models, creates Azure
resources or turns on cloud features. Agent setup, more prompts and guardrails: [coding agents](docs/coding-agents.md).
Prefer to do it by hand? Use Quickstart A or B below.

## Quickstart A: fully local (no cloud account)

Requirements: Windows 10/11 x64, 16 GB RAM recommended, about 10 GB free disk for models.

1. **Clone** the repository and open PowerShell in it:
   ```powershell
   git clone https://github.com/luisquintanilla/media-studio.git
   cd media-studio
   ```
2. **Install prerequisites** (.NET 11 SDK, WebView2, FFmpeg, Node.js, eSpeak NG, Ollama, browser worker):
   ```powershell
   .\scripts\Install-Prerequisites.ps1
   ```
3. **Set up local models.** Detects your hardware tier, pulls Ollama models, downloads
   Kokoro, Whisper and Julia (hash-verified), writes settings and tests them:
   ```powershell
   .\scripts\Setup-LocalModels.ps1
   ```
4. **Start the app:**
   ```powershell
   .\Start-MediaStudio.ps1
   ```
5. **Create a project.** Add notes or a PDF, ask for a storyboard and review the scenes.
   To render your first MP4 end to end today, follow
   [your first video](docs/getting-started-local.md#5-render-your-first-video).

Details, options and fixes: [getting started (local)](docs/getting-started-local.md).

## Quickstart B: Azure (Microsoft Foundry)

Requirements: an Azure subscription, the [Azure Developer CLI](https://aka.ms/azd) and the
[Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli), plus steps 1–2 above.

1. **Sign in:**
   ```powershell
   azd auth login
   az login
   ```
2. **Provision one Foundry resource** with all model deployments (prompts for an environment
   name, subscription and region; `swedencentral` is recommended):
   ```powershell
   azd up
   ```
3. **Settings are written for you.** The post-provision hook waits for role assignments, probes the
   services and runs `configure azd`. Idle cost is $0: every deployment is pay-per-use.
4. **Check every role:**
   ```powershell
   dotnet run --project src/VideoProduction.Cli -- configure test
   ```
5. **Start the app** with `.\Start-MediaStudio.ps1`. Local models remain available; cloud tiers are
   used when routing escalates. Remove everything later with `azd down --purge`.

Details, options and costs: [getting started (Azure)](docs/getting-started-azure.md).

## Hardware tiers

`Setup-LocalModels.ps1` and `configure local` pick a tier from your RAM and GPU memory.
Models are pulled through Ollama; see [models](docs/models.md) for sizes and measured results.

| Tier | Detected when | Fast / balanced chat | Whisper |
| --- | --- | --- | --- |
| S | less than 16 GB RAM and 8 GB VRAM | `qwen3.5:2b` / `qwen3.5:4b` | base |
| M | 16 GB RAM or 8 GB VRAM | `qwen3.5:4b` / `qwen3.5:9b` | large-v3-turbo (q5) |
| L | 32 GB RAM and 12 GB VRAM | `qwen3.5:9b` / `gpt-oss:20b` | large-v3-turbo (q5) |
| XL | 24 GB VRAM, opt-in (`-AllowXl`) | `qwen3.5:9b` / `qwen3.8:27b` | large-v3-turbo (q5) |

## Local vs. cloud

Everything has a local default. Cloud is off until you allow it (`privacy.allowCloud`).

| Role | Local (default) | Cloud (opt-in) |
| --- | --- | --- |
| Chat and authoring | Ollama tiers (fast, balanced) | Foundry `gpt-6-luna`, `gpt-6.1-sol`; optional Claude; GitHub Copilot |
| Routing decisions | Julia (ONNX) → heuristic | OpenAI Decisions (compiled out today) |
| Vision and embeddings | Ollama vision model, `embeddinggemma` | Foundry `text-embedding-3-small` |
| Narration (TTS) | Kokoro-82M | Azure Speech MAI-Voice-2.1 |
| Transcription (STT) | Whisper (whisper.cpp) | Azure Speech MAI-Transcribe-2 |
| Documents | PdfPig, local vision model | Mistral OCR, Document Intelligence, Content Understanding |
| Illustrations | SVG from the chat router | `gpt-image-2.5-flare` PNG |

## Documentation

| Start here | Reference |
| --- | --- |
| [Getting started: local](docs/getting-started-local.md) | [Configuration](docs/configuration.md) |
| [Getting started: Azure](docs/getting-started-azure.md) | [CLI reference](docs/cli.md) |
| [Architecture](docs/architecture.md) | [Models and benchmarks](docs/models.md) |
| [Product tour](docs/media-studio-product.md) | [Routing](docs/routing.md) |
| [Troubleshooting](docs/troubleshooting.md) | [Speech](docs/speech.md), [Documents](docs/documents.md), [Illustrations](docs/illustrations.md) |
| [Contributing](CONTRIBUTING.md) | [GitHub Copilot](docs/copilot.md) |
| [Coding agents and skills](docs/coding-agents.md) | |

Deeper production references: [project contracts](docs/contracts.md),
[file production](docs/file-production.md), [transcription](docs/transcription.md),
[production upgrades](docs/production-upgrades.md) and [animated mechanisms](docs/animated-mechanisms.md).

## Repository layout

| Path | Contents |
| --- | --- |
| `src/MediaStudio.App` | Desktop app (WPF + Blazor Hybrid), setup wizard |
| `src/MediaStudio.Configuration` | Settings v2, credentials, model assets, `configure` commands |
| `src/MediaStudio.Agents` | Agent pipeline, GitHub Copilot runtime |
| `src/MediaStudio.Sources`, `src/MediaStudio.Speech` | Document extraction and routing; Kokoro, Whisper, Azure Speech |
| `src/VideoProduction.*` | Rendering engine, model providers, decisions, the `video-production` CLI |
| `src/audio-transcriber`, `src/AudioTranscriber` | The `audio-transcriber` CLI and its library |
| `infra/`, `azure.yaml` | Bicep and azd hooks for one Foundry resource |
| `scripts/` | Prerequisite, local-model and agent-skill setup scripts |
| `tests/` | xUnit and console test runners |

## Known limitations

- **Generation inside the app** uses a production configuration derived from your settings (Kokoro or
  the configured speech, routed illustrations, the bundled Inter font, FFmpeg and Node from `PATH`). If
  something is missing, Generate names the command to run (`assets ensure`, `Install-Prerequisites.ps1`);
  `configure production` shows the same check. A full render with the derived configuration (in the app
  or with the CLI) hasn't been verified live for this release; the
  [CLI path](docs/getting-started-local.md#5-render-your-first-video) is the documented way to render.
- **Not yet verified live:** `azd up` end to end, cloud speech, cloud document extractors and
  gpt-image were tested only against recorded or fake responses. The `Category=Live` tests exist but
  have not been run against a real Foundry resource. GitHub Copilot sign-in has not been
  verified against a live account.
- **OpenAI SDK pinned to 2.14.0.** 2.15 breaks streaming in Microsoft.Extensions.AI.OpenAI 11.0.0, so
  native OpenAI Decisions are compiled out. See [troubleshooting](docs/troubleshooting.md#openai-sdk-version).
- The app is Windows-only. The CLIs and libraries are set up to build on Linux in CI, but that job has not run on hosted runners yet.

More in [troubleshooting](docs/troubleshooting.md#known-limitations).

## License

Code is MIT licensed: see [LICENSE](LICENSE). Models, fonts,
native runtimes and the GitHub Copilot CLI have their own terms: see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and [SOURCE-ORIGINS.md](SOURCE-ORIGINS.md).
Report security issues as described in [SECURITY.md](SECURITY.md). This project follows a
[code of conduct](CODE_OF_CONDUCT.md).
