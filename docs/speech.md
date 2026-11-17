# Speech

Media Studio narrates and transcribes locally by default. Cloud speech is opt-in
and is refused unless `privacy.allowCloud` is `true` in the provider settings.
All engines live in `src/MediaStudio.Speech` and implement the
Microsoft.Extensions.AI `ITextToSpeechClient` / `ISpeechToTextClient` interfaces.

| Role | Local (default) | Cloud (opt-in) |
| --- | --- | --- |
| Narration (TTS) | Kokoro-82M on ONNX Runtime | Azure Speech MAI-Voice-2.1 (`en-US-Harper:MAI-Voice-2.1-Flash`, style `narrator`) |
| Transcription (STT) | Whisper.net (`ggml-base`; `large-v3-turbo` q5_0 on larger tiers) | Azure Speech MAI-Transcribe-2 (word timestamps) |

## Kokoro pipeline

`text → espeak-ng (IPA) → tokenizer.json vocab → ONNX Runtime → 24 kHz PCM`

1. **Phonemes.** `espeak-ng` must be installed and on `PATH` (or in the default
   Windows install location). Without it narration fails with an install hint;
   there is no silent fallback.
2. **Tokens.** Phonemes map one character at a time through the model's
   `tokenizer.json` vocabulary. Text is split at sentence boundaries into
   segments of at most 510 tokens.
3. **Style.** Each segment uses row `min(tokens, 509)` of the selected voice file.
4. **Inference and output.** Segments are synthesized at the requested speed,
   concatenated and written as 16-bit mono WAV.

Model files are fetched with `assets ensure --id kokoro-v1.0-q8` (or
`kokoro-v1.0-fp32` for `quality: "fp32"`) into the per-user model store
(`MEDIASTUDIO_HOME/models`). Hashes are pinned in `assets/model-manifest.json`.

## Choosing local or cloud speech

Local Kokoro and Whisper are the defaults (`configure local`). To narrate and transcribe with Azure Speech
(MAI voices and MAI-Transcribe-2) on your Foundry resource, use the setup wizard's **Cloud speech** checkbox or:

```powershell
dotnet run --project src/VideoProduction.Cli -- configure azd --cloud-speech
```

This writes the `speech` section of the [settings file](configuration.md#speech):

```json
"tts": { "provider": "azure-speech", "connection": "azure", "voice": "en-US-Harper:MAI-Voice-2.1-Flash", "style": "narrator" },
"stt": { "provider": "azure-speech", "connection": "azure", "model": "MAI-Transcribe-2", "style": "clean" }
```

`azure-speech` is the cloud provider for narration (`tts`) and transcription (`stt`).

## Live dictation (push-to-talk)

The app can dictate into the chat box:

- **Local (default):** Whisper push-to-talk (`live.provider: "whisper-ptt"`). Start the app with
  `--voice-config <file>` to enable the "Push-to-talk, local transcription" panel.
- **Cloud:** `live.provider: "foundry-openai"` with the `transcribe-live` deployment (`gpt-live-transcribe`) from
  `azd up`. It opens a transcription-only realtime session (24 kHz PCM), shows provisional text while you
  speak, and commits when you press Stop. It never sends the message for you and has no automatic fallback.

## Narration configuration (`tools.json`)

```json
"speech": { "provider": "kokoro", "voice": "af_heart", "language": "en-us", "speed": 1, "quality": "quantized" }
```

`provider` is `kokoro` or `azure-speech`. An approval pins the speech identity:
the provider, the client type and, for Kokoro, the manifest hashes of the
model, `tokenizer.json` and the selected voice. Changing any of them requires a
new approval.

## Azure Speech (MAI)

Both clients call the Speech REST APIs on the Foundry resource from the
configured Foundry connection. No model deployment is needed.

- **Text to speech:** `POST {speech-host}/cognitiveservices/v1` with an SSML body
  and `X-Microsoft-OutputFormat: riff-24khz-16bit-mono-pcm`.
- **Transcription:** `POST {endpoint}/speechtotext/transcriptions:transcribe?api-version=2025-10-15`
  with multipart `audio` + `definition` (`enhancedMode.model = "MAI-Transcribe-2"`,
  word timestamps).

### Authentication

Microsoft Entra tokens (scope `https://cognitiveservices.azure.com/.default`) are
sent as `Authorization: Bearer` to the custom-domain endpoint. The regional
host uses the `aad#{resourceId}#{token}` form. API-key connections use
`Ocp-Apim-Subscription-Key` instead.

Credentials are only sent to trusted Azure hosts.

## Benchmarks

- `bench tts [--text ...] [--voice ...] [--out dir] [--local-only]` times Kokoro, plus Azure when it is configured and allowed. It writes one WAV per engine and prints load time, synthesis time, audio length and real-time factor.
- `bench stt [--files dir|dir/*.wav] [--model base|large-v3-turbo] [--local-only]` reports word error rate against reference transcripts. Each `<name>.wav` needs a `<name>.txt` next to it. Without `--files` it uses `samples/stt`. If there are no clips, it synthesizes four reference clips with Kokoro, which gives a fully local round trip.
- `bench cloud-stt` is the same as `bench stt`.
- Cloud engines run only when `privacy.allowCloud` is true and a Foundry connection exists.
- Measured on one dev machine (CPU, Kokoro q8, Whisper base): Kokoro real-time factor about 2.3; Whisper base real-time factor about 0.5 with 3.8% word error rate on four synthetic clips. See [models](models.md#measured-results).

## Troubleshooting

- `speech.load` with "Run: video-production assets ensure --id …": the model files are missing; run the command.
- Kokoro fails with an eSpeak NG hint: install it (`winget install eSpeak-NG.eSpeak-NG`).
- Azure Speech errors: see [Speech](troubleshooting.md#speech) and [Speech auth](troubleshooting.md#speech-auth).

Azure Speech has been tested only with recorded and fake responses so far; see
[known limitations](troubleshooting.md#known-limitations).
