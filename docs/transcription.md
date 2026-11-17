# Local transcript learning

> Reference for the `video-production` transcript workflow. For choosing speech providers and the
> `audio-transcriber` CLI, start with [speech](speech.md) and [CLI](cli.md).

`Transcription.Learn` observes speech by running the `audio-transcriber` CLI from this repository
(`src/audio-transcriber`, local Whisper). It does not implement an ASR
backend, invent a provider protocol, call a remote service, provision models,
or edit a project, scene, script, narration, or caption.

## Authored script captions versus recorded speech

New deployments can explicitly set generation configuration `narrationPolicy`
(copied into new projects) to
`{"schemaVersion":1,"mode":"script-phrases-v1","analyzeSpeech":false}`.
Omitting the policy retains the legacy whole-take/strict ASR path and old JSON
fingerprints. No installed configuration is changed by a source update.

In this opt-in path the authored script stays canonical, including Unicode,
punctuation and code-like tokens. Deterministic UTF16 source spans are synthesized
once each through the existing backend with the same provider/model/request
identity. Original phrase WAVs and receipts are frozen. Their actual mono PCM16
is concatenated unchanged; the configured real-silence end guard is applied
**once to the scene**, not to each phrase. Captions use returned phrase sample
boundaries, optionally grouping adjacent short phrases. They never distribute
a whole-scene duration, invent word timing, trim pauses or subdivide an overlong
returned take. Two lines,42columns,1..6seconds and20characters/second remain
mandatory. An unmet measured policy retains takes, master and an explicit
`caption-policy-failure.json`; it fails rather than silently retrying synthesis.

The hashed `scriptCaptionAssetId` receipt binds canonical source spans, synthesis
inputs, provider metadata identities, phrase hashes/offsets, original composition,
final master, scene end guard and exact cues. Validation checks actual PCM
byte-for-byte as well as assets/receipts. All evidence is part of frozen export
and uses the shared preview/final/SRT/VTT projection. Evidence remains
`TakeWindow`: supplied text and measured windows are not proof of pronunciation,
word alignment, natural prosody or human accessibility acceptance.

ASR is **not required** for script captions. Unused transcriber dependencies are
not readiness prerequisites. Optional `analyzeSpeech:true` requires explicit
pinned ASR configuration and runs a separate, non-adopting observation.
`analyze-speech --project FILE --scene ID --transcriber-config FILE` invokes the
same advisory path explicitly. Missing analysis receipt means **not run**, not
passed. Receipts say `findings`, `observed-agreement` (not acoustic verification),
or `failed` with an error code. Findings/failure never replace script or cues,
set `TranscriptPending`, or require caption adoption. Cancellation still cancels
the operation; it is not disguised as failed QA.

Recorded/unknown/imported audio retains explicit sample evidence and the strict
`transcribe`/`adopt-transcript` workflow below. Calling strict `transcribe` on an
authored script-caption take refuses before changing the project.
Listening for phrase-boundary prosody and a real end-to-end rendering acceptance
remain separate human/model-run gates; deterministic fixtures do not satisfy them.

## Explicit prerequisites

- A published `audio-transcriber` build (`dotnet publish src/audio-transcriber -c Release`).
  Configure its executable SHA-256 and every file listed by
  `Transcription.RequiredDependencyPaths`, including all four Windows x64
  Whisper native libraries. Pin these from a trusted/reviewed artifact;
  computing hashes of arbitrary files does not establish their provenance.
- Local FFmpeg, configured with its executable SHA-256.
- The **already present** default model cache file
  `%LOCALAPPDATA%\AudioTranscriber\model-cache\ggml-base.bin`.
  Only the transcriber's fixed-English `openai/whisper-base` profile is
  supported. Its artifact identity is
  `sandrohanea/whisper.net@v5/classic/ggml-base.bin`, SHA-256
  `60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe`.
  The transcriber uses Whisper.net/Whisper.net.Runtime 1.9.0.

Provisioning is a separate authorized operation, not a transcription option.
The adapter rejects missing/corrupt model files **before** invoking the CLI,
whose own missing-cache behavior would otherwise download the model. Alternate
model/cache paths are rejected because the inspected CLI has no such option.
The verified model, executable, dependencies, FFmpeg, and source master are held
read-only through the child process lifetime on Windows. No model download,
network request, upload, push, or publication is performed by this unit.

## Typed integration

