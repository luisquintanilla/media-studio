# Implementation notes

## P1–P2 — Restructure, lean core, GA packages

Deviations from the plan:

* **OpenAI gate (P2.5) failed.** `Microsoft.Extensions.AI.OpenAI` 11.0.0 is the newest published version. It is compiled against OpenAI 2.14. With OpenAI 2.15, streaming Responses throws `MissingMethodException`; the `Category=OpenAICompat` test reproduces this offline.
  * Applied mitigation (b): OpenAI is pinned to **2.14.0**.
  * `OpenAI.Decisions` code is behind the `OPENAI_DECISIONS` symbol, which is set by `-p:OpenAIDecisions=true`. This covers `OpenAiDecisionClient`, the `ConfiguredDecisions` native branch (it now throws `decision.unavailable`), `NativeDecisionsSmokeProof` and its CLI switches, and the Decisions typed-HTTP fixture tests. The `MediaJudgementTests` native section has an `#else` that asserts the explicit failure.
  * `DecisionRefusedException` stays unconditional.
  * Documented in `docs/troubleshooting.md`.
  * The dotnet/extensions issue was **not filed**; that needs a human.
  * The patch tooling (`tools/patches`, `Prepare-OpenAICompatibility.ps1`, `docs/openai-compatibility.md`) was deleted anyway, because it only produced a private package that a nuget.org-only build cannot use.
* **Package versions kept rather than bumped:** OnnxRuntime 1.23.2, Anthropic 13.3.0, System.CommandLine 2.0.14, Whisper.net 1.9.0 (matches `WhisperNetIntegration.PackageVersion`), and Tokenizers 2.0.0 / Tokenizers.HuggingFace 3.23.1.
* **GitHub.Copilot.SDK is pinned to 1.0.14**, the version `Microsoft.Agents.AI.GitHub.Copilot` 1.28.0 requires. Agents.AI moved from 1.26.0 to 1.28.0. Hosting moved from 10.0.13 to 11.0.0.
* **NuGet.Config** clears sources, uses only nuget.org v3, and adds `packageSourceMapping` (`*` → nuget.org).
  * For local verification only, `api.nuget.org` failed TLS from the build machine. A gitignored `artifacts/restore/NuGet.Config` pointing the same `nuget.org` key at `https://www.nuget.org/api/v2/` was passed with `--configfile`.
  * Lock files were generated that way. Content hashes are package hashes, so they are source-independent.
* **Tests moved to extras:**
  * All of `VideoStudio.Tests` moved to `extras/web-studio`.
  * MCP-only runners moved to `extras/mcp/tests`, including `FileProductionTests` and the wire/contract runners. Extras are not in `MediaStudio.slnx` and the MCP test project no longer builds as-is.
  * `extras/api-probe-proofs` likewise no longer builds, because `MediaDraftToolProof` moved to `tests/MediaStudio.Tests` and the agents moved to `src/MediaStudio.Agents`.
* `CopilotSetupProof` moved into the app (`src/MediaStudio.App`).
* The Sherpa voice-setup proof modes were removed together with Sherpa (P1.5). The Model Garden doctor check was replaced by a `whisper-model` presence check.
* **`AuthoringAgentFactory` is a minimal first version:**
  * `CreateMaf` wraps a provided `IChatClient` in a `ChatClientAgent`.
  * `CreateCopilot` keeps the existing `CopilotAuthoringAgent` path.
  * The full refactor is P4.5.
* **Console-runner test projects are kept**, run with `dotnet run --project`: `VideoProduction.Tests`, `VideoProduction.Visual.Tests` and `VideoProduction.Decisions.Tests`. `VideoProduction.Visual.Tests` must run from the repo root because it reads `fixtures/`.
* **CLI-spawning tests need the .NET 11 runtime.** `MediaStudio.Tests` (CliAdapter and MediaProjects) spawn `dotnet` from `PATH`. If that resolves to an older machine-wide `dotnet`, put the repo-local `.dotnet` first on `PATH`.
* **Docs touched minimally.** Stale RC SDK and `review` package mentions were updated in README and storyboard-routing. Historical records were left for P8: SOURCE-ORIGINS, `process-and-judgment-gates.md`, `media-verification.json` and `studio.md`.

## P7 — Azure: one Foundry resource via azd

Files: `azure.yaml`, `infra/main.bicep`, `infra/main.parameters.json`, `infra/models.json`, `infra/modules/foundry.bicep`, `infra/modules/role-assignments.bicep`, `infra/scripts/Find-FoundryRegion.ps1`, `infra/scripts/regions.json`, `infra/hooks/{pre,post}provision.{ps1,sh}`; `.azure/` added to `.gitignore`.

Deviations and corrections, all verified on Microsoft Learn and with `az cognitiveservices model list` on 2026-11-16:

* **Cognitive Services User GUID.** The plan's `a97b65f3-24c7-4388-baec-2e87ef14b4b2` is wrong. The correct GUID is `a97b65f3-24c7-4388-baec-2e87135dc908`. Its data actions are `Microsoft.CognitiveServices/*`, which covers OpenAI v1, Speech (MAI), Document Intelligence, Content Understanding and Mistral. So *Cognitive Services OpenAI User* and *Cognitive Services Speech User* are not assigned.
* **"Azure AI User" is now "Foundry User"** (`53ca6127-db72-4b80-b1b0-d745d6d5456d`; the ID did not change). It is assigned for the project endpoint and the Anthropic route.
* **Model versions and names:**
  * `gpt-live-transcribe` is **2026-07-28**, not 07-29.
  * `gpt-image-2.5-flare` is **2026-09-08**.
  * Mistral OCR is **`mistral-ocr-4-0`** (hyphens), version `1`, format `Mistral AI`.
  * Claude (opt-in) is **`claude-sonnet-5`**, version `2`, format `Anthropic`. It is hosted on Azure and GA.
  * All other versions match the plan.
* **Region checks are split into required and advisory.** No region is green for everything:
  * `gpt-image-2.5-flare` and `gpt-live-transcribe` (GlobalStandard) do not overlap the MAI-Transcribe regions.
  * The Decisions API excludes `eastus2`.
  * So `Find-FoundryRegion.ps1` treats every enabled `models.json` deployment (name + version + SKU) as **required** (blocking).
  * MAI voices, MAI-Transcribe, Decisions, Content Understanding and Document Intelligence are **advisory**: each has an app fallback, they are listed in `regions.json` with source URLs, and they only rank regions.
  * Today's scan: `swedencentral` is recommended (only MAI-Transcribe ❌). `westus3` ties. `eastus2` is required-green but has no Decisions. `eastus`, `northcentralus`, `australiaeast`, `japaneast` and `uksouth` lack image and live-transcribe.
  * Preprovision fails only on required gaps.
  * The `.sh` hook needs `pwsh` for the check and warns and skips without it.
* **Extra parameters and outputs:**
  * `cuEmbeddingModel` (env `CU_EMBEDDING_MODEL`) swaps the `embedding` deployment to `text-embedding-3-large`. It is the fix that postprovision prints.
  * Extra outputs `MEDIASTUDIO_MISTRAL_OCR_ENABLED` and `MEDIASTUDIO_CU_EMBEDDING_MODEL` are added.
  * All flags are env-overridable via `azd env set` (`DEPLOY_OPENAI_AUDIO`, `DEPLOY_MISTRAL_OCR`, `DEPLOY_CLAUDE`, `DISABLE_LOCAL_AUTH`, `ASSIGN_RBAC`, `ENABLE_CONTENT_UNDERSTANDING`, `CLAUDE_ORGANIZATION_NAME`, `CLAUDE_COUNTRY_CODE`, `CLAUDE_INDUSTRY`).
