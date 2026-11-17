---
{
  "smart_tool_format": 1,
  "name": "video-production",
  "version": "0.1.0",
  "description": "Produce independent approved PCM16 audio masters and local narrated technical videos, with measured narration, checked media and versioned integrity evidence.",
  "use_cases": [
    "Produce a narrated coding demo without desktop recording or a video editor",
    "Replace generated narration with explicitly imported human audio",
    "Rerender an evidence-backed demo offline from frozen takes",
    "Compose a first-class audio explanation without video configuration or visual runtimes",
    "Produce exact approved Audio/Video/Both project outputs with durable per-output integrity receipts"
  ],
  "platforms": ["windows"],
  "requires": [
    { "name": "ffmpeg-and-ffprobe", "purpose": "Video only: probe, normalize, encode and decode-check media; requires libx264 and AAC. Independent PCM16 audio composition does not use this runtime.", "install": "README.md#prerequisites", "optional": true },
    { "name": "chromium", "purpose": "Video only: matching Playwright 1.61.0 Chromium revision 1228 for browser capture and frame templates; not needed for audio.", "install": "README.md#prerequisites", "optional": true },
    { "name": "kokoro-backend", "purpose": "Local model-backed narrate capability; optional when using frozen or imported narration.", "install": "docs/speech.md", "optional": true },
    { "name": "whisper-base-transcriber", "purpose": "Explicit Windows x64 transcript learning with a separately pinned CLI and preprovisioned model; not needed for frozen rendering.", "install": "docs/transcription.md", "optional": true }
  ]
}
---

# Media production (compatible video-production adapter)

This remains one production smart tool, alongside the separate transcription
smart tool. `Media.Narration` and `Media.Audio` are libraries, not additional CLIs
or mandatory MCP hops. `audio-produce` composes a reviewed, approval-bound audio
script using measured mono PCM16 takes and explicit sample pauses. It publishes
an immutable candidate WAV, reviewed-script transcript, retained takes and a
complete integrity manifest. `audio-check` verifies those bytes and identities;
it does not certify acoustic transcript correctness or authorize release.

`media-produce` handles explicit Audio/Video/Both saved revisions and exact
configuration-bound stage grants through the existing tracked producer. Audio
does not depend on the video Core, visual providers, browser, Node, capture or
video encoding. Both records outcomes independently: a video failure does not
erase a verified committed audio output or turn the overall attempt into success.
UI Apply saves a revision, never approves production. Replaying admission observes
the existing attempt, never synthesizes or renders again. The explicit offline
verification configuration produces synthetic tones and authored static cards;
it is not an automatic fallback or evidence of live inference quality.

## Compatible video commands

The typed .NET library is the tool; the CLI is a thin wrapper. Generate local
English 1080p30 technical demos from a versioned, inert project. Use actual
bundled browser/CLI fixtures, code/title/diagram templates and imported media.
No desktop/microphone/system-audio capture, PTY/TUI, upload, cloud or editor.

`narrate` consumes official MEAI `ITextToSpeechClient` (Abstractions10.11.0,
experimental MEAI001 speech API) through a pinned, separately provisioned local
Kokoro provider. Legacy direct CLI generation requires explicit opt-in and is
never a fallback. `transcribe` explicitly invokes the separately provisioned Windows
x64 fixed-English Whisper Base CLI with pinned model/native artifacts. These two
capabilities are model-backed; frozen render/validate need neither model nor AI.
Missing configuration is an explicit error, not a fallback
voice. Imported mono PCM16 narration and explicit sample-based cue evidence are
first-class replacements.

Explicit generation/project `narrationPolicy` schemaVersion1,
`mode:"script-phrases-v1"` uses canonical script spans, unchanged actual phrase
PCM and measured readable `TakeWindow` captions. Omission retains legacy behavior.
ASR QA defaults off; `analyze-speech` is optional model-backed **non-adopting**
analysis with not-run/findings/observed-agreement/failed outcomes, never an
implicit exact-match caption gate. Recorded/unknown speech stays strict.
See `docs/transcription.md` for receipts, boundaries and acoustic-review limits.

Read `<capability> --help` before calling it. Common arguments are `--project
FILE`, `--source SOURCE_ROOT`, or an out-of-band `--config TOOLS.json`. All
results are JSON stdout; errors are JSON stderr and nonzero exits (130 on
cancellation). `manifest`, help and version do not need external runtimes.

`produce --request REVIEWED_JSON --production-config PRIVATE_JSON` is a separate
foreground whole-attempt capability wrapping the existing pipeline. Versioned
file requests/status/manifests and revisioned draft commands need no always-on
Studio owner. One production per configured private root; explicit busy without
queue, exact replay without rerun, bound cancellation and conservative crash
interruption. `run` still means capture/acquire. Read `docs/file-production.md`;
the inherited video wire/completion contract is preserved.

Acquire only trusted synthetic fixtures or typed reviewed actual loopback
Jellyfin profiles after explicit approval of the concrete action graph.
Dedicated nonadmin credentials enter the private unrecorded login context via
stdin; never put them in project/assets. Real app search responses, visible
result identities and native detail links must agree. No mocked responses.
Executables, argv, files, origins, pinned web assets, worker/browser identities and
workspace bind the approval; changed actions require reapproval. A process or
worktree is NOT a sandbox; arbitrary/untrusted host acquisition is not supported.

Render frozen sources with `video-production render --project demo\project.json
--source .`. This writes an immutable machine-checked candidate, not a
human-approved release. Preview is visibly marked and separate. Release checks
the exact reviewed movie hash and named human checks for claims, voice, pace,
privacy, captions, attribution and encoded viewing size.

Captions retain take-window, ASR-segment or human-verified evidence. No word
alignment is invented. Caption exceptions and weak boundaries need review.
For the strict recorded/legacy path, `transcribe` preserves the master, creates a 16kHz derivative and retains raw
segment evidence plus text discrepancies. Pending/failed STT blocks output until
successful explicit `adopt-transcript` or replacement narration import. Intended
speech, observed acoustic transcript and visual accessibility description stay
separate. Spoken ranges outside the source are rejected, never clipped.
An explicitly pinned optional `verified-digital-silence-v1` provider profile
may classify only a literal whole `[BLANK_AUDIO]` marker whose in-file
intersection independently verifies as exact-zero PCM. Its unmodified raw
text/range and classification proof remain mandatory frozen evidence; the
default stays strict. Unknown markers and any nonzero support reject.

Optional `scene.edit` selects original half-open source frames and binds bounded
spatial focus to hash/geometry/PTS-verified anchors. Reduced-motion static
highlights/dim/callouts/zoom/click emphasis are editorial annotations, not native
app features. Chapters are real MP4 metadata; player support varies.
The portable source bundle includes frozen takes and installed JavaScript
dependencies, not FFmpeg, Chromium, phonemizer or model binaries. See README.md
for explicit setup, offline boundaries and the tested platform.
