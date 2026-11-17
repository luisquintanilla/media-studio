# Third-party boundaries

The MIT license covers newly authored video-production code, not dependencies,
fonts, model weights, native runtimes, browser binaries or imported assets.

| Component | Terms / handling |
| --- | --- |
| Microsoft.Extensions.AI / .Abstractions / .OpenAI 11.0.0 | Official Microsoft NuGet packages, MIT. The speech API is experimental (MEAI001). Microsoft copyright/permission notice below. |
| Microsoft.Extensions.Hosting 11.0.0, System.CommandLine 2.0.14, System.Numerics.Tensors 11.0.0 and other Microsoft/System dependencies | Official Microsoft packages, MIT; Microsoft notice below. Exact dependency closures and hashes are in each project's `packages.lock.json`. |
| Azure.Identity 1.21.0 / Azure.Core / System.ClientModel | Official Microsoft MIT packages. Azure sign-in uses `DefaultAzureCredential` (or an interactive browser credential) only when you configure an Azure connection; no Azure CLI, tokens or credentials are bundled. |
| OpenAI 2.14.0 | Official .NET OpenAI client, MIT, copyright (c) 2026 OpenAI (https://openai.com). Pinned to 2.14.0 for compatibility with Microsoft.Extensions.AI.OpenAI 11.0.0 ([details](docs/troubleshooting.md#openai-sdk-version)). The experimental bearer-policy constructor is isolated with OPENAI001. |
| Anthropic 13.3.0 | Official Anthropic C# SDK including its MEAI `IChatClient` adapter, source anthropics/anthropic-sdk-csharp at a0cdd4c2cd909c5ae29ed939a8ad6e1568c0377a. Apache-2.0 from 13.0.0; earlier contributions retain MIT terms. Both notices are preserved in `docs/licenses/Anthropic.LICENSE.txt` and `Anthropic.LICENSE-MIT.txt` and copied to provider output. Used only with an explicitly configured Foundry deployment. |
| Microsoft.Agents.AI 1.28.0 / Microsoft.Agents.AI.GitHub.Copilot 1.28.0 | Official Microsoft MIT packages, used by the optional `MediaStudio.Agents` runtime. `AsIChatClient` conversion is experimental MAAI001. |
| GitHub.Copilot.SDK 1.0.14 | Official MIT NuGet package, copyright GitHub, Inc.; MIT notice below. Experimental permission types GHCP001. The CLI runtime it downloads is listed separately below. |
| GitHub Copilot CLI runtime (version pinned by GitHub.Copilot.SDK) | Proprietary GitHub Copilot CLI License, NOT MIT. Downloaded at build time only for MediaStudio.App from the github/copilot-cli GitHub release and SHA256-verified by the SDK's build targets; never committed. Redistributed unmodified, solely as part of the app, never standalone; its LICENSE.md is copied to `licenses/GitHubCopilotCLI.LICENSE.md` in app output. Use is optional and requires the user's own GitHub Copilot subscription (or their own BYOK provider) and GitHub's terms; premium requests are billed to the user. Pass `-p:CopilotSkipCliDownload=true` to build without it. See [Copilot](docs/copilot.md). |
| AGUI.Abstractions / AGUI.Client / AGUI.Server 1.0.1 | AG-UI Protocol packages, MIT. |
| Microsoft.AspNetCore.Components.WebView.Wpf 11.0.10, Microsoft.AspNetCore.Components.Web 11.0.0, Microsoft.AspNetCore.Components.AI 0.1.0-preview | Official Microsoft packages, MIT. |
| Microsoft.Extensions.DocumentExtraction / .Abstractions / Microsoft.Extensions.DataIngestion 11.0.0-preview | Official Microsoft preview packages, MIT. |
| CommunityToolkit.DocumentExtraction.* 1.0.0-preview (PdfPig, VisionLanguageModel, Mistral, AzureDocumentIntelligence, AzureContentUnderstanding) | .NET Community Toolkit packages, MIT. Cloud extractors upload documents only after consent ([documents](docs/documents.md)). |
| PdfPig 0.1.15 | Transitive dependency of the PdfPig extractor, Apache-2.0, copyright UglyToad. |
| dotnet/extensions video proposal, PR 7420 | Local adapted contracts in VideoProduction.Providers.Experimental, MIT, copyright Microsoft Corporation. Pinned head a7a06c9bc9becfa4e40fc55aa313037801505457; closed/unmerged proposal, NOT an official released Microsoft video interface. Shared MEAI base types are referenced from the official package, not copied. |
| dotnet/extensions decision proposal, PR 7796 | Five source-pinned files from e87f1126cf22108fb27f6391c7826fb91c25138e, adapted into `src/VideoProduction.Decisions`, not a released Microsoft decision interface. .NET Foundation/Microsoft MIT notices and the adaptation ledger are retained in that project (`NOTICE.txt`, `SourceHashes.txt`). |
| typesafe-meai Julia adapter | Selected source graph pinned ce28e92b338b0edfbc22cca3c62101fa75c008f7. See `src/VideoProduction.Decisions/NOTICE.txt` for original source hashes and adaptations; do not infer a third-party license from this repository's license grant. |
| Julia-1 decision model | Apache-2.0, from huggingface.co/SupersonicLabs/Julia-1-ONNX at a pinned revision. Optional; downloaded on request by `assets ensure --id julia-1` and verified by SHA-256; never committed or redistributed. |
| Microsoft.ML.OnnxRuntime 1.23.2 / Microsoft.ML.Tokenizers 2.0.0 | Official Microsoft packages, MIT. The ONNX Runtime upstream notice file is preserved at `third_party/onnxruntime-notices` (see below). |
| Tokenizers.HuggingFace 3.23.1 | Apache-2.0. Used for the Julia tokenizer. |
| Kokoro-82M model and voices (Kokoro v1.0 ONNX) | Apache-2.0, from huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX at a pinned revision. Downloaded on request (`assets ensure --id kokoro-v1.0-q8`) and verified by SHA-256; weights and voices are not committed or redistributed. The Kokoro inference pipeline in `src/MediaStudio.Speech` is code in this repository. |
| eSpeak NG | GPL-3.0. Kokoro uses it for phonemes by running the separately installed `espeak-ng` executable as its own process; it is not linked, bundled or silently installed. Install it yourself (for example `winget install eSpeak-NG.eSpeak-NG`). |
| Whisper.net / Whisper.net.Runtime 1.9.0 | MIT, copyright Sandro Hanea. Native runtime libraries are restored from NuGet. |
| Whisper models (ggml base, large-v3-turbo q5_0) | MIT (OpenAI Whisper), converted files from huggingface.co/ggerganov/whisper.cpp at a pinned revision. Downloaded on request (`assets ensure`) and verified by SHA-256; never committed. |
| Markdig 1.6.0 | CommonMark renderer for inert author responses. BSD-2-Clause, copyright 2016-2026 Alexandre Mutel. The notice is retained in `docs/licenses/Markdig.LICENSE.txt` and copied to app output. Raw HTML, link/image parsing and automatic links are disabled. |
| Playwright / Chromium driver JavaScript | Playwright Apache-2.0; preserve installed LICENSE, NOTICE and ThirdPartyNotices. Exact pinned version in `workers/browser/package.json`. Chromium is installed separately, not redistributed here. |
| GSAP 3.14.2 | Curated browser `dist/gsap.min.js` from greensock/GSAP commit e830e8fc16fafa10644a93d341613ef54133eb5f. Original header retained. GSAP Standard "no charge" license, https://gsap.com/standard-license/, NOT MIT. See `workers/browser/runtime/NOTICE.txt` and `origins.json`; review the license obligations before distribution. |
| HyperFrames 0.8.33 | Published browser runtime from npm hyperframes 0.8.33, heygen-com/hyperframes source commit 6e3308be4f2ab886597fcee7c5896a5f842ec4b6. Apache-2.0 license retained in `workers/browser/runtime/HyperFrames-LICENSE.txt`. Only the runtime is imported. |
| Inter 4.1 (Inter-Regular.ttf) | Bundled default render font in `assets/fonts/`, SIL Open Font License 1.1, Copyright (c) 2016 The Inter Project Authors, no Reserved Font Name. Unmodified `extras/ttf/Inter-Regular.ttf` from the rsms/inter v4.1 release (SHA-256 40d692fce188e4471e2b3cba937be967878f631ad3ebbbdcd587687c7ebe0c82); the full license is `assets/fonts/OFL.txt`, copied to output and preserved next to the font in every export. |
| Roboto Mono | SIL Open Font License 1.1. Optional: import a font file and its OFL.txt with `--font`/`--font-license`; exports preserve both. No proprietary Windows font is bundled. |
| FFmpeg / ffprobe | External prerequisites, never bundled. Builds vary (LGPL/GPL/nonfree flags, external codecs). `doctor` records the full `-version` build configuration; inspect the actual licensing before distributing any binary. |
| H.264 / AAC | Availability in an FFmpeg build does not settle patent, licensing or territorial obligations. Review your intended distribution. |
| Imported narration / media | You must establish rights, consent and provenance. No voice cloning or implied permission to publish. |

Dependency restore/provisioning is an explicit online setup operation. Rendering
frozen takes needs none of the external provider/model/frontend dependencies and does not
download anything. `export` includes installed Playwright JS with its existing
notices, the selected font/license, authored sources and frozen takes. It does
not collect unrelated host files or include FFmpeg/browser/model binaries.

## Microsoft and OpenAI MIT notices

ONNX Runtime's Microsoft MIT license and full upstream third-party notice file are preserved
at `third_party\onnxruntime-notices`, pinned to
`33ca9628233dc8f002435e868d4c2e9f82766ca1`. Notices describe the upstream
distribution, not a claim that every listed dependency is linked in this selected
CPU build. Preserve the matching binary distribution's obligations when packaging.
No native runtime or weights are redistributed by this source sample.

Copyright (c) Microsoft Corporation. All rights reserved.

Copyright (c) 2026 OpenAI (https://openai.com)

Copyright (c) GitHub, Inc.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
