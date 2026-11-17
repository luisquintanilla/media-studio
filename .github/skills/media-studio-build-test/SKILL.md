---
name: media-studio-build-test
description: >-
  Build and test the Media Studio solution exactly like CI: .NET 11 SDK per global.json, locked NuGet restore with
  Central Package Management, warnings-as-errors, xUnit test categories, the console-runner suites, Bicep build and
  the hygiene checks. Use when asked to "build", "run the tests", "CI is failing", "NU1004 lock file", "add a package",
  "update a NuGet version", "run the model tests", "run only Kokoro/Whisper/Julia tests", or "why is a warning an
  error".
---

# Build and test

Docs: [cli](../../../docs/cli.md), [troubleshooting](../../../docs/troubleshooting.md). CI definition:
`.github/workflows/ci.yml` (PRs), `.github/workflows/models-nightly.yml`, `.github/workflows/live-smoke.yml`.

## SDK

`global.json` pins SDK `11.0.100` with `rollForward: latestFeature`. Use a system install, or a repo-local one at
`.\.dotnet\dotnet.exe` (ignored by git) if `dotnet --version` is older. `scripts/Install-Prerequisites.ps1` installs
prerequisites on Windows (`scripts/install-prerequisites.sh` on Linux/macOS).

## CI sequence (run from the repo root)

```powershell
dotnet restore MediaStudio.slnx --locked-mode
dotnet build MediaStudio.slnx -c Release --no-restore -warnaserror
dotnet format whitespace MediaStudio.slnx --verify-no-changes   # .editorconfig rules; fix by dropping --verify-no-changes
dotnet test MediaStudio.slnx -c Release --no-build --filter "Category!=Julia&Category!=Kokoro&Category!=Whisper&Category!=Live&Category!=Copilot&Category!=LocalRuntime"
dotnet run --project tests/VideoProduction.Tests -c Release --no-build
dotnet run --project tests/VideoProduction.Decisions.Tests -c Release --no-build
dotnet run --project tests/VideoProduction.Visual.Tests -c Release --no-build   # reads fixtures/ from the repo root
az bicep build --file infra/main.bicep --stdout | Out-Null
```

`MediaStudio.App` is Windows-only; the Linux CI job builds `src/VideoProduction.Cli` and the cross-platform libraries.

## Test categories (`[Trait("Category", ...)]`)

| Category | Needs | Run with |
|---|---|---|
| *(none)* | nothing — offline, deterministic | default filter above |
| `Julia` | `julia-1` asset (`assets ensure --id julia-1`), `MEDIASTUDIO_JULIA_TOKENIZER` | `--filter "Category=Julia"` |
| `Kokoro` | `kokoro-v1.0-q8` + espeak-ng (or `MEDIASTUDIO_KOKORO_DIR`) | `--filter "Category=Kokoro"` |
| `Whisper` | Whisper asset (or `MEDIASTUDIO_WHISPER_MODEL`) | `--filter "Category=Whisper"` |
| `LocalRuntime` | running Ollama with the tier models (`MEDIASTUDIO_VISION_MODEL` overrides the vision tag) | `--filter "Category=LocalRuntime"` |
| `Copilot` | Copilot CLI signed in | `--filter "Category=Copilot"` |
| `Live` | `azd up` + `configure azd` (settings with `allowCloud` and a Foundry connection) and `az login` | `--filter "Category=Live"` |

Model categories run against `tests/MediaStudio.Tests` (e.g.
`dotnet test tests/MediaStudio.Tests -c Release --filter "Category=Julia|Category=Kokoro|Category=Whisper"`). Tests that
need a missing resource must **skip**, not fail. New tests that touch a model, network or cloud need one of these
traits so the default filter stays offline.

## Packages (Central Package Management)

* Versions only in `Directory.Packages.props`; `<PackageReference Include="X" />` without `Version` in projects.
* Every project has a committed `packages.lock.json`. After adding/changing a package:
  ```powershell
  dotnet restore MediaStudio.slnx --force-evaluate
  git add Directory.Packages.props '*packages.lock.json'
  ```
  `--locked-mode` failing with NU1004 means the lock files are stale — regenerate, do not disable locked mode.
* Sources come from `NuGet.Config`. `OpenAI` is pinned to 2.14.0 (see `docs/troubleshooting.md`).

## Warnings

`Directory.Build.props` sets `TreatWarningsAsErrors` and `NoWarn` for experimental-API diagnostics only:
`MEDE0001;EXTEXP0019;CTDE0001;MEAI001;OPENAI001;OPENAI002;MAAI001;AIRoutingChat;VPD001;VPROD001;GHCP001`.
Fix other warnings; if a new experimental API needs suppressing, prefer a local `#pragma warning disable` around the
call over growing the global list.

## Hygiene (CI `hygiene` job)

```powershell
gitleaks dir . --config .gitleaks.toml --redact --no-banner
```

CI also greps tracked files for owner-specific values with a regex kept in the `OWNER_VALUES_PATTERN` repository
secret (the check is skipped on forks, where the secret is not set). Maintainers can run it locally with
`git grep -n -i -I -E "$env:OWNER_VALUES_PATTERN"`. Both must find nothing. Use placeholders like `<account>`,
`<subscription-id>`, `<resource-name>`.

## Verification

A change is ready when the CI sequence passes locally, the relevant model categories pass (or skip with a clear
reason) and the hygiene checks are clean.

## Pitfalls

* `-p:OpenAIDecisions=true` only builds after pinning an OpenAI version that has `OpenAI.Decisions`; leave it off.
* `CopilotSkipCliDownload` is true for every project except `MediaStudio.App`, so library builds stay offline.
* Visual tests compare against `fixtures/`; run them from the repo root or they cannot find baselines.
* Don't run `dotnet test` with `--no-build` after editing code without rebuilding Release.