* **Role assignments run inside the foundry module**, before the deployments. The deployments depend on them, following the Azure-Samples/claude pattern, which gives RBAC a head start on propagation.
* **Postprovision ordering:** the RBAC wait runs first and the CU defaults PATCH runs second, so the PATCH does not fail on unpropagated roles.
  * The `.ps1` hook uses `Invoke-WebRequest` for the CU PATCH. This avoids `az rest --body` JSON quoting problems on Windows and is equivalent to the plan's `az rest` call.
  * The `.sh` hook uses `az rest` as in the plan.
  * The Decisions probe body follows the Learn page's `predicate` question shape. It is warning-only.
* **Not yet verified, because nothing was provisioned:** the Foundry API version `2025-10-01-preview` matches Azure-Samples/claude and builds cleanly with Bicep 0.49.2. `azd up` was not run. The plan's "Done when" end-to-end check is still pending.
## P3 — Configuration, credentials, model assets

Commits: `76505fa` P3.1, `488b761` P3.2, `aaa7e1f` P3.3, `dc70633` P3.4 (with P3.6), `3e8f0ff` P3.7, and P3.8 (these notes).

New project `src/MediaStudio.Configuration` (namespace `MediaStudio.Configuration`):

* **Settings and storage:** the settings v2 records, `ProviderSettingsStore`/`Validator`/`Migrator`/`Defaults`, and `FoundryEndpoints`.
* **Credentials and assets:** `CredentialFactory`, `ModelManifest` + `ModelAssets` (`assets/model-manifest.json`), `LocalRuntimeDetector`/`HardwareProfile`/`TierSelector`, and `OllamaPuller`.
* **Configure CLI:** `AzdEnvironmentImporter`, `ConfigurationTester` and `ConfigureCommands`.

Deviations from the plan:

* **CLI names.** The model commands are `assets list|verify|ensure [--id]`, not `models download|verify|list`. This avoids colliding with the chat-model wording. The extra `configure show` prints the effective settings without secrets.
* **CLI hosts.** The commands run in `video-production` and also through `MediaStudio.exe --configure <sub>`, which uses `AttachConsole` so the WinExe can print.
* **`configure test` checks.**
  * Local roles get real probes: a 1-token chat, an embedding call, Ollama model presence, asset verification and espeak-ng.
  * Cloud roles get an Entra token or API-key check plus a model-list/deployment check per connection. The per-role TTS word, 1 s STT sample, PDF and decision probes from the plan are not implemented yet; they need live Azure (P4–P6).
  * Offline, cloud checks degrade to a warning with a remediation.
* **Foundry Local** is detected with `foundry --version`; there is no HTTP probe.
* **The settings file is not ACL-protected.** It holds no secrets: API keys come only from `MEDIASTUDIO__CONNECTIONS__<ID>__APIKEY` or the user-secrets ID `MediaStudio.Sample.Development`.
* **v1 compatibility.**
  * The `"azure-cli"` v1 auth label is still accepted on import, but it maps to `Entra` (DefaultAzureCredential).
  * The legacy App `ModelProviderState` bridge (`ProviderSettingsProjection`) only projects Entra Foundry connections.
  * There is no App projection unit test, because `MediaStudio.Tests` does not reference the App.
* **Ollama tiers.** The L/XL embedding model is `qwen3-embedding`; S/M use `embeddinggemma`. `OllamaPuller` (streaming `/api/pull`) was added so the wizard can pull missing tags.
* **`ModelAssets` progress.** It also has an `IProgress<ModelAssetProgress>` overload (bytes, total, phase) for the CLI and the wizard.
* **Julia is not migrated yet.** Its assets are in the manifest, but the Julia runtime still uses its own directory resolution. Wiring it to `ModelAssets.RequirePath("julia-1")` is P4.
* **P3.2 gates:**
  * `ChatRoutePolicy.DevelopmentEvidenceSha256` was deleted.
  * `StoryboardRoutingPolicy.DevelopmentEvidenceSha256` is kept as an optional, ungated provenance field for the routing-evaluation harness.
  * The routing `Approved` fields are kept but inert, for JSON compatibility. Tests now disable candidates with `Available = false`.
  * The native image generator now accepts any deployment name and `*.cognitiveservices.azure.com` hosts, but still requires an HTTPS `/openai/v1` endpoint on a trusted host.
