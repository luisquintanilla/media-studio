---
name: media-studio-troubleshoot
description: >-
  Diagnose Media Studio problems from symptoms, `configure test` rows and error codes (routing.*, copilot.*,
  speech.*, documents.*): Ollama not reachable or model not pulled, espeak-ng missing, asset hash mismatch,
  Azure 401/403 or RBAC delay, region/quota failures in azd up, Copilot not signed in, cloud disabled, locked restore
  failures. Use when asked "why does X fail", "configure test shows Failed", "the app says cloud is disabled",
  "narration is silent", "azd up failed", "Copilot not available", or "fix my setup".
---

# Troubleshoot

Docs: [troubleshooting](../../../docs/troubleshooting.md) (the code links to its `#speech` and `#speech-auth`
anchors), [configuration](../../../docs/configuration.md).

## 1. Collect facts first

```powershell
dotnet run --project src/VideoProduction.Cli -- diagnostics          # versions, settings summary (no secrets), runtimes, assets
dotnet run --project src/VideoProduction.Cli -- configure show       # effective settings file
dotnet run --project src/VideoProduction.Cli -- configure test       # one row per role: Ok / Failed / Skipped + remediation
dotnet run --project src/VideoProduction.Cli -- assets verify
```

The CLI skips `copilot.*` rows; for those run `.\Start-MediaStudio.ps1 --configure test` (or the app's Setup page).
Per-user data lives under `%LOCALAPPDATA%\MediaStudio` (`settings\model-providers.json`, `models\`, `svg-library\`);
`MEDIASTUDIO_HOME` relocates it. Ask the user to share `diagnostics` output, never the settings file with keys or
tokens from their environment.

## 2. Map the failing row / error code

| Row or code | Likely cause | Fix |
|---|---|---|
| `chat:*` "not reachable" | Ollama not running / wrong endpoint (`http://localhost:11434/v1`) | Start Ollama; `ollama list` |
| `chat:*` "is not pulled" | tier tag missing | `ollama pull <tag>` or `.\scripts\Setup-LocalModels.ps1 -Tier <T> -Yes` |
| chat "did not complete" | model still loading / too large | retry; `configure local --tier S` |
| `speech:tts espeak-ng` | espeak-ng not installed / PATH stale | `winget install eSpeak-NG.eSpeak-NG`, restart shell |
| `speech:tts (kokoro)`, `speech:stt (whisper)`, decisions Julia | asset missing or hash mismatch | `assets ensure --id <id>` (Julia is optional: heuristic decider is used) |
| `routing.cloud-disabled`, `copilot.cloud-disabled`, upload "nothing was sent" | `privacy.allowCloud` is false (by design) | Opt in via the wizard, `configure azd`, or `configure copilot --allow-cloud` — only if the user wants cloud |
| `routing.config` | tier points at an unknown connection / no tiers / bad endpoint | `configure local`; remote OpenAI-compatible endpoints need `allowRemote` + HTTPS |
| `routing.model-missing` | pinned local decision asset/dir missing | `assets ensure --id julia-1` or clear the pin |
| `routing.no-eligible`, `routing.uncertain-ineligible` | no allowed tier can take the request | add a stronger tier or allow cloud; see [routing](../../../docs/routing.md) |
| Azure "No Entra token" | not signed in / wrong tenant | `az login --tenant <tenant-id>` (or `azd auth login`) |
| Azure HTTP 401/403 | RBAC missing or not yet propagated | role **Cognitive Services User** (azd assigns it); wait ~5 min; `configure test --cloud-only` |
| Azure "unreachable" | network/VPN/firewall | check network or work offline |
| `speech.authentication` / `speech:*` cloud | MAI speech auth/region | [speech](../../../docs/speech.md); app falls back to Kokoro/Whisper if you switch providers back |
| `copilot.not-installed` | CLI not found | `MEDIASTUDIO_COPILOT_CLI` or install the Copilot CLI on PATH |
| `copilot.token-missing` (`copilot.login`: login could not start) | not signed in | `copilot` then `/login`, or `COPILOT_GITHUB_TOKEN`/`GH_TOKEN`/`GITHUB_TOKEN` |
| settings errors with a JSON path (e.g. `speech.tts.deployment`) | `model-providers.json` invalid or hand-edited | fix the named field, or rerun `configure local` (v1 files are migrated; the original is kept as `model-providers.v1.bak.json`) |
| `config.path`, `config.missing`, `config.permissions` | an explicit private file path is relative, missing or not owned by you | use an absolute path you own |

## 3. Azure provisioning

* Preprovision stops with a region list when `AZURE_LOCATION` is unset — pick one and `azd env set AZURE_LOCATION <region>`.
  Recheck with `pwsh infra/scripts/Find-FoundryRegion.ps1`. `MEDIASTUDIO_SKIP_REGION_CHECK=true` skips it (not
  recommended).
* Quota errors: lower `capacity` in `infra/models.json` or disable optional deployments (`DEPLOY_CLAUDE`,
  `DEPLOY_MISTRAL_OCR`, `DEPLOY_OPENAI_AUDIO`).
* Postprovision warnings are non-fatal; rerun the printed `configure azd` / `configure test` commands.
  See the `media-studio-deploy-azure` skill.

## 4. Build / restore

* `NU1004` with `--locked-mode` → `dotnet restore MediaStudio.slnx --force-evaluate` and commit lock files.
* SDK not found → install the version in `global.json` (11.0.100+) or use `.\.dotnet\dotnet.exe`.
* `OpenAI` version conflicts → keep 2.14.0 (see the "OpenAI SDK version" section of `docs/troubleshooting.md`).
  See the `media-studio-build-test` skill.

## Verification

The issue is fixed when the relevant `configure test` row is `Ok` (or an intentional `Skipped`) and the original
action (narration, chat, extraction, render) succeeds. Add a `docs/troubleshooting.md` entry for any new failure mode.

## Pitfalls

* Never "fix" a cloud-disabled error by flipping `privacy.allowCloud` without the user's consent, and never bypass
  `ConsentGatedDocumentExtractionClient` / `CloudGuard`.
* Never put API keys in `model-providers.json`; use `MEDIASTUDIO__CONNECTIONS__<ID>__APIKEY` or user-secrets.
* Don't paste tokens, tenant/subscription ids or resource names into issues or docs; use placeholders.
