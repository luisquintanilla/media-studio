# Foreground file-oriented production v1

This is an additional UI-independent .NET/CLI surface, not a deployment or
replacement of the installed Studio owner. It wraps `ProductionVideoPipeline`;
there is no second renderer, speech stack, agent framework, scheduler or queue.
The existing CLI **`run` still means approved capture/acquire**.

## Explicit configuration and reviewed requests

`FileProductionConfiguration` is an owner-private JSON file:

```json
{
  "schemaVersion": 1,
  "stateRoot": "C:\\path\\to\\video-files-v1",
  "generation": {
    "toolsFile": "C:\\path\\to\\tools.json",
    "fontFile": "C:\\path\\to\\font.ttf",
    "fontSha256": "<reviewed SHA-256>",
    "fontLicenseFile": "C:\\path\\to\\OFL.txt",
    "fontLicenseSha256": "<reviewed SHA-256>",
    "narrationPolicy": {
      "schemaVersion": 1,
      "mode": "script-phrases-v1",
      "analyzeSpeech": false
    }
  }
}
```

Paths/pins must refer to already provisioned, approved local dependencies.
Placeholder hashes above are documentation, not valid configuration.
No downloads, credential fallback or automatic model selection occur.
Existing `GenerationConfiguration` optional frozen narration and strict-ASR
fields retain their meanings. Caller-reviewed storyboards bypass planners;
illustrations still require an explicitly configured library image adapter,
not an implicit service in this first CLI configuration.

An operator-private generation configuration may additionally bind reviewed
frozen footage/images to specific planned scenes:

```json
"importedVisuals": [{
  "sceneId": "scene-01",
  "file": "C:\\path\\to\\owned-window-recording.mp4",
  "sha256": "<reviewed SHA-256>",
  "provenance": "Actual owned WPF window; waiting condensed and labelled",
  "license": "Caller-owned sample and source; review required",
  "allowHold": false
}]
```

Bindings are optional and cannot be authored by the model's proposal tool. They
require an explicit plan, unique existing scene IDs, trusted non-reparse local
paths, supported raster/video extensions, exact hashes and provenance/rights.
Imports cannot be combined with generated illustrations or a scene's animated
template treatment. Separately pinned frozen narration remains subject to its
unchanged exact script/scene/clock and asset checks; imports replace visuals
only after that validation and cannot modify the retained audio/captions.
The existing pipeline copies and
rehashes each visual into immutable project assets and uses its existing
`Media` scene decoder, SDR BT.709 checks, narration timing, captions, composition
and export. `allowHold` defaults false; setting it explicitly authorizes only the
existing mapped last-frame hold, not stretching speech or inventing actions.
No arbitrary filtergraph, URL, native capture or alternate video engine is added.
Native recordings must be separately authorized, acquired and reviewed before
being pinned here; the CLI still never records the user's desktop or microphone.

A `ReviewedVideoRequest` contains `schemaVersion:1`, a stable ASCII
`idempotencyKey` (1..128characters), and `request` with `prompt`, `constraints`
(`maxDurationSeconds:5..300`, `maxScenes:1..12`) and a non-null `plan` matching
`video-outline.schema.json`. `draft` is either null/omitted or a saved
`DraftSnapshot` from `draft-snapshot`; do not invent a binding to a legacy brief.
Full canonical JSON identity includes the saved draft revision/content hash.
Request identity uses the same `VideoRequest` hashing and validation as jobs.
See `schemas/file-production.schema.json` and the public record types in
`FileProduction.cs` / `FileDrafts.cs`. The schema covers commands, configuration,
persisted attempts/drafts/cancellation, public status and final manifests;
domain validation additionally enforces cross-file identity, clocks, hashes
and transaction invariants.

```powershell
video-production produce --request reviewed.json --production-config private-production.json
video-production status --state-root $env:LOCALAPPDATA\MediaStudio\video-files-v1 --attempt attempt-0123456789abcdef0123456789abcdef
video-production cancel --state-root $env:LOCALAPPDATA\MediaStudio\video-files-v1 --attempt attempt-0123456789abcdef0123456789abcdef
```

`produce` is **foreground**: execute, report, exit. Its stdout is NDJSON with
`schemaVersion`, `kind` (`accepted`, `progress`, `result`, `replay`) and `value`
(`FileAttemptStatus`). The accepted event is emitted only after the actual
producer holds exclusive capacity and its immutable request, attempt/index and
any draft admission are committed. A replay has no new accepted event; an
already-running replay reports running, not successful completion.
If stdout disconnects, a private `output-channel-failed.json` explicitly records
the delivery failure while production continues. No browser/relay authorization
is implied by a local directory, attempt ID or internal ownership token.
Private configuration and diagnostic messages must not become browser errors.

| Exit | Meaning |
|---|---|
| 0 | Completed, or exact replay/read result; inspect returned status |
| 1 | Invalid input/configuration or failed production; inspect typed code |
| 73 | Conflicting request/operation identity or stale revision |
| 74 | Exact replay of an interrupted attempt; no automatic retry |
| 75 | Busy/short transaction contention; no new work accepted or queued |
| 130 | Explicit observed cancellation |

