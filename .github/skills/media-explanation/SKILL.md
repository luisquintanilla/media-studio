---
name: media-explanation
description: Plan and revise original explanatory audio, video, or paired productions. Use when turning a selected brief and evidence into a self-contained spoken script, a teaching-aware storyboard, or a revision-bound authoring proposal. Separate shared teaching intent from medium-specific expression and use only the supplied capability contract.
metadata:
  guidance-version: "1.0.0"
---

# Explain an idea, not just a sequence of facts

Produce reviewable authoring data for the requested medium. This guidance is
not permission to run a tool, inspect files, browse, provision a model, spend
money, adopt a transcript, generate media, or publish. Follow the actual host's
tool, evidence, approval, and output-schema constraints.

The application must supply this reviewed instruction explicitly to its bounded
authoring agent. A repository skill or a package asset does not automatically
make it available at runtime or expand the agent's ambient access.

## Establish the learning intent

Before polishing the script, establish:

- Who is listening or watching, and what relevant knowledge can be assumed?
- What should they be able to explain, distinguish, or predict afterward?
- Which misconception or missing relationship prevents that understanding now?
- What concrete situation will make the question worth answering?
- What mechanism, comparison, or explanatory reveal changes their mental model?
- Which evidence supports the explanation, and where does the explanation stop?

Keep the scope small enough to explain a meaningful relationship. Do not equate
a topic list, a collection of attractive images, or a confident voice with a
learning objective. Use a structure appropriate to the content; do not force a
fixed scene count, duration, dramatic hook, or three-style blend.

## Use a shared factual spine, not a mandatory shared script

Audio and video may share their brief, audience, teaching objective, selected
sources, supported claims, terminology, and rights/provenance. Their best scripts
may differ. Retain the same factual meaning while adapting how it is explained.

For each segment or scene, make its teaching purpose legible: what the audience
knows before it, what changes, and why that change matters. Use the existing
typed fields supplied by the host. This checklist is not a new wire schema;
do not invent unsupported fields, tools, renderer presets, or executable code.

Preserve project, source, segment, scene, and base-revision identities as required
by the actual contract. Propose changes against the supplied revision. A late
proposal does not authorize overwriting a newer edit.

## Make audio complete without pictures

- Name the entities and relationships the listener needs. Explain essential
  spatial or numerical comparisons rather than referring to an unseen diagram.
- Replace unsupported references such as "this one," "over here," or "as shown"
  with a concise, unambiguous spoken description.
- Use signposts when changing perspective, comparing alternatives, or returning
  to a previous idea. Avoid turning a table into a long unstructured recital.
- Give a difficult idea room to land. Suggest deliberate pauses or segment
  boundaries only through supported authoring controls.
- Distinguish intended pacing or target duration from measured audio timing.
  Never invent sample counts, word alignment, timestamps, or actual duration.
- Keep terminology, units, comparison direction, and pronunciation intentions
  consistent. Let configured voice/language and the actual synthesis provider
  determine what is supported.
- Do not assume multiple speakers, music, sound effects, chapters, or automatic
  ducking. They require explicit supported capabilities and appropriate rights.

Audio production is not microphone transcription, a stray narration take, or a
video with its pictures discarded. A self-contained audio script must be
reviewed as such. Reusing an approved take is appropriate only when its text
and complete synthesis identity match the requested production.

## Make video explain through visible relationships

- Give important entities stable names and visual identities. Reinforce semantic
  color with text, shape, or position; color alone cannot carry the explanation.
- Introduce only what the current reasoning step needs. Preserve context instead
  of redrawing unrelated decorative cards at each sentence.
- Use movement or a reveal to show a meaningful change. Make important invariants
  visible, and hold long enough for the audience to inspect the result.
- Co-design narration and visuals. They should support the same reasoning step
  rather than compete with dense paragraphs or describe unrelated action.
- For a comparison, preserve its basis: units, scale, conditions, labels, and
  metric direction. Do not silently crop away relevant contrary evidence.
- Suggest an opportunity to predict or notice a relationship when useful; do
  not force an interactive pause into every scene.
- Use only the renderer capabilities and eligible treatments supplied by C#.
  Explain the desired teaching effect without inventing JavaScript, Python,
  C#, arbitrary SVG behavior, or a new rendering engine.

Current diagrams, code reveals, or before/after treatments are not a general
mathematical animation or illustrated whiteboard system. If the requested
explanation depends on an unavailable effect, state the limitation and propose
a genuinely adequate supported alternative for review. Do not quietly replace
the requested mechanism with decorative slides and call it equivalent.

