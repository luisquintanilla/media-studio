---
name: media-studio-routing
description: >-
  Change how Media Studio routes chat requests between local and cloud tiers: add or reorder chat tiers, tune the
  confidence floor and authoring floor, plug in a new IDecisionClient (decider), and evaluate routing accuracy.
  Use when asked to "add a chat tier", "change routing", "adjust the confidence floor", "route more to local",
  "add a decider", "replace Julia", "evaluate routing", "run bench local", or when a request goes to the wrong tier.
---

# Chat routing

Every chat request goes through `DecisionRoutingChatClient` (`src/VideoProduction.Providers/ChatRouting.cs`, a
`FailoverChatClient`). A decider (`IDecisionClient`) picks a tier; if its confidence is below the floor the request
escalates to the next stronger tier, and transient failures fail over. Docs: [routing](../../../docs/routing.md).

## Where things live

| Concern | File / type |
|---|---|
| Settings (`chat.tiers[]`, `chat.routing`) | `src/MediaStudio.Configuration/ProviderSettings.cs` (`ChatTier`, `ChatTierNames`) |
| Validation | `src/MediaStudio.Configuration/ProviderSettingsValidator.cs` |
| Tier clients, keyed DI | `src/VideoProduction.Providers/ProviderFactory.cs` (`CreateTiers`, `CreateRouter`, `CreateTierClient`, `Strength`, `MediaStudioAIKeys`) |
| Router + options + receipt | `src/VideoProduction.Providers/ChatRouting.cs` (`DecisionRoutingOptions`, `ChatRouteReceipt`, ActivitySource `MediaStudio.Routing`) |
| Decider chain | `src/VideoProduction.Providers/Decisions/DecisionClientFactory.cs` → Julia (`julia-1`) → [OpenAI, only with `-p:OpenAIDecisions=true`] → `HeuristicDecisionClient`, wrapped in `FallbackDecisionClient` |
| Decider contract | `src/VideoProduction.Decisions/Contract/DecisionClient.cs` (`IDecisionClient`) |
| Fixtures + tests | `tests/MediaStudio.Tests/Routing/` (`routing-fixtures-v1.json`, `RoutingAccuracy.cs`, `DecisionRoutingTests.cs`, `DecisionChainTests.cs`, `ProviderFactoryTests.cs`) |

## Adjust tiers

Settings (`%LOCALAPPDATA%\MediaStudio\settings\model-providers.json`):

```json
"chat": {
  "tiers": [
    { "name": "fast",     "connection": "local", "model": "<tag>" },
    { "name": "balanced", "connection": "local", "model": "<tag>" },
    { "name": "cloud",    "connection": "azure", "model": "chat",      "api": "responses" },
    { "name": "strong",   "connection": "azure", "model": "authoring", "api": "responses" }
  ],
  "routing": { "decider": "auto", "confidenceFloor": 0.6, "authoringFloor": "balanced", "failover": true }
}
```

* Tier names are `fast`, `balanced`, `cloud`, `strong`, `copilot` (`ChatTierNames`). `ProviderFactory.Strength` orders
  them: fast 0, balanced 1, unknown names 2, cloud 3, strong/copilot 4. Escalation always moves to a stronger tier.
* `thinking: true` lets a local reasoning model think (off by default; Ollama ignores token limits while thinking).
* `api` is `chat-completions` (default for local), `responses` or `anthropic` (Claude on Foundry).
* Cloud tiers are skipped while `privacy.allowCloud` is false (`routing.cloud-disabled`); never route around that.
* `confidenceFloor`: if the decider's top probability is below it (or within 0.2 of the runner-up,
  `MinimumMargin`) the request escalates one tier. Raise it to escalate more often, lower it to keep more local.
* `authoringFloor`: the weakest tier allowed for storyboard authoring purposes.
* `decider`: `auto` (Julia when installed, else heuristic) or `heuristic` (skip Julia).

## Add a new decider

1. Implement `IDecisionClient` (`GetResponseAsync(DecisionRequest, DecisionOptions?, CancellationToken)`, `GetService`,
   `Dispose`) in `src/VideoProduction.Providers/Decisions/`. It must return a complete response or throw; never guess.
2. Insert it in `DecisionClientFactory.Create` **before** `HeuristicDecisionClient` (the heuristic must stay last so a
   decision is always produced). Wrap expensive loads in `LazyDecisionClient`.
3. If it calls the cloud, gate it on `settings.Privacy.AllowCloud` and a Foundry connection, like the OpenAI link.
4. Add a `DecisionChainTests` case (ordering + fallback on timeout; each link gets ~2 s) and run the accuracy fixtures.

## Evaluate routing

```powershell
dotnet test tests/MediaStudio.Tests -c Release --filter "FullyQualifiedName~MediaStudio.Tests.Routing&Category!=Julia&Category!=LocalRuntime"
dotnet test tests/MediaStudio.Tests -c Release --filter "Category=Julia"          # needs assets ensure --id julia-1
dotnet run --project src/VideoProduction.Cli -- bench local --prompts 10           # needs Ollama
dotnet run --project src/VideoProduction.Cli -- bench local --candidates <tag>,<tag> --no-judge
dotnet run --project src/VideoProduction.Cli -- bench chat --tiers fast,balanced
```

`bench local` uses a copy of `routing-fixtures-v1.json`; a test keeps the two in sync, so update both when you add
cases. Every routed response carries a `ChatRouteReceipt` (`media.chat.route`) showing the chosen tier and why.

## Verification

* `configure test` shows a ✅ `chat:<tier>` row per tier.
* Routing tests pass; the Julia accuracy test (`JuliaAccuracyAndLatencyOnTheFixtureSet`) still meets its floor.
* With `privacy.allowCloud: false` no receipt names a cloud tier.

## Pitfalls

* `-p:OpenAIDecisions=true` does not build today (MEAI.OpenAI 11.0.0 vs OpenAI 2.15); keep cloud decisions off.
* `AIRoutingChat` and `VPD001` are experimental APIs; after a package bump re-run the Routing tests.
* Unknown tier names get strength 2 (between balanced and cloud); prefer the standard names.
* Do not hard-code model tags in tiers you ship as defaults; defaults come from `assets/model-manifest.json`.