* **P3.4 scrub.**
  * Proofs and docs use `example-*` endpoints.
  * The Mistral OCR endpoint is derived from settings (`FoundryEndpoints.MistralOcr`), with a placeholder fallback.
  * The "Use saved cloud defaults" button and `--configure-foundry-roles` were removed.
  * Owner-local doc paths now point under `%LOCALAPPDATA%\MediaStudio\`.
* **Setup wizard (P3.6).**
  * P3.6 was committed together with P3.4 because the two steps touch the same files.
  * The wizard's **Test** button runs `configure test` for all roles rather than for each row.
  * The Copilot sign-in step is deferred to P4.5.
* **Pre-existing:** `-p:OpenAIDecisions=true` does not build (see P1–P2); P3 does not change this.

Verification (local, 2026-11-16):

* Release build: 0 warnings, 0 errors.
* `MediaStudio.Tests` default filter: 61 passed. That includes 43 configuration tests, with fake HTTP handlers and a tiny fake asset, and no network.
* Console runners pass: `VideoProduction.Tests`, `VideoProduction.Decisions.Tests` and `VideoProduction.Visual.Tests`.
* `configure local --tier S` and `configure test` were run against local Ollama, with `qwen3.5:4b` and `embeddinggemma` pulled:
  * Chat, vision and embeddings passed.
  * The remaining failures were real and had the right fixes: `qwen3.5:2b` was not pulled, and the Kokoro and Whisper assets were not downloaded.

Not yet verified:

* `azd`-backed `configure azd` and the cloud `configure test` against real resources (they need a login).
* A full `assets ensure` download of the real models.

## P4 — Smart chat routing (Julia first-class)

Commits: `P4.1`–`P4.4`.

* **Decision chain.** `DecisionClientFactory.Create(settings, assets)` builds `FallbackDecisionClient`
  over local Julia (resolved through `ModelAssets` asset `julia-1`, loaded lazily), the native OpenAI
  Decisions client only when cloud is allowed and the build has `-p:OpenAIDecisions=true`, and
  `HeuristicDecisionClient` last. Each link has a 2 s timeout, so routing always answers with zero models.
  `ConfiguredDecisions` was replaced; Julia no longer resolves its own model files.
* **Router.** `DecisionRoutingChatClient` (built on MEAI `FailoverChatClient`) replaced `PolicyChatRouter`.
  * The existing route selectors were not reduced to "pick a keyed client"; they keep their tier lists and
    now share the router.
  * `UncertainRoute` and `NamedChatRoute.Approved` were dropped; low confidence escalates to the next
    stronger eligible tier instead.
  * `ChatRouteReceipt` is telemetry/logging only (`media.chat.route`, ActivitySource `MediaStudio.Routing`);
    a per-project `chat-route.json` replaces the old receipt binding.
  * The decider sees at most the last 16k characters of the transcript. Routers created through DI are
    lenient (an unanswerable decision falls back to the weakest eligible tier instead of throwing).
  * Authoring purposes (`authoring`, `video-outline`, `storyboard`, `document-authoring`) apply a
    tier floor (`below-authoring-floor`).
  * Added `ChatTier.Thinking` (default false): on `openai-compatible` tiers reasoning is sent as
    `none`, because Ollama ignores output limits while a model thinks.
  * `RoutingEvaluation` (fixture accuracy report) was kept and updated rather than removed.
* **DI.** `AddMediaStudioAI` registers keyed routers per purpose, plus vision, embeddings and the
  `routing` decider. VideoProduction.Providers now references Microsoft.Extensions.AI (for
  `FailoverChatClient`, logging and OpenTelemetry).
* **Not done in P4.** App consumers (Create/Storyboard selectors, IllustrationProviders) still build
  their clients through the existing paths; they do not yet resolve the keyed clients.
* **Pre-existing:** native OpenAI Decisions stay compiled out (`-p:OpenAIDecisions=true` does not build);
  unchanged.

Verification: Release build 0 warnings; default tests green; `Category=LocalRuntime` routing tests
(2) pass against local Ollama (`qwen3.5:4b`). The `Category=Julia` test was not run: the Julia
assets are not present on this machine.

## P4.5 — GitHub Copilot connection kind and optional agent runtime

Commits: `P4.5.1`–`P4.5.8`.

* **No spike.** Without a GitHub login the "step 0" spike against a live Copilot session could not run.
  The device-login parser is tested against a synthesized `copilot login` transcript.
* **Tier client.** `CopilotTierChatClient` runs `AIAgent.RunAsync` with a fresh session per call instead
  of `AsIChatClient`, so usage and the stateless contract are explicit. JSON format is enforced by
  instruction + `JsonDocument` validation with one repair retry (ported from `BoundedAuthoring`, which
  stays in `extras/service`).
* **BYOK.** `OnListModels` returns only the configured model for the BYOK connection.
* **Sign-in.** The CLI runs with a private `COPILOT_HOME` under `%LOCALAPPDATA%\MediaStudio\copilot`, so
  sign-in goes through the app (device login) or the `env-token` mode (`COPILOT_GITHUB_TOKEN`/`GH_TOKEN`).
  Reuse of an existing `gh auth login` is unverified.
* **Runtime naming.** `--authoring copilot` became `--authoring agent`; the agent runtime (MAF or Copilot)
  comes from settings. `CopilotAuthoringAgent` was kept as the authoring AG-UI agent for both runtimes
  (it is used throughout the app and proofs); its Copilot runtime now comes from `CopilotClientFactory`.
* **Pipeline.** `AgentPipeline.Wrap` streaming buffers one update so the usage receipt can be attached
  to the final update.
* **`configure copilot`.** Replaces the whole Copilot configuration, adds `--illustrations`,
  `--allow-cloud` and `--off`, and needs no login. `video-production configure test` does not reference
  MediaStudio.Agents, so it reports the `copilot.*` rows as skipped and points to
  `MediaStudio.exe --configure test`.
* **Checks.** The `copilot.agent` check calls a tiny `media_probe` `AIFunction` instead of a fake
  `MediaDraftTool`.
* **Wizard.** The Copilot step comes after Roles (optional). The tier checkbox is disabled for BYOK.
  Whether the CLI reports the new auth status without a client restart after device login is unverified.
* **Illustration runtime.** The Copilot override is a keyed-DI registration only; the app's
  IllustrationProviders path does not yet consume keyed clients (same gap as P4).
* **CLI runtime bundling.** `CopilotSkipCliDownload` is now true for every project except
  `MediaStudio.App`, which downloads the SDK-pinned CLI (1.0.85, SHA256-verified) at build. The CLI's
  LICENSE.md, which the SDK's copy step omits, is copied to `licenses/GitHubCopilotCLI.LICENSE.md` as its
  redistribution terms require. Offline/CI builds pass `-p:CopilotSkipCliDownload=true`.
* **Pre-existing:** an order-dependent test on the shared `Json.Options` instance can fail when run in
  isolation; unchanged.

Not verified (needs a GitHub Copilot sign-in or a manual run): done-when items 1, 2 and 5 (a real
device-login round trip, a live Copilot tier answer and premium-request accounting). The Copilot
unit tests use fakes and run by default; the single live `Category=Copilot` test returns early without
`COPILOT_GITHUB_TOKEN`, so it passed vacuously here.
## P6 (speech)

This covers only the speech steps of P6. Illustration grounding and the critique loop (P6.8) are left for later, as are the chat and image bench commands.

What was built:

* **New project `src/MediaStudio.Speech`.** It references MediaStudio.Configuration plus these packages: MEAI.Abstractions, OnnxRuntime, ML.Tokenizers, Numerics.Tensors, Whisper.net and Whisper.net.Runtime. All of them were already in CPM, so `Directory.Packages.props` is unchanged.
* **Kokoro.**
  * Kokoro runs on ONNX Runtime and implements `ITextToSpeechClient`.
  * It uses espeak-ng for phonemes, reading stdout as UTF-8 and stripping U+200D, `_` and newlines. The `tokenizer.json` vocabulary is used for tokens.
  * Text is split into chunks of at most 510 tokens at sentence boundaries. The voice style row is `min(n, 509)`.
  * Output is a 24 kHz 16-bit WAV.
* **Whisper.net** implements `ISpeechToTextClient` and returns word timings under `TimedWord.WordsKey`.
* **Azure MAI-Transcribe** has its own REST client.
* **Azure TTS** uses SSML, with the default voice `en-US-Harper:MAI-Voice-2.1-Flash` and the `narrator` style.
* **`SpeechAuthHandler`** sends a bearer token, `aad#resource#token`, or a subscription key. It reuses `FoundryEndpoints.SpeechAuthorization`/`IsTrustedHost` and caches tokens.
* **`SpeechClientFactory`** picks the engine from settings.
* **Narration.** `NarrationSpeechClient` replaces `SpeechClientLease` and the old ALC loader.
* **DI.** `AddMediaStudioSpeech` registers keyed `narration`/`transcription` clients.
* **Bench.** `bench tts|stt|cloud-stt` reports WER and RTF.

Deviations:

* **Sentence splitting and resampling** live in MediaStudio.Speech (`TextChunker`, `AudioMath`). This avoids a reference cycle with Media.Narration and Media.Audio.
* **Kokoro streaming** yields one update per chunk, not per frame.
* **The MAI-Transcribe response fixture** (`tests/MediaStudio.Tests/Speech/fixtures/mai-transcribe-response.json`) is hand-written from the documented schema. The P6.2b live spike was not run because there was no Azure login.
* **Foundry OpenAI audio** (`foundry-openai` TTS/STT alternates, P6.4) is not in `SpeechClientFactory`. It throws `NotSupportedException`, and it belongs with P4's `ProviderFactory`.
* **`CloudGuard` is not on this branch.** For now, `SpeechClientFactory.CloudConnection` enforces `Privacy.AllowCloud` itself (marked with a TODO). **Merge note:** route it through `CloudGuard` after P4 lands.
* **espeak-ng path.** If an espeak-ng path is configured explicitly but doesn't exist, the phonemizer returns null. It doesn't fall back to PATH, so a bad configuration is visible. `ConfigurationTester` now reports a missing espeak-ng as Failed, with install hints. The previous message wrongly claimed there was a built-in G2P.
* **Legacy Kokoro path.** `KokoroBackend`/`KokoroConfig` (`execution: "legacy-cli"`) are kept unchanged.
* **Kokoro narration identity** is now the manifest SHA-256 of the model, tokenizer and voice. It used to be provider assembly hashes. `LocalMediaDemo` renames `providerAssemblySha256` to `speechIdentitySha256`.
* **`RoutingTests`** uses `azure-speech` in its fixture, so it does not depend on local model assets.
* **`extras/web-studio/VideoStudio.Tests/PrerequisiteProof.cs`** was updated to the new API. It is not built, because it is not in the slnx.
* **The bench code** (`Wer`, `SpeechBenchCommands`) lives in `MediaStudio.Speech/Bench`, not in the Video CLI, so it can be tested. The CLI only dispatches to it.
* **`docs/speech.md`** was created now and replaces `docs/kokoro-adapter.md`. P8 will expand it.

Shared files touched (merge list):

