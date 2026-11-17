---
name: media-studio-speech
description: >-
  Configure, troubleshoot and extend Media Studio narration (text-to-speech) and transcription (speech-to-text):
  local Kokoro + espeak-ng, local Whisper, and cloud Azure Speech / MAI voices and MAI-Transcribe through Foundry.
  Use when asked to "change the narrator voice", "use MAI voices", "enable cloud speech", "fix espeak-ng",
  "Kokoro fails", "switch Whisper model", "transcription is slow", "benchmark TTS/STT", or "add a speech provider".
---

# Speech (TTS / STT)

Code: `src/MediaStudio.Speech/` (`SpeechClientFactory.cs`, `Kokoro/`, `Azure/`, `Bench/`). Settings: `speech.tts`,
`speech.stt`, `speech.live` in `ProviderSettings.cs`. Docs: [speech](../../../docs/speech.md).

| Setting | Values (JSON) | Default |
|---|---|---|
| `speech.tts.provider` | `kokoro`, `azure-speech` | `kokoro`, voice `af_heart`, `quality: quantized` (or `fp32`) |
| `speech.stt.provider` | `whisper`, `azure-speech` | `whisper`, `model: large-v3-turbo` (or `base`) |
| `speech.live.provider` | `whisper-ptt`, `foundry-openai` | `whisper-ptt` |

`foundry-openai` exists only for `speech.live` (push-to-talk dictation; needs `connection` and `deployment`). Cloud
narration and transcription use `azure-speech`.

## Local (default, offline)

```powershell
dotnet run --project src/VideoProduction.Cli -- assets ensure --id kokoro-v1.0-q8
dotnet run --project src/VideoProduction.Cli -- assets ensure --id whisper-large-v3-turbo-q5   # or whisper-base on tier S
dotnet run --project src/VideoProduction.Cli -- assets ensure --fp32                            # optional kokoro-v1.0-fp32
winget install eSpeak-NG.eSpeak-NG     # macOS: brew install espeak-ng ; Linux: apt install espeak-ng
```

* Kokoro voices shipped (in the `kokoro-v1.0-q8` asset): `af_heart`, `af_bella`, `am_michael`, `am_adam`, `bf_emma`,
  `bm_george`. The first letter picks the phonemizer language (`a` = en-us, `b` = en-gb).
* espeak-ng is found by `LocalRuntimeDetector.FindEspeakNg` (PATH and the standard install folders).

## Cloud (Azure Speech / MAI, opt-in)

```powershell
dotnet run --project src/VideoProduction.Cli -- configure azd --cloud-speech   # after azd up; sets MAI TTS + MAI-Transcribe-2
dotnet run --project src/VideoProduction.Cli -- configure test --cloud-only
```

* Needs `privacy.allowCloud: true` and a Foundry connection (`speech.tts.connection` / `speech.stt.connection`);
  `CloudGuard` refuses otherwise.
* Voices come from `cloudVoices` in `assets/model-manifest.json` (`AzureSpeechTextToSpeechOptions.FromManifest`):
  MAI-Voice-2.1-Flash Harper (default), Grant, Sage, MAI-Voice-2.1 Harper (`quality`), DragonHD Ava (`fallback`).
  A Kokoro-style voice id (no `-`) falls back to the manifest default. `speech.tts.style` picks a voice style.
* STT uses `MAI-Transcribe-2` by default with `style: clean` and optional `diarization`.
* Auth: Entra by default (`az login`; role **Cognitive Services User** on the Foundry resource) via
  `SpeechAuthHandler`. TTS uses the custom-domain endpoint with a plain bearer token; the regional AAD mode
  (`SpeechTtsMode.RegionalAad`) also needs `connections[].resourceId`. API key: env
  `MEDIASTUDIO__CONNECTIONS__<ID>__APIKEY`.
* MAI voices are region-limited; `infra/hooks/postprovision.ps1` probes them and only warns when unreachable (the
  app keeps Kokoro/Whisper unless you pass `--cloud-speech`).

## Benchmarks

```powershell
dotnet run --project src/VideoProduction.Cli -- bench tts --local-only
dotnet run --project src/VideoProduction.Cli -- bench tts --text "Hello" --voice am_michael --out out\tts
dotnet run --project src/VideoProduction.Cli -- bench stt --model large-v3-turbo      # uses samples/stt or synthesizes clips
dotnet run --project src/VideoProduction.Cli -- bench cloud-stt                       # alias of bench stt
```

## Adding a speech provider

1. Add the enum value to `TtsProvider`/`SttProvider` (with `[JsonStringEnumMemberName]`) and validator rules in
   `ProviderSettingsValidator`.
2. Implement `Microsoft.Extensions.AI` `ITextToSpeechClient` / `ISpeechToTextClient` under `src/MediaStudio.Speech/`.
3. Add the case to `SpeechClientFactory`; cloud providers go through `CloudConnection` (`CloudGuard.Require`).
4. Add a `speech:tts (<name>)` / `speech:stt (<name>)` check in `ConfigurationTester.TestAsync`.
5. Tests in `tests/MediaStudio.Tests/Speech/` with recorded responses (see `tests/MediaStudio.Tests/Speech/fixtures/mai-transcribe-response.json`);
   no live calls in default tests.

## Verification

```powershell
dotnet test tests/MediaStudio.Tests -c Release --filter "FullyQualifiedName~MediaStudio.Tests.Speech&Category!=Kokoro&Category!=Whisper&Category!=Live"
dotnet test tests/MediaStudio.Tests -c Release --filter "Category=Kokoro|Category=Whisper"   # needs the assets above
dotnet run --project src/VideoProduction.Cli -- configure test        # rows speech:tts*, speech:stt*, speech:live
```

## Pitfalls

* `speech:tts espeak-ng` failed → install espeak-ng and restart the shell/app so PATH refreshes.
* Asset hash mismatch → `assets verify --id <id>`, delete the folder under the asset root and `assets ensure` again.
* `DEPLOY_OPENAI_AUDIO=true` deploys `gpt-4o-mini-transcribe` / `gpt-4o-mini-tts`, but the app does not use them yet.
* Do not hard-code voice names in code; add them to `cloudVoices` / the Kokoro asset file list in the manifest.
