# Product tour

Media Studio turns a brief (typed, spoken, or grounded in documents and links) into a narrated explainer
video, an audio piece, or both. You review and edit a storyboard before anything is produced, and every
production keeps a record of exactly what was generated and which models were used.

New here? Start with [getting started (local)](getting-started-local.md).

## The app

The desktop app (`MediaStudio.App`, WPF + Blazor Hybrid, Windows) has four places in the navigation rail,
plus **New project** and **Settings**:

| Place | What you do there |
| --- | --- |
| **Create** | Write or dictate a brief, choose the output (Video, Audio or Both), and **Add sources** (files or links). Sending creates a draft, not a production. |
| **Storyboard** | Review the scenes (video) or script parts (audio). Reorder, edit narration, tune timing, ask the author for changes in the conversation, and use **Undo** / **Redo**. **Generate…** opens the review. |
| **Jobs** | Follow running and finished productions. Jobs survive navigation and closing the window. |
| **Artifacts** | Play finished videos and audio, open their files, and **Generate cover** for the library. |

**New project** starts a blank project and keeps your existing projects, drafts and jobs. `--new-project`
does the same at launch ([app options](cli.md#app-options)).

### Settings

**Settings → Setup** runs the first-run wizard again: pick local or Azure, choose model tiers, allow or block
cloud, sign in to GitHub Copilot, and **Test** every role. The same choices are available from the command line
with `configure` ([configuration](configuration.md), [CLI](cli.md#setup-commands)).

### Writing the storyboard

The author is a language model chosen by [routing](routing.md). It edits the draft through tools (add a scene,
update narration, and so on), so every change appears as a change card you can undo. Authoring can also run
through the [GitHub Copilot agent](copilot.md). Nothing is produced until you approve **Generate…**.

### Sources

Documents are optional. When you add them, Media Studio extracts text locally by default, cites sources in the
storyboard, and asks before uploading anything (**Upload document**). See [documents](documents.md).

### Voice

The microphone dictates into the prompt (push to talk). It transcribes locally with Whisper unless you chose
cloud speech, and it never sends the request for you. See [speech](speech.md#live-dictation-push-to-talk).

## Producing a video

**Generate…** shows a review: output, target duration, providers and the saved revision. After you confirm,
the job:

1. narrates each scene (Kokoro locally, or Azure Speech) and measures the real audio length;
2. draws illustrations as sanitized SVG ([illustrations](illustrations.md)) and lays out each scene;
3. renders frames in headless Chromium and encodes the video with FFmpeg;
4. writes captions, a manifest, and receipts that record which models made the plan and the illustrations.

The same pipeline runs from the `video-production` CLI ([CLI](cli.md#production-commands)), which is the
recommended way to render today. The app derives its production configuration from your settings
(`configure production` shows it); a full in-app render hasn't been verified live yet
([known limitations](troubleshooting.md#known-limitations)).

How the pieces fit together is described in [architecture](architecture.md); file formats are in
[contracts](contracts.md).

## Local first

Everything above runs on your PC with Ollama, Kokoro, Whisper and the optional Julia decision model. Azure AI
Foundry and GitHub Copilot are opt-in, can be turned off with one switch (**Allow cloud**), and each upload
asks first. See [models](models.md) for what runs where.

## Design references

These diagrams were drawn as the design target for the app. They show the intended structure and journey, not a
pixel-accurate picture of the current screens (for example, "Projects" is labeled **Artifacts** in the app). The
Aspire AppHost, MCP host and Jellyfin companion they show are not part of this repository.

- [User journey](media-studio-user-journey-v2.svg)
- [Library-first target](media-library-first-target-v2.svg)
- [Projects and composition](media-projects-and-composition-v2.svg)
