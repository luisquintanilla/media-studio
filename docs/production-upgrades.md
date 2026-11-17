# Reviewed captures, source edits and acoustic transcripts

These are optional additive schema1 fields; baseline projects and the `Tools`
configuration retain their meaning. `schemas/project.schema.json` describes the
JSON shapes; the typed validator also checks hashes, exact clocks, cross-field
bounds, anchor evidence and caption policy. No executable model-proposed code,
shell command, arbitrary URL, JavaScript or FFmpeg filtergraph is accepted.

## Actual Jellyfin capture

A browser scene's optional `capture` is a `JellyfinProfile`: exact dynamic HTTP
IPv4 loopback `origin`, server/version, reviewed web/plugin/corpus provenance,
dedicated nonadmin user and explicit library IDs, actual query (2..40 characters),
expected real `Id/Name/Type`, observed native search endpoint, exact finite
`allowedApiPaths`/`allowedWebsocketPaths`, and hash-pinned `webAssets`.
`webAssets` entries use relative `web/...` paths and SHA-256, including
`web/index.html`. No guessed IDs or default ports are provided.

Create this profile from the actual isolated app setup, its pinned web assets and
runtime observations. Nonadmin `/System/Info/Public` verifies server identity.
Plugin/corpus generation is externally reviewed setup provenance, **not**
independently verified by the nonadmin browser; administrative status is never
recorded. Keep the sanitized setup manifest as a hashed project asset.

```powershell
dotnet run --project src\VideoProduction.Cli -- plan --project demo\project.json --private-auth $env:LOCALAPPDATA\MediaStudio\ephemeral-demo-user.json --source .
dotnet run --project src\VideoProduction.Cli -- approve --project demo\project.json --scope reviewed-jellyfin-loopback --by actual-scope-approver --private-auth $env:LOCALAPPDATA\MediaStudio\ephemeral-demo-user.json --source .
dotnet run --project src\VideoProduction.Cli -- run --project demo\project.json --private-auth $env:LOCALAPPDATA\MediaStudio\ephemeral-demo-user.json --source .
```

The private file, **outside the project root**, contains only `username` and
`password`. Do not commit/share it. Use an isolated ephemeral nonadmin account,
not a personal profile. Its absolute path and hash bind the local action approval;
changed path/bytes require reapproval. The local plan is not bundled/exported.
The worker authenticates through genuine UI before
recording, checks policy/library scope, and moves session state only in memory.
It observes the actual submitted query, returned native Items/SearchHints,
visible card identity/type/name, actual app-rendered detail href and detail
response/title. No intercept fulfillment, seeded results, semantic-origin badges,
snippet or time-seek claim. Exact app-version layout differences fail closed and
need reviewed worker/profile updates, not a selector/code escape hatch.

Every fetched web asset must match the reviewed hash list. Requests outside
the explicit origin/API/assets/WebSocket scope abort and fail acquisition.
Login screens, cookies, auth headers, token URLs, state, HAR and traces are not
exported. Failed recorded captures are discarded rather than committed as takes.
Request headers or unsanitized server bodies are never the public evidence.

The immutable `assets/takes/<scene>/` contains original `browser.webm`, genuine
screenshots, sanitized `native-evidence.json`, monotonic `landmarks.json`,
`source-clock.json`, `observations.json`, hash-bound `anchors.json` and probe.
Source frames/PTS and monotonic action events remain distinct clocks.
Acquisition finalizes context/video and probes/hashes before atomic commit.
Source bundles allow only named take artifacts, not arbitrary capture directories.
Frozen native evidence includes the complete approved capture profile. Import
the exact browser scene ID/profile, entire finalized take directory and unchanged
`build/stages/acquire-<scene>.json`; a naked WebM or relabeled media scene does
not preserve this contract. Changing the scene's profile cannot relabel a frozen
take. Native image links may have playback overlays; the pinned client uses its
actual visible text-action detail link, with expected item identity checked
before navigation and again against the real detail response/title.
If the validated result card lies below the caption-safe region, the trusted
worker performs native DOM scrolling before its result/link observations.
Scroll start/end, actual bounding boxes and monotonic times remain in the
recorded take's ledger; no wait or footage is removed. Oversized or still
out-of-bounds targets fail the unchanged screenshot/anchor geometry guard.

## Non-destructive original-footage edits

`scene.edit` is a `FootageEdit`, for recorded browser/imported video only:

```json
{
  "sourceSha256": "<actual original movie SHA-256>",
  "sourceFps": { "numerator": 25, "denominator": 1 },
  "startFrame": 0,
  "endFrame": 250,
  "reason": "Explicit source selection; original waits/event ledger remain available",
  "anchorAssetId": "actual-capture-anchors",
  "effects": [],
  "reducedMotion": true
}
```

This is a shape example, **not** actual app evidence. Use real decoded source
frame bounds and hash. Raw footage is never rewritten. Selection is half-open
original frames. An edit cannot loop, stretch, silently remove latency or hold
beyond selected source. Author/disclose each cut and retain original/event ledger.
VFR editing is explicitly unsupported; separately normalized footage must have
its own provenance. `source-maps` bind every final local frame to original
frame/actual PTS/time base using duration-preserving floor conversion (25->30
repeats, no unique30fps acquisition claim).

An optional `edit.tailHold` is an explicit authored repeat of the **last selected
source frame**, not extra native elapsed time:

```json
"tailHold": { "frames": 30, "reason": "Authored one-second settling hold after the completed action" }
```

