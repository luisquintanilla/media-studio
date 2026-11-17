# Getting started: local

This guide sets up Media Studio with no cloud account. Every model runs on your machine:
chat and vision through [Ollama](https://ollama.com), routing decisions through Julia (ONNX),
narration through Kokoro and transcription through Whisper.

For Azure, see [getting started: Azure](getting-started-azure.md). The two can be combined: cloud tiers are
added on top of local ones.

> [!TIP]
> Using a coding agent? Start it in the repository and ask: *"Set up Media Studio locally on this machine. Show me
> what you'd install and download, and wait for my go-ahead."* It follows the `media-studio-setup-local` skill, which
> runs the same steps as this guide. See [coding agents](coding-agents.md).

## Requirements

| | Minimum | Recommended |
| --- | --- | --- |
| OS | Windows 10/11 x64 | Windows 11 x64 |
| Memory | 8 GB RAM (tier S) | 16 GB RAM or an 8 GB GPU (tier M) |
| Disk | about 6 GB (tier S models and assets) | 15 GB or more for tiers L/XL |
| GPU | not required | NVIDIA or AMD GPU supported by Ollama |

The desktop app is Windows-only (WPF + WebView2). The `video-production` and `audio-transcriber`
CLIs are .NET console apps.

## 1. Install prerequisites

```powershell
.\scripts\Install-Prerequisites.ps1
```

The script detects each component and installs only what is missing, mostly through `winget`:

| Component | Used for |
| --- | --- |
| .NET 11 SDK | building and running everything (see `global.json`) |
| WebView2 runtime | the desktop app's UI |
| FFmpeg / ffprobe | encoding, decoding and checking video and audio |
| Node.js 24 | the headless browser worker that renders scenes |
| eSpeak NG | phonemes for Kokoro narration (a separate GPL program, not bundled) |
| Ollama | local chat, vision and embedding models |
| browser worker | `npm` packages and Playwright Chromium in `workers/browser` |

Useful options:

```powershell
.\scripts\Install-Prerequisites.ps1 -WhatIf                     # show what would be installed
.\scripts\Install-Prerequisites.ps1 -Skip ollama, browser-worker  # leave components out
```

On Linux or WSL, `scripts/install-prerequisites.sh` installs the CLI prerequisites (the app itself needs Windows).

## 2. Set up local models

```powershell
.\scripts\Setup-LocalModels.ps1
```

This script:

1. Detects your [hardware tier](models.md#local-tiers) (S, M, L or XL).
2. Pulls the tier's fast, balanced, vision and embedding models with `ollama pull` (missing ones only).
3. Runs `assets ensure` to download Kokoro, the tier's Whisper model and Julia, and verifies their SHA-256 hashes.
4. Runs `configure local` to write `%LOCALAPPDATA%\MediaStudio\settings\model-providers.json`, then
   `configure test` to probe every role.

It prints the download size and asks before downloading. Options:

| Option | Effect |
| --- | --- |
| `-DryRun` | Detect and print the plan only |
| `-Tier S\|M\|L\|XL` | Override the detected tier |
| `-AllowXl` | Allow tier XL when 24 GB of GPU memory is available |
| `-Family gemma4` | Use Gemma 4 models instead of Qwen 3.5 |
| `-Fast <tag>`, `-Balanced <tag>`, `-Vision <tag>` | Use specific Ollama tags |
| `-SkipOllama`, `-SkipJulia` | Leave out the Ollama pulls or the Julia download |
| `-Yes` | Don't ask for confirmation |

Without the script, run the same steps yourself:

```powershell
ollama pull qwen3.5:4b
ollama pull embeddinggemma
dotnet run --project src/VideoProduction.Cli -- assets ensure --tier S
dotnet run --project src/VideoProduction.Cli -- configure local --tier S
dotnet run --project src/VideoProduction.Cli -- configure test
```

`configure test` prints a ✅/❌ table with a fix for every failing row. See
[troubleshooting](troubleshooting.md) when something stays red.

## 3. Start the app

```powershell
.\Start-MediaStudio.ps1
```

`Start-MediaStudio.cmd` does the same from Explorer or `cmd`. The launcher builds `src/MediaStudio.App` in
Release, using a repo-local `.dotnet` SDK if one exists. Options:

| Option | Effect |
| --- | --- |
| `-NoBuild` | Skip the build when the app is already built |
| `-Configuration Debug` | Build and run Debug |
| `-DotnetRoot <dir>` | Use a specific .NET 11 SDK |
| anything else | Passed to the app, for example `.\Start-MediaStudio.ps1 --diagnostics` |

## 4. First run: the setup wizard

When no settings file exists, the app opens the setup wizard:

1. **How do you want to run models?** Choose **Local** (recommended), **Azure** or **Both**.
2. **Local runtimes.** Shows your RAM/GPU and the recommended tier, the Ollama models for that tier and the
   speech and decision assets. **Pull models** and **Download speech and decision models** fetch what is missing.
3. **Microsoft Foundry** (Azure or Both only). **Import from azd** or paste an endpoint, and choose whether to
   allow cloud and cloud speech. See [Azure](getting-started-azure.md).
4. **Roles.** Shows which model serves each role. **Test** runs every check; **Save** writes the settings.
5. **GitHub Copilot (optional).** Opened from the Roles step. See [Copilot](copilot.md).

If you ran `Setup-LocalModels.ps1`, the settings already exist and the wizard is skipped. You can reopen it
from Settings. Then create a project: add notes or a PDF as sources, ask the assistant for a storyboard,
and review and edit the scenes. See the [product tour](media-studio-product.md).

## 5. Render your first video

Inside the app, **Generate** uses a production configuration derived from your settings: Kokoro narration,
routed illustrations, the bundled Inter font and FFmpeg/Node from `PATH`. If anything is missing (Kokoro
assets, FFmpeg, eSpeak NG, the browser worker), Generate tells you which command to run; check it first with
`.\Start-MediaStudio.ps1 --configure production`. `--media-config <file>` still overrides it. A full in-app render
hasn't been verified live yet ([known limitations](troubleshooting.md#known-limitations)), so the tested way to
render an MP4 today is the `video-production` CLI. This renders the bundled synthetic demo with local Kokoro narration:

1. Create a tools file, for example `artifacts\local-tools.json` (`artifacts\` is ignored by git):

   ```json
   {
     "sourceRoot": ".",
     "node": "node",
     "ffmpeg": "ffmpeg",
     "ffprobe": "ffprobe",
     "speech": { "provider": "kokoro", "voice": "af_heart", "language": "en-us", "speed": 1, "quality": "quantized" }
   }
   ```

2. Create, narrate, approve and render the demo from the repository root:

   ```powershell
   $cli = 'src/VideoProduction.Cli'
   $tools = 'artifacts\local-tools.json'
   dotnet run --project $cli -- create-demo --directory artifacts\demo --config $tools
   dotnet run --project $cli -- narrate --project artifacts\demo\project.json --config $tools
   dotnet run --project $cli -- validate --project artifacts\demo\project.json --config $tools
   dotnet run --project $cli -- plan --project artifacts\demo\project.json --config $tools
   dotnet run --project $cli -- approve --project artifacts\demo\project.json --scope trusted-bundled-fixture-only --by your-name --config $tools
   dotnet run --project $cli -- run --project artifacts\demo\project.json --config $tools
   dotnet run --project $cli -- render --project artifacts\demo\project.json --profile preview --config $tools
   ```

3. `render` prints the output path. Check the movie against the resolved timeline:

   ```powershell
   dotnet run --project $cli -- check --project artifacts\demo\project.json --movie <path-from-render> --config $tools
   ```

`narrate` needs the Kokoro asset (step 2 installs it). `plan`, `run` and `render` need the Playwright browser
worker that `Install-Prerequisites.ps1` sets up (`npm ci` and `npx playwright install chromium` in
`workers\browser`). If `plan` fails with `Cannot find package 'playwright'`, rerun that script.

Every command prints JSON. Use `--profile candidate` (the default) for a release candidate that needs
human review. See the [CLI reference](cli.md) and [project contracts](contracts.md).

## Next steps

- Add Azure models: [getting started: Azure](getting-started-azure.md).
- Tune tiers and routing: [configuration](configuration.md), [routing](routing.md).
- Measure your machine: `bench local`, `bench tts`, `bench stt` in the [CLI reference](cli.md#benchmarks).
- Something failed: [troubleshooting](troubleshooting.md).
