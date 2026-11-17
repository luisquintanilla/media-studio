---
name: media-studio-make-video
description: >-
  Help a user make a narrated explainer video with Media Studio: check the machine is ready, render the bundled demo
  end to end with the video-production CLI, draft a brief and script for the user's own topic from the
  media-explanation guidance, start the desktop app for authoring, and check a rendered movie. Use when asked to
  "make a video", "render my first video", "render the demo", "create an explainer about <topic>", "write a script
  for a video", "turn my notes into a video", "produce an MP4", or "check a rendered movie".
---

# Make a video

Media Studio turns a brief into a storyboard, narration, illustrations and a captioned MP4. Two paths work today:

| Path | Who drives | Status |
|---|---|---|
| **A. Render the bundled demo** with the `video-production` CLI | you (the agent), with the user's approval | documented, tested path |
| **B. The user's own topic** in the desktop app | the user; you prepare the brief and start the app | in-app render not verified live yet |

User guide: [getting started (local), step 5](../../../docs/getting-started-local.md#5-render-your-first-video).

## Human gates: never cross these yourself

* **`approve`** records that a named person approved the production graph. Ask the user before you run it and pass
  **their** name with `--by`. Never invent a reviewer and never approve on their behalf without asking.
* **Generate** in the app, accepting or rejecting agent edits, and release review are the user's actions.
* **Cloud**: do not turn on `privacy.allowCloud`, sign in to anything or upload documents unless the user asks.
  Path A is fully local.

## 0. Check the machine

```powershell
dotnet run --project src/VideoProduction.Cli -- configure test          # every local row should be ✅
dotnet run --project src/VideoProduction.Cli -- configure production    # FFmpeg, Node, browser worker, Kokoro, font
```

A ❌ row prints its fix. Missing pieces usually mean setup has not run: use skill `media-studio-setup-local`
(`Install-Prerequisites.ps1`, then `Setup-LocalModels.ps1`). Both install software or download gigabytes, so
confirm with the user before running them.

## A. Render the bundled demo (local)

1. Write `artifacts\local-tools.json` (`artifacts\` is ignored by git):

   ```json
   {
     "sourceRoot": ".",
     "node": "node",
     "ffmpeg": "ffmpeg",
     "ffprobe": "ffprobe",
     "speech": { "provider": "kokoro", "voice": "af_heart", "language": "en-us", "speed": 1, "quality": "quantized" }
   }
   ```

2. Run the stages from the repository root. Each prints JSON; stop and report on the first non-zero exit.

   ```powershell
   $cli = 'src/VideoProduction.Cli'
   $tools = 'artifacts\local-tools.json'
   dotnet run --project $cli -- create-demo --directory artifacts\demo --config $tools
   dotnet run --project $cli -- narrate --project artifacts\demo\project.json --config $tools
   dotnet run --project $cli -- validate --project artifacts\demo\project.json --config $tools
   dotnet run --project $cli -- plan --project artifacts\demo\project.json --config $tools
   ```

3. **Ask the user** to approve the bundled demo and for the name to record, then:

   ```powershell
   dotnet run --project $cli -- approve --project artifacts\demo\project.json --scope trusted-bundled-fixture-only --by "<user's name>" --config $tools
   dotnet run --project $cli -- run --project artifacts\demo\project.json --config $tools
   dotnet run --project $cli -- render --project artifacts\demo\project.json --profile preview --config $tools
   ```

4. `render` prints the movie path. Check it against the resolved timeline and give the user the path:

   ```powershell
   dotnet run --project $cli -- check --project artifacts\demo\project.json --movie <path-from-render> --config $tools
   ```

`--profile preview` is for a quick look; `candidate` (the default) produces a release candidate that still needs
human review.

## B. The user's own topic

1. **Draft the brief and script.** Read [`media-explanation`](../../../.github/skills/media-explanation/SKILL.md) and follow it: learning
   intent, audience, one idea per scene, a self-contained spoken script. Ground every claim in the user's notes or
   documents; do not invent facts. Save the draft under `artifacts\` (for example `artifacts\briefs\<topic>.md`) and
   show it to the user. `media-explanation` is read-only for you: the app embeds and hash-checks it, so never edit it.
2. **Start the app** with `.\Start-MediaStudio.ps1` and tell the user to create a project, paste the brief, add their
   notes or PDF and ask for a storyboard. The app's authoring agent proposes typed edits the user accepts or rejects.
3. **Generate** is the user's click. If it reports something missing, it names the command to run; run it after
   confirming with the user, then let them retry.

PDFs are read locally by default (PdfPig or the local vision model). Cloud extractors ask before uploading; see skill
`media-studio-add-document-extractor` and [documents](../../../docs/documents.md).

## Agent-friendly CLI

* `video-production --help` lists commands; `video-production <command> --help` prints that command's contract.
* `video-production manifest` prints the tool manifest (JSON); [`SMART_TOOL.md`](../../../SMART_TOOL.md) describes it.
* Output is JSON on stdout, errors are JSON on stderr; exit 1 is an error and 130 a cancellation. `produce` adds
  73 (conflict), 74 (interrupted) and 75 (busy).
* Whole-production commands (`produce`, `media-produce`, `audio-produce`) take reviewed request files; read
  [file production](../../../docs/file-production.md) and [contracts](../../../docs/contracts.md) before using them.

## Pitfalls

* `narrate` fails: the Kokoro asset or eSpeak NG is missing. Run
  `dotnet run --project src/VideoProduction.Cli -- assets ensure` (downloads the assets for the configured tier) and
  install eSpeak NG (skill `media-studio-speech`).
* `plan` fails with `Cannot find package 'playwright'`: rerun `.\scripts\Install-Prerequisites.ps1` (it runs `npm ci`
  and `npx playwright install chromium` in `workers\browser`).
* `ffmpeg` not found: install it and reopen the terminal so `PATH` refreshes.
* Full in-app renders are not verified live in this preview. If one fails, collect
  `dotnet run --project src/VideoProduction.Cli -- diagnostics` (no secrets) and use skill `media-studio-troubleshoot`.
