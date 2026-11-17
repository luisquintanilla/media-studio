---
name: media-studio-models
description: >-
  Change, add or re-pin the AI models Media Studio uses: local Ollama tier tags and downloadable assets (Kokoro,
  Whisper, Julia) in assets/model-manifest.json, and Azure Foundry deployments in infra/models.json. Use when asked to
  "update the models", "bump a model version", "change the tier models", "add a gemma/qwen alternative", "pin a new
  Whisper/Kokoro file", "add a Foundry deployment", "which model does tier M use", or "benchmark local models".
---

# Models and assets

Model ids live in exactly two files — never in C#, scripts or docs prose without a manifest entry:

| File | What | Consumed by |
|---|---|---|
| `assets/model-manifest.json` | `assets` (pinned files: path, url, size, sha256), `ollamaTiers` S/M/L/XL, `ollamaFamilies` (alternates for `-Family`), `cloudVoices` | `ModelManifest`, `ProviderSettingsDefaults.Local`, `assets` CLI, `scripts/Setup-LocalModels.ps1` / `.sh`, speech |
| `infra/models.json` | Foundry `deployments[]`: `name`, `role`, `model`, `version`, `format`, `sku`, `capacity`, `enabledBy` | `infra/modules/foundry.bicep` (`loadJsonContent`), `infra/scripts/Find-FoundryRegion.ps1`, `configure azd` |

Docs: [models](../../../docs/models.md) (the table users read — update it in the same PR).

## Change a local tier model

1. Edit `ollamaTiers.<S|M|L|XL>` (`fast`, `balanced`, `vision`, `embedding`, `whisperAsset`) in
   `assets/model-manifest.json`. Check the tag exists: `ollama pull <tag>` (or `ollama show <tag>` once pulled).
   Vision models must accept images; `fast`/`balanced` should support tool calls.
2. Benchmark against the current pick and keep the evidence:
   ```powershell
   dotnet run --project src/VideoProduction.Cli -- bench local --candidates qwen3.5:9b,<new-tag> --out out\bench-local
   dotnet run --project src/VideoProduction.Cli -- bench chat --tiers fast,balanced
   ```
3. The tier-M fallback in `src/MediaStudio.Configuration/ProviderSettingsStore.cs` (`BuiltInManifest`, used only when
   the manifest file is missing) should match the new tier-M tags.
4. Re-apply on a machine: `dotnet run --project src/VideoProduction.Cli -- configure local --tier M` (or
   `.\scripts\Setup-LocalModels.ps1 -Tier M -Yes`). Changing the embedding model is safe: indexes built with another
   model id/dimension are ignored and rebuilt (`VectorIndex.Load`).

## Add or re-pin a downloadable asset

1. Use an immutable revision URL (Hugging Face commit hash, never `/resolve/main/`), and record exact `size` and
   lowercase `sha256` for every file:
   ```powershell
   (Get-FileHash .\file.onnx -Algorithm SHA256).Hash.ToLowerInvariant(); (Get-Item .\file.onnx).Length
   ```
2. Set `revision` to that hash (the URL must contain it), `dir`, `license`, `source`.
3. Verify: `dotnet run --project src/VideoProduction.Cli -- assets ensure --id <id>` then `assets verify --id <id>`.
4. The test `ManifestPinsEveryAssetToAnImmutableRevision` enforces the pinning rules and that every tier's
   `whisperAsset` exists.

## Change an Azure deployment

1. Edit `infra/models.json`. Keep `role` values stable (`authoring`, `chat`, `mini`, `embedding`, `transcribe`,
   `transcribe-live`, `tts`, `image`, `ocr`, `claude`): `configure azd` maps deployments **by role** via the
   `MEDIASTUDIO_DEPLOYMENTS` output. `enabledBy` is `always` or a flag (`deployOpenAIAudio`, `deployMistralOcr`,
   `deployClaude`, set via `DEPLOY_OPENAI_AUDIO` / `DEPLOY_MISTRAL_OCR` / `DEPLOY_CLAUDE`).
2. The embedding deployment's model comes from `CU_EMBEDDING_MODEL` (default `text-embedding-3-small`).
3. Check region availability: `pwsh infra/scripts/Find-FoundryRegion.ps1` (every enabled entry is required).
4. Validate: `az bicep build --file infra/main.bicep`, then `azd provision` and
   `dotnet run --project src/VideoProduction.Cli -- configure test --cloud-only`.

## Verification

```powershell
python -c "import json; json.load(open('assets/model-manifest.json')); json.load(open('infra/models.json'))"
dotnet test tests/MediaStudio.Tests -c Release --filter "FullyQualifiedName~MediaStudio.Tests.Configuration&Category!=LocalRuntime"
dotnet run --project src/VideoProduction.Cli -- assets list
dotnet run --project src/VideoProduction.Cli -- configure local --tier M
dotnet run --project src/VideoProduction.Cli -- configure test
```

## Pitfalls

* XL tier is never auto-selected without `--allow-xl` / `-AllowXl` (needs ≥ 24 GB VRAM).
* `ollamaFamilies` entries replace only `fast`/`balanced`/`vision`; embedding and Whisper come from the tier.
* Tests read the real manifest (`MissingOllamaTags` asserts tier-M tags), so update failing expectations
  deliberately, not by loosening the test.
* Respect licenses: add `license`/`source` for every asset and list new third-party models in
  `THIRD-PARTY-NOTICES.md` if redistributed.
