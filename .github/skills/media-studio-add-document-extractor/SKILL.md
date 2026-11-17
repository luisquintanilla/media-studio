---
name: media-studio-add-document-extractor
description: >-
  Add a new document extractor (OCR / PDF / image text extraction) to Media Studio's routed "Auto" extractor: wrap a
  Microsoft.Extensions.DocumentExtraction `DocumentExtractionClient`, register it as a router candidate, gate remote
  ones behind upload consent and `privacy.allowCloud`, add heuristic ranking rules, and test it. Use when asked to
  "add a document extractor", "add an OCR provider", "support another PDF reader", "route scanned PDFs to X",
  "change document routing rules", "documents probe", or "bench documents".
---

# Add a document extractor

All extraction code is in `src/MediaStudio.Sources/Routing/`. Docs: [documents](../../../docs/documents.md).

| Piece | File / type |
|---|---|
| Candidate names | `DocumentCandidates` in `src/MediaStudio.Configuration/ProviderSettings.cs` (`All`, `IsCloud`) |
| Factory + DI | `DocumentExtractorFactory.cs` (`Available`, `CreateCandidates`, `CreateRouter`, `CreateExplicit`, `TryCreate`), `AddMediaStudioDocuments`, `MediaStudioDocumentKeys` (`documents`, `documents:<candidate>`) |
| Candidate record | `ExtractorCandidate(Name, Description, IsRemote, Client, SupportsPdf, SupportsImages, MaxPages, MaxBytes)` in `ExtractionRouting.cs` |
| Router | `DecisionRoutingDocumentExtractionClient` (decider + fail-over on `ExtractionQuality.IsAcceptable`) |
| Heuristic order | `DocumentRoutingRules.Rank` in `DecisionRoutingDocumentExtractionClient.cs` |
| Consent gate | `ConsentGatedDocumentExtractionClient`, `IUploadConsent`, `FixedUploadConsent`, `UploadConsentDeniedException` in `UploadConsent.cs` |
| Probe | `DocumentProbe.cs` (text layer, pages, scanned ratio, tables, form fields, language) |
| `configure test` rows | `DocumentConfigurationCheck.cs` |

## Steps

1. **Package.** Add the extractor package version to `Directory.Packages.props`, reference it from
   `src/MediaStudio.Sources/MediaStudio.Sources.csproj`, then `dotnet restore MediaStudio.slnx --force-evaluate`.
2. **Name it.** Add a constant to `DocumentCandidates` and include it in `All`; if it uploads anything, add it to
   `IsCloud`. Add a `DisplayName` case in `DocumentExtractorFactory`.
3. **Create it in `TryCreate`.** Local extractors return `new ExtractorCandidate(name, description, false, client)`.
   Remote extractors **must** be returned as `Gate(new LazyDocumentExtractionClient(() => ...))` with `IsRemote: true`:
   * `Gate` wraps the client in `ConsentGatedDocumentExtractionClient(client, DisplayName(name), consent, allowCloud)`,
     which refuses when `privacy.allowCloud` is false and asks `IUploadConsent` before every upload.
   * `LazyDocumentExtractionClient` means no credential is touched until a document is routed there.
   * Credentials: `CredentialFactory.CreateTokenCredential(connection)` (Entra) or `CredentialFactory.GetApiKey(id)`;
     honour `DocumentExtractorFactoryOptions` (`Handler`, `Credential`, `ApiKey`) so tests can inject fakes.
   * Declare hard limits via `MaxPages`/`MaxBytes` (see Mistral: 30 pages / 30 MB) so the router skips it when the
     document does not fit.
4. **Description matters.** The `Description` string is what the decision model reads when choosing; say what the
   extractor is good at, its cost/latency and whether it uploads.
5. **Heuristic rank.** Add the name to the right `order` arrays in `DocumentRoutingRules.Rank` (digital PDFs, structured
   tables/forms, > 30 pages, remote available, local only). Keep the rule that partly scanned PDFs try `pdfpig` first.
6. **Settings.** Users opt in by listing it in `documents.candidates`; cloud ones also need a Foundry connection
   (`DocumentExtractorFactory.FoundryConnection`). Do not add cloud candidates to the defaults.
7. **`configure test`.** Make `DocumentConfigurationCheck` (or `ConfigurationTester` for Foundry-only checks) report a
   `documents:<name>` row with a remediation hint.

## Tests

`tests/MediaStudio.Tests/Documents/DocumentRoutingTests.cs` — add cases for:

* ranking (`DocumentRoutingRules.Rank`) for the document shapes you target;
* `Available`/`CreateCandidates` include it only when configured;
* a remote candidate is consent-gated: with `FixedUploadConsent.Deny` or `allowCloud: false` it throws
  `UploadConsentDeniedException` and **no HTTP request is made** (use a recording `Handler`);
* fail-over when its output fails `ExtractionQuality.IsAcceptable`.

Anything needing a live model gets `[Trait("Category", "LocalRuntime")]` (see `LocalVisionDocumentTests.cs`).

## Verification

```powershell
dotnet test tests/MediaStudio.Tests -c Release --filter "FullyQualifiedName~MediaStudio.Tests.Documents&Category!=LocalRuntime"
dotnet run --project src/VideoProduction.Cli -- documents probe <file.pdf>                 # shows Auto's first choice
dotnet run --project src/VideoProduction.Cli -- documents probe <file.pdf> --extract       # local only
dotnet run --project src/VideoProduction.Cli -- documents probe <file.pdf> --extract --allow-cloud
dotnet run --project src/VideoProduction.Cli -- bench documents <a.pdf> <b.png> --local-only
dotnet run --project src/VideoProduction.Cli -- configure test
```

Nothing is uploaded unless `--allow-cloud` is passed **and** `privacy.allowCloud` is on.

## Pitfalls

* Never return an ungated remote client, not even from `CreateExplicit`; explicit choices bypass the router, not the
  consent gate.
* `vlm` is local unless the vision connection is cloud; then it is gated too (`visionIsRemote`).
* Extractors that cannot read images must set `SupportsImages: false` (like `pdfpig`).
* `MEDE0001` / `CTDE0001` (experimental DocumentExtraction APIs) are suppressed globally; re-run these tests after
  bumping `Microsoft.Extensions.DocumentExtraction` or `CommunityToolkit.DocumentExtraction.*`.