`status`, `cancel` and draft commands resolve no renderer, speech or model tools.
Their successful read/transaction exits do not claim a film was produced.

## Ownership, completion and cancellation

A dedicated private root has an explicit `video-file-state.json` marker.
Legacy job/brief directories and unknown versions are refused, not migrated.
State comprises immutable reviewed `request.json`, atomic mutable `attempt.json`,
admission key indexes, bounded progress/status and final `result.json`.
Output paths are allocated beneath this configured root; choosing different
output directories cannot evade its **one active production** rule.

A short cross-process admission transaction replays identical existing keys,
refuses conflicting reuse, acquires root-wide exclusive production ownership
and then commits acceptance. The producer holds that active lock throughout
pipeline execution and terminal commit. Busy creates neither an attempt nor a
queue entry. Liveness requires exclusive ownership plus matching attempt token,
PID and process-start identity; PID existence alone is insufficient. A held but
unverified owner is not falsely marked live or interrupted.

`result.json` is the completion commit point. It binds version, attempt, token,
reviewed request and all artifact handles/hashes. Partial outputs are diagnostic,
never a published result. Status rechecks committed hashes. If a producer exits,
an exclusive reconciler marks abandoned nonterminal attempts **interrupted**,
unless a valid final manifest already committed completion. It never resumes or
reruns a stage. Terminal failures retain private diagnostics and typed public
codes. Pipeline implementations must not overwrite protocol control files.

`cancel` atomically commits an immutable, attempt/token/request-bound
`cancel.json`; the active owner observes it through its CancellationToken.
Existing owned subprocess-tree cancellation remains in force. A cancellation
processed before terminal commit prevents publication even if a backend ignores
its token. Completion and cancellation serialize under the admission transaction;
a late cancel leaves an already committed result unchanged. No arbitrary
process-kill interface is exposed. An unrelated backend cancellation is failure,
not an empty successful result.

## Revisioned draft transactions

New-format `FileDraft` files are independent of protected installed `StudioBrief`
storage. Start, read, edit, snapshot and fork use bounded cross-process
transactions and per-draft locks; state and operation receipts share one atomic
commit. No lock is held across an authoring/model call in this slice.

```powershell
video-production draft-start --state-root $env:LOCALAPPDATA\MediaStudio\video-files-v1 --request start.json
video-production draft-read --state-root $env:LOCALAPPDATA\MediaStudio\video-files-v1 --draft draft-0123456789abcdef0123456789abcdef
video-production draft-edit --state-root $env:LOCALAPPDATA\MediaStudio\video-files-v1 --request edit.json
video-production draft-snapshot --state-root $env:LOCALAPPDATA\MediaStudio\video-files-v1 --request snapshot.json
video-production draft-fork --state-root $env:LOCALAPPDATA\MediaStudio\video-files-v1 --request fork.json
```

`FileDraftStart` supplies version, stable operation ID and `content` (prompt,
audience, outcome, constraints and nullable storyboard). `FileDraftEdit` supplies
version, draft ID, saved revision, operation ID and exactly the typed payload
for `set-prompt`, `set-audience`, `set-outcome`, `set-duration-seconds`,
`set-scene-limit`, `replace-storyboard` or `replace-content`. The latter uses a
complete `content` payload to atomically replace the validated brief and optional
storyboard at one exact revision; no other payload may accompany it. Its omitted
property preserves legacy edit serialization and operation hashes.
String edits use `value`; numeric edits
use `number`; storyboard replacement uses `storyboard`. Numeric changes preserve
the existing storyboard/scripts and refuse incompatible bounds. A changed prompt
explicitly invalidates the storyboard rather than falsely retaining review.
Exact operation replay changes nothing; conflicting reuse/stale edits refuse.

`FileDraftSnapshotCommand` contains version, draft ID and exact saved revision.
Its output is a reviewed request, **not submission or production authorization**.
Submitting it through explicit `produce` validates the still-saved revision
while acquiring actual capacity. Busy/preflight/stale admission cannot falsely
mark a draft submitted. The durable accepted index is authoritative if a response
or the subsequent draft write is lost; reads/edits reconcile it before exposing
editable state. A submitted draft is immutable. `FileDraftFork` (version, ID,
revision, new operation ID) explicitly creates a separate review only after
terminal production; it does not retry or render. Old legacy submissions are
never converted into this format.

## Remaining integration gates

The installed Studio/MCP/optional generator facade keep their existing versioned
queue/owner behavior. They are **not silently switched** to these no-queue
contracts. Future native/MCP adapters must call the same file store/executor and
explicitly negotiate that behavioral change; the library already exposes it
without requiring CLI subprocesses. No Canvas relay, authoring-transport
replacement, Hybrid shell migration, stable package cutover, extension reload
or detached UI launch mechanism is included here.

An ordinary foreground CLI producer is verified independently across processes,
including crash/interruption, busy/replay, explicit cancel, owned child/descendant
cleanup and concurrent draft edits. Surviving panel/conversation/extension
teardown still requires the separately approved supported-launcher acceptance;
`detached` alone is not proof. Tests use labeled isolated pipelines/PCM fixtures,
not actual synthesis/rendering or human prosody/accessibility acceptance.
