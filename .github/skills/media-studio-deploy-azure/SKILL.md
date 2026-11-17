---
name: media-studio-deploy-azure
description: >-
  Provision the optional Azure AI Foundry resource for Media Studio with azd, choose a region, toggle Claude,
  Content Understanding, Mistral OCR and OpenAI audio, import the outputs with `configure azd`, tear it down, and set
  up the GitHub OIDC pipeline. Use when asked to "deploy to Azure", "azd up", "find a Foundry region", "enable Claude",
  "enable Content Understanding", "import azd outputs", "configure azd", "azd down", "azd pipeline config", or
  "run the live smoke test".
---

# Deploy the optional Azure resources

Azure is opt-in. `azd up` creates **one** AIServices (Foundry) account + project in one region with the pay-per-use
deployments listed in `infra/models.json`. User guide: [getting-started-azure](../../../docs/getting-started-azure.md).

Files: `azure.yaml`, `infra/main.bicep`, `infra/main.parameters.json`, `infra/models.json`,
`infra/modules/foundry.bicep`, `infra/modules/role-assignments.bicep`, hooks `infra/hooks/preprovision.ps1` / `.sh` and
`infra/hooks/postprovision.ps1` / `.sh`.

## 1. Prerequisites

`azd` and `az` (Bicep) installed, then `azd auth login` (and `az login` for the hooks). The signed-in principal gets
the *Cognitive Services User* and *Foundry User* roles (`ASSIGN_RBAC=true`); local auth (keys) is disabled by default.

## 2. Pick a region

```powershell
.\infra\scripts\Find-FoundryRegion.ps1                    # scans candidates, recommends one
.\infra\scripts\Find-FoundryRegion.ps1 -Location swedencentral
.\infra\scripts\Find-FoundryRegion.ps1 -Candidates swedencentral,eastus2 -Json
```

The candidate data lives in `infra/scripts/regions.json`. The preprovision hook runs the same check for
`AZURE_LOCATION`; if it is unset the hook prints the scan and stops (`azd env set AZURE_LOCATION <region>`). Skip the
check with `MEDIASTUDIO_SKIP_REGION_CHECK=true`.

## 3. Choose features (before `azd up`)

```powershell
azd env new <env-name>
azd env set AZURE_LOCATION <region>
azd env set DEPLOY_CLAUDE true                 # opt-in; Marketplace terms + attestation
azd env set CLAUDE_ORGANIZATION_NAME "<org>"   # with CLAUDE_COUNTRY_CODE (US) / CLAUDE_INDUSTRY (technology)
azd env set DEPLOY_OPENAI_AUDIO true           # optional gpt-4o-mini-transcribe / gpt-4o-mini-tts alternates
azd env set DEPLOY_MISTRAL_OCR false           # default true
azd env set ENABLE_CONTENT_UNDERSTANDING false # default true; CU_EMBEDDING_MODEL defaults to text-embedding-3-small
```

Other flags (see `infra/main.parameters.json`): `DISABLE_LOCAL_AUTH` (default true), `ASSIGN_RBAC` (default true).
To add or change a deployment edit only `infra/models.json` (`name`, `role`, `model`, `version`, `format`, `sku`,
`capacity`, `enabledBy`) and `docs/models.md` — see skill `media-studio-models`.

## 4. Provision

```powershell
azd up
```

The postprovision hook configures Content Understanding defaults, waits for RBAC, probes Decisions and MAI speech
(warnings only) and runs `dotnet run --project src/VideoProduction.Cli -- configure azd`, which imports
`azd env get-values` into the settings file and turns on `privacy.allowCloud`.

Run the import yourself when needed:

```powershell
dotnet run --project src/VideoProduction.Cli -- configure azd --env <env-name>
dotnet run --project src/VideoProduction.Cli -- configure azd --cloud-speech     # also switch narration/transcription to MAI
dotnet run --project src/VideoProduction.Cli -- configure azd --from-file <path> --tenant <tenant-id>
```

Outputs consumed: `MEDIASTUDIO_DEPLOYMENTS`, `MEDIASTUDIO_CU_ENABLED`, `MEDIASTUDIO_CU_EMBEDDING_MODEL`,
`MEDIASTUDIO_CLAUDE_ENABLED`, `MEDIASTUDIO_OPENAI_AUDIO_ENABLED`, `MEDIASTUDIO_MISTRAL_OCR_ENABLED`.

## 5. CI with GitHub OIDC (no secrets)

```powershell
azd pipeline config --provider github
```

This creates a federated service principal and the repository variables `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`,
`AZURE_SUBSCRIPTION_ID`, `AZURE_ENV_NAME`, `AZURE_LOCATION` used by `.github/workflows/azure-dev.yml` (provision on
`infra/**` changes) and `.github/workflows/live-smoke.yml` (manual: `configure azd`, `configure test --cloud-only`,
`Category=Live` tests). Both skip cleanly in forks without those variables.

## 6. Teardown

```powershell
azd down --purge      # purge frees the soft-deleted account name and its quota
```

Then rerun `configure local` (or delete the `azure` connection) so settings no longer point at the deleted endpoint.

## Verification

```powershell
az bicep build --file infra/main.bicep
dotnet run --project src/VideoProduction.Cli -- configure test --cloud-only
dotnet run --project src/VideoProduction.Cli -- configure show
```

All cloud rows ✅. The settings file must contain the endpoint and deployment names but **no keys**.

## Pitfalls

* `configure test` cloud rows ❌ with 401/403 right after provisioning: RBAC propagation takes minutes; wait and rerun.
* Quota/`InsufficientQuota`/model-not-available: rerun the region finder and pick another region; never edit model ids
  outside `infra/models.json`.
* Claude fails without the organization name / attestation; leave `DEPLOY_CLAUDE` false unless needed.
* Keys: with `DISABLE_LOCAL_AUTH=false` and `auth: api-key`, provide the key via
  `MEDIASTUDIO__CONNECTIONS__AZURE__APIKEY` or user-secrets, never in the settings file.
* Never commit `.azure/` or paste real endpoints, subscription or tenant ids into the repo; use `<account>` placeholders.
