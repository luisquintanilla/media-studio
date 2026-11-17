# Troubleshooting

Start with the built-in check. It probes every role and prints a fix next to each ❌:

```powershell
dotnet run --project src/VideoProduction.Cli -- configure test
.\Start-MediaStudio.ps1 --configure test      # also checks GitHub Copilot and document extraction
dotnet run --project src/VideoProduction.Cli -- diagnostics   # versions, settings summary, runtimes, assets
```

The same check runs in the app under **Settings → Setup → Roles → Test**. Include the `diagnostics` output
(it contains no secrets) when you report a problem.

## Local models

| Symptom | Fix |
| --- | --- |
| `chat:<tier>` ❌ "Install and start Ollama" | `winget install Ollama.Ollama`, then reopen the terminal. Ollama must listen on `http://localhost:11434`. |
| `chat:<tier>` ❌ "<model> is not pulled" | `ollama pull <model>` (the fix column shows the exact tag), or rerun `.\scripts\Setup-LocalModels.ps1`. |
| Answers are very slow | Use a smaller tier (`configure local --tier S`) or check GPU use with `ollama ps`. Measure with `bench local` ([models](models.md#measured-results)). |
| `routing.no-eligible` | No tier can serve the request. Usually only cloud tiers fit and cloud is off: allow cloud, or add a local tier. See [routing](routing.md). |
| `routing.local-unavailable` "Local model unavailable; cloud is disabled in Settings." | Every local tier failed. Start Ollama, or allow cloud in **Settings → Setup**. |

## Model assets

| Symptom | Fix |
| --- | --- |
| "Model asset '…' is not installed or failed verification. Run: video-production assets ensure --id …" | Run the command shown; it downloads and verifies the files. |
| `assets verify` reports a hash mismatch | Delete the asset folder under `%LOCALAPPDATA%\MediaStudio\models` and run `assets ensure --id <asset>` again. |
| `decisions:julia` "not installed; the heuristic decider is used" | Optional. `assets ensure --id julia-1` for better routing decisions. |
| Kokoro asks for eSpeak NG | `winget install eSpeak-NG.eSpeak-NG`, then reopen the terminal (or put `espeak-ng` on `PATH`). |

## Video production

| Symptom | Fix |
| --- | --- |
| `plan` or `doctor` fails with `Cannot find package 'playwright'` | Install the browser worker: `.\scripts\Install-Prerequisites.ps1` (it runs `npm ci` in `workers\browser` and installs Playwright Chromium). |
| FFmpeg not found | `.\scripts\Install-Prerequisites.ps1`, or install FFmpeg and put it on `PATH`. |
| The app can't generate videos | Generate shows what is missing and the command to fix it. Run `.\Start-MediaStudio.ps1 --configure production` to see the same check: `assets ensure --id kokoro-v1.0-q8` for narration, `.\scripts\Install-Prerequisites.ps1` for FFmpeg, eSpeak NG, Node and the browser worker. See [CLI](cli.md#setup-commands). |

## Azure

| Symptom | Fix |
| --- | --- |
| `azd up` stops: AZURE_LOCATION is not set | `azd env set AZURE_LOCATION <region>`. `.\infra\scripts\Find-FoundryRegion.ps1` lists regions with every model. |
| Pre-provision says a model isn't available in the region | Pick a region from `Find-FoundryRegion.ps1`, or set `MEDIASTUDIO_SKIP_REGION_CHECK=true` to proceed anyway. |
| Quota error during provisioning | Lower the `capacity` of that deployment in `infra/models.json`, or request quota. |
| ❌ "Sign in with `az login` or `azd auth login`" | Sign in. If the resource is in another tenant, add `--tenant <id>` (and pass `--tenant` to `configure azd`). |
| HTTP 401/403 right after `azd up` | Role assignments can take a few minutes; post-provision waits up to 5 minutes. Retry `configure test --cloud-only`. |
| Cloud tiers show `cloud-disabled` | Cloud is off in settings. Turn on **Allow cloud** in **Settings → Setup**, or rerun `configure azd`. |

See the [Azure guide](getting-started-azure.md).

## Speech

Narration uses local Kokoro and transcription local Whisper unless you chose cloud speech. If `azd up`
warns "Speech (MAI) not reachable - the app will use Kokoro/Whisper locally", the MAI voice probe failed for
your resource or region. Media Studio keeps working with local speech. To use cloud speech:

- Make sure the region supports MAI voices (`Find-FoundryRegion.ps1` shows it), or redeploy in another region.
- Check the [Speech auth](#speech-auth) items below, then run `configure azd --cloud-speech` and
  `configure test --cloud-only`.
- Cloud narration and transcription use `azure-speech`; `foundry-openai` is only valid for `speech.live`
  ([speech](speech.md#choosing-local-or-cloud-speech)).

## Speech auth

Azure Speech errors name the HTTP status:

| Status | Meaning and fix |
| --- | --- |
| 401 / 403 | Your identity needs the **Cognitive Services User** role on the Foundry resource (`azd up` assigns it to the deploying user), in the right tenant (`az login --tenant <id>`), or the API key is wrong. Role assignments can take a few minutes. |
| 400 with a voice name | The voice isn't available in your resource's region. Pick another voice or region. |
| 429 | Throttled after one retry. Wait and try again, or switch back to local speech. |

## Documents

| Symptom | Fix |
| --- | --- |
| A cloud extractor never runs | It needs `privacy.allowCloud`, a Foundry connection and your consent in the **Upload document** dialog. |
| Result tagged `mediastudio.low-quality` | Every extractor returned little text. Try a vision model (`vlm`) or a cloud extractor. |
| `documents:vlm` ❌ | Set a `vision` model (`configure local` does this). |
| Mistral rejects a document | Mistral OCR accepts up to 30 pages / 30 MB. |

See [documents](documents.md).

## GitHub Copilot

| Row | Fix |
| --- | --- |
| `copilot.auth` / `copilot.login` | Sign in through **Settings → Setup → Roles → GitHub Copilot (optional)…**, or set `COPILOT_GITHUB_TOKEN`. |
| `copilot.byok` | Check the BYOK connection (`configure copilot --byok azure --runtime`) and its sign-in. |
| `copilot.models` "'<model>' is not in your model list" | Pick a model your plan offers (`configure copilot --model <id>`). |
| `copilot.agent` "did not call the media tool" | Pick a model with tool calling. |
| `agent.cloud-disabled` | The subscription backend needs **Allow cloud**; or use BYOK on a local connection. |

See [Copilot](copilot.md).

## OpenAI SDK version

OpenAI 2.15.0 changed experimental Responses streaming from
`AsyncCollectionResult<T>` to `Task<AsyncStreamingResult<T>>`. The published
`Microsoft.Extensions.AI.OpenAI` 11.0.0 is compiled against OpenAI 2.14.0, so
with OpenAI 2.15.0 every streamed Responses call fails with:

```text
System.MissingMethodException: Method not found:
'System.ClientModel.AsyncCollectionResult`1<OpenAI.Responses.StreamingResponseUpdate>
OpenAI.Responses.ResponsesClient.CreateResponseStreamingAsync(...)'
```

The `Category=OpenAICompat` tests in `tests/MediaStudio.Tests` reproduce this
offline with recorded SSE:

```powershell
dotnet test tests\MediaStudio.Tests --filter Category=OpenAICompat
```

A standalone, offline file-based repro (it also shows that the speech-to-text and text-to-speech streaming
paths break) and a text-only workaround are in
[this gist](https://gist.github.com/luisquintanilla/b517a550720778feadc8c30ab46ec5b8).

Until a Microsoft.Extensions.AI.OpenAI release supports OpenAI 2.15:

* `Directory.Packages.props` pins **OpenAI 2.14.0**.
* Native OpenAI Decisions (`OpenAI.Decisions`, added in 2.15) are compiled out.
  The `OPENAI_DECISIONS` symbol is defined only when building with
  `-p:OpenAIDecisions=true`, which also requires pinning OpenAI 2.15+.
* Choosing the typed OpenAI decision provider fails with `decision.unavailable`.
  Routing keeps working through the Julia and Heuristic decision providers.

To re-enable native Decisions after an updated Microsoft.Extensions.AI.OpenAI
ships: bump both packages, run the OpenAICompat tests, then build with
`-p:OpenAIDecisions=true` and remove this workaround.

## Known limitations

This release was built and tested mostly offline. Not yet verified live:

- **`azd up` has not been run end to end** against a real subscription. The Bicep compiles and the hooks are
  tested, but expect rough edges.
- **Cloud speech, cloud document extractors (Document Intelligence, Content Understanding, Mistral) and
  gpt-image** were tested only with fake or recorded responses.
- **GitHub Copilot**: live device sign-in, live Copilot answers, reuse of a `gh auth login` session and
  premium-request accounting are unverified.
- **OpenAI SDK pinned to 2.14**; native OpenAI Decisions are compiled out ([details](#openai-sdk-version)).
- **Generating videos in the app** uses a configuration derived from your settings, but a full in-app render
  hasn't been verified live yet. The `video-production` CLI is the tested path ([getting started](getting-started-local.md)).
- The routed illustration pipeline isn't available in production when the illustration role uses the Copilot
  runtime ([illustrations](illustrations.md#limitations)).
- The Linux CI job has not run on hosted runners yet. The app is Windows-only; libraries and CLIs target .NET 11.
