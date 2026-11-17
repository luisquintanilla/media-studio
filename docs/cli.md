# Command-line reference

Media Studio has three entry points:

| Tool | Run from source | What it's for |
| --- | --- | --- |
| `MediaStudio.exe` (the app) | `.\Start-MediaStudio.ps1 [app options]` | The desktop studio; also `--configure` and `--diagnostics`. |
| `video-production` | `dotnet run --project src/VideoProduction.Cli -- <command>` | Setup, benchmarks, documents and every production stage. |
| `audio-transcriber` | `dotnet run --project src/audio-transcriber -- <command>` | Local Whisper transcription of WAV files ([SMART_TOOL.md](../SMART_TOOL.md)). |

Examples below use `video-production` for `dotnet run --project src/VideoProduction.Cli --`. All tools read
the settings file described in [configuration](configuration.md); set `MEDIASTUDIO_HOME` to use another folder.

## Setup commands

```text
video-production configure local [--tier S|M|L|XL] [--allow-xl] [--fast <tag>] [--balanced <tag>] [--vision <tag>]
video-production configure azd [--env <name>] [--from-file <path>] [--tenant <id>] [--cloud-speech]
video-production configure test [--cloud-only]
video-production configure show
video-production configure production [--write] [--state-root <dir>] [--settings <file>] [--models <dir>]
video-production configure copilot [--model <id>] [--tier] [--runtime] [--illustrations] [--byok <connection>] [--allow-cloud] [--off]
video-production assets list
video-production assets verify [--id <asset>]
video-production assets ensure [--id <asset>] [--tier S|M|L|XL] [--fp32]
video-production diagnostics
```