The bound is1..300output30fps frames. Use `timing: "action"` or `fixedFrames`
equal to the entire selected duration (floor-converted to30fps) plus the declared
hold. Narration must fit; a shorter narration-led scene cannot silently remove
the selected footage or hold. `allowHold` does not replace this declaration for
edited footage. Source maps flag each repeated frame `authoredTailHold: true`
and retain its actual original source index/PTS. Spatial effects over a hold
still require the **actual last frame** inside the unchanged observed anchor
interval; this never extends stale anchor evidence.

At most32 `SpatialEffect`s select `highlight`, `dim`, `callout`, `zoom`, `pointer`
or `click`. Each contains output `[startFrame,endFrame)`, `anchorId`, exact
`expectedRegion` (`x,y,width,height`, normalized) and optional inert canvas text.
Spatial targets must match hash-bound observed identity/layout/viewport/region
and stable decoded-frame intervals. Screenshot ROI matching uses actual decoded
source SSIM, not invented frame-accurate click timestamps. Wrong/out-of-range/
stale targets fail. Pointer/click emphasis additionally requires a genuine click
observation/coordinate, not an authored fictional cursor.
The contiguous source interval must score at least0.94 and within0.01 of the
best ROI match. The latter allows measured VP8 quantization variation in a
stationary counter glyph; it is not a DOM assertion or exact event timestamp.
The before/after native assertions and screenshot identities remain separate
required evidence.

Only static reduced-motion focus is supported. Crop/zoom is bounded to2x;
overlapping zooms are rejected. Bounds are validated after simultaneous crop
transforms. The bottom20% is reserved for captions; focus strokes, dim, callouts
and click rings cannot enter it. Labels distinguish postproduction focus from
native application features. Encoded-pixel regression verifies actual geometry,
not merely JSON or an unencoded screenshot. Synthetic geometry tests are not
evidence that a native application was recorded.

Optional `audioSelection` (`startSample,endSample,reason`) selects complete
observed narration cues from the unchanged master; crossing a cue is an error.
The final caption projection rebases selected speech samples after trims/
reordering and retains its evidence kind. No word alignment is inferred.

## Chapters and descriptions

`project.chapters` entries are `id,sceneId,title`. They resolve to rational frame
times, are muxed as actual MP4 chapters and verified against encoded metadata;
the delivery also carries `chapters.json`. Player chapter navigation/display is
player-dependent, not promised universally.

`scene.visualDescription` is descriptive accessibility text exported separately
as `descriptions.json`. It is not a claim that those words were spoken. Acoustic
captions/index speech come from actual imported/STT cues; see
[transcription](transcription.md).

## Insert-only settled scene handoffs

An incoming scene may declare this optional field (never on the first scene):

```json
"transitionIn": {
  "frames": 12,
  "outgoingStaticFrames": 6,
  "incomingStaticFrames": 6,
  "audioRampSamples": 240,
  "reason": "Reviewed settled outgoing card and static incoming diagram"
}
```

This inserts12new authored frames/400ms/19200silent48k samples **between**
the complete scenes; it does not overlap, shorten or time-stretch either scene.
The bridge dissolves repeated outgoing-last/incoming-first rendered frames with
incoming weights1/13..12/13. Bounds:1..30bridge frames,2..30static handle frames,
1..2400ramp samples. A declaration alone is not proof: every decoded frame in
the outgoing/incoming handle must be pixel-identical **after** all rendering,
crop, highlight, reveal/fade and other scene effects. Moving UI, loading or
typing handles fail; omit that bridge or author genuinely static edges.

Both boundary ramp intervals are independently decoded and must contain
**exact-zero PCM16 samples** at48k before sample-indexed fade-out/fade-in is
applied. These silence-only ramps do not fade speech or promise a crossfade of
nonzero ambience. The inserted bridge is digital silence. No narration is
truncated, shifted within its scene, substituted or stretched.

Incoming scene starts, captions and chapters advance by the exact inserted
frame/sample count. The preceding chapter includes its outgoing bridge;
the incoming chapter starts at the incoming scene, not inside the dissolve.
`transitions.json` maps every authored bridge frame to both rendered scene-edge
frame indices and rational weight, with `authoredBridge: true`. For edited
footage those indices compose with the retained per-scene original-frame/PTS
source map. Bridge images are authored repetitions, not extra measured native
latency. Export/offline rerender carries the same project, maps and clocks.
Changing these fields invalidates whole-delivery transcript composition.

Absent `transitionIn` and `tailHold` preserve hard cuts and existing duration
rules. There is no arbitrary FFmpeg expression, moving-UI dissolve, middle-of-take
hold or general transition framework. The maintainer encoded/offline regression
(not included in this repository) uses clearly labeled synthetic red/blue/tone
fixtures, rejects nonzero audio and rendered moving handles, and checks final
picture blends, exact caption/chapter clocks and relocated maps/media.

## Offline and release boundaries

Frozen source export includes referenced assets (master/STT derivative/raw report/
adoption/anchor evidence), font notice, finalized safe takes, project/timeline
and renderer sources/installed JavaScript. It excludes machine configs, private
stderr/auth/state, native runtimes, models, FFmpeg and browser binaries.
Relocated rerender needs only explicit preinstalled render prerequisites, never
app credentials/app startup/ASR/TTS/model/network/reacquisition.

Changing scripts, capture profile/origin/endpoints/assets, trim/effects/geometry,
transcript/decision or runtime invalidates relevant fingerprints/approvals.
Read-only `doctor` reports prerequisites; it does not provision or transcribe.
Candidates/previews remain distinct and exact-byte human release gates remain.
No machine check implies a human audition, privacy/caption/claim approval or
permission to redistribute third-party model/runtime/font/media assets.
