# Optional animated mechanisms

> Reference for scene motion. Related: [illustrations](illustrations.md), [contracts](contracts.md),
> [architecture](architecture.md).

For Video or Both, an existing Diagram can opt into `MotionVisual` through the
standard framework-generated `set_scene_motion` AIFunction. `clear_scene_motion`
removes it. These focused tools bind the current host-owned transaction/version
just like existing edits; there is no custom tool JSON parser or schema rewrite.
An audio-only project does not receive these tools.

Try a targeted storyboard request:

> Keep the narration and independent audio unchanged. Use a Diagram with an
> animated mechanism to show warming water, moist air rising and cooling, liquid
> cloud droplets forming and combining, and a larger drop falling as rain.
> Use arrows for invisible vapor-carrying air, not visible vapor droplets.
> Keep entity identity stable and explain each change in stage captions.

The model proposes bounded **data**, never JavaScript, arbitrary SVG, URLs,
selectors or assets. Supported shapes are circle, box, cloud, drop, sun and arrow,
with four fixed color tones. Use 1..20 unique labeled entities, 2..8 ordered
poses per entity (explicit 0 and 100 percent), and 1..6 explanatory stages
starting at zero. An all-static or invisible scene is rejected. Labels, sizes,
positions, finite opacity and compatibility are checked in both .NET and Node.
Motion cannot coexist with another preset, an illustration/imported visual, or
legacy template effects. Explicit illustration/layout edits remove incompatible
motion; advisory Decisions cannot silently overwrite it.

The storyboard offers a read-only Play/Pause and normalized progress slider.
Remove animation, Undo/Redo and saved reopening use existing draft operations.
Playing or applying an edit does not authorize generation.

## Rendering and timing

Only opted-in scenes use the pinned GSAP3.14.2/HyperFrames0.8.33 browser runtime.
The existing plain renderer remains unchanged for every other scene. Runtime
bytes are hash-checked before execution, network requests are blocked, and only
code-owned timelines execute. Export seeks the actual paused HyperFrames player
at 30fps and checks every observed entity pose against the authored interpolation.
Reverse and repeated seeks are deterministic.

On a frame-worker failure, `renderer-failure.json` beside the captured frames
retains the current scene/frame, observed page/Chromium target crashes (including
the reported status/error code), disconnect timing, and bounded Playwright error
text with available Chromium stderr. Missing crash events remain unknown, not an
invented cause. Cleanup errors do not erase the original rendering failure, and
diagnostic capture never retries the browser or starts another production.

Creative percentages resolve against the **actual measured narration clock**;
100 percent is the last exported frame. The scene retains its full sample-derived
duration. At least 60 frames/two seconds are required, with no automatic narration
truncation or fabricated hold. This is a schematic, not a physical simulation or
measured evidence. Independent audio remains self-contained and unchanged.

Source/frozen exports include the runtime, hashes and original licenses.
GSAP is **not MIT**; preserve its Standard license obligations. HyperFrames is
Apache-2.0. See `workers/browser/runtime/NOTICE.txt`, `origins.json`, and the
root third-party notices.

## Verification boundaries

`fixtures/rain-motion.json` is original inert test data, not a model-generated
production or a replacement for a user's saved project. The visual executable
tests check C# contracts and interpolation; the Node tests compare those poses
and inspect real native initial/middle/final, backwards and repeated seeks.
The native-function registry must exactly match all advertised draft functions,
including both motion tools. The transport rejects unregistered calls and
unfinished acknowledgments instead of reporting a successful no-op.
The WPF proof invokes the actual motion AIFunction through the AG-UI transport
and UIAgent into a plain Diagram with an incompatible pending illustration,
then checks the acknowledged animation, read-only play/pause/seek, removal,
Undo/Redo and save/reopen. It does not seed Motion directly into the UI as a
substitute for testing authoring.
The frozen integration additionally renders final/preview movies and rerenders
the exact exported source/runtime closure using synthetic test tones, without
speech, model requests or network acquisition.

New live authoring/production quality is not inferred from these deterministic
checks. Existing accepted media stays untouched; generating a new project still
requires explicit approval.

The legacy maximum-text browser case overflows with the retained production
font, both at the preceding committed baseline and with this change. Its normal
layout/seek cases and the new motion cases pass; the pre-existing typography
limit is not silently relaxed to make the gate green.