`Tools` is unchanged. Load a separate `TranscriberConfig` using `Json.Read`.
Its positional fields are:

```csharp
TranscriberConfig(
    string Executable, string ExecutableSha256,
    ArtifactHash[] Dependencies, string ModelPath, string ModelSha256,
    string Ffmpeg, string FfmpegSha256,
    int TimeoutSeconds = 600, int MaxOutputBytes = 4_000_000,
    int MaxAudioSeconds = 1800, string? Profile = null)
```

Dependency paths are portable, deployment-relative paths; use `Files.Under`.
All configured hashes must be lowercase SHA-256. Do not commit machine-specific
configuration. The model pin is not user-selectable.

```csharp
TranscriptLearning result = await Transcription.Learn(
    masterWav, intendedSpokenScript, descriptiveAccessibilityText,
    newOutputDirectory, transcriberConfig, cancellationToken);
```

The caller must choose a nonexistent output directory. Existing evidence is
never overwritten. The master must satisfy the existing `Wav.Inspect` mono
PCM16 WAV contract; the default duration limit is 30 minutes. Timeout and
captured-output bounds apply independently to each child process.

The adapter calls `Processes.Run` (no shell, argument-list API, sanitized
environment, bounded output, cancellation/tree cleanup). FFmpeg makes a
duration-preserving **16 kHz, mono, PCM16 derivative**, with metadata removed.
The original master is not rewritten or normalized. Resampler duration drift
greater than one derivative sample is rejected.

The exact invocation is:

```text
audio-transcriber.exe transcribe --input <derivative.wav> --format json
```

The accepted JSON is a one-element array with exactly `source`, `model`,
`provider`, and `segments`; segments contain exactly `start`, `end`, and `text`.
Times use `HH:MM:SS.mmm`. Missing/duplicate/unknown fields, wrong types, different
source basename, provider/model mismatches, no speech, overlaps, reversal, or
timestamps outside either WAV are errors. No fallback accepts script text as STT.

### Explicit digital-silence classification

The default remains strict. A transcriber build that supports the
**explicit** private-config `"profile": "verified-digital-silence-v1"` can opt in
(the `audio-transcriber` CLI in this repository does not accept `--profile` yet).
Unknown profiles fail; a missing/null profile never enables classification.
The adapter adds only this fixed contract:

```text
--profile verified-digital-silence-v1 --input-sha256 <actual derivative SHA256> --diagnostics <new transcriber-profile.json>
```

The same English model/decoder runs unchanged. Only a whole literal
`[BLANK_AUDIO]` after outer whitespace trim may be classified non-speech:
onset must be inside the actual derivative, and **every intersecting PCM16
sample must equal digital zero**, not a threshold or a no-speech probability.
One nonzero sample, unknown marker, substring, out-of-file onset or spoken EOF
overrun fails. No speech is clipped, padded, shifted or replaced from a script.
Marker-only output is not success.

The required versioned sidecar retains every raw text/range/classification/
reason/intersection and every effective speech segment, including the original
out-of-file marker end. On rejection it is diagnostic evidence, not accepted
captions. Legacy JSON stdout contains only unchanged effective speech on
successful opt-in; rejected invocations retain empty stdout.

The video adapter independently verifies sidecar identity/model/input hash/
sample clock, raw-to-effective coverage/text/ticks and actual intersecting zero
PCM bytes. `TranscriptLearning.Profile` and `ProfileEvidenceSha256` pin the
sidecar as a separate project asset. Adoption, delivery binding, export and
relocated offline resolution revalidate it from frozen evidence without
running ASR. Missing or stale evidence fails closed. Source/master and
derivative clocks remain explicitly distinct and hash-bound.

This narrowly addresses a measured 4s authored end-card case: raw marker22
85.680->95.680s intersects exact zeros at derivative samples1370880..1387733;
the21 prior spoken segments remain unchanged, ending85.520s inside the actual
86.7333125s derivative. It is **not** ordinary default-source acceptance.
The1s-hold and original135ms spoken-overrun cases still reject. Lexical
differences (including PIN/pinned and Onks/ONNX) remain visible for review.

A maintainer exact-fixture regression (not included in this repository) covers this
profile; it is not a general-purpose transcription command. It takes a frozen CLI, explicit provider
config, the approved one-scene4s selectedmaster project and the original
unpadded negative project. It pins those actual master/derivative/sidecar
hashes, checks rejected empty stdout and pending output, explicitly adopts
machine evidence with unresolved human review, rejects missing profile proof,
and compares relocated model-free captions. Fixtures and provider/model
deployments are private external prerequisites; the script provisions none.

