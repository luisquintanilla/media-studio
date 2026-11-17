# Using Media Studio with a coding agent

Media Studio is built to be set up, run, extended and debugged with a coding agent such as GitHub Copilot, Claude
Code or OpenAI Codex. The repository includes project instructions and task skills that tell the agent what to run,
which defaults to keep, and when it must stop and ask you.

* [What the repository gives your agent](#what-the-repository-gives-your-agent)
* [Quick start](#quick-start)
* [Agent setup](#agent-setup): [GitHub Copilot](#github-copilot), [Claude Code](#claude-code), [OpenAI Codex](#openai-codex), [other agents](#other-agents)
* [Skills](#skills) and [prompt cookbook](#prompt-cookbook)
* [What the agent asks you first](#what-the-agent-asks-you-first)
* [Copilot cloud agent](#copilot-cloud-agent)
* [Writing or changing a skill](#writing-or-changing-a-skill)
* [Troubleshooting](#troubleshooting)

Not the same thing: [GitHub Copilot inside the app](copilot.md) is an optional chat tier and authoring runtime of
Media Studio itself. This page is about the coding agent you use **on the repository**.

## What the repository gives your agent

| File | Read by | Purpose |
|---|---|---|
| [`AGENTS.md`](../AGENTS.md) | Copilot, Claude Code, Codex and most other agents | Project instructions: first steps, skill index, rules, build and test commands. |
| [`.github/copilot-instructions.md`](../.github/copilot-instructions.md) | GitHub Copilot | The same rules plus the repository map, in Copilot's native file. |
| [`.github/skills/media-studio-*/SKILL.md`](../.github/skills/) | Copilot natively; Claude Code and Codex after [linking](#claude-code) | Twelve task skills: step-by-step procedures with exact commands, checks and pitfalls. |
| [`.github/skills/media-explanation/SKILL.md`](../.github/skills/media-explanation/SKILL.md) | The **app** at runtime | Teaching guidance the app embeds and hash-checks. Agents may read it for writing scripts; never edit it. |
| [`.github/workflows/copilot-setup-steps.yml`](../.github/workflows/copilot-setup-steps.yml) | Copilot cloud agent | Prepares the .NET SDK and packages before the cloud agent starts. |

Skills follow the open [Agent Skills](https://agentskills.io) format: a folder with a `SKILL.md` whose frontmatter has
a `name` and a `description`. Agents read the descriptions up front and load a skill's body only when your request
matches it, so you don't need to name the skill; describing the task is enough.

The CLIs are agent-friendly too: `video-production` prints JSON on stdout and errors as JSON on stderr,
`video-production <command> --help` prints each command's contract, and `video-production manifest` describes the tool
([SMART_TOOL.md](../SMART_TOOL.md)). `configure test` prints a ✅/❌ row with a fix for every failure, and
`diagnostics` prints a report without secrets that the agent can read.

## Quick start

1. **Clone** the repository and open a terminal in it:

   ```powershell
   git clone https://github.com/luisquintanilla/media-studio.git
   cd media-studio
   ```

2. **Start your agent** in that folder: `copilot` (GitHub Copilot CLI), `claude` (Claude Code), `codex` (OpenAI Codex),
   or open the folder in VS Code / Visual Studio and switch Copilot Chat to agent mode. Claude Code and Codex users:
   run the [link script](#claude-code) once first.

3. **Ask** for what you want, in your own words:

   > Set up Media Studio locally on this machine. Check what's missing, tell me which tier you'd pick and how much it
   > downloads, then wait for my go-ahead.

The agent loads `media-studio-setup-local`, runs the read-only checks, proposes a plan and asks before it installs
software or downloads models. Then try:

> Render the demo video so I can see the pipeline work end to end.

Media Studio's desktop app runs on Windows. The `video-production` CLI and libraries also build on Linux and macOS,
but the models, narration and render path are documented and tested on Windows.

## Agent setup

### GitHub Copilot

Works with no extra setup in the Copilot CLI, the GitHub Copilot app, VS Code, Visual Studio and JetBrains agent
mode, and the Copilot cloud agent. Copilot reads `.github/copilot-instructions.md`, `AGENTS.md` and the skills in
`.github/skills`.

* Copilot CLI: `/skills` lists the skills it found; you can also name one in a prompt ("use the
  media-studio-troubleshoot skill").
* Don't run the link script: Copilot also reads `.claude/skills` and `.agents/skills`, so linking would list every
  skill twice.
* Using Copilot *inside* Media Studio as a model tier is a separate opt-in: [Copilot](copilot.md).

### Claude Code

Claude Code reads `AGENTS.md` as project instructions. It loads project skills from `.claude/skills`, so link them once
(the links are ignored by git and point back to `.github/skills`, so updates arrive with `git pull`):

```powershell
.\scripts\Link-AgentSkills.ps1 -Agent claude      # Windows: directory junctions, no admin rights needed
```

```bash
bash scripts/link-agent-skills.sh --agent claude  # macOS / Linux: relative symlinks
```

Start a new session afterwards. Claude uses a skill when your request matches it, or you can run one directly with
`/media-studio-setup-local`. More: [Claude Code skills](https://code.claude.com/docs/en/skills).

### OpenAI Codex

Codex (CLI, IDE extension and the ChatGPT desktop app) reads `AGENTS.md` and loads skills from `.agents/skills`:

```powershell
.\scripts\Link-AgentSkills.ps1 -Agent codex
```

```bash
bash scripts/link-agent-skills.sh --agent codex
```

Restart Codex, then use `/skills` to list them or mention one with `$media-studio-setup-local`. More:
[Codex skills](https://developers.openai.com/codex/skills).

Both scripts default to Claude **and** Codex, preview with `-WhatIf` / `--dry-run`, are safe to re-run, never replace
a folder you created yourself and undo their links with `-Remove` / `--remove`.

### Other agents

Most coding agents (Cursor, Gemini CLI, Windsurf, Aider and others) read an `AGENTS.md` at the repository root or can
be pointed at one; check your agent's documentation (Gemini CLI, for example, reads `GEMINI.md` unless you add
`AGENTS.md` to its context file names). `AGENTS.md` is self-contained: it has the rules, the commands and a table of
every skill with its path, so an agent without skill support can open the right `SKILL.md` when you ask, for example
"follow `.github/skills/media-studio-deploy-azure/SKILL.md`".

## Skills

| Skill | Use it to | Try |
|---|---|---|
| [`media-studio-setup-local`](../.github/skills/media-studio-setup-local/SKILL.md) | Install prerequisites, pick a tier, pull models, write offline settings | "Set up Media Studio to run fully offline." |
| [`media-studio-make-video`](../.github/skills/media-studio-make-video/SKILL.md) | Render the demo, draft a brief and script for your topic, check a movie | "Help me make a 2-minute explainer about how DNS works." |
| [`media-studio-deploy-azure`](../.github/skills/media-studio-deploy-azure/SKILL.md) | Provision the optional Foundry resource with `azd`, import it, tear it down | "Deploy the Azure resources in a region that has Claude, then connect the app." |
| [`media-studio-troubleshoot`](../.github/skills/media-studio-troubleshoot/SKILL.md) | Diagnose `configure test` failures, error codes and symptoms | "`configure test` shows two red rows. Fix them." |
| [`media-studio-speech`](../.github/skills/media-studio-speech/SKILL.md) | Narration and transcription: Kokoro, Whisper, eSpeak NG, optional Azure Speech | "Narration is silent. Find out why." |
| [`media-studio-models`](../.github/skills/media-studio-models/SKILL.md) | Change or re-pin local and Foundry models, benchmark them | "Benchmark the local models on this machine and suggest the best tier." |
| [`media-studio-routing`](../.github/skills/media-studio-routing/SKILL.md) | Chat tiers, confidence floors, the decider, routing evaluation | "Route more requests to local models without hurting quality." |
| [`media-studio-copilot`](../.github/skills/media-studio-copilot/SKILL.md) | Connect GitHub Copilot as an in-app tier or authoring runtime | "Use my GitHub Copilot subscription as the strong tier." |
| [`media-studio-illustrations`](../.github/skills/media-studio-illustrations/SKILL.md) | SVG generation, the sanitizer, grounding, critique, optional PNG | "Illustrations ignore my sources. Investigate." |
| [`media-studio-add-document-extractor`](../.github/skills/media-studio-add-document-extractor/SKILL.md) | Add or re-rank an OCR / PDF extractor behind the consent gate | "Add a new OCR provider to the Auto extractor." |
| [`media-studio-add-provider`](../.github/skills/media-studio-add-provider/SKILL.md) | Add a model role, connection kind or setup-wizard row end to end | "Support another OpenAI-compatible local server." |
| [`media-studio-build-test`](../.github/skills/media-studio-build-test/SKILL.md) | Build and test exactly like CI, update packages and lock files | "Bump this package and get CI green." |

## Prompt cookbook

Good prompts say what you want, what's off limits and when to stop. Some that work well:

**First run**

> Set up Media Studio locally. Use `-DryRun` / `-WhatIf` first and show me what would be installed and downloaded
> before you do anything. Don't enable any cloud features.

> Run `configure test` and explain each row. Fix anything red that doesn't need a download or an install; ask me about
> the rest.

**Make something**

> Render the bundled demo with the CLI, ask me before you approve it, and give me the path to the MP4.

> Here are my notes on vector databases (`notes\vectors.md`). Draft a brief and a six-scene spoken script following the
> media-explanation guidance, grounded only in my notes, and save it under `artifacts\briefs\`.

**Add the cloud (optional)**

> I want Claude and cloud OCR. Check which Azure regions support both, tell me what `azd up` will create and roughly
> what it costs, and wait for my go-ahead.

> Tear down the Azure resources. Show me what `azd down` will delete first.

**Fix things**

> Narration fails with an eSpeak error. Use the troubleshooting skill, collect `diagnostics` and fix it.

> Ollama is running but the app says the balanced model isn't pulled. Find out why.

**Contribute**

> Add a new OpenAI-compatible connection kind for my local server. Follow the add-provider skill, add tests, and run
> the CI gates before you tell me it's done.

> Update `docs/models.md` and the manifest for a newer Whisper file, regenerate what needs regenerating and run the
> tests.

## What the agent asks you first

The instructions and skills tell agents to stop and get your go-ahead before anything that costs money, downloads a
lot, changes your machine, sends data off it or can't be undone:

| Action | Why it asks |
|---|---|
| Installing software (`Install-Prerequisites`, winget, package managers) | Changes your machine; it shows `-WhatIf` output first. |
| Pulling models (`Setup-LocalModels`, `ollama pull`, `assets ensure`) | Several GB; it shows the `-DryRun` plan first. |
| `azd up` / `azd provision` | Creates billable Azure resources. |
| `azd down` (especially `--purge`) | Deletes resources permanently. |
| Turning on `privacy.allowCloud`, `--cloud-speech`, cloud extractors | Sends prompts, narration or documents off your machine. |
| GitHub Copilot sign-in | A device-code flow you complete; may use premium requests. |
| `approve` and the app's **Generate** | Human review gates; the agent passes **your** name, never its own. |
| `git push`, opening pull requests, `azd pipeline config` | Publishes or creates credentials. |

Agents never ask for, paste or commit API keys or tokens. Keys go in the environment variable
`MEDIASTUDIO__CONNECTIONS__<ID>__APIKEY` or in user-secrets, which you set yourself; Azure uses your `az` / `azd`
sign-in. When you report a problem, share `diagnostics` output rather than your settings file or `azd env get-values`.

Your agent's own permission prompts are a second line of defence: keep them on for commands that install, delete or
deploy.

## Copilot cloud agent

You can assign issues to the Copilot cloud agent or ask it for changes on github.com. When it runs on GitHub Actions,
[`copilot-setup-steps.yml`](../.github/workflows/copilot-setup-steps.yml) installs the .NET SDK from `global.json`
and restores and builds the cross-platform projects before it starts. Copilot code review reuses the same file.

Its environment is Ubuntu by default, so:

* It can build the CLI and libraries (the projects in the Linux CI job) and edit docs, skills and infrastructure.
* It **can't** build the WPF app (`MediaStudio.App`), run local models, Ollama, Kokoro or FFmpeg, or reach Azure. The
  Windows CI job checks those parts on its pull requests.
* A Windows environment needs self-hosted runners or larger runners with Azure private networking, because the
  integrated firewall doesn't support Windows; see
  [customizing the agent environment](https://docs.github.com/en/copilot/how-tos/copilot-on-github/customize-copilot/customize-cloud-agent/customize-the-agent-environment).
* Sessions that run in a cloud sandbox instead of GitHub Actions don't use the setup steps.

Good tasks for it: docs, skills, CLI features, provider code with unit tests, Bicep changes (`az bicep build`). Do
machine-specific work (setup, models, rendering) with a local agent.

## Writing or changing a skill

* One folder per skill under `.github/skills/`; the folder name and frontmatter `name` match, use lowercase letters,
  digits and hyphens, and are at most 64 characters. Start new Media Studio skills with `media-studio-`.
* The `description` (at most 1024 characters) is what agents match on: say what the skill does **and** list the
  phrases users say ("Use when asked to ...").
* The body: the goal, the exact commands, how to verify, pitfalls and an **Ask the user first** section for anything
  costly, slow, destructive or cloud-bound. Link docs with paths relative to the skill folder
  (`../../../docs/<page>.md`) so the links keep working through the `.claude/skills` and `.agents/skills` links.
* Keep model ids, tags and voice ids out of skills; point to `assets/model-manifest.json` and `infra/models.json`.
* Add the skill to the tables in [`AGENTS.md`](../AGENTS.md) and on this page.
* `AgentGuidanceTests` (in `tests/MediaStudio.Tests`) checks the frontmatter, that `AGENTS.md` and this page list
  every skill and that relative links resolve. Run it with
  `dotnet test tests/MediaStudio.Tests -c Release --filter FullyQualifiedName~AgentGuidance`.
* Never edit `media-explanation`: the app embeds it and checks its hash at runtime.

## Troubleshooting

| Symptom | Fix |
|---|---|
| The agent doesn't use a skill | Describe the task with the words in the skill's description, or name the skill. Start a new session after adding or linking skills. |
| Claude Code or Codex lists no skills | Run the link script and restart. Check `.claude/skills` or `.agents/skills` contains `media-studio-*` entries. |
| Copilot lists each skill twice | Remove the links: `.\scripts\Link-AgentSkills.ps1 -Remove`. |
| `ln -s` makes copies on Windows Git Bash | Use `Link-AgentSkills.ps1` on Windows, or set `MSYS=winsymlinks:nativestrict` with Developer Mode on. |
| The agent wants to paste a key into a file | Stop it. Set `MEDIASTUDIO__CONNECTIONS__<ID>__APIKEY` yourself ([configuration](configuration.md)). |
| The agent ran something you didn't want | Keep your agent's approval prompts on for shell commands. Local settings are in `%LOCALAPPDATA%\MediaStudio`; `configure local` rewrites them. |
| Anything else | Ask the agent to use the troubleshooting skill, or see [troubleshooting](troubleshooting.md). |