* `MediaStudio.slnx`
* `tests/MediaStudio.Tests/MediaStudio.Tests.csproj`
* `src/Media.Narration/Media.Narration.csproj`
* `packages.lock.json` files: regenerate them after the merge.
* `src/VideoProduction.Cli/Program.cs`: 2 lines.
* `src/VideoProduction.Core/Help.cs`
* `src/MediaStudio.App/MediaApplication.cs`: 1 line.
* `src/MediaStudio.App/LocalMediaDemo.cs`
* `src/MediaStudio.Configuration/ConfigurationTester.cs`
* `src/VideoProduction.Core/Production.cs`
* `src/VideoProduction.Providers/VideoPipeline.cs`
* `src/Media.Narration/{Speech.cs,ConfiguredNarrationBackend.cs}`
* `SMART_TOOL.md`
* `docs/mcp.md`
* `docs/speech.md`: new.
* `docs/kokoro-adapter.md`: deleted.
* `tests/MediaStudio.Tests/RoutingTests.cs`
* `tests/VideoProduction.Tests/SpeechTests.cs`
* the extras `PrerequisiteProof.cs`

Verification (local):

* Release build: 0 warnings, 0 errors.
* `MediaStudio.Tests` default filter: 150 passed.
* Console runners pass: `VideoProduction.Tests` (1376), `Decisions` and `Visual` (93).
* The `Kokoro` and `Whisper` categories were run with `MEDIASTUDIO_HOME` pointing at the downloaded assets (kokoro-v1.0-q8, whisper-base) and passed:
  * Kokoro produces a sentence with a sane duration and RMS.
  * Kokoro→Whisper base round-trip recovers the key words.
* `bench tts --local-only`: Kokoro RTF ≈ 2.3 on CPU.
* `bench stt --local-only --model base`, on 4 synthetic Kokoro clips: WER 3.8%, RTF ≈ 0.5, word timestamps yes.

Not done:

* P6.2b live MAI spike: needs an Azure login.
* P6.5 live dictation.
* P6.8 illustration grounding and critique.
* `bench chat|image|local`.
* LibriSpeech CC-BY sample clips in `samples/stt`. The bench synthesizes clips with Kokoro instead.
* Foundry OpenAI audio alternates.
* Live Azure speech calls: only fake-handler tests exist.

## P9 — CI/CD

Files: `.github/workflows/{ci,models-nightly,azure-dev,live-smoke}.yml`, `.github/dependabot.yml`, `.gitleaks.toml`.

* **Action pinning.** Every action is pinned to a full commit SHA with a `# vX.Y.Z` comment, which Dependabot keeps updated. These are newer majors than the plan names: checkout v7, setup-dotnet v6, cache v6, upload-artifact v7, `azure/login` v3 and `Azure/setup-azd` v2. Every workflow sets `permissions: contents: read`; only the azd and live workflows add `id-token: write` for OIDC.
* **`ci.yml` has three jobs:**
  * `hygiene` (ubuntu): the owner-values check (`git grep -i -E` over tracked files, `extras/` excluded) and gitleaks 8.30.1. Gitleaks is the CLI binary, verified by SHA-256. It scans the tree on every run, and on PRs it also scans the PR commits. We use the CLI rather than `gitleaks-action` because the action needs a license for organization repositories.
  * `windows`: `restore --locked-mode`, `build -warnaserror`, `dotnet test MediaStudio.slnx` with the filter `Category!=Julia&Category!=Kokoro&Category!=Whisper&Category!=Live&Category!=Copilot&Category!=LocalRuntime` (TRX uploaded), the three console runners (from the repo root), and `az bicep build`.
  * `linux`: a locked restore and `-warnaserror` build of the CLI, `audio-transcriber`, Agents, Sources, AgentFramework and Contracts. Together these cover every library except the WPF app.
* **The owner-values check currently fails** on the private-folder example paths in `docs/file-production.md`. P10 rewrites that doc; until then the failure is expected.
* **`.gitleaks.toml`** extends the default rules. It allowlists SHA-256 pin lines and ignored build/tool folders: `JuliaModelAssets.TokenizerSha256` had triggered `generic-api-key`. With the config, both `gitleaks dir .` and `gitleaks git .` (93 commits) are clean.
* **`dotnet format --verify-no-changes` is not enforced.** It reports existing whitespace/final-newline issues in `tests/VideoProduction.Tests`, `tests/VideoProduction.Visual.Tests` and `src/AudioTranscriber`, which are outside P9's scope. The "warnings" gate is `TreatWarningsAsErrors` plus `-warnaserror`. A format gate can be added after a one-time `dotnet format`.
* **`models-nightly.yml`** runs on windows-latest:
  * It caches `~/AppData/Local/MediaStudio/models`, keyed on `hashFiles('assets/model-manifest.json')`.
  * It installs espeak-ng 1.52.0 from the pinned, SHA-256-checked release MSI. This is a separate executable, not bundled (N3).
  * It runs `assets ensure --id` for `julia-1`, `kokoro-v1.0-q8` and `whisper-base`, then `assets verify`. It sets `MEDIASTUDIO_JULIA_TOKENIZER` from the manifest `dir` and runs `Category=Julia|Category=Kokoro|Category=Whisper`.
  * Whisper is added beyond the plan's Julia|Kokoro, because the P6 tests use that category.
  * On manual runs, the optional input `local-runtime` installs Ollama, pulls a small model and adds `Category=LocalRuntime`.
* **`azure-dev.yml`** runs on dispatch and on pushes to `infra/**` or `azure.yaml` on main. It authenticates with `azure/login` (OIDC) for the hooks' `az` calls and with `azd auth login --federated-credential-provider github`. It then runs `azd env new` → `azd env set AZURE_PRINCIPAL_TYPE ServicePrincipal` → `azd provision --no-prompt`.
  * `AZURE_PRINCIPAL_ID` is the service principal's object id, read from the ARM token's `oid` claim. The optional `AZURE_PRINCIPAL_ID` variable overrides it.
  * The optional `AZD_INITIAL_ENVIRONMENT_CONFIG` secret from `azd pipeline config` is honored.
