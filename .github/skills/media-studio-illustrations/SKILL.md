---
name: media-studio-illustrations
description: >-
  Work on Media Studio's illustration pipeline: local SVG generation by a chat model, the SafeSvg sanitizer, grounding
  in project sources and the SVG exemplar library (embeddings), vision critique-and-retry, optional cloud PNG
  (gpt-image) and the Copilot illustration runtime. Use when asked to "improve illustrations", "SVG is rejected",
  "illustrations ignore my sources", "turn off critique", "use gpt-image / PNG mode", "bench image", "change the
  sanitizer", or "add an image generator".
---

# Illustrations

Code: `src/VideoProduction.Providers/Illustrations/` plus `src/VideoProduction.Providers/ChatSvgImageGenerator.cs`
(`ChatSvgImageGenerator`, `ChatSvgOptions`, `SafeSvg`). Docs: [illustrations](../../../docs/illustrations.md).

## How it is composed

`IllustrationPipeline.AddMediaStudioIllustrations` (called by `AddMediaStudioAI`) registers the keyed
`IImageGenerator` `"illustration"` as **grounding → critique → base** (`IllustrationPipeline.Create`; the first `Use()`
is outermost):

| Layer | Type | Needs | Setting |
|---|---|---|---|
| Grounding | `GroundedImageGenerator` — adds top source passages (`SourceIndex`, `.mediastudio/source.embeddings.bin` in the project) and reuses a near-identical past SVG (`SvgExemplarLibrary`, `svg-library/index.bin` under the Media Studio data root, reuse at cosine ≥ 0.97) | keyed `"embedding"` generator | `images.grounding`, `images.exemplars` |
| Critique | `CritiquingImageGenerator` — vision model scores 0–1, retries below threshold | keyed `"vision"` client | `images.critique.enabled` / `threshold` (0.7) / `maxRetries` (1) |
| Base | `ChatSvgImageGenerator` over keyed `"illustration"` chat (router, or Copilot), output passed through `SvgSanitizer` / `SafeSvg`; or cloud gpt-image when `images.mode: png` + `privacy.allowCloud` + Foundry connection | chat tiers | `images.mode` (`svg`/`png`), `images.deployment` (`image`) |

Missing embeddings or vision silently drop that layer (local-first degradation, not an error).

## Common tasks

* **Use the Copilot runtime for SVGs:** `dotnet run --project src/VideoProduction.Cli -- configure copilot --illustrations`
  (`AgentSvgImageGenerator` in `src/VideoProduction.AgentFramework/`; see the `media-studio-copilot` skill).
* **Cloud PNG:** set `images.mode` to `png`, enable cloud, deploy the `image` role from `infra/models.json`.
* **Tune critique:** change `images.critique` in settings; keep `maxRetries` small — each retry is a full generation.
* **Change sanitizer rules:** edit `SvgSanitizer.cs` (element budget 128, nesting depth 4) and the allow-list in
  `SafeSvg` (`ChatSvgImageGenerator.cs`);   bump `SafeSvg.PolicyRevision` (recorded in every illustration receipt) so
    outputs from an older policy can be told apart. Never allow `script`, `foreignObject`, event handlers, or external `href`s.
* **Add a decorator:** derive from `DelegatingImageGenerator`, add it in `IllustrationPipeline.Create` in the right
  order, gate it on a new `ImageSettings` flag (see the `media-studio-add-provider` skill for settings/validator).

## Benchmark

```powershell
dotnet run --project src/VideoProduction.Cli -- bench image --briefs 5 --out out\bench-image
```

Reports per-brief validity, latency, critique score/attempts and grounded facts, then a summary line. Use the same briefs before/after a change.

## Verification

```powershell
dotnet test tests/MediaStudio.Tests -c Release --filter "FullyQualifiedName~MediaStudio.Tests.Illustrations&Category!=LocalRuntime"
dotnet test tests/MediaStudio.Tests -c Release --filter "Category=LocalRuntime&FullyQualifiedName~Illustrations"   # needs Ollama
dotnet run --project tests/VideoProduction.Visual.Tests -c Release   # visual regression over fixtures/
```

Test classes in `tests/MediaStudio.Tests/Illustrations/IllustrationTests.cs`: `SvgSanitizerTests`, `VectorIndexTests`,
`GroundingTests`, `CritiqueTests`, `IllustrationDiTests`.

## Pitfalls

* Changing the embedding model invalidates vectors; `VectorIndex.Load` ignores indexes with a different model id
  (`{connection}/{model}`) or dimension and they are rebuilt on next use — do not "migrate" them.
* A registered `"illustration"` image generator is kept as is; register overrides before `AddMediaStudioAI`.
* PNG mode silently falls back to SVG when cloud is off — check the `images:png` row of `configure test`.
* Small local models (tier S) often produce SVG that needs heavy repair; prefer `balanced` or Copilot for authoring.
* Known gap: the app does not yet fill `IllustrationSources.Sources` from opened project sources, so grounding uses
  only the exemplar library there, and the production illustration step (serialized configuration) cannot use the
  DI route — see the P6.8 notes in `docs/implementation-notes.md`.