## Evidence and separate meanings

The output directory retains:

- `stt-16khz.wav`: recognition derivative, never the master;
- `transcriber-result.json`: unmodified captured CLI JSON;
- `transcriber-stderr.txt`: local diagnostic stream;
- `transcriber-profile.json`: required raw/effective evidence only when the
  explicit digital-silence profile is selected;
- `transcript.json`: validated `TranscriptLearning`, written only on success.

The report binds the source basename and original WAV SHA-256/sample clock to
the derivative hash/clock, exact CLI/native dependency pins, model artifact/hash,
FFmpeg hash, and raw JSON hash. Local transcripts/diagnostics may contain
sensitive speech; keep these artifacts private and review before sharing.

`IntendedScript`, `DescriptiveAccessibilityText`, and `ObservedText` are separate
fields. Descriptive visual accessibility text is not assumed to have been spoken.
`TranscriptDiscrepancy` compares only the intended script with observed speech:
case and whitespace are normalized, punctuation remains significant. A mismatch
requires review; equality is not an accuracy, semantic-equivalence, or WER claim.
Nothing overwrites the intended script.

Segments retain actual CLI millisecond endpoints and `Evidence.AsrSegment`.
Mapping to original master samples uses exact integers: start is
`floor(milliseconds * masterRate / 1000)`, end is
`ceiling(milliseconds * masterRate / 1000)`. Consecutive segments sharing an
endpoint reuse the previous mapped end, avoiding a rounding-induced one-sample
overlap at rates such as 22050 Hz. The CLI renderer truncates its
upstream `TimeSpan` to milliseconds, so the adapter does not claim finer original
timestamp precision. No word timestamps/alignment or forced alignment are
inferred. Converting to publishable captions remains an explicit parent/user
step subject to caption policy and review.

## Project/CLI integration

```powershell
dotnet run --project src\VideoProduction.Cli -- transcribe --project demo\project.json --scene code --transcriber-config private-transcriber.json --source .
dotnet run --project src\VideoProduction.Cli -- adopt-transcript --project demo\project.json --scene code --discrepancy-decision "Retain observed speech; inspect differences before final review" --caption-exception "Retain coarse ASR segments; review line count/pace" --source .
```

`Production.Transcribe` consumes the scene's frozen narration master and its
intended `script` and optional `visualDescription`. Configuration is separate;
the original `Tools` constructor/schema is unchanged. Before inference, the
project is persisted with `narration.transcriptPending=true`, invalidating old
approvals/candidates. Even a failed transcription blocks resolve/render/release;
retry transcription or explicitly import replacement narration. Failure never
falls back to script-derived captions.

On success, assets under `assets/transcripts/<scene>-<unique>/` retain the hashed
derivative, raw provider JSON and report; `transcriptAssetId` references the report.
Private stderr remains local and is not a project asset/export. The CLI returns
the relative report path, actual observed text and explicit discrepancy.

After inspecting the actual segments, `adopt-transcript` converts their exact
source-sample intervals to acoustic cues; it wraps words without inventing new
timing boundaries. A discrepancy requires an explicit editorial disposition.
Noncompliant coarse segment captions require an explicit policy exception, not
silent fake word alignment. A hashed adoption decision binds report/master hashes
and cues; pending state is cleared only after full validation. The intended script
and visual description remain unchanged. Adoption is **not** human release review.
Subsequent script/master/description/cue changes invalidate the evidence.

## Full selected delivery master

An optional project-level `deliveryCaptions` binding makes a **full selected
delivery** master authoritative instead of requiring every intermediate fragment
to pass recognition independently. Intermediate rejections remain evidence;
they are not relabeled successful speech. The default per-scene path is unchanged.

Decode the actual checked candidate's complete audio, including FFmpeg's AAC
skip-sample/edit-list handling:

```powershell
ffmpeg -v error -nostdin -n -i demo\output\candidate\HASH\movie.mp4 -map 0:a:0 -vn -ar 48000 -ac 1 -c:a pcm_s16le -map_metadata -1 selected-delivery-master.wav
```

Import that **untrimmed** WAV into a separate one-scene project with the actual
full intended script/description, then `transcribe` and explicitly `adopt-transcript`.
`resolve` on this one-scene project writes acoustic SRT/VTT without rendering.
It does not confer release approval.

