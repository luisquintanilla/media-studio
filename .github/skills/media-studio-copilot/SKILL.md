---
name: media-studio-copilot
description: >-
  Connect GitHub Copilot to Media Studio as an optional cloud chat tier or as the authoring/illustration agent runtime,
  using the user's own GitHub sign-in or BYOK on their own models; run the Copilot tests; and keep the SessionConfig
  locks and device-login parser correct when bumping GitHub.Copilot.SDK. Use when asked to "connect Copilot",
  "sign in to GitHub Copilot", "configure copilot", "use Copilot as a tier", "use the Copilot agent runtime",
  "set up BYOK", "run Copilot tests", "bump the Copilot SDK", or "Copilot not signed in".
---

# GitHub Copilot (optional)

Copilot is never the default. Two independent uses (both in `agents.copilot` / `chat.tiers` of the settings file):

| Use | What it does | Setting |
|---|---|---|
| **Tier** | Copilot-subscription model as the `copilot` chat tier (strength 4, like `strong`) | `configure copilot --tier --model <id>` |
| **Agent runtime** | Copilot harness as the MAF `AIAgent` for authoring and/or SVG illustrations | `--runtime` / `--illustrations` |

Backends: `subscription` (user's own GitHub account, any plan incl. Free within its limits; needs
`privacy.allowCloud`) or `byok` (the Copilot agent on an `openai-compatible` or `foundry` connection, no GitHub
sign-in). Docs: [copilot](../../../docs/copilot.md). Code: `src/MediaStudio.Agents/Copilot/`.

## Ask the user first

* **Sign-in** is a GitHub device-code flow that the user completes in their own browser. Show them the code and URL;
  never ask for, store or paste a GitHub token.
* **Cost**: Copilot-subscription models may use the user's premium requests. Mention it before you enable the tier or
  the runtime.
* **Cloud**: the `subscription` backend needs `privacy.allowCloud`. Ask before turning it on.
* `Copilot` tests need `COPILOT_GITHUB_TOKEN` set by the user in their own shell. Never write it to a file.

## Connect

**In the app (preferred):** Settings → Setup → "GitHub Copilot (optional)…". Sign-in uses the GitHub device flow
(`CopilotDeviceLogin`): the app shows the code and opens `https://github.com/login/device`. Tokens are never stored by
Media Studio; the CLI keeps sign-in in a private `COPILOT_HOME` under `%LOCALAPPDATA%\MediaStudio\copilot`.

**From the command line:**

```powershell
dotnet run --project src/VideoProduction.Cli -- configure copilot --tier --model <id> --allow-cloud
dotnet run --project src/VideoProduction.Cli -- configure copilot --runtime --illustrations --allow-cloud
dotnet run --project src/VideoProduction.Cli -- configure copilot --runtime --byok local     # no sign-in, stays local
dotnet run --project src/VideoProduction.Cli -- configure copilot --off                      # remove tier + runtime
```

Each call replaces earlier Copilot options. `--model` ids come from the user's Copilot model list (the app shows
it after sign-in); do not hard-code one. Headless/CI sign-in: set `COPILOT_GITHUB_TOKEN` (or `GH_TOKEN`/`GITHUB_TOKEN`)
and set the
`copilot` connection's `auth` to `env-token` (`logged-in-user` is the default).

The CLI runtime is found by `CopilotCliLocator`: `MEDIASTUDIO_COPILOT_CLI`, then the runtime bundled next to the app
(`runtimes/<rid>/native/copilot.exe`, only `MediaStudio.App` downloads it), then `copilot` on `PATH`.

## Verification

```powershell
.\Start-MediaStudio.ps1 --configure test                  # includes copilot.* rows
dotnet run --project src/VideoProduction.Cli -- configure show
```

`video-production configure test` skips `copilot.*` rows by design; use `MediaStudio.exe --configure test` or the
wizard's Test button. Expect ✅ for sign-in, model list and a one-sentence probe.

## Tests

```powershell
dotnet test tests/MediaStudio.Tests -c Release --filter "FullyQualifiedName~MediaStudio.Tests.Copilot&Category!=Copilot"
$env:COPILOT_GITHUB_TOKEN = '<token>'   # live test only; never commit it
dotnet test tests/MediaStudio.Tests -c Release --filter "Category=Copilot"
```

Offline tests in `tests/MediaStudio.Tests/Copilot/CopilotTests.cs` and `AgentPipelineTests.cs` cover the policy,
device-login parsing, validator rules and BYOK wiring. `LiveSubscriptionStatusAndOneSentence` is the live one.

## Bumping GitHub.Copilot.SDK / Microsoft.Agents.AI.GitHub.Copilot

1. Bump both in `Directory.Packages.props` together (Agents.AI.GitHub.Copilot pins a matching SDK), then
   `dotnet restore MediaStudio.slnx --force-evaluate` and commit the lock files.
2. **Session locks** — `CopilotSessionPolicy.Create` (`CopilotSessionPolicy.cs`) must still disable every ambient
   capability: config/instruction discovery, file hooks, skills, host git, session store, MCP servers, custom agents,
   infinite sessions, replaced system message, rejected permissions and a pre-tool hook that denies anything outside
   the allow-list. If the new SDK adds a `SessionConfig` capability, lock it here and extend
   `SessionPolicyLocksEveryAmbientCapability`.
3. **Device-login parser** — `CopilotDeviceLogin.Parse` matches `XXXX-XXXX` and `https://github.com/login/device` in
   the CLI's output. Record the new CLI's login output and update `DeviceLoginParsesRecordedOutput` /
   `DeviceLoginReadsUntilCodeAndTimesOutOrCancels` if the format changed.
4. Run the Copilot tests above plus the live test with a token, and `MediaStudio.exe --configure test`.

## Pitfalls

* `copilot.cloud-disabled`: the subscription backend sends prompts to GitHub; it needs `privacy.allowCloud`
  (`--allow-cloud`). BYOK on a local connection does not.
* `copilot.not-installed`: build/run `MediaStudio.App` (it bundles the CLI) or set `MEDIASTUDIO_COPILOT_CLI`.
* `copilot.token-missing` in env-token mode: set `COPILOT_GITHUB_TOKEN`.
* GitHub Copilot connections can only back the `copilot` tier and have no endpoint; they are not BYOK targets.
* Copilot use consumes the user's premium requests; never make it a default.
* `GHCP001` is suppressed in `Directory.Build.props`; do not add other suppressions for SDK warnings.
