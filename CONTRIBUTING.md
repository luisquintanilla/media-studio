# Contributing

Thanks for helping improve Media Studio. Issues and pull requests are welcome.

- Read the [architecture](docs/architecture.md) overview first.
- For anything larger than a small fix, open an issue to discuss it before you start.
- By participating you agree to follow the [code of conduct](CODE_OF_CONDUCT.md).
- Report security problems privately ([security policy](SECURITY.md)), not in public issues.

## Prerequisites

- Windows 10/11 x64 for the app; the libraries and CLIs also build on Linux.
- The .NET SDK pinned in [`global.json`](global.json) (.NET 11).
- Node.js 24 for the browser worker tests.
- For running videos locally: the tools from [getting started (local)](docs/getting-started-local.md#1-install-prerequisites).

## Build and test

```powershell
dotnet restore MediaStudio.slnx --locked-mode
dotnet build MediaStudio.slnx -c Release -warnaserror
dotnet format whitespace MediaStudio.slnx --verify-no-changes   # fix with: dotnet format whitespace MediaStudio.slnx
dotnet test MediaStudio.slnx -c Release --no-build --filter "Category!=Julia&Category!=Kokoro&Category!=Whisper&Category!=Live&Category!=Copilot&Category!=LocalRuntime"
```

That filter is the offline set CI runs. Some test projects are console runners:

```powershell
dotnet run --project tests/VideoProduction.Tests -c Release --no-build
dotnet run --project tests/VideoProduction.Decisions.Tests -c Release --no-build
dotnet run --project tests/VideoProduction.Visual.Tests -c Release --no-build
```

Browser worker tests:

```powershell
cd workers/browser
npm ci
npm test
```

Test categories that need more than the source tree:

| Category | Needs |
| --- | --- |
| `Julia`, `Kokoro`, `Whisper` | The model assets (`video-production assets ensure --id <asset>`) and, for Kokoro, eSpeak NG |
| `LocalRuntime` | Ollama running with the models from `scripts/Setup-LocalModels.ps1` |
| `Copilot` | A GitHub Copilot subscription; set `COPILOT_GITHUB_TOKEN` (otherwise the test returns early) |
| `Live` | An Azure deployment from `azd up`, imported with `configure azd`; covers Foundry chat tiers, embeddings, Azure Speech and Document Intelligence |
| `OpenAICompat` | Nothing; offline guard for the OpenAI SDK pin ([details](docs/troubleshooting.md#openai-sdk-version)) |

Packages use central package management (`Directory.Packages.props`) with lock files. After changing a package
version, run `dotnet restore MediaStudio.slnx --force-evaluate` and commit the updated `packages.lock.json` files.

## Pull requests

- Keep changes focused and include tests for new behavior.
- Builds must pass with `-warnaserror`.
- Update the docs in [`docs/`](docs/) when you change commands, settings or behavior.
- **Never commit secrets, keys, tokens, endpoints of your own resources, or personal paths.** Use placeholders such
  as `https://<your-resource>.services.ai.azure.com/`. Settings and secrets live under `%LOCALAPPDATA%\MediaStudio`
  and user secrets, outside the repository ([configuration](docs/configuration.md#secrets)).
- Don't commit model files, recordings or generated output; `artifacts/` is ignored for scratch work.

## Continuous integration

| Workflow | Runs | What it does |
| --- | --- | --- |
| `ci.yml` | Every pull request and push to `main` | Hygiene (owner-specific values check and gitleaks secret scan), Windows build + offline tests + Bicep build, Linux build of the cross-platform projects |
| `models-nightly.yml` | Nightly and on demand | Downloads the pinned model assets, installs eSpeak NG, runs the `Julia`, `Kokoro` and `Whisper` tests |
| `azure-dev.yml` | On demand, and on pushes to `main` that change `infra/` | `azd provision` with GitHub OIDC (no stored secrets) |
| `live-smoke.yml` | On demand | Imports the azd outputs (`configure azd`), runs `configure test --cloud-only` and the `Live` tests |

The Azure workflows need these **repository variables**; without them they skip with a notice:
`AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `AZURE_ENV_NAME`, `AZURE_LOCATION`.
Optional: the `AZURE_PRINCIPAL_ID` variable and the `AZD_INITIAL_ENVIRONMENT_CONFIG` secret.
Create them, with the service principal and its federated credentials, by running:

```powershell
azd pipeline config --provider github
```

Dependabot opens weekly update pull requests for NuGet, npm and GitHub Actions.
