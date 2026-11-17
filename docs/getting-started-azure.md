# Getting started: Azure (Microsoft Foundry)

This guide provisions **one Microsoft Foundry resource** with every cloud model Media Studio can use, then
points the app at it. Local models keep working: cloud is used when you allow it and routing decides a task
needs it. To run without Azure, see [getting started: local](getting-started-local.md).

> [!NOTE]
> The Bicep template builds and the hooks are tested locally, but a full `azd up` has not yet been verified
> end to end. Please report problems; see [known limitations](troubleshooting.md#known-limitations).

> [!TIP]
> Using a coding agent? Ask: *"Deploy Media Studio's Azure resources with azd. Find a region that has the models,
> tell me the cost, and wait for my go-ahead before azd up."* It follows the `media-studio-deploy-azure` skill and
> asks before anything billable or destructive. See [coding agents](coding-agents.md).

## Requirements

- An Azure subscription where you can create resources and **role assignments** (Owner, or Contributor plus
  User Access Administrator), because the template grants your account data-plane roles.
- [Azure Developer CLI](https://aka.ms/azd) (`azd`) and [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) (`az`).
- [PowerShell 7](https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-windows) (`pwsh`):
  the Windows hooks run in `pwsh`. On Linux/macOS the `.sh` hooks need `pwsh` only for the region check.
- The .NET 11 SDK and the app prerequisites: run `.\scripts\Install-Prerequisites.ps1` (see
  [local step 1](getting-started-local.md#1-install-prerequisites)).

## 1. Sign in

```powershell
azd auth login
az login
```

`azd` provisions; the hooks use `az` for role checks and probes. Media Studio itself signs in with your Azure
identity through Microsoft Entra ID. No keys are written to settings.

## 2. Choose an environment and region

```powershell
azd env new media-studio-dev
azd env set AZURE_LOCATION swedencentral
```

No region offers every model. `swedencentral` is recommended (only MAI-Transcribe is missing; the app falls
back to Whisper) and `westus3` is equivalent. To compare regions for your subscription:

```powershell
.\infra\scripts\Find-FoundryRegion.ps1 -Location swedencentral
.\infra\scripts\Find-FoundryRegion.ps1 -Json
```

The pre-provision hook runs the same check and stops only when a *required* deployment is unavailable.
Skip it with `azd env set MEDIASTUDIO_SKIP_REGION_CHECK true`.

## 3. Provision

```powershell
azd up
```

This repository is infrastructure-only, so `azd up` provisions and deploys no code. It creates a resource
group, one Foundry (AIServices) account with a project, and these pay-per-use **GlobalStandard** deployments:

| Deployment | Model | Used for | Default |
| --- | --- | --- | --- |
| `authoring` | `gpt-6.1-sol` | strong tier: storyboards, authoring | on |
| `chat` | `gpt-6-luna` | cloud tier, OpenAI Decisions | on |
| `mini` | `gpt-5.4-mini` | low-cost cloud tasks | on |
| `embedding` | `text-embedding-3-small` | embeddings, Content Understanding | on |
| `transcribe-live` | `gpt-live-transcribe` | live dictation | on |
| `image` | `gpt-image-2.5-flare` | PNG illustrations | on |
| `ocr` | `mistral-ocr-4-0` | scanned PDFs | on (`DEPLOY_MISTRAL_OCR`) |
| `transcribe`, `tts` | `gpt-4o-mini-transcribe`, `gpt-4o-mini-tts` | alternative audio models | off (`DEPLOY_OPENAI_AUDIO`) |
| `claude` | `claude-sonnet-5` | alternative strong tier | off (`DEPLOY_CLAUDE`) |

Azure Speech MAI voices and MAI-Transcribe-2 use the same resource and need no deployment. Document
Intelligence and Content Understanding are also served by the same resource.

Your account gets **Cognitive Services User** and **Foundry User** on the resource. Local (key) auth is disabled.

### Options

Set these with `azd env set <NAME> <value>` before `azd up`:

| Variable | Default | Effect |
| --- | --- | --- |
| `DEPLOY_MISTRAL_OCR` | `true` | Deploy Mistral OCR |
| `DEPLOY_OPENAI_AUDIO` | `false` | Deploy `gpt-4o-mini-transcribe` and `gpt-4o-mini-tts` |
| `DEPLOY_CLAUDE` | `false` | Deploy Claude; also set `CLAUDE_ORGANIZATION_NAME` (Marketplace attestation), optionally `CLAUDE_COUNTRY_CODE`, `CLAUDE_INDUSTRY` |
| `ENABLE_CONTENT_UNDERSTANDING` | `true` | Configure Content Understanding defaults after provisioning |
| `CU_EMBEDDING_MODEL` | `text-embedding-3-small` | Use `text-embedding-3-large` if Content Understanding asks for it |
| `DISABLE_LOCAL_AUTH` | `true` | Disable API keys on the resource |
| `ASSIGN_RBAC` | `true` | Assign the data-plane roles to `AZURE_PRINCIPAL_ID` |
| `AZURE_PRINCIPAL_ID`, `AZURE_PRINCIPAL_TYPE` | your account, `User` | Who gets the roles (`ServicePrincipal` in CI) |

## 4. Settings are written for you

After provisioning, the post-provision hook:

1. Waits (up to 5 minutes) for role assignments to take effect.
2. Configures Content Understanding defaults.
3. Probes OpenAI Decisions and Azure Speech (warnings only; the app falls back to local engines).
4. Runs `dotnet run --project src/VideoProduction.Cli -- configure azd`, which imports `azd env get-values`
   into `%LOCALAPPDATA%\MediaStudio\settings\model-providers.json` and allows cloud.
5. Prints a summary. Idle cost is $0: deployments are billed per use.

If a step warns, rerun it yourself:

```powershell
dotnet run --project src/VideoProduction.Cli -- configure azd
dotnet run --project src/VideoProduction.Cli -- configure test
```

`configure azd` keeps your local tiers and adds the Foundry connection. Add `--cloud-speech` to narrate and
transcribe with Azure Speech instead of Kokoro and Whisper, `--env <name>` to pick another azd environment,
or `--from-file <path>` to import saved `azd env get-values` output.

## 5. Start the app

```powershell
.\Start-MediaStudio.ps1
```

Chat tiers run cheapest first; [routing](routing.md) escalates to `chat` or `authoring` only when a local model
isn't confident enough or a task needs a stronger tier. Each routing decision is logged and written to the
project's `chat-route.json`.
In the app, **Settings → Setup → Test** runs the same checks as `configure test`, including GitHub Copilot.

For a deeper check that makes real calls (each Foundry chat tier, embeddings, Azure Speech narration and
transcription, Document Intelligence with and without upload consent), run the `Live` tests:

```powershell
dotnet test tests/MediaStudio.Tests -c Release --filter "Category=Live"
```

They use the settings `configure azd` wrote and skip when no Foundry connection is configured. Override a deployment
name with `MEDIASTUDIO_LIVE_<ROLE>_DEPLOYMENT` (for example `MEDIASTUDIO_LIVE_EMBEDDING_DEPLOYMENT`).

## Costs and cleanup

All deployments are pay-per-use. The biggest costs come from authoring with `gpt-6.1-sol`, image generation
and OCR of long documents. To remove everything, including the soft-deleted account and its quota:

```powershell
azd down --purge
```

## Set up CI (optional)

The workflows `.github/workflows/azure-dev.yml` (provision on infrastructure changes) and `live-smoke.yml`
(manual cloud checks) sign in with OpenID Connect. Configure them with:

```powershell
azd pipeline config --provider github
```

They need these **repository variables**: `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`,
`AZURE_ENV_NAME` and `AZURE_LOCATION`. Optional: the `AZURE_PRINCIPAL_ID` variable (defaults to the
workflow identity's object ID) and the `AZD_INITIAL_ENVIRONMENT_CONFIG` secret written by `azd pipeline config`.
When any required variable is missing, the workflows skip with a notice. See [contributing](../CONTRIBUTING.md#continuous-integration).

## Using an existing resource

You don't have to use `azd`. Point the app at any Foundry resource you can sign in to:

- In the setup wizard, choose **Azure** or **Both**, then **Or paste an endpoint**
  (`https://<your-resource>.services.ai.azure.com/`), or
- edit the settings file directly; see [configuration](configuration.md).

Deployment names must match the names in the table above, or be set in the settings file. For API-key
access, see [configuration: secrets](configuration.md#secrets).
