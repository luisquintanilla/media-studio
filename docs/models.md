# Models

Every model Media Studio uses, where it runs, and how to change it. Local models are pulled by Ollama or
downloaded as verified assets; cloud models are deployed by `azd up` into your own Microsoft Foundry resource.

## Local tiers

`scripts\Setup-LocalModels.ps1` (and the setup wizard) detects your RAM and VRAM and picks a tier from
[`assets/model-manifest.json`](../assets/model-manifest.json):

| Tier | Hardware | fast | balanced | vision | embeddings | Whisper |
| --- | --- | --- | --- | --- | --- | --- |
| **S** | < 16 GB RAM and < 8 GB VRAM | `qwen3.5:2b` | `qwen3.5:4b` | `qwen3.5:4b` | `embeddinggemma` | base |
| **M** | ≥ 16 GB RAM or ≥ 8 GB VRAM | `qwen3.5:4b` | `qwen3.5:9b` | `qwen3.5:9b` | `embeddinggemma` | large-v3-turbo (q5) |
| **L** | ≥ 32 GB RAM and ≥ 12 GB VRAM | `qwen3.5:9b` | `gpt-oss:20b` | `qwen3.5:9b` | `qwen3-embedding:0.6b` | large-v3-turbo (q5) |
| **XL** (opt-in) | ≥ 24 GB VRAM | `qwen3.5:9b` | `qwen3.8:27b` | `qwen3.8:27b` | `qwen3-embedding:0.6b` | large-v3-turbo (q5) |

`-Family gemma4` swaps only fast/balanced/vision: S uses `gemma4:e4b` for all three; M and L use
`gemma4:e4b` / `gemma4:12b` / `gemma4:12b`; XL uses `gemma4:12b` for all three.

```powershell
.\scripts\Setup-LocalModels.ps1 -DryRun             # show what would be pulled
.\scripts\Setup-LocalModels.ps1 -Tier M -Family gemma4
.\scripts\Setup-LocalModels.ps1 -Tier L -Balanced qwen3.5:9b   # override one role
.\scripts\Setup-LocalModels.ps1 -Tier XL -AllowXl
```

The same overrides exist on the CLI: `configure local --tier M --fast <tag> --balanced <tag> --vision <tag>`.
Any Ollama chat model works; the tiers are tested starting points, not requirements.

## Local assets

Assets are downloaded from Hugging Face into `%LOCALAPPDATA%\MediaStudio\models` and checked against the SHA-256
hashes in the manifest. Manage them with `video-production assets list | verify | ensure` (see
[CLI](cli.md#setup-commands)).

| Asset id | What | License | Size |
| --- | --- | --- | --- |
| `kokoro-v1.0-q8` | Kokoro-82M text-to-speech, quantized, plus voices (default) | Apache-2.0 | ~95 MB |
| `kokoro-v1.0-fp32` | Kokoro-82M, full precision (`assets ensure --fp32`) | Apache-2.0 | ~326 MB |
| `whisper-base` | Whisper base speech-to-text (tier S) | MIT | ~148 MB |
| `whisper-large-v3-turbo-q5` | Whisper large-v3-turbo, q5_0 (tiers M+) | MIT | ~574 MB |
| `julia-1` | Julia-1 routing decision model, ONNX | Apache-2.0 | ~614 MB |

Sources: [Kokoro](https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX),
[Whisper (whisper.cpp ggml)](https://huggingface.co/ggerganov/whisper.cpp),
[Julia-1](https://huggingface.co/SupersonicLabs/Julia-1-ONNX). Kokoro needs eSpeak NG for phonemes
(`winget install eSpeak-NG.eSpeak-NG`, installed by `Install-Prerequisites.ps1`); without it narration fails
with an install hint.

## Azure deployments

`azd up` deploys these into your Foundry resource ([`infra/models.json`](../infra/models.json)). The
`Find-FoundryRegion.ps1` pre-provision check picks a region where they're all available.

| Deployment | Model | Version | Used for |
| --- | --- | --- | --- |
| `authoring` | `gpt-6.1-sol` | 2026-09-29 | `strong` tier: storyboards and authoring |
| `chat` | `gpt-6-luna` | 2026-09-22 | `cloud` tier: chat; OpenAI Decisions (when enabled) |
| `mini` | `gpt-5.4-mini` | 2026-03-17 | deployed for your own use; not referenced by default settings |
| `embedding` | `text-embedding-3-small` | 1 | cloud embeddings |
| `transcribe-live` | `gpt-live-transcribe` | 2026-07-28 | live push-to-talk dictation |
| `image` | `gpt-image-2.5-flare` | 2026-09-08 | `png` illustrations |
| `ocr` | `mistral-ocr-4-0` | 1 | Mistral document OCR (`DEPLOY_MISTRAL_OCR`, on by default) |
| `transcribe` | `gpt-4o-mini-transcribe` | 2025-12-15 | off by default (`DEPLOY_OPENAI_AUDIO=true`); not used by the app yet |
| `tts` | `gpt-4o-mini-tts` | 2025-12-15 | off by default (`DEPLOY_OPENAI_AUDIO=true`); not used by the app yet |
| `claude` | `claude-sonnet-5` | 2 | off by default (`DEPLOY_CLAUDE=true`) |

`capacity` values are in `infra/models.json`; lower them if `azd up` reports a quota error.

Cloud narration and transcription use Azure Speech (MAI voices such as `en-US-Harper:MAI-Voice-2.1-Flash`,
fallback `en-US-Ava:DragonHDLatestNeural`) on the same Foundry resource; see [speech](speech.md).
Document Intelligence and Content Understanding also use the same resource; see [documents](documents.md).

## Measured results

> Measured on **one dev machine, CPU only** (Ollama on CPU). Treat these as a smoke test of the pipeline,
> not as benchmarks of the models. Run the commands on your machine to get your numbers.

| Command | Result |
| --- | --- |
| `bench local --candidates qwen3.5:4b --prompts 3 --no-judge` | routing agreement 86%, valid JSON 100%, 42.9 tokens/s, first token 5.7 s |
| `bench chat --tiers fast --prompts 2` | valid JSON 100%, 17.7 tokens/s |
| `bench image --briefs 2` | 2 of 2 valid SVGs; critique scores 1.00 and 0.20 |
| `bench tts --local-only` (Kokoro q8) | real-time factor ≈ 2.3 (slower than real time) |
| `bench stt --local-only --model base` (4 synthetic clips) | word error rate 3.8%, real-time factor ≈ 0.5 |

Cloud benches (`bench chat --tiers cloud,strong`, `bench stt` and `bench tts` without `--local-only`) have not
been run for these docs. See [Benchmarks](cli.md#benchmarks) for all options.