| Command | Does |
| --- | --- |
| `configure local` | Detects hardware and runtimes and writes local-first settings for a tier ([models](models.md#local-tiers)). Prints the `ollama pull` and `assets ensure` commands still needed. |
| `configure azd` | Imports `azd env get-values` (or a saved file) into a Foundry connection with Entra ID auth and allows cloud. `--cloud-speech` also switches narration and transcription to Azure Speech. See [Azure guide](getting-started-azure.md). |
| `configure test` | Probes every role and prints a ✅/❌ table with a fix for each failure. Exit code 1 when anything fails. `--cloud-only` checks only cloud roles. |
| `configure show` | Prints the settings path and contents (no secrets are stored there). |
| `configure production` | Derives the production configuration that Generate uses from the settings, the installed assets, the bundled Inter font and the tools on `PATH`. Prints the file paths, a summary and a fix for anything missing (exit code 1 then); `--write` saves `media-config.json`, `tools.json` and `production-config.json` (for `produce --production-config`) under `<state-root>\settings\default-production`. Default state root: `%LOCALAPPDATA%\MediaStudio\production`. No secrets are written. |
| `configure copilot` | Sets the optional GitHub Copilot tier/runtime ([Copilot](copilot.md)). |
| `assets list/verify/ensure` | Lists, re-hashes or downloads the pinned Kokoro, Whisper and Julia files. `ensure` without `--id` fetches the assets for the tier. |
| `diagnostics` | Versions, a settings summary (no secrets), runtimes and asset state — useful in bug reports. |

Common options: `--settings <path>` (default `%LOCALAPPDATA%\MediaStudio\settings\model-providers.json`),
`--models <dir>` (default `%LOCALAPPDATA%\MediaStudio\models`), `--manifest <path>`.

The app accepts the same commands after `--configure`, and adds the GitHub Copilot and document checks to
`configure test`:

```powershell
.\Start-MediaStudio.ps1 --configure test
.\Start-MediaStudio.ps1 --configure assets ensure --id julia-1
```

The scripts in `scripts\` wrap these commands: `Install-Prerequisites.ps1` (tools via winget),
`Setup-LocalModels.ps1` (tier detection, `ollama pull`, assets; `-DryRun`, `-Tier`, `-Family`, `-Fast`,
`-Balanced`, `-Vision`, `-AllowXl`, `-SkipOllama`, `-SkipJulia`, `-Yes`).

## Benchmarks

```text
video-production bench chat  [--tiers <a,b>] [--prompts <n>] [--out <dir>] [--settings <file>] [--models <dir>]
video-production bench image [--briefs <n>] [--out <dir>] [--settings <file>] [--models <dir>]
video-production bench local [--candidates <tag,tag>] [--endpoint <url>] [--prompts <n>] [--judge <tier>|--no-judge] [--out <dir>] [--settings <file>]
video-production bench tts   [--text <text>] [--voice <kokoro-voice>] [--out <dir>] [--local-only] [--settings <file>] [--models <dir>]
video-production bench stt   [--files <dir|dir/*.wav>] [--model base|large-v3-turbo] [--out <dir>] [--local-only] [--settings <file>] [--models <dir>]
video-production bench documents <file>... [--local-only] [--allow-cloud] [--settings <file>]
```

| Bench | Measures |
| --- | --- |
| `chat` | Every configured chat tier: latency, tokens/s, valid JSON. Cloud tiers only when cloud is allowed. |
| `image` | Short briefs through the [illustration pipeline](illustrations.md): valid SVG, critique scores. |
| `local` | Each local Ollama model (default: all installed) on 14 routing cases and up to 10 storyboard prompts; optionally judged by your strongest tier. Use it to choose models for your machine. |
| `tts` | Narration speed (real-time factor) for Kokoro and, unless `--local-only`, Azure Speech. |
| `stt` | Word error rate and speed. Pairs `<name>.wav` with `<name>.txt`; without `--files` it synthesizes reference clips with Kokoro. `bench cloud-stt` is an alias. |
| `documents` | Auto and each extractor on your files ([documents](documents.md)). |

Results from one machine are in [models](models.md#measured-results).

## Documents

```text
video-production documents probe <file> [--extract] [--allow-cloud] [--settings <file>]
```

Prints the local probe, the plan Auto would follow and the candidates as JSON. `--extract` also runs it.
Nothing is uploaded unless `--allow-cloud` is passed and `privacy.allowCloud` is on.

## Production commands

`video-production --help` prints every production command with its options; `video-production <command> --help`
shows one; `video-production -V` prints the version; `video-production manifest` prints the tool manifest
(JSON). Output is JSON on stdout and errors on stderr; exit code 1 means an error, 130 a cancellation.

| Group | Commands |
| --- | --- |
| Demo and checks | `create-demo`, `doctor`, `validate` |
| Plan and approve | `plan`, `approve`, `run` |
| Narration and captions | `narrate`, `import-narration`, `analyze-speech`, `transcribe`, `adopt-transcript`, `bind-delivery-transcript` |
| Render and deliver | `resolve`, `render`, `check`, `release`, `export` |
| Drafts | `draft-start`, `draft-read`, `draft-edit`, `draft-snapshot`, `draft-fork` |
| Whole productions | `produce`, `status`, `cancel`, `media-produce`, `audio-produce`, `audio-check` |

`produce` also uses exit codes 73 (conflict), 74 (interrupted) and 75 (busy). The stage-by-stage walkthrough
is in [getting started (local)](getting-started-local.md); contracts and receipts are in
[contracts](contracts.md), [file production](file-production.md) and [production upgrades](production-upgrades.md).

## App options

```text
MediaStudio.exe [--state-root <dir>] [--media-config <file>] [--source-config <file>] [--voice-config <file>]
                [--new-project] [--authoring fixture|agent] [--concierge] [--model <id> | --studio-default-model]
MediaStudio.exe --configure <configure or assets command>
MediaStudio.exe --diagnostics [summary | list | window [<output-dir>] | window-capture <output-dir> [--gdi]
                              | local-whisper <16kHz-mono.wav> [<voice-config.json>]]
```

| Option | Does |
| --- | --- |
| `--state-root <dir>` | Private folder for projects, drafts and productions (absolute path). Default: `%LOCALAPPDATA%\MediaStudio\studio` (`%MEDIASTUDIO_HOME%\studio` when set). |
| `--media-config <file>` | Production configuration to use instead of the default one derived from your settings (see `configure production`); needs `--state-root`. |
| `--source-config <file>` | Source (OCR) configuration; needs `--state-root`. |
| `--voice-config <file>` | Enables local Whisper push-to-talk dictation. |
| `--new-project` | Opens a new project. |
| `--authoring fixture\|agent` | `fixture` (default) uses canned edits. `agent` authors with the GitHub Copilot agent ([Copilot](copilot.md)); it needs `--state-root` and a model: `--model <id>`, the Copilot model in settings, or `--studio-default-model`. `--concierge` starts from an empty storyboard and lets the agent propose the whole brief and scene structure, which you accept or reject as a unit. `--concierge`, `--model` and `--studio-default-model` require `--authoring agent`. |
| `--diagnostics [name]` | Runs a diagnostic and exits (see below). Without a name it runs `summary`. |

### Diagnostics

| Name | Does |
| --- | --- |
| `summary` (default) | Versions (including WebView2), a settings summary without secrets, local runtimes and model assets. |
| `list` | Lists the available diagnostics. |
| `window [<output-dir>]` | Opens the app window on a throwaway state, navigates every view and saves a screenshot. |
| `window-capture <output-dir> [--gdi]` | Records a 5-second FFmpeg capture of the app window (`--gdi` uses GDI capture). |
| `local-whisper <16kHz-mono.wav> [<voice-config.json>]` | Transcribes a WAV file with local Whisper to check dictation. |

An unknown name exits with code 2; a failed diagnostic exits with code 1.

With no options the app opens normally; on first run the setup wizard appears.