## Keep evidence and illustration distinct

Identify whether a scene or spoken assertion describes:

- An actual application capture or measured result.
- A selected source's reported result, with its conditions and qualifications.
- A schematic reconstruction that explains a mechanism.
- An analogy or explicitly hypothetical worked example.

Never present generated footage as an experiment or a diagram as a measurement.
Do not fabricate benchmark values, citations, captured states, model behavior,
rights, or evidence that a selected source does not provide.

Use the source references actually supplied by the application. Preserve figure
and caption identity, table headers, model groups, units, and metric direction.
Extracted text or a figure caption alone does not establish that the agent
inspected or understood the original image. Report that limitation explicitly.

An analogy must name the relationship it preserves and the point where it
breaks. A concise explanation may omit detail, but it cannot silently change
the claim. Separate a reported result from an interpretation or prediction.

## Apply judgment without taking authority

The authoring agent proposes content and teaching intent. Deterministic C#
enforces eligible capabilities, identities, validation, budgets, and policy.
Configured decision providers may make their explicitly assigned bounded
judgments; this skill neither adds a new judge nor overrides their result.

Applying authoring edits does not approve production. Production approval
binds an exact saved revision, medium-specific inputs, requested output set,
voice/providers, and resource bounds. A new draft or an additional medium
requires the appropriate review; it is not covered by a vague earlier approval.

Do not infer that a complete render proves factual accuracy, good teaching,
intelligible speech, authorization to publish, or human comprehension.
Mechanical and model-based checks are evidence, not substitutes for content
review and the actual user's release decision.

## Original worked authoring patterns

These are teaching examples, not claims about completed application behavior.
They do not require new application features.

### Mechanism: why an old approval cannot authorize a changed draft

**Learning change:** distinguish approval of a snapshot from approval of all
future edits to a project.

**Audio:** establish that draft A was reviewed and approved. Describe a later
edit producing draft B. Explain that checking B against A's approval fails
because the reviewed content identity changed, even though the project title
is the same.

**Video:** keep one project label stable, show two distinctly labeled revision
snapshots, and connect the approval only to A. Reveal the failed identity check
for B through an eligible diagram or code treatment.

**Weak alternative:** a decorative approval stamp followed by an unrelated
screen, with narration claiming "the system is safe." It does not expose the
identity relationship that explains the result.

### Analogy: a script is a recipe, not the finished recording

**Learning change:** distinguish authored instructions from the measured artifact
produced by following them.

**Audio:** introduce the recipe analogy, then return to the literal system:
text is synthesized into samples; production measures those samples before
making timing claims. The analogy does not explain model quality or timing
accuracy by itself.

**Video:** preserve the script identity while showing its relationship to the
returned take and its measured sample clock, using only eligible primitives.
Do not simulate a waveform measurement and present it as actual synthesis.

**Weak alternative:** a predicted reading time labeled as the recording's
measured duration.

### Evidence comparison: explain a result without claiming universal superiority

**Learning change:** read a selected result in its stated setting rather than
turning one favorable comparison into a universal conclusion.

**Audio:** name the supplied source and comparison, explain the metric direction
and relevant conditions, then state both the supported result and its limitation.
Include a supplied contrary result when it changes the conclusion.

**Video:** retain the original table/figure reference and necessary headers,
highlight the relevant supplied comparison, then reveal its qualification.
If the source image is unavailable, clearly label a supported reconstruction
instead of claiming visual inspection.

**Weak alternative:** "this is better in every way" over invented benchmark bars.
No numerical results are supplied by this pattern; use actual selected evidence.

## Inspiration and originality

General teaching principles were informed by the public materials below:

- [3Blue1Brown: About](https://www.3blue1brown.com/about/)
- [Summer of Math Exposition: clarity, motivation, novelty and memorability](https://www.3blue1brown.com/blog/some1/)
- [After Skool: About](https://www.afterskool.net/about)
- [Two Minute Papers: research communication](https://users.cg.tuwien.ac.at/zsolnai/gfx/two-minute-papers-awesome-research-for-everyone/)

These are research references, not artistic presets or authorization to reuse
scripts, frames, illustrations, voices, music, branding, or catchphrases.
Create an original explanation and visual identity appropriate to this audience.
Do not imitate a named creator or assume a two-minute runtime from a channel name.
