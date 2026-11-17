---
name: media-studio-setup-local
description: >-
  Set up Media Studio to run fully offline: install prerequisites (.NET 11, WebView2, ffmpeg, espeak-ng, Ollama),
  pick a hardware tier, pull the Ollama models, download Julia/Kokoro/Whisper and write local settings.
  Use when asked to "set up Media Studio locally", "install prerequisites", "pick a tier", "pull local models",
  "configure local", "run configure test", "use Llama.app or Foundry Local instead of Ollama", or when a fresh clone
  does not run.
---

# Set up Media Studio locally

Local is the default and needs no cloud account. Settings are written to
`%LOCALAPPDATA%\MediaStudio\settings\model-providers.json` and models to `%LOCALAPPDATA%\MediaStudio\models`
(override the root with `MEDIASTUDIO_HOME`). User guide: [getting-started-local](../../../docs/getting-started-local.md).

## 1. Prerequisites

```powershell
.\scripts\Install-Prerequisites.ps1 -WhatIf          # preview
.\scripts\Install-Prerequisites.ps1                  # .NET 11, WebView2, ffmpeg, Node, espeak-ng, Ollama, browser worker
.\scripts\Install-Prerequisites.ps1 -Skip node,browser-worker
```

macOS/Linux: `scripts/install-prerequisites.sh` (the desktop app is Windows-only; the CLI and libraries are cross-platform).
`-Skip` accepts `dotnet,webview2,ffmpeg,node,espeak-ng,ollama,browser-worker`.

## 2. Pick a tier

Tier models come from `ollamaTiers` in `assets/model-manifest.json`. Detection (`LocalRuntimeDetector`) uses:

| Tier | Detected when | Notes |
|---|---|---|
| `S` | below the M thresholds | smallest Qwen3.5 models, `whisper-base` |
| `M` | RAM ≥ 16 GB or VRAM ≥ 8 GB | Whisper large-v3-turbo |
| `L` | RAM ≥ 32 GB and VRAM ≥ 12 GB | adds `gpt-oss:20b` as the balanced tier |
| `XL` | VRAM ≥ 24 GB **and** opt-in | needs `-AllowXl` / `--allow-xl` |

Never copy tag names into code; read them from the manifest (see skill `media-studio-models`).
`configure local` without `--tier` detects hardware and picks one.

## 3. One-shot setup (recommended)

```powershell
.\scripts\Setup-LocalModels.ps1 -DryRun                  # show what would be pulled/downloaded
.\scripts\Setup-LocalModels.ps1 -Tier M -Yes
.\scripts\Setup-LocalModels.ps1 -Tier L -Family gemma4   # documented alternate family
```

Options: `-Tier S|M|L|XL`, `-Family qwen3.5|gemma4`, `-Fast/-Balanced/-Vision <tag>`, `-AllowXl`, `-SkipOllama`,
`-SkipJulia`, `-Yes`, `-DryRun`. Bash twin: `scripts/setup-local-models.sh --tier M --yes` (needs bash, curl, jq).
The script pulls the Ollama tags, then runs `assets ensure --tier`, `configure local --tier` and `configure test`.

## 4. Manual equivalent

```powershell
ollama pull <tag>                                                     # each tag of the tier
dotnet run --project src/VideoProduction.Cli -- assets ensure --tier M
dotnet run --project src/VideoProduction.Cli -- configure local --tier M
dotnet run --project src/VideoProduction.Cli -- configure test
```

Override individual models: `configure local --tier M --balanced <tag> --vision <tag>`.

## 5. Alternate local runtimes

All use the `openai-compatible` connection kind. Edit the `local` connection's `endpoint` in the settings file:

* Ollama (default): `http://localhost:11434/v1`
* Llama.app: `http://localhost:9931/v1`
* Foundry Local: the loopback `/v1` endpoint Foundry Local reports (detected by `foundry --version`, no HTTP probe)

Non-loopback endpoints need `allowRemote: true` **and** HTTPS (`ProviderSettingsValidator`).

## 6. Start the app

```powershell
.\Start-MediaStudio.ps1                      # builds and runs src/MediaStudio.App
.\Start-MediaStudio.ps1 -Configuration Release -NoBuild
.\Start-MediaStudio.ps1 --configure show     # pass-through arguments
```

The in-app Setup wizard (Settings → Setup) does steps 2–4 with buttons and shows the same ✅/❌ table.

## Verification

```powershell
dotnet run --project src/VideoProduction.Cli -- configure show
dotnet run --project src/VideoProduction.Cli -- assets verify
dotnet run --project src/VideoProduction.Cli -- configure test
dotnet run --project src/VideoProduction.Cli -- diagnostics
```

Every local row in `configure test` should be ✅. Cloud rows are ⏭️ (skipped) while `privacy.allowCloud` is false;
that is expected.

## Pitfalls

* `speech:tts espeak-ng ❌`: install espeak-ng (`winget install eSpeak-NG.eSpeak-NG`, `brew install espeak-ng`,
  `apt install espeak-ng`) and reopen the terminal so `PATH` refreshes.
* `chat:<tier> ❌` (model not pulled): `ollama pull <tag>` for the tag shown, or rerun the setup script.
* Ollama not running: start the Ollama app or `ollama serve`, then rerun `configure test`.
* Julia missing is not fatal: `decisions.julia: auto` falls back to the heuristic decider, so `-SkipJulia` is fine on
  low-disk machines.
* XL is never auto-selected without `--allow-xl`; on a smaller GPU it pages to system RAM and is very slow.
* More: [troubleshooting](../../../docs/troubleshooting.md) and skill `media-studio-troubleshoot`.
