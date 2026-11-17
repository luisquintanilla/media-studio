# Copilot instructions for Media Studio

Media Studio is a **local-first** AI explainer-video studio: a Windows desktop app (WPF + Blazor Hybrid),
a cross-platform `video-production` CLI and the libraries behind them. Everything works offline on Ollama,
Julia, Kokoro and Whisper. Azure AI Foundry and GitHub Copilot are optional and opt-in.

Task-specific guidance lives in skills under `.github/skills/` (setup, Azure, routing, Copilot, providers,
documents, speech, models, illustrations, build/test, troubleshooting). Load the matching skill before you start.

## Repository map

| Path | What it is |
|---|---|
| `src/MediaStudio.App` | Windows desktop app (assembly `MediaStudio`): setup wizard, studio UI, `--configure`/`--diagnostics` |
| `src/VideoProduction.Cli` | `video-production` CLI: production commands plus `configure`, `assets`, `bench`, `documents` |
| `src/MediaStudio.Configuration` | Settings v2 (`ProviderSettings*`), `CredentialFactory`, `CloudGuard`, `ModelManifest`/`ModelAssets`, `ConfigureCommands`, `ConfigurationTester` |
| `src/VideoProduction.Providers` | `ProviderFactory` + `MediaStudioAIKeys` (keyed DI), `DecisionRoutingChatClient`, `Decisions/`, `Illustrations/`, `Bench/` |
| `src/VideoProduction.Decisions` | `IDecisionClient` contract and the local Julia decision model |
| `src/MediaStudio.Sources` | Document extraction; `Routing/` has the extractor router and the upload-consent gate |
| `src/MediaStudio.Speech` | Kokoro TTS, Whisper.net STT, Azure Speech (MAI) REST clients, speech bench |
| `src/MediaStudio.Agents` | Agent pipeline, `Copilot/` (GitHub Copilot SDK runtime and tier) |
| `src/VideoProduction.AgentFramework`, `src/VideoStudio.Contracts` | Agent Framework SVG generator and shared studio contracts |
| `src/VideoProduction.Core`, `src/Media.Audio`, `src/Media.Narration`, `src/AudioTranscriber`, `src/audio-transcriber` | Production pipeline, audio, narration, audio CLI |
| `tests/` | `MediaStudio.Tests`, `AudioTranscriber.Tests` (xUnit) and three console-runner suites |
| `assets/model-manifest.json` | Pinned local model assets, Ollama tier tags, cloud voice ids |
| `infra/` | `azd` Bicep for one Foundry resource; `infra/models.json` lists every cloud deployment |
| `scripts/` | `Install-Prerequisites`, `Setup-LocalModels` (PowerShell and bash) |
| `docs/` | User and contributor docs; `docs/implementation-notes.md` records what was actually built and every deviation |

## Build and test

* SDK: .NET 11 (`global.json` pins `11.0.100`, `rollForward: latestFeature`). Use the repo-local SDK when present
  (`.\.dotnet\dotnet.exe`, gitignored), otherwise a system `dotnet` that is .NET 11. CLI-spawning tests need the .NET 11
  `dotnet` first on `PATH`.
* Restore (locked) and build with zero warnings (`TreatWarningsAsErrors` is on in `Directory.Build.props`):

  ```powershell
  dotnet restore MediaStudio.slnx --locked-mode
  dotnet build MediaStudio.slnx -c Release --no-restore -warnaserror
  dotnet format whitespace MediaStudio.slnx --verify-no-changes   # CI gate; run without the flag to fix
  ```

* Offline tests (the CI default filter):

  ```powershell
  dotnet test MediaStudio.slnx -c Release --no-build --filter "Category!=Julia&Category!=Kokoro&Category!=Whisper&Category!=Live&Category!=Copilot&Category!=LocalRuntime"
  dotnet run --project tests/VideoProduction.Tests -c Release --no-build
  dotnet run --project tests/VideoProduction.Decisions.Tests -c Release --no-build
  dotnet run --project tests/VideoProduction.Visual.Tests -c Release --no-build   # run from the repo root (reads fixtures/)
  ```