```powershell
dotnet run --project src\VideoProduction.Cli -- bind-delivery-transcript --project demo\project.json --movie demo\output\candidate\HASH\movie.mp4 --transcript-project wholemaster\project.json --source .
dotnet run --project src\VideoProduction.Cli -- render --project demo\project.json --source .
```

Binding requires the main project's exact current checked candidate provenance,
one full unselected 48kHz mono master, successful adopted STT, and actual raw
PCM16 equality between that WAV and the candidate's decoded audio. It copies
the master/report/derivative/raw JSON/adoption as hashed nonsecret main-project
assets. It never asserts that lossy AAC samples equal the original pre-encode
source waveform. Only the final documented AAC tail of at most1024samples may
extend beyond the visual timeline; no speech cue can enter that tail.

The binding pins ordered source scenes/assets/take records/audio selections/
timing/pauses/effects, resolved frame/sample clocks and offsets, actual movie/
decoded PCM and decoder executable hashes. Any changed composition/clock/
selection/evidence invalidates it. Every delivered rerender decodes its actual
AAC and checks the bound PCM/sample count with the pinned decoder; no silent
caption shifting/resampling. Rebind a newly transcribed selectedmaster after
intentional changes, explicitly removing the stale binding before making a new
candidate. Relocated frozen rerender uses the same binding and no STT/TTS.

A maintainer delivery-transcript regression (not included in this repository) exercises real candidate decode -> actual
wholemaster STT -> explicit adoption/binding -> unchanged movie -> relocated
source rerender and exact canonical captions. It also rejects changed composition,
missing report, stale timeline clock and changed cue text. Synthetic fixture
speech remains labeled synthetic. Human review remains unresolved throughout.

The maintainer integrated fixture runner (not included in this repository) accepts explicit
config/master/source-project/script paths; it exercises the real parent CLI,
pending/discrepancy gates, actual candidate, source export and relocated frozen
rerender. It labels synthetic imported speech honestly. No runtime/provider is
needed during that frozen rerender.

Static callable tests in `TranscriptionTests`:

- `ContractAndDiscrepancy()`: strict schema, duplicate/unknown fields, identity,
  timestamps, ordering/overlap, duration, no-speech, exact clock mapping, and
  discrepancy semantics; returns assertion count.
- `PreflightControls(config, scratchRoot, ct)`: bad profile/path/bounds,
  missing/changed executable, missing/duplicate/changed dependency pins, and
  changed FFmpeg pin; confirms no output/inference begins.
- `RealTtsAndImportedWav(config, kokoroConfig, scratchRoot, ct)`: local technical
  Kokoro TTS through actual STT, then an explicitly **synthetic imported WAV**
  through `NarrationImport` and STT. It asserts actual segments, identity/hash
  preservation, JSON roundtrip, independent text fields, and a deliberately
  mismatched imported script. It is not a human-speaker accuracy benchmark.

The scratch integration runner and its machine-specific pins live only under
ignored `artifacts\transcription-*`; no external repository was edited or built
by this unit. Deterministic controls passed (25 contract/discrepancy assertions,
11 preflight controls). The original published transcriber rejected the
unpadded technical fixture with `Transcription segment 3 ... ends after the
audio duration.` That is an upstream artifact failure, not an accepted transcript.
An exploratory, honestly labeled source fixture with an explicitly authored
one-second trailing pause produced three actual segments for both technical
and synthetic-imported speech using the same artifact/model. That distinct
fixture does **not** fix or hide the ordinary unpadded-source failure. The
adapter never pads input or silently clamps out-of-bounds results.

The isolated transcriber investigation established that native Whisper predicts
segment timestamps without an EOF cap: the 12.425-second source's last predicted
segment ends at 12.560 seconds (135ms overrun). Native10ms units/model20ms token
quantum do not justify treating that as rounding. The diagnostic artifact retains
strict rejection and unchanged successful JSON; there is no accepted clipping,
padding or normalization policy. The source is a negative regression, not a
fixed/accepted recording. Authored-pause positives are separate real source content.
Ordinary learning takes may independently hit this model limitation and will fail
explicitly. A supported positive does not establish universal input acceptance.
Missing/corrupt model cache mutation was not
performed against the shared real cache; its preflight checks exist, but the
successful fixture uses the already present, hash-verified approved model.
