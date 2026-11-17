# Source origins

This repository started as a curated snapshot of three earlier projects by the same owner. Each was imported
with `git archive` at a fixed revision (no untracked files, credentials, private configuration, model weights,
installed dependencies, binaries or generated output) and has since been reworked here. It is not kept in sync
with those projects.

| Source | Revision | What came from it | Where it lives now |
| --- | --- | --- | --- |
| video-production-smart-tool | `fd1fc1001b7aceeed74f40a66b7b026ee135b100` | Production pipeline, browser worker, schemas, tests, and the former WPF experiment | `src/`, `workers/`, `schemas/`, `tests/`; the app is now `src/MediaStudio.App` |
| audio-transcriber-smart-tool | `7a7d2e8bf08a00c07bc5a841422378bc883d278d` | Local Whisper transcription library and CLI | `src/AudioTranscriber`, `src/audio-transcriber` |

Narration and WAV helpers were later extracted into `Media.Narration`. The Kokoro speech pipeline, model asset
manager, configuration, routing, documents and Copilot integration are new code in this repository. See
[architecture](docs/architecture.md) for the current layout.

## Licensing

- All code in this repository is offered under the MIT license in [`LICENSE`](LICENSE), which retains the
  original video-production notice alongside the Media Studio sample notice. This includes the owned
  AudioTranscriber source imported at `7a7d2e8bf08a00c07bc5a841422378bc883d278d`.
- No license here changes third-party, model, font, native-runtime or media obligations; see
  [third-party notices](THIRD-PARTY-NOTICES.md).

No model files are committed. Models are downloaded on request (`assets ensure`, `Setup-LocalModels.ps1`) from
the pinned sources listed in [`assets/model-manifest.json`](assets/model-manifest.json) and verified by SHA-256.

## Adapted sources

Experimental contracts adapted from unreleased proposals keep their provenance in the project that holds them
(for example `src/VideoProduction.Decisions/NOTICE.txt` and `SourceHashes.txt`) and must not be presented as a
public package API. See [third-party notices](THIRD-PARTY-NOTICES.md).

## Byte-preserved files

`.github/skills/media-explanation/SKILL.md` and the synthetic audio fixtures under `samples/synthetic-audio/` are
stored byte-for-byte (CRLF included) because they are verified by hash; `.gitattributes` marks them `-text`.
