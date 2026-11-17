# AGENTS.md

Instructions for coding agents (GitHub Copilot, Claude Code, OpenAI Codex and others) working in this repository.
People: see [docs/coding-agents.md](docs/coding-agents.md) for how to set up your agent and example prompts.

Media Studio is a **local-first** AI explainer-video studio: a Windows desktop app (WPF + Blazor Hybrid), the
cross-platform `video-production` CLI and the libraries behind them. Everything works offline with Ollama, Julia,
Kokoro and Whisper; Azure AI Foundry and GitHub Copilot are optional and opt-in. Most people using this repository
are not .NET developers: they want the app running, a video made or a setup fixed. Explain what you are about to do in
plain language, prefer the documented scripts and commands, and verify with `configure test`.

## First steps for a new user

1. Read-only checks first: `dotnet --version` (needs .NET 11), then
   `dotnet run --project src/VideoProduction.Cli -- diagnostics` and `... -- configure test`.
2. Local setup: follow `media-studio-setup-local` (prerequisites, tier, models). Offer `-WhatIf` / `-DryRun` first.
3. First video: follow `media-studio-make-video` (render the bundled demo, then the user's own topic).
4. Cloud is optional: only when the user asks, follow `media-studio-deploy-azure` or `media-studio-copilot`.

## Skills

Task procedures live in [`.github/skills/`](.github/skills/). Load the matching `SKILL.md` before you start; if your
agent doesn't load skills automatically, open the file in the table.

| Skill | Use when the user wants to |
|---|---|
| [`media-studio-setup-local`](.github/skills/media-studio-setup-local/SKILL.md) | set up the app offline: prerequisites, tier, Ollama models, Kokoro/Whisper/Julia assets |
| [`media-studio-make-video`](.github/skills/media-studio-make-video/SKILL.md) | render the demo, draft a brief and script, start the app, check a movie |
| [`media-studio-deploy-azure`](.github/skills/media-studio-deploy-azure/SKILL.md) | deploy or remove the optional Foundry resource with `azd`, import it with `configure azd` |
| [`media-studio-troubleshoot`](.github/skills/media-studio-troubleshoot/SKILL.md) | fix a failure: `configure test` rows, error codes, symptoms |
| [`media-studio-speech`](.github/skills/media-studio-speech/SKILL.md) | change or fix narration and transcription (Kokoro, Whisper, eSpeak NG, Azure Speech) |
| [`media-studio-models`](.github/skills/media-studio-models/SKILL.md) | change, re-pin or benchmark local and Foundry models |
| [`media-studio-routing`](.github/skills/media-studio-routing/SKILL.md) | change chat tiers, floors or the decider; evaluate routing |
| [`media-studio-copilot`](.github/skills/media-studio-copilot/SKILL.md) | use GitHub Copilot as an in-app tier or agent runtime |
| [`media-studio-illustrations`](.github/skills/media-studio-illustrations/SKILL.md) | work on SVG/PNG illustrations, the sanitizer, grounding, critique |
| [`media-studio-add-document-extractor`](.github/skills/media-studio-add-document-extractor/SKILL.md) | add or re-rank a document/OCR extractor |
| [`media-studio-add-provider`](.github/skills/media-studio-add-provider/SKILL.md) | add a model role, provider or connection kind end to end |
| [`media-studio-build-test`](.github/skills/media-studio-build-test/SKILL.md) | build and test like CI, change packages and lock files |

`media-explanation` is not a coding-agent skill: the app embeds it and checks its hash at runtime. Read it when
writing a brief or script; **never edit it**.

## Ask the user first

Stop and get an explicit yes before you:

* install software (`scripts/Install-Prerequisites.ps1`, winget, apt, brew) — show `-WhatIf` output first;
* download models (`scripts/Setup-LocalModels.ps1`, `ollama pull`, `assets ensure`) — several GB; show `-DryRun` first;
* run `azd up` / `azd provision` (billable), `azd down` (destructive, `--purge` is permanent) or `azd pipeline config`;
* turn on `privacy.allowCloud`, `--cloud-speech` or a cloud document extractor (data leaves the machine);
* start a GitHub Copilot sign-in (the user completes the device code; may use premium requests);
* run `approve` or press the app's Generate — these are human review gates; pass the user's name, never invent one;
* push, open pull requests or delete files you did not create.

Never ask for, accept, print or commit API keys, tokens or passwords. Keys come only from the environment variable
`MEDIASTUDIO__CONNECTIONS__<ID>__APIKEY` (id upper-cased, `-` → `_`) or user-secrets id
`MediaStudio.Sample.Development`, set by the user; Azure uses `az` / `azd` sign-in (`DefaultAzureCredential`). Share
`diagnostics` output, not the settings file or `azd env get-values`.

## Rules

1. **Local first.** Defaults work offline. Cloud features are optional, have a local path or a clear message, and ask
   `CloudGuard` (`src/MediaStudio.Configuration/CloudGuard.cs`) before any byte leaves the machine.
   `privacy.allowCloud` is `false` until the user turns it on.
2. **Never bypass the privacy gates**: `allowCloud`, `CloudGuard`, `IUploadConsent` /
   `ConsentGatedDocumentExtractionClient` (`src/MediaStudio.Sources/Routing/UploadConsent.cs`) and the Copilot
   `CopilotSessionPolicy` locks.
3. **Model names and versions live in two files only:** `assets/model-manifest.json` and `infra/models.json`. Update
   `docs/models.md` in the same change.
4. **No secrets** in settings, code, docs, tests or commits. Never commit `.azure/`, model binaries, `artifacts/`,
   `bin/`, `obj/` or `.dotnet/`.
5. **No owner-specific values**: use placeholders such as `<account>` and `https://<account>.services.ai.azure.com/`.
   CI's hygiene job and gitleaks enforce this.
6. **Do not edit `.github/skills/media-explanation/SKILL.md`.**
7. Experimental APIs and pinned packages (`OpenAI` 2.14.0, `GitHub.Copilot.SDK` 1.0.14) are pinned on purpose; see
   `docs/implementation-notes.md` before bumping.

## Build and test

```powershell
dotnet restore MediaStudio.slnx --locked-mode
dotnet build MediaStudio.slnx -c Release --no-restore -warnaserror
dotnet format whitespace MediaStudio.slnx --verify-no-changes
dotnet test MediaStudio.slnx -c Release --no-build --filter "Category!=Julia&Category!=Kokoro&Category!=Whisper&Category!=Live&Category!=Copilot&Category!=LocalRuntime"
dotnet run --project tests/VideoProduction.Tests -c Release --no-build
dotnet run --project tests/VideoProduction.Decisions.Tests -c Release --no-build
dotnet run --project tests/VideoProduction.Visual.Tests -c Release --no-build
```

* .NET 11 SDK (`global.json`); prefer the repo-local `.\.dotnet\dotnet.exe` when it exists.
* The full solution builds on Windows only (the WPF app). On Linux/macOS build the projects listed in the Linux job of
  `.github/workflows/ci.yml`.
* Package versions only in `Directory.Packages.props`; regenerate lock files with
  `dotnet restore MediaStudio.slnx --force-evaluate` after a package change and commit them with it.
* Tests needing models, network or credentials carry a `Julia`, `Kokoro`, `Whisper`, `LocalRuntime`, `Copilot` or
  `Live` trait and skip cleanly when the prerequisite is missing.
* Update the docs that describe behaviour you change and record deviations in `docs/implementation-notes.md`.

Copilot also reads [`.github/copilot-instructions.md`](.github/copilot-instructions.md), which adds a repository map.