* Test categories that need something extra: `Julia`, `Kokoro`, `Whisper` (downloaded assets), `LocalRuntime` (a running
  Ollama), `Copilot` (`COPILOT_GITHUB_TOKEN`), `Live` (Azure). `OpenAICompat` runs offline by default. New tests that
  need models, network or credentials **must** carry one of these traits and skip cleanly when the prerequisite is missing.
* Run the CLI from source: `dotnet run --project src/VideoProduction.Cli -- <command>`.

## Packages and lock files

* Central Package Management: versions live only in `Directory.Packages.props`
  (`CentralPackageTransitivePinningEnabled` is on). Never put `Version=` on a `PackageReference`.
* Every project has a `packages.lock.json` (`RestorePackagesWithLockFile`). After adding or bumping a package, regenerate
  the lock files with `dotnet restore MediaStudio.slnx --force-evaluate` and commit them with the change. CI restores with
  `--locked-mode` and fails on stale lock files.
* `NuGet.Config` uses nuget.org only, with package source mapping. Do not add other feeds.
* Pinned on purpose (see `docs/implementation-notes.md`): `OpenAI` 2.14.0 (MEAI.OpenAI 11.0.0 breaks on 2.15),
  `GitHub.Copilot.SDK` 1.0.14 (required by `Microsoft.Agents.AI.GitHub.Copilot` 1.28.0).
* Experimental-API diagnostics suppressed in `Directory.Build.props` `NoWarn`: `MEDE0001`, `EXTEXP0019`, `CTDE0001`,
  `MEAI001`, `OPENAI001`, `OPENAI002`, `MAAI001`, `AIRoutingChat`, `VPD001`, `VPROD001`, `GHCP001`. Do not add to this list
  to silence a real warning; fix the warning instead.

## Non-negotiable rules

1. **Local first.** Defaults must work offline. Any feature that calls a cloud service must be optional, must have a local
   path or a clear message, and must ask `CloudGuard` (`src/MediaStudio.Configuration/CloudGuard.cs`) before any byte leaves
   the machine. `privacy.allowCloud` is `false` until the user turns it on.
2. **Never bypass the privacy gates.** Remote document extractors are always wrapped in `ConsentGatedDocumentExtractionClient`
   (`src/MediaStudio.Sources/Routing/UploadConsent.cs`). Never weaken `allowCloud`, `CloudGuard`, `IUploadConsent` or the
   Copilot `CopilotSessionPolicy` locks, and never add a code path that skips them.
3. **Model names and versions live in two files only:** `assets/model-manifest.json` (local assets, Ollama tags, cloud voice
   ids) and `infra/models.json` (Foundry deployments). Do not hard-code model ids, tags, versions or voice ids elsewhere;
   update `docs/models.md` in the same change.
4. **No secrets in settings or in the repo.** The settings file holds no keys. API keys come only from the environment
   variable `MEDIASTUDIO__CONNECTIONS__<ID>__APIKEY` (id upper-cased, `-` → `_`) or user-secrets id
   `MediaStudio.Sample.Development`. Entra uses `DefaultAzureCredential`. Never commit keys, tokens, `.azure/`, model
   binaries, `artifacts/`, `bin/`, `obj/` or `.dotnet/`.
5. **No owner-specific values.** Use placeholders such as `<account>` and `https://<account>.services.ai.azure.com/`.
   CI's hygiene job runs a case-insensitive `git grep` for owner resource names and private paths (regex in the
   `OWNER_VALUES_PATTERN` repository secret) plus gitleaks; both must stay clean.
6. **Do not edit `.github/skills/media-explanation/SKILL.md`.** The app hash-checks it at runtime.
7. Experimental APIs (`AIRoutingChat`, `MEAI001`, `VPD001`, `GHCP001`, ...) are pinned; when you bump them, re-run the
   tests named in the relevant skill.

## Commits and pull requests

* Small, reviewable commits with an imperative summary; keep generated lock-file updates in the same commit as the
  package change.
* Before opening a PR: locked restore, Release build with 0 warnings, the offline test filter and the three console
  runners pass. Update the docs that describe the behaviour you changed and record deviations in
  `docs/implementation-notes.md`.
* Never include real endpoints, subscription or tenant ids, user names or emails in code, docs, tests or fixtures.
