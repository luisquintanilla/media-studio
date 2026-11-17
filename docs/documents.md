# Documents

Media Studio turns PDFs and images into source text for your videos. Reading is **local by default**; cloud
extractors are used only when cloud is allowed in settings *and* you approve the upload.

## Extractors

| Id | Runs | Best for |
| --- | --- | --- |
| `pdfpig` | locally ([PdfPig](https://github.com/UglyToad/PdfPig)) | digital PDFs with a text layer (fast, exact) |
| `vlm` | locally, your `vision` model (Ollama) | scanned pages and images; slower |
| `document-intelligence` | Azure AI Document Intelligence (`prebuilt-layout`) | scans, tables and forms |
| `content-understanding` | Azure AI Content Understanding (`prebuilt-documentSearch`) | mixed, image-heavy documents |
| `mistral` | Mistral OCR deployment in Foundry (`ocr`) | scans, up to 30 pages / 30 MB |

All cloud extractors use the first Foundry connection in your settings (from `configure azd`), with Entra ID
or its API key. They are built on the published `Microsoft.Extensions.DocumentExtraction` and
`CommunityToolkit.DocumentExtraction` preview packages.

## Auto routing

**Auto** picks an extractor per document:

1. **Probe.** PdfPig reads cheap local features: pages, size, text per page, scanned ratio, images only,
   table-like lines, form fields, language and orientation. Nothing is uploaded.
2. **Fast path.** A digital PDF goes straight to `pdfpig`.
3. **Decide.** Otherwise the decision model (Julia, or rules when it isn't installed; see [routing](routing.md))
   ranks the eligible extractors in `documents.candidates`. Cloud extractors are eligible only when
   `privacy.allowCloud` is on.
4. **Try in order.** Each extractor is tried in rank order. The next one is used if an extractor fails, you
   decline its upload, or its result looks too thin (too little text for the pages).
5. **Result.** The text is tagged with the extractor used and the attempts. If every result is thin, the best
   one is returned and tagged `mediastudio.low-quality`. Only when every extractor fails does reading fail.

## Upload consent

Every cloud extractor is wrapped in a consent check. Before any byte leaves your machine, the app shows an
**Upload document** dialog naming the document and the service: **Upload and read** or **Keep it local**.
If you decline, or cloud is off, nothing is uploaded and Auto moves on to the next extractor. Tools without a
consent prompt deny uploads.

## In the app

The PDF reader selector offers:

- **Local PdfPig** (the default),
- **Auto (recommended)** (opt-in): the routing above,
- each configured extractor by name (explicit; skips routing but still asks for consent),
- the existing Mistral OCR readers and **Vision · &lt;model&gt;** readers.

## Settings

```json
"documents": {
  "candidates": ["pdfpig", "vlm", "document-intelligence", "content-understanding", "mistral"],
  "mistralDeployment": "ocr"
}
```

`configure local` sets `["pdfpig", "vlm"]`; `configure azd` adds the three cloud extractors. Remove an id to
stop Auto from using it. See [configuration](configuration.md#documents).

## Command line

```powershell
# Show the probe, the plan Auto would follow, and the candidates (JSON). Nothing is uploaded.
dotnet run --project src/VideoProduction.Cli -- documents probe .\paper.pdf

# Also run the routed extraction (cloud only with --allow-cloud and privacy.allowCloud on).
dotnet run --project src/VideoProduction.Cli -- documents probe .\paper.pdf --extract --allow-cloud

# Compare Auto and each extractor: time, characters, pages, quality (TSV).
dotnet run --project src/VideoProduction.Cli -- bench documents .\paper.pdf .\scan.png --local-only
```

`configure test` includes `documents:*` rows: a real PdfPig extraction of a generated PDF, and a check that
`vlm` has a vision model.

## Limitations

Document Intelligence, Content Understanding and Mistral were tested only with fake HTTP handlers (request
shapes), not against live Azure services. The `vlm` extractor was tested live with Ollama `qwen3.5:4b`.
