# GitHub Copilot (optional)

Media Studio can use your GitHub Copilot plan in two ways. Both are off by default.

| Use | What it does | Setting |
| --- | --- | --- |
| **Chat tier** | Adds a `copilot` tier just below `strong`, so routing can escalate to a Copilot model | `configure copilot --tier --model <id>` |
| **Agent runtime** | Runs storyboard authoring and/or illustration through the GitHub Copilot agent instead of the Microsoft Agent Framework | `--runtime`, `--illustrations` |

> [!IMPORTANT]
> With your Copilot subscription, requests use **your premium requests** and prompts are sent to GitHub.
> It therefore requires `privacy.allowCloud`.

## Backends

- **Subscription** (default): sign in with GitHub. Media Studio never stores your token: sign-in is kept in a
  private Copilot folder (`%LOCALAPPDATA%\MediaStudio\copilot`), separate from any other Copilot CLI login.
- **BYOK (bring your own key)**: runs the Copilot agent on one of your own connections (for example your local
  Ollama or Foundry connection). No GitHub sign-in, and only the configured model is offered. Use
  `--byok <connection-id>`.

## Set up in the app

1. Open **Settings → Setup**, go to **Roles**, then **GitHub Copilot (optional)…**.
2. Choose **My GitHub Copilot subscription** and turn on **Allow cloud**, or choose **BYOK** and pick a connection.
3. Subscription only: **Sign in with GitHub**. The app shows a device code (**Copy code**, **Open browser**);
   enter it on the GitHub page. Alternatively set `COPILOT_GITHUB_TOKEN` before starting Media Studio.
4. Pick a **Model**, then **Use as strong cloud tier for authoring** (subscription only) and/or **Use the
   Copilot agent runtime for authoring**.
5. **Use these Copilot settings**, then **Save** on the Roles step. **Don't use Copilot** removes it.

The illustration runtime is set from the command line (`--illustrations`).

## Set up from the command line

```powershell
# Copilot as a chat tier (needs a model id from your Copilot model list) and as the authoring runtime:
dotnet run --project src/VideoProduction.Cli -- configure copilot --model <model-id> --tier --runtime --allow-cloud

# The Copilot agent on your own Foundry connection, no GitHub sign-in:
dotnet run --project src/VideoProduction.Cli -- configure copilot --byok azure --runtime

# Remove Copilot again:
dotnet run --project src/VideoProduction.Cli -- configure copilot --off
```

Each `configure copilot` call replaces the previous Copilot settings. Then sign in through the app, or set a
token for unattended use: give the connection `"auth": "env-token"` and set `COPILOT_GITHUB_TOKEN` (or `GH_TOKEN`).

The `video-production configure test` command skips the `copilot.*` checks. Run them with the app:

```powershell
.\Start-MediaStudio.ps1 --configure test
```

## How it works

- The app bundles the GitHub Copilot CLI runtime (version pinned by the GitHub Copilot SDK, downloaded and
  SHA-256-verified when `MediaStudio.App` builds). Its license is copied to
  `licenses/GitHubCopilotCLI.LICENSE.md`. Offline builds can skip the download with
  `-p:CopilotSkipCliDownload=true`, which disables Copilot.
- As a chat tier, each request runs in a fresh Copilot session. JSON answers are validated, with one repair retry.
- As an agent runtime, authoring still produces typed storyboard edits that you review.
- When cloud is off, Copilot requests fail with `agent.cloud-disabled` and nothing is sent.

## Not yet verified

A live device-login round trip, a live Copilot tier answer and premium-request accounting have not been
verified against a real account. The automated tests use fakes. See
[known limitations](troubleshooting.md#known-limitations).
