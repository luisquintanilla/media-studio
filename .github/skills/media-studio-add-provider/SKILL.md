---
name: media-studio-add-provider
description: >-
  Add a new AI role or provider to Media Studio end to end: settings v2 schema, validator, defaults, ProviderFactory
  keyed DI (or an ITierChatClientFactory for a new connection kind), the setup-wizard row, the `configure test` probe,
  and tests. Use when asked to "add a provider", "add a new connection kind", "support another OpenAI-compatible
  server", "add a new model role", "register a keyed IChatClient", "add a configure test check", or "add a row to the
  setup wizard".
---

# Add a role or provider

Follow the chain in order; each step has a test. Keep it local-first: a new cloud path must be refused while
`privacy.allowCloud` is false. Docs: [architecture](../../../docs/architecture.md),
[configuration](../../../docs/configuration.md).

## 0. Decide what you are adding

| You need | Do this |
|---|---|
| Another OpenAI-compatible server (vLLM, LM Studio, Foundry Local, ...) | **No code.** Add a `connections[]` entry with `kind: openai-compatible` (loopback, or `allowRemote: true` + HTTPS) and point a tier/role at it |
| A new model role (like `vision`, `embeddings`) | Steps 1–6 |
| A new connection kind backed by another runtime | Steps 1–2, then implement `ITierChatClientFactory` (step 3b) |

## 1. Settings schema

`src/MediaStudio.Configuration/ProviderSettings.cs` (schema v2, `CurrentSchemaVersion = 2`).

* Add the section/property with `init` setters and a safe local default. Use `[JsonStringEnumMemberName("kebab-case")]`
  for enum values and `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]` for optional fields.
* A new connection kind: add to `ConnectionKind` and update `ConnectionSettings.IsCloud`.
* Never add a key/secret property. Keys come from `CredentialFactory.GetApiKey(connectionId)` (env
  `MEDIASTUDIO__CONNECTIONS__<ID>__APIKEY` or user-secrets `MediaStudio:Connections:<id>:ApiKey`).
* Defaults that pick models read `assets/model-manifest.json` (`ProviderSettingsDefaults.Local`) — no literal tags.
* If old files need rewriting, extend `ProviderSettingsMigrator`.

## 2. Validator

`src/MediaStudio.Configuration/ProviderSettingsValidator.cs`: reject unknown connections, cloud use without
`privacy.allowCloud`, and kind-specific mistakes (e.g. endpoint rules in `ValidateConnection`). Errors carry a JSON path
(`"speech.tts.deployment"`) and an actionable message.

## 3. Provider wiring (keyed DI)

`src/VideoProduction.Providers/ProviderFactory.cs`:

a. **New role:** add a key to `MediaStudioAIKeys`, register it in `AddMediaStudioAI` with
   `services.AddKeyedChatClient(key, ...)` / `AddKeyedEmbeddingGenerator(...)` guarded by
   `Allowed(settings, connectionId)`, and chain `.UseLogging().UseOpenTelemetry()`. Do not overwrite a client that is
   already registered (the Copilot runtime registers some first).

b. **New connection kind:** implement `ITierChatClientFactory { ConnectionKind Kind; IChatClient Create(ChatTier,
   ConnectionSettings, ProviderSettings) }` in the runtime's own project and register it with
   `services.TryAddEnumerable(ServiceDescriptor.Singleton<ITierChatClientFactory, YourFactory>())` from an
   `AddMediaStudio<Runtime>()` extension, like `CopilotTierChatClientFactory` / `AddMediaStudioCopilot`.
   `CreateTierClient` falls through to it and keeps the cloud guard (`routing.cloud-disabled`).

## 4. Setup wizard row

`src/MediaStudio.App/Components/Setup/SetupWizard.razor`, method `Rows()`: add
`yield return new("<Label>", "<what it uses>", "<check-prefix>");`. The status cell shows the `configure test` checks
whose `Role` starts with that prefix.

## 5. `configure test` probe

* Built-in roles: add a check in `ConfigurationTester.TestAsync` (`src/MediaStudio.Configuration/ConfigurationTester.cs`)
  returning `RoleCheck(role, status, detail, remediation)`. Local roles do a real 1-token probe; cloud roles must be
  `Skipped` unless `privacy.allowCloud`.
* Roles from projects the configuration layer cannot reference: implement `IConfigurationCheck` (see
  `DocumentConfigurationCheck`, `CopilotConfigurationCheck`). Never throw for "not installed"/"not signed in"; return a
  failed row with remediation. Pass it in `src/VideoProduction.Cli/Program.cs` and `src/MediaStudio.App/Program.cs`
  (`checks: [...]`) and in `SetupWizard.razor` (`extensions: [...]`).

## 6. Tests

* Settings round-trip + validator: `tests/MediaStudio.Tests/Configuration/ConfigurationTests.cs`.
* Keyed DI and cloud refusal: `tests/MediaStudio.Tests/Routing/ProviderFactoryTests.cs`.
* Anything needing a live runtime: `[Trait("Category", "LocalRuntime")]` (or `Live` for Azure) and skip cleanly.

## Verification

```powershell
dotnet build MediaStudio.slnx -c Release --no-restore -warnaserror
dotnet test tests/MediaStudio.Tests -c Release --filter "FullyQualifiedName~Configuration|FullyQualifiedName~ProviderFactory"
dotnet run --project src/VideoProduction.Cli -- configure test
dotnet run --project src/VideoProduction.Cli -- diagnostics      # must print no secrets
```

## Pitfalls

* Adding a package: version only in `Directory.Packages.props`, then `dotnet restore MediaStudio.slnx --force-evaluate`
  and commit the `packages.lock.json` changes.
* Remote OpenAI-compatible endpoints over plain HTTP are rejected on purpose.
* Model ids belong in `assets/model-manifest.json` / `infra/models.json` and `docs/models.md`, not in code.
* `diagnostics` and `configure show` must never print keys; add redaction if you add sensitive fields.
