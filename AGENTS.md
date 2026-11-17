# AGENTS.md

Full guidance for coding agents is in [`.github/copilot-instructions.md`](.github/copilot-instructions.md).
Task skills live in [`.github/skills/`](.github/skills/): `media-studio-setup-local`, `media-studio-deploy-azure`,
`media-studio-routing`, `media-studio-copilot`, `media-studio-add-provider`, `media-studio-add-document-extractor`,
`media-studio-speech`, `media-studio-models`, `media-studio-illustrations`, `media-studio-build-test`,
`media-studio-troubleshoot`. (`media-explanation` is a runtime skill used by the app; do not edit it.)

## Essentials

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
* Package versions only in `Directory.Packages.props`; regenerate lock files with
  `dotnet restore MediaStudio.slnx --force-evaluate` after a package change.
* Local first: cloud calls go through `CloudGuard`, `privacy.allowCloud` and, for documents,
  `ConsentGatedDocumentExtractionClient`. Never bypass them.
* Model ids live only in `assets/model-manifest.json` and `infra/models.json`.
* No secrets in settings or the repo: keys come from `MEDIASTUDIO__CONNECTIONS__<ID>__APIKEY` or user-secrets.
* No owner-specific resource names, ids, emails or paths; CI's hygiene job and gitleaks enforce this.
