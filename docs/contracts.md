# Project, timing and trust contracts (v1)

> Reference for the project file format used by `video-production` and the app. For the big picture see
> [architecture](architecture.md); for commands see [CLI](cli.md).

`schemas/project.schema.json` describes authoring syntax. The .NET deserializer
also rejects missing required arguments, unknown members and integer enum values.
`validate` performs the cross-reference/hash/path/range/policy checks JSON Schema
cannot express. Null narration is a draft only. IDs are lowercase stable
identifiers; asset IDs/paths and scene IDs cannot collide ignoring case.

`Asset`: portable path, SHA-256, provenance and license statement. Never interpret
project paths as shell syntax. Native host paths belong only in caller-supplied
tool configuration. Asset traversal, absolute paths, devices, links/reparse
points and unsafe Windows names are rejected. Trust the media decoder only with
approved local assets; parsing a file is not a sandbox.

`Scene`: presentation `text`, spoken `script`, narration reference, timing master,
explicit hold permission, optional visual and typed effects. Browser is either the
bundled loopback counter or a typed reviewed actual Jellyfin profile; terminal is
the bundled noninteractive sum demo. They are
not a generic arbitrary browser/CLI automation API. Real fixtures can be added by
reviewing source, not by putting executable JavaScript into JSON. Images support
PNG/JPEG; videos MP4/WebM/Matroska with explicit SDR BT.709 tags. SVG, HTML,
playlists and untagged/HDR imported videos are refused. Titles/code/diagrams
are trusted templates, not evidence that an IDE or external app performed work.

All positions are integer frames/samples and half-open `[start,end)` ranges.
`Rate` uses BigInteger intermediates and explicit floor/ceiling/nearest rounding;
at 30/1 and 48 kHz there are exactly 1600 samples/frame. Narration-led duration
uses ceiling so no speech is cut. Fixed/action-led scenes reject speech overrun.
Recorded footage must cover the scene unless `allowHold` explicitly permits a
frozen last frame. No loops, time stretching, `-shortest`, fabricated successes
or inferred word boundaries. Optional `edit` and `audioSelection` select explicit
original source-frame/complete-cue sample ranges with reasons and exact PTS maps;
raw originals stay immutable. No mixing, crossfades or multi-track overlaps.

Caption cues are local sample ranges with `takeWindow`, `asrSegment` or
`humanVerified` evidence, tied by narration to a hashed WAV. Reordering/fixed
pauses project into the final timeline. Sample-to-delivery and millisecond
projection use nearest-away-from-zero; shared boundaries round identically.
Policy: <=2 lines, <=42 chars/line, 1..6 seconds, <=20 chars/second, or a
nonempty authored exception retained in provenance. Exceptions are not
auto-approval. Long takes require imported/verified segmentation; no uniform
word interpolation. The explicit Windows x64 external `transcribe` adapter
produces real segments, derivative/raw evidence and discrepancies without changing
the script; separate adoption is mandatory for the strict legacy/recorded path.
Explicit `script-phrases-v1` authored narration instead freezes measured phrase
PCM and canonical script cues; optional ASR analysis never adopts captions.
See [transcription](transcription.md)
for pinned prerequisites, pending-output gates and strict EOF limitations.

The additional [foreground file-production contracts](file-production.md) expose
versioned reviewed requests, attempts, final manifests and revisioned drafts.
They use one root-wide cross-process production owner with busy/replay rather
than a queue, conservative interruption and explicit cancellation. Existing
`run`/acquisition and installed Studio/job storage are not reinterpreted.

Trusted effects: full-panel highlight, text arrow annotation and frame-index
fade; code/terminal line reveal is visibly scripted presentation. All text is
HTML-escaped; font is a hash-verified explicit asset. Effects do not apply to
browser/media scenes; validation rejects these template effects there. Separate
typed source-footage `edit.effects` support bounded actual highlights/dim/callout/
static zoom/click emphasis bound to observed geometry and source PTS, not arbitrary
compositing. See [production upgrades](production-upgrades.md). No universal
editor/effect interchange is claimed.

Approval fingerprint binds serialized project/assets, workspace, concrete action
argv/executable identity, worker/dependency tree, Chromium and recorder encoder,
origins, output limits and timeout. Unknown action kinds cannot be supplied:
execution regenerates the expected typed graph and compares the whole plan.
Changed graph requires a fresh `plan` and explicit `approve`. Approved fixtures
are trusted; this is NOT OS/network isolation. Untrusted acquisition needs an
independently implemented reviewed container/VM profile and is refused here.
Child environment uses a small allowlist (no NODE_OPTIONS, preload hooks or
FFREPORT); stdout/stderr and exit/event evidence are genuine. Captures have
authored visible holds after real readiness/assertions, plus a monotonic ledger.

Stage records hash complete outputs and dependency fingerprints, not existence
alone. Incomplete staging never counts as success. Valid unrelated acquisition
resumes without rerunning it. Scene render cache keys include that scene/take/
font/effects/renderer/tools; changing narration invalidates its descendants, not
another scene's take. Final candidates are built in staging, checked, moved
atomically, then committed by stage record. Cancellation kills/reaps owned child
process trees; unfinished staging remains diagnostic, not an accepted take.
No automatic destructive cleanup or overwrite of frozen takes.

Delivery uses normalized PTS, H.264/yuv420p SDR BT.709, AAC mono48kHz and faststart
MP4. Native browser capture is **25fps**, duration-preservingly repeated to30;
it is not30 unique acquired frames. Templates are indexed at30fps. Pinned
browser recorder's untagged VP8 uses an explicitly assumed BT.601 limited
matrix with sRGB transfer; graphic frames use sRGB. Both are converted to limited
BT.709; already-tagged BT.709 video retains its transfer. There is no generic
HDR/color-management claim. Validate encoded swatches and final viewing size.

Two-pass loudness targets -16LUFS and -1.5dBTP pre-encode for AAC headroom.
Final decoded target remains -16+/-1LUFS and <=-1dBTP. Validate video PTS per
decoded frame, exact frame count, audio sample count (at most one1024-sample AAC
padding frame), start/duration/codec/color, full decode, loudness and detector
evidence. Freezes/silence are evidence, not instructions to delete content.
Human release review is a separate exact-movie hash checklist; previews cannot
be released and no automated process fills reviewer checks.