* **`live-smoke.yml`** runs on dispatch on windows-latest, with OIDC login. It runs `azd env refresh`, then `configure azd --env <env> --tenant <tenant>`, then `configure test --cloud-only`, then `dotnet test tests/MediaStudio.Tests --filter Category=Live`. The `Live` suite (`tests/MediaStudio.Tests/Live/FoundryLiveTests.cs`) reads the settings `configure azd` wrote and skips cleanly without them.
* **Missing variables skip cleanly.** In both Azure workflows, when any of the vars `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `AZURE_ENV_NAME` or `AZURE_LOCATION` is missing, a notice-only job runs instead and the real job is skipped.
* **`dependabot.yml`** updates nuget (grouped: MEAI+OpenAI, Agents/Copilot/AG-UI, Azure, ONNX/tokenizers/Whisper, test, and other minor/patch), npm (`/workers/browser`) and github-actions, all weekly.
* **Validated locally:**
  * actionlint 1.7.12 (with shellcheck) reports 0 findings. All YAML parses, and the TOML parses.
  * The CI commands pass on this tree: Release build with 0 warnings; xUnit 150 + 78 passed and 1 skipped; all console runners PASS.
  * Not yet run on GitHub-hosted runners. The Linux build in particular is unverified.

## P5 — Documents on published packages, plus decision routing

Commits `ed6f623` (INT.1) through `P5.8`. P5.3 and P5.4 landed together in P5.2, and P5.6b/P5.7/P5.8 follow the tests.

P5.1 spike (`artifacts/spikes/docs`, not committed): public API of the published packages, found by reflection.

* **PdfPig:** `PdfPigDocumentExtractionClient(IPageSegmenter? = null, IWordExtractor? = null, Func<…>? roleResolver = null)`.
* **VisionLanguageModel:** `VisionLanguageModelDocumentExtractionClient(IChatClient, VisionLanguageModelDocumentExtractionClientOptions? = null)`. It takes images only, so `PdfPageImagesDocumentExtractionClient` renders PDF pages to PNG first.
* **Mistral:**
  * `MistralDocumentExtractionClient(MistralDocumentExtractionClientOptions, string apiKey | TokenCredential, HttpClient? = null)`, with options `OcrEndpoint`/`DefaultModel`.
  * The model is a free string, so `mistral-ocr-4.0` (or a Foundry deployment name) passes through unvalidated. Whether the service accepts it was not tested live.
  * We send `settings.Documents.MistralDeployment` (default `ocr`).
* **AzureDocumentIntelligence:** `DocumentIntelligenceExtractionClient(Uri, TokenCredential, DocumentIntelligenceExtractionOptions?, DocumentIntelligenceClientOptions?)` or `(DocumentIntelligenceClient, options?)`. We use `prebuilt-layout`.
* **AzureContentUnderstanding:** `ContentUnderstandingExtractionClient(Uri, TokenCredential, ContentUnderstandingExtractionOptions?, ContentUnderstandingClientOptions?)` or `(ContentUnderstandingClient, options?)`. We use `prebuilt-documentSearch`.
* **DI:** `AddDocumentExtractionClient` and `AddKeyedDocumentExtractionClient(services, key, factory, lifetime)`, plus the `DocumentExtractionClientBuilder.Use`/`UseLogging` pipeline.

What was built (`src/MediaStudio.Sources/Routing/`):

* **Published packages only.** `Microsoft.Extensions.DocumentExtraction` 11.0.0-preview.1.26560.3 and `CommunityToolkit.DocumentExtraction.*` 1.0.0-preview, from CPM. No local feed.
  * `DocumentSources.cs` follows the published API.
  * The receipt schema is now 2. It records the provider name, assembly and package version instead of `ProviderAssemblySha256`, and v1 receipts migrate on read.
* **`IUploadConsent`, `FixedUploadConsent`, `UploadConsentDeniedException`, `ConsentGatedDocumentExtractionClient`.** Every remote extractor is wrapped. When privacy forbids cloud, or the user declines, the wrapper throws before any byte leaves the machine.
* **`DocumentProbe`** gathers local PdfPig features: page count, bytes, text characters per page, scanned ratio, images only, table-like lines, form fields, script/language and landscape ratio. `ToState()` turns these into the decision state.
* **`DecisionRoutingDocumentExtractionClient`:**
  * Digital PDFs take a fast path to PdfPig.
  * Otherwise `IDecisionClient` answers a `ChoiceDecisionQuestion` over the eligible candidates. When there is no decider, or the provenance is `heuristic`, `DocumentRoutingRules` decide instead.
  * Candidates are tried in rank order. The router fails over on errors, declined uploads and results that fail `ExtractionQuality.IsAcceptable`.
  * `LastPlan`/`PlanAsync` expose the plan. Results are tagged with `mediastudio.extractor`/`provider`/`attempts`.
* **`DocumentExtractorFactory`:**
  * `Available`, `CreateCandidates`, `CreateRouter` and `CreateExplicit`.
  * Cloud candidates use the first Foundry connection through `FoundryEndpoints` and `CredentialFactory`, or an api key. They are created lazily, so no credential is touched until a document is routed to one.
  * Mistral is limited to 30 pages and 30 MB.
* **DI:** `AddMediaStudioDocuments(settings)` registers keyed `documents` (Auto) and `documents:<candidate>` clients. It uses the keyed `vision` chat client, the `routing` decider and the `IUploadConsent` service; when no consent service is registered, uploads are denied.
* **App:**
  * `StudioPdfSelect` offers "Auto (recommended)" plus each configured extractor (`doc|<candidate>`). An explicit choice bypasses the router but keeps the consent wrapper.
  * `WorkspaceUploadConsent` drives a new "Upload document" dialog in `MediaHome.razor`.
  * `MediaSourceSettings.Extractor` persists the choice.
* **CLI:**
  * `documents probe <file> [--extract] [--allow-cloud] [--settings <file>]` prints the probe, the plan and the candidates as JSON.
  * `bench documents <file>... [--local-only] [--allow-cloud]` prints a TSV of time, characters, pages and quality for Auto and each candidate.
  * The code lives in `MediaStudio.Sources` (`DocumentCommands`), and the CLI only supplies the vision client and decider.
* **`configure test`:** `DocumentConfigurationCheck` runs a real PdfPig extraction of a generated PDF and flags `vlm` when no vision model is set. It is wired into the CLI, `MediaStudio.exe --configure test` and the Setup wizard.
* **INT (a):** `CloudGuard` (MediaStudio.Configuration) is shared. `SpeechClientFactory` and `AgentPipeline`/`CloudGuardAgent` use it, and the TODO is removed.
* **INT (b):** the owner-values `git grep` from `ci.yml` matches only `docs/file-production.md`, which P10 rewrites.

Deviations:

* **Where candidates are built.** They are built in Sources (`DocumentExtractorFactory`), not `ProviderFactory`, so that Providers does not have to reference Sources. The vision client and decider are injected (keyed DI or `DocumentCommandServices`).
* **Router constructor.** It is `(IEnumerable<ExtractorCandidate>, IDecisionClient?, bool allowCloud, ILogger?)`. Consent sits on each candidate wrapper rather than in the router.
* **When every result is below the quality bar,** the router returns the best one, tagged `mediastudio.low-quality`, instead of throwing. It throws `DocumentRoutingException` only when every candidate failed, was declined or was ineligible.
* **U+FFFD check.** The check is whole-document, not per page.
* **Cloud documents** use the first Foundry connection; there is no per-candidate connection setting.
* **Existing readers kept.** The explicit Entra Mistral reader and the Vision chat reader (`mistral`, `mistral-images`, vision) are kept beside the new extractors. "Local" stays the default reader so existing proofs and receipts don't change, and Auto is opt-in in the list.
* **Consent dialog.** The app uses a new consent dialog rather than the OCR confirmation flow. The OCR flow still covers the legacy readers.
* **Probe tests** use generated PDFs (digital, blank and image-only, plus a table layout), not `samples/` PDFs: there are no PDFs in `samples/`.
* **`configure test`** adds a `documents:extract (pdfpig)` row next to the existing `documents:pdfpig built in` row. `ConfigurationTester` cannot reference Sources, so the check comes in through `IConfigurationCheck`.

Shared files touched (merge list):

* `Directory.Packages.props`
* `src/MediaStudio.App/MediaApplication.cs`: two DI lines plus the workspace resolver.
* `src/MediaStudio.App/Program.cs` and `Components/Setup/SetupWizard.razor`: one extra entry in the `checks` array.
* `src/MediaStudio.App/{MediaWorkspace.cs, DocumentProviders.cs, MediaHome.razor, Components/StudioPdfSelect.razor, MediaLiveAuthoringProof.cs}`
* `src/MediaStudio.Speech/SpeechClientFactory.cs`
* `src/MediaStudio.Agents/AgentPipeline.cs`
* `src/VideoProduction.Cli/{Program.cs, VideoProduction.Cli.csproj}`: added a Sources reference.
* `src/VideoProduction.Core/Help.cs`: one line.
* Lock files for App, Sources, Tests and Cli.

Verification:

* `dotnet build MediaStudio.slnx -c Release`: 0 warnings.
* Filtered MediaStudio.Tests: 226/226.
* Console runners pass: VideoProduction.Tests (1376 assertions), Decisions.Tests and Visual.Tests (93).
* `dotnet restore --locked-mode` works.
* LocalRuntime: `LocalVisionDocumentTests` extracts "HELLO WORLD" from a generated PNG through VisionLanguageModel on Ollama `qwen3.5:4b`.
* Cloud request shapes were checked with fake handlers only, because there was no Azure login: `/documentintelligence/documentModels/prebuilt-layout:analyze`, `/contentunderstanding/analyzers/prebuilt-documentSearch:analyze`, `/providers/mistral/azure/ocr`.
## P6.8 / app wiring

* **Illustration pipeline** (`VideoProduction.Providers.Illustrations`). The keyed `IImageGenerator` `"illustration"` is built by `AddMediaStudioIllustrations` (one line at the end of `AddMediaStudioAI`). The layers are grounding → critique → base. The base is `ChatSvgImageGenerator` over the keyed `"illustration"` chat client (the router, or the Copilot runtime when `agents.illustration.runtime = copilot`). In png mode, with cloud allowed and a Foundry connection, the base is gpt-image instead.
* **Grounding is the outermost layer, not the innermost.** It embeds the brief once, and on an exemplar hit (cosine ≥ 0.97) it skips generation entirely.
* **Receipts live on the image, not the response.** `ImageGenerationResponse` has no `AdditionalProperties` in MEAI 11, so receipts go on `DataContent.AdditionalProperties` (`IllustrationProperties` keys).
* **Rasterization is optional.** The DI generator returns `image/svg+xml` unless a `SvgRasterConfiguration` is registered. The app's cover path still rasterizes through its own Node/font configuration.
* **Help for small local models.** qwen3.5:4b often emits malformed SVG. `ChatSvgOptions` therefore adds a lexical repair of unescaped `<`/`&` in text, plus one invalid-output retry (`InvalidOutputRetries = 1`). The legacy constructor path (null options) keeps zero retries and the exact old behaviour.
* **Critique scores.** `Critique.Parse` normalizes 0–10 scores to 0–1. Small models often answer on a 10-point scale.
* **Bench commands:**
  * The `bench local` model list is `--candidates`, because `--models` already means the asset directory.
  * Bench OCR pages are skipped with a message; they are deferred to the P5 documents work.
  * The 14 routing cases are copied from `tests/.../routing-fixtures-v1.json`, and a test keeps the two in sync.
* **Bench results (Ollama, this machine):**

  | Command | Results |
  | --- | --- |
  | `bench local --candidates qwen3.5:4b --prompts 3 --no-judge` | routing 86%, JSON 100%, 42.9 tok/s, first token 5.7 s |
  | `bench image --briefs 2` | 2/2 valid SVG, critique scores 1.00 / 0.20 |
  | `bench chat --tiers fast --prompts 2` | JSON 100%, 17.7 tok/s |

* **App wiring:**
  * `MediaApplication` now calls `workspace.AttachServices(services, providerSettings)` after building the container.
  * There are two new opt-in choices: **Settings routing** in the authoring selector, and **Settings illustration route (SVG)** in the illustration selector. They are labelled "GitHub Copilot" when the illustration runtime is Copilot, otherwise with the tier names. They resolve the keyed `"authoring"` / `"illustration"` clients through `SharedChatClient`, a wrapper that does not forward disposal so the DI singletons stay alive.
  * The choices are persisted as `ModelProviderState.RoutedAuthoring` / `RoutedIllustration`. Both are omitted from JSON when false.
  * Explicit per-deployment selections and the legacy `ModelProviders.Chat` path are unchanged. This keeps proofs and saved state compatible.
* **Left undone:**
  * The production illustration step runs from a serialized `MediaProductionConfiguration`, so it cannot use DI clients. When the routed illustration route is selected, `ReviewSetup` falls back to the local or saved setup. Only library covers use the route in-process.
  * `IllustrationSources.Sources` is not yet populated from opened project sources. Grounding therefore uses only the exemplar library until the P5 documents work supplies a `SourceIndex`.
  * `StoryboardWorkspace` (AG-UI) builds no model client of its own, so it needed no change.

## P8 — Setup scripts, launch, fonts and diagnostics

* **P8.1 `scripts/Install-Prerequisites.ps1`** (Windows, winget) and **`scripts/install-prerequisites.sh`** (macOS brew / Linux apt):
  * Detect first, then install only what is missing: .NET 11 SDK, WebView2 runtime (Windows), FFmpeg, Node.js 24, eSpeak NG and Ollama. Then `npm ci` and Playwright Chromium in `workers/browser`. The `.sh` script also installs `jq`, which `setup-local-models.sh` needs.
  * PowerShell flags: `-WhatIf` (dry run, `SupportsShouldProcess`) and `-Skip dotnet,webview2,ffmpeg,node,espeak-ng,ollama,browser-worker`. Shell flags: `--dry-run` and `--skip <name>` (repeatable).
  * Both print a status table and exit 1 when a required item is still missing.
  * Deviation: a newer Node major is accepted with an "untested" note rather than replaced.
  * INT.4 fix: when the Ollama server is down, `ollama --version` prints only `Warning:` lines. The scripts and `Diagnostics` now show the version ("client version is 0.x") instead of the connection warning.
* **P8.2 `scripts/Setup-LocalModels.ps1`** and **`scripts/setup-local-models.sh`**:
  * Steps: detect the tier, pull the missing Ollama tags (fast/balanced/vision/embedding, deduplicated), run `video-production assets ensure --tier <T>` (Kokoro, the tier's Whisper model and Julia, hash-verified through `ModelAssets`), then `configure local` and `configure test`.
  * The script prints the total download size and asks for confirmation.
  * PowerShell flags: `-Tier S|M|L|XL`, `-Family qwen3.5|gemma4`, `-Fast/-Balanced/-Vision <tag>`, `-AllowXl`, `-SkipOllama`, `-SkipJulia`, `-Yes`, `-DryRun`. The shell script has the same flags in kebab-case (`--tier`, `--family`, `--allow-xl`, `--skip-ollama`, `--skip-julia`, `--dry-run`, ...).
  * Deviation: `-Family` alternates live in `assets/model-manifest.json` under `ollamaFamilies`. They replace only fast/balanced/vision; embedding and Whisper still come from the tier. For this, `configure local` gained `--fast/--balanced/--vision <tag>` (`ConfigureCommands.WithModelOverrides`).
  * XL is chosen only with `-Tier XL` or `-AllowXl` (and ≥ 24 GB VRAM), matching `TierSelector`.
* **P8.3 `Start-MediaStudio.ps1`** (plus the `Start-MediaStudio.cmd` wrapper):
  * Uses `.\.dotnet` when present (or `-DotnetRoot`), otherwise `dotnet` on PATH, and requires a .NET 11 SDK.
  * Builds (`-Configuration Release` by default, `-NoBuild` skips) and runs the built app. Remaining arguments pass through, for example `.\Start-MediaStudio.ps1 --diagnostics list`.
  * The PATH-truncation hack and the owner paths are removed.
  * Deviation: it builds and runs the DLL rather than calling `dotnet run`, so pass-through arguments are not parsed by `dotnet`.
* **P8.4 Fonts:**
  * `assets/fonts/Inter-Regular.ttf` and `assets/fonts/OFL.txt` (SIL OFL 1.1) are embedded through `BundledFont`.
  * `create-demo` uses Inter unless `--font/--font-license` is given.
* **P8.5 `.gitignore` / `.gitattributes`:**
  * Ignores `/models/`, `/.mediastudio/` (`MEDIASTUDIO_HOME` relocated into the repo), `*.v1.bak.json`, `.vs/`, `.vscode/`, `*.swp` and `.DS_Store`. `artifacts/`, `.azure/` and state roots were already ignored.
  * `*.sh` files are checked out with LF line endings, and `*.ttf` is treated as binary.
* **P8.6 Diagnostics:**
  * `video-production diagnostics` and app `--diagnostics` print versions, a settings summary, local runtimes and model assets.
  * Endpoints are reduced to scheme and host, and per-user paths become `%LOCALAPPDATA%`/`%USERPROFILE%`. No keys or tokens are printed. `configure show` also redacts its settings path (INT.4).
* **P8.6b App `Program.cs`:** the remaining app flags are:
  * `--configure ...`, `--diagnostics [<name> [args]]`, `--state-root`, `--media-config`, `--voice-config`, `--source-config`, `--new-project`
  * `--authoring fixture|agent`, `--model`, `--studio-default-model`, `--concierge`

  About 30 proof modes and 33 `*Proof.cs` files were deleted, together with the legacy hybrid `Home.razor` window.
  * Removed: `--self-test`, `--verify-window`, `--verify-media-window`, `--verify-media-window-capture`, `--verify-media-flow`, `--verify-product-ui`, `--verify-live-demo`, `--verify-live-native-decisions*`, `--record-local-media-flow`, `--prepare-media-reel`, `--sandbox-host-observation`, `--authoring-acceptance`, `--production-config`, `--production-acceptance`, `--tutorial-*`, `--concierge-proof`, `--known-voice-wave`, `--startup-evidence`, `--draft`.
  * Also removed: the native image, OCR, cloud speech, realtime, provider onboarding and UI proofs.

  Kept diagnostics (`--diagnostics list`). An unknown name exits 2, and a failure prints `FAIL <name>: ...` and exits 1.

  | Name | Description |
  | --- | --- |
  | `summary` | Versions (app, .NET, WebView2, tools), settings summary, local runtimes and model assets. This is the default. |
  | `list` | Lists the available diagnostics. |
  | `window [<output-dir>]` | Opens the app window on a throwaway state and navigates all four views; saves a screenshot. The output is `four-view-native.png` and `result.json`, by default in `%LOCALAPPDATA%\MediaStudio\diagnostics\window-<timestamp>`. The temporary state is deleted afterwards. |
  | `window-capture <output-dir> [--gdi]` | Records a 5-second FFmpeg capture of the app window to check screen recording. The output is `native-window.mp4` plus capture JSON and log files. Needs `ffmpeg`. |
  | `local-whisper <16kHz-mono.wav> [<voice-config.json>]` | Transcribes a WAV with the bundled Whisper.net runtime and the whisper-base model. Uses `ggml-base.bin` in `%LOCALAPPDATA%\MediaStudio\models`. The WAV needs a canonical 44-byte header and must be at most 60 s; create one with `ffmpeg -i in.wav -ar 16000 -ac 1 -c:a pcm_s16le -bitexact -map_metadata -1 -fflags +bitexact out.wav`. |

  `LocalWhisper` gained a `LocalWhisper(NativeWhisperConfiguration)` constructor for this diagnostic.

### Integration gaps

* **INT.1 Routed production illustrations.** This resolves the first "left undone" item under P6.8.
  * New `GenerationConfiguration.RoutedIllustration` (`RoutedIllustrationConfiguration(SettingsFile, SettingsSha256, SourcesFile?, SourcesSha256?)`).
  * `ReviewSetup` pins the current provider settings into the state root through `IllustrationProviders.RouteThroughSettings(video, settings, stateRoot, passages?)`. This happens when the routed illustration choice is selected, or by default when no other illustration source is configured and the storyboard has illustration prompts.
  * The producer verifies the pinned hash and builds the keyed `"illustration"` `IImageGenerator` with `AddMediaStudioIllustrations` (`IllustrationProviders.CreateRoutedAsync`).
  * Error codes: `image.routed-settings`, `image.routed-sources`, `image.routed-copilot`, `image.routed-unconfigured`.
  * The saved-config paths (chat illustration, local image, base illustration) are still used when selected.
  * Limitation: production runs in the separate `video-production` process. The Copilot illustration runtime is therefore not available there, and routing is skipped when it is selected.
* **INT.2 Grounding from project sources.** This resolves the second "left undone" item under P6.8.
  * The app maps the opened project's extracted source chunks into `SourcePassage`s: `SourceList` projections that the consent-gated document router already produced, at most `IllustrationProviders.MaximumGroundingPassages` = 1024.
  * The passages are pinned next to the settings (`settings/illustration-sources-<hash16>.json`).
  * In the producer, when `Images.Grounding` is on and an embedding model is configured, `CreateRoutedAsync` builds a `SourceIndex` with the keyed `"embedding"` generator and sets `IllustrationSources.Sources`. `GroundedImageGenerator` then retrieves real source passages.
  * New `SourceIndex.FromChunks(sourceId, chunks, maxCharacters = 800)`.
  * `ProviderFactory.AddMediaStudioAI` no longer replaces a keyed `"embedding"` generator that is already registered, so tests and hosts can supply one.
  * In-app `IllustrationSources` is still not set on project open, because nothing in-process consumes the keyed image generator.
* **INT.3 Banned strings.** The last code hit was a test using a removed provider name, which is now `unsupported-tts`. Tracked files outside `docs/`, `README.md`, `extras/` and `SOURCE-ORIGINS.md` are clean, and there are no private-path hits. `SOURCE-ORIGINS.md` still names a removed provider and a preview build string; it is left to the docs pass.
* **INT.4 Script fixes.** This covers the Ollama version detection and the `configure show` redaction described above. PSScriptAnalyzer 1.25.0 reports no findings for `scripts/*.ps1` or `Start-MediaStudio.ps1`, and `bash -n` passes for both `.sh` scripts.
## P11

Agent guidance for contributors using Copilot or other coding agents.

* **Added:** `.github/copilot-instructions.md` (repo map, SDK, locked restore and lock-file regeneration, NoWarn list, test categories and the default CI filter, non-negotiable local-first/consent/secrets/model-manifest rules, commit and PR hygiene including the owner-values check), a root `AGENTS.md` pointer, and 11 skills under `.github/skills/`: `media-studio-setup-local`, `media-studio-deploy-azure`, `media-studio-routing`, `media-studio-copilot`, `media-studio-add-provider`, `media-studio-add-document-extractor`, `media-studio-speech`, `media-studio-models`, `media-studio-illustrations`, `media-studio-build-test` and `media-studio-troubleshoot`. `media-explanation` is unchanged (runtime hash-checked).
* **Verified against code:** every CLI command and option against the usage text in `ConfigureCommands.cs`, `ChatBenchCommands.cs`, `SpeechBenchCommands.cs` and `DocumentCommands.cs`; `Setup-LocalModels.ps1` parameters; every backticked repo path exists; frontmatter parses and `name` matches the folder; banned-strings check is clean.
* **Facts recorded in the skills that differ from the plan:**
  * `foundry-openai` speech is accepted by settings and the validator but `SpeechClientFactory` throws `NotSupportedException`; skills point to `azure-speech` for cloud speech.
  * `SafeSvg` (allow-list, `PolicyRevision`) lives in `ChatSvgImageGenerator.cs`; `SvgSanitizer` repairs output to 128 elements / depth 4.
  * `video-production configure test` skips the `copilot.*` rows; the skills use `.\Start-MediaStudio.ps1 --configure test` for them.
  * `ProviderSettingsStore` keeps a tier-M `BuiltInManifest` fallback; the models skill says to keep it in sync with the manifest.
* **Depends on the docs work:** the skills link to `docs/getting-started-local.md`, `docs/getting-started-azure.md`, `docs/architecture.md`, `docs/routing.md`, `docs/copilot.md`, `docs/models.md`, `docs/illustrations.md`, `docs/documents.md`, `docs/configuration.md`, `docs/cli.md`, `docs/speech.md` and `docs/troubleshooting.md` (code already links to its `#speech` and `#speech-auth` anchors).
## P10

Documentation rewrite. It is local first: README quickstarts, then getting-started-local and getting-started-azure, then a reference set (architecture, routing, copilot, models, illustrations, documents, speech, configuration, cli, troubleshooting), plus CONTRIBUTING, SECURITY, CODE_OF_CONDUCT, THIRD-PARTY-NOTICES and SOURCE-ORIGINS.

* **Deleted:**
  * The evidence ledgers and the stale docs: studio, providers, chat-routing, storyboard-routing, local-speech-input, process-and-judgment-gates, validation and others.
  * The machine paths in file-production.md and mcp.md, replaced by placeholders.
* **Kept on purpose:**
  * production-upgrades, transcription, file-production and contracts, because `Help.cs` and SMART_TOOL.md link to them.
  * mcp.md, with an "extras, unsupported" banner.
  * SMART_TOOL.md is untouched.
* **Deviations:**
  * The docs describe the app flags after the diagnostics refactor in main:
    * There are no proof flags and no `--draft`.
    * `--diagnostics [summary|list|window|window-capture|local-whisper]` exits 2 on an unknown name and 1 on a failure.
  * They also describe the routed illustration pipeline as the production default, grounded in up to 1024 source passages and unavailable with the Copilot runtime.
  * Both come from orchestrator notes, ahead of the merge into this worktree.
  * References to `tools\*.ps1` regression scripts became "maintainer regression, not included". The in-repo `audio-transcriber` has no `--profile` option.
  * The configuration example comes from real `configure` output.
  * The quick demo was checked through `validate`; the steps after it were derived from code.
  * eSpeak NG is documented as required for Kokoro. The `foundry-openai` speech provider is documented as accepted in settings but not implemented.
  * The SVG label `StudioHybrid.Wpf` was renamed `MediaStudio.App`, and the SVGs are labelled design targets.
  * Links were checked with a small Python checker instead of lychee.
* **Validation:**
  * 191 relative links in 27 Markdown files, 0 broken (anchors included).
  * The banned-string scan of README, the root *.md and docs (excluding this file) is clean.
* **Open:**
  * ~~Nothing writes `--media-config` for app generation~~ — closed by INT.5/INT.6 below.
  * LICENSE vs sample license (N6).
  * Decisions provenance (N5).
  * Resource names remain in git history (N7).
  * Not verified live: `azd up`, cloud speech, documents and images, and Copilot sign-in.
  * The Linux CI job has never run.
  * The docs need a fresh-machine QA pass.

## INT (default production configuration)

* **Problem:** the app could only Generate with a hand-written `--media-config`, so a user following the Quickstart (`configure local`/`azd`, then `Start-MediaStudio.ps1`) couldn't generate.
* **Builder (INT.5):** `DefaultProductionConfiguration` in VideoProduction.Providers (it already references Configuration, Speech and the model assets, so no cycle).
  * It derives `MediaProductionConfiguration` from settings v2: speech from `NarrationSpeechSettings` (Kokoro identity hashed from the installed manifest files), sample rate 24000, the bundled Inter font and OFL hashes (`BundledFont`), and a tools file with `node`/`ffmpeg`/`ffprobe` from `PATH` and the repository source root.
  * Illustrations stay routed by settings at review (INT.1 default). The Julia Decisions directory is set only when Julia isn't Off and `julia-1` is installed; otherwise Decisions are heuristic.
  * It writes `media-config.json`, `tools.json` and `production-config.json` (`FileProductionConfiguration` for `produce --production-config`) under `<stateRoot>\settings\default-production` with PrivateFiles semantics. Files are rewritten only when their JSON changes, which acts as the settings fingerprint.
  * No secrets: connections are referenced only through the saved settings, which the producer reloads; a test sets an API key in the environment and asserts it and the endpoint are absent.
  * Missing prerequisites are listed with a fix each: Kokoro assets (`assets ensure --id kokoro-v1.0-q8`), eSpeak NG, FFmpeg, FFprobe, Node and the browser worker's Playwright packages (`Install-Prerequisites.ps1`), the renderer sources (`VIDEO_PRODUCTION_HOME`), the bundled font, and speech the producer can't use (`foundry-openai`, or Azure Speech without cloud consent — reported, never downgraded).
  * Source-root discovery: `VIDEO_PRODUCTION_HOME`, then walking up from the app base directory and the current directory to `workers\browser\worker.mjs`.
* **App:** without `--media-config` (explicit flag still wins) a normal launch derives the default for its own state root. When it isn't ready the app still starts; Generate and the Generation panel show "Generation needs setup…" with the fix commands instead of "not configured". Setup review re-derives it, so fixing a prerequisite doesn't need a restart.
* **CLI:** `video-production configure production [--write] [--state-root <dir>] [--settings <file>] [--models <dir>]` (and `MediaStudio.exe --configure production`, which uses the app's state root). Exit code 1 while anything is missing. `configure test` is unchanged and still probes roles only.
* **Voice and source configs:** `--voice-config` (Whisper dictation) and `--source-config` (OCR) are optional extras; the default sources/documents path comes from settings via `AddMediaStudioDocuments`. They were left as they are.
* **Fix found live (INT.6):** with settings present, the first app launch on a fresh state root crashed. `SetupState.SeedFromSettings` wrote `settings\model-providers.json` before `FileProductionStore` claimed the root, and the store only adopts empty roots. Seeding now claims the store first.
* **Verified live on the build machine:**
  * `configure local` (tier L) → `configure production` listed Kokoro and Playwright as missing.
  * `assets ensure --id kokoro-v1.0-q8` downloaded and verified; the `Category=Kokoro` test synthesized with the installed assets.
  * `configure production --write` wrote the three files: Kokoro identity, bundled font hashes, no secrets. It reported only Playwright as missing.
  * A normal app launch (no arguments) started, derived the default into its state root, and was stopped by PID.
* **Not verified:**
  * `npm ci` in `workers\browser` failed with a TLS handshake error to registry.npmjs.org from the build machine, so no full render (CLI `produce` or in-app Generate) ran with the derived configuration.
  * The Generate button state was not inspected in the GUI.
  * `configure azd` is covered only by tests.

## Release decisions (v0.1.0-preview)

* **Publishing:** the public repository starts from one squashed commit, so no development history, branch names or earlier owner-specific values are published. Release tag: `v0.1.0-preview`, marked as a pre-release.
* **License:** `LICENSE` (MIT) is the only license; `LICENSE-SAMPLE` was removed. `src/VideoProduction.Decisions/NOTICE.txt` records that the decision code is published under the same MIT license.
* **`extras/` removed:** the MCP host, service, web studio, Aspire, Jellyfin and API-probe folders were never built or supported, so they are not published. `docs/mcp.md` and the ModelContextProtocol license notice went with them.
* **`foundry-openai` speech provider removed:** settings accepted it, but no implementation was ever built. The validator now rejects `tts.provider`/`stt.provider` `foundry-openai`. Cloud speech is `azure-speech` (Azure Speech / MAI voices, MAI-Transcribe).
* **Hygiene check:** CI's owner-value `git grep` reads its pattern from the `OWNER_VALUES_PATTERN` repository secret, so the pattern itself is never committed; the step is skipped when the secret is unset (forks). gitleaks always runs.
* **Live tests:** `tests/MediaStudio.Tests/Live/FoundryLiveTests.cs` (`Category=Live`) covers every Foundry chat tier with streaming (this exercises the pinned OpenAI 2.14 Responses path), a cloud-only routed answer with its receipt, embeddings, Azure Speech TTS→STT round trip, and Document Intelligence behind the upload-consent gate (deny and allow). Tests read the user's settings read-only and skip with a reason when there is no v2 settings file, `allowCloud` is false or no Foundry connection is configured. Deployment names default to the role names from `infra/models.json`; override with `MEDIASTUDIO_LIVE_<ROLE>_DEPLOYMENT`. Not yet run against a real resource.
* **Format gate:** a root `.editorconfig` holds the whitespace rules; existing code was reformatted once with `dotnet format whitespace`, and all `.cs`/`.razor` files were normalized to CRLF. CI's Windows job runs `dotnet format whitespace MediaStudio.slnx --verify-no-changes` (Windows only, because line endings are CRLF).
* **OpenAI 2.15:** OpenAI stays pinned at 2.14.0. A standalone offline repro and workaround are published as a [gist](https://gist.github.com/luisquintanilla/b517a550720778feadc8c30ab46ec5b8). It shows that Responses streaming, speech-to-text streaming and text-to-speech streaming all throw `MissingMethodException` with Microsoft.Extensions.AI.OpenAI 11.0.0. dotnet/extensions PR #7948 tracks the fix.
* **Not verified live for this release:** `azd up` against a real subscription, the `Live` suite, Copilot sign-in, and a full MP4 render (blocked on `npm ci` from the build machine).