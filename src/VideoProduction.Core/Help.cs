namespace VideoProduction;

public static class Help
{
    private static readonly Dictionary<string, string> Commands = new() {
        ["audio-produce"] = "--request AUDIO_JSON --approval APPROVAL_JSON --speech-config PRIVATE_JSON --state-root PRIVATE_ROOT : tracked independent approved PCM16 audio master, reviewed-script transcript and integrity manifest; no visual runtime",
        ["audio-check"] = "--directory AUDIO_OUTPUT : verify committed audio candidate clock, request/approval identities and master/transcript hashes; not release approval",
        ["media-produce"] = "--request REVIEWED_MEDIA_JSON --production-config PRIVATE_JSON [--output-channel file] : exact saved Audio/Video/Both revision and per-stage grants; durably tracked independent outputs, per-output receipts, partial failure stays failed; file channel is owned by the detached native-app producer",
        ["manifest"] = ": print canonical embedded manifest fields and skill body as JSON; no runtime dependencies",
        ["produce"] = "--request REVIEWED_JSON --production-config PRIVATE_JSON : foreground whole-attempt production through the existing pipeline; NDJSON accepted/progress/result or exact replay; one active per private root, busy without queue; explicit review required",
        ["status"] = "--state-root ROOT --attempt ID : read versioned committed attempt/progress/artifacts; reconcile abandoned producers as interrupted, never resume",
        ["cancel"] = "--state-root ROOT --attempt ID : atomically request cancellation bound to this exact accepted attempt; never kill arbitrary process IDs; late cancellation preserves committed completion",
        ["draft-start"] = "--state-root ROOT --request JSON : create/replay an explicit new-format draft with bounded operation identity; does not author or produce",
        ["draft-read"] = "--state-root ROOT --draft ID : read committed revision and reconcile a lost durable admission response; legacy Studio drafts are not migrated",
        ["draft-edit"] = "--state-root ROOT --request JSON : typed revision/operation-bound draft transaction; stale/conflicting edits refuse and accepted submissions are immutable",
        ["draft-snapshot"] = "--state-root ROOT --request JSON : export exact saved reviewed revision as a request; no production/false submitted state before actual admission",
        ["draft-fork"] = "--state-root ROOT --request JSON : explicitly copy an immutable submission into a separately editable review; never automatically retry",
        ["create-demo"] = "--directory NEW_DIR [--font TTF --font-license TXT] : (default font: bundled Inter, SIL OFL) author six synthetic scenes and an imported color-card; draft until narration exists",
        ["doctor"] = "--project FILE : launch matched headless browser and report versions, hashes, FFmpeg build/license flags and isolation limitations",
        ["validate"] = "--project FILE : reject invalid profile, hashes, paths, references, caption policy and clocks",
        ["plan"] = "--project FILE [--private-auth FILE] : write concrete graph; Jellyfin credentials path/hash permission is approval-bound locally, never bundled",
        ["approve"] = "--project FILE --scope trusted-bundled-fixture-only|reviewed-jellyfin-loopback --by NAME [--private-auth FILE] : approve unchanged bounded graph (not human release review)",
        ["run"] = "--project FILE [--private-auth FILE] : execute approved synthetic fixtures or reviewed actual loopback Jellyfin profile; authenticate privately before recording",
        ["narrate"] = "--project FILE --config FILE : MEAI ITextToSpeechClient10.11.0 with pinned local Kokoro; explicit project narrationPolicy script-phrases-v1 synthesizes canonical spans once, composes unchanged PCM and measures readable TakeWindow cues; omission retains legacy whole takes; no fallback/download",
        ["analyze-speech"] = "--project FILE --scene ID --transcriber-config FILE : optional non-adopting ASR QA for script-phrase captions; persist findings/observed-agreement/failed, never replace script/cues or require adoption; not acoustic proof",
        ["import-narration"] = "--project FILE --scene ID --wav FILE --cues JSON --text-file UTF8 : replace narration with mono PCM16 WAV plus explicit sample evidence",
        ["transcribe"] = "--project FILE --scene ID --transcriber-config FILE : explicit local Windows x64 Whisper Base STT; hash-preflight all artifacts/cache, preserve master/derivative/raw evidence; strict default, separately pinned opt-in exact-zero marker profile; no provisioning",
        ["adopt-transcript"] = "--project FILE --scene ID [--discrepancy-decision TEXT] [--caption-exception REASON] : adopt observed STT segments as acoustic captions; never change script or claim word alignment/human release",
        ["bind-delivery-transcript"] = "--project FILE --movie CHECKED_CANDIDATE --transcript-project FILE : bind adopted full selectedmaster STT to exact ordered source/timeline and decoded delivery PCM; unchanged audio/visuals, offline captions preserved",
        ["resolve"] = "--project FILE : resolve exact integer frames/samples and final caption projection; reject overrun/short footage without authored hold",
        ["render"] = "--project FILE [--profile candidate|preview] : render frozen sources, optional verified-static inserted dissolves and explicit mapped tail holds; candidate requires human review; preview visibly marked",
        ["check"] = "--project FILE --movie FILE : probe/decode/measure final H264 AAC delivery against resolved timeline",
        ["release"] = "--project FILE --candidate output/candidate/HASH --review FILE : copy exact machine-checked human-approved bytes, never rename a preview",
        ["export"] = "--project FILE --output NEW_ZIP : portable source+frozen takes+JS dependencies+font bundle; no FFmpeg/browser/model/native redistribution"
    };

    public static string Root(bool full) => full
        ? "<skill_content name=\"video-production\">\nSkill directory: " + AppContext.BaseDirectory
          + "\nVersion: " + SelfDescription.Manifest().Manifest.Version
          + "\nRelative paths in this skill are relative to the skill directory.\n"
          + SelfDescription.Manifest().Body + "\n"
          + "Stage-command arguments: --source SOURCE_ROOT (default cwd or VIDEO_PRODUCTION_HOME), or --config TOOLS.json. File commands use explicit --state-root/--production-config instead.\n"
          + "Source project paths use portable forward slashes; shell arguments use native host paths. JSON stdout, errors stderr; exit1 errors/130 cancellation.\n"
          + "No sandbox is provided: acquisition is limited to explicitly approved synthetic fixtures or typed reviewed loopback Jellyfin scopes. Never pass untrusted code. Nothing uploads.\n"
          + "Kokoro default is a candidate voice, not an audition approval. Captions use take-window/segment evidence, not invented word times.\n"
          + "Setup: configure local|azd|test|show|production and assets list|verify|ensure write %LOCALAPPDATA%\\MediaStudio\\settings\\model-providers.json and verify pinned model downloads (run: video-production configure).\n"
          + "Bench: bench tts|stt|cloud-stt time local Kokoro/Whisper and, when cloud is allowed, Azure Speech MAI (WER, real-time factor, word timestamps).\n"
          + "Documents: documents probe FILE shows local features and the extractor Auto would use; bench documents FILE... times PdfPig, the local vision model and (with --allow-cloud) Foundry extractors.\n"
          + "Bench: bench chat|image|local measure chat tiers, the illustration pipeline and local Ollama candidates (routing agreement, JSON validity, judge score, tokens/s).\n"
          + "## Capabilities\n" + string.Join("\n", Commands.Select(c => $"- {c.Key} [{(c.Key is "narrate" or "transcribe" or "analyze-speech" or "produce" ? "model-backed" : "deterministic")}] {c.Value}"))
          + "\n\nExample: video-production render --project demo\\project.json --source .\n"
          + "Result identifies artifact paths; README.md documents offline setup and review requirements.\n"
          + "<skill_resources>\n<file>SMART_TOOL.md</file>\n<file>README.md</file>\n<file>docs/contracts.md</file>\n"
          + "<file>docs/speech.md</file>\n<file>docs/transcription.md</file>\n<file>docs/production-upgrades.md</file>\n<file>docs/file-production.md</file>\n</skill_resources>\n</skill_content>"
        : "video-production " + SelfDescription.Manifest().Manifest.Version + "\n"
          + string.Join("\n", Commands.Select(c => $"{c.Key}: {c.Value}"));

    public static string Capability(string command, bool full = true)
    {
        Guard.Require(Commands.ContainsKey(command), "argument.command", "Unknown capability; read --help.");
        var fileCommand = command is "produce" or "audio-produce" or "audio-check" or "media-produce" or "status" or "cancel" or "draft-start" or "draft-read" or "draft-edit" or "draft-snapshot" or "draft-fork";
        var globals = fileCommand ? "Only the explicit file-command flags above are supported."
            : "Global optional flags: --source SOURCE_ROOT or --config TOOLS.json.";
        if (!full) return $"{command} {Commands[command]}\n{globals}";
        var example = command switch {
            "manifest" => "manifest",
            "produce" => @"produce --request reviewed.json --production-config private-production.json",
            "media-produce" => @"media-produce --request reviewed-media.json --production-config private-media.json --output-channel file",
            "audio-produce" => @"audio-produce --request audio-request.json --approval audio-approval.json --speech-config private-speech.json --state-root %LOCALAPPDATA%\MediaStudio\media",
            "audio-check" => @"audio-check --directory samples\synthetic-audio",
            "status" or "cancel" => command + @" --state-root %LOCALAPPDATA%\MediaStudio\video-files --attempt attempt-0123456789abcdef0123456789abcdef",
            "draft-read" => @"draft-read --state-root %LOCALAPPDATA%\MediaStudio\video-files --draft draft-0123456789abcdef0123456789abcdef",
            "draft-start" or "draft-edit" or "draft-snapshot" or "draft-fork" => command + @" --state-root %LOCALAPPDATA%\MediaStudio\video-files --request operation.json",
            "create-demo" => @"create-demo --directory artifacts\demo",
            "approve" => @"approve --project demo\project.json --scope trusted-bundled-fixture-only --by reviewer-name",
            "narrate" => @"narrate --project demo\project.json --config local-tools.json",
            "transcribe" => @"transcribe --project demo\project.json --scene code --transcriber-config private-transcriber.json",
            "analyze-speech" => @"analyze-speech --project demo\project.json --scene code --transcriber-config private-transcriber.json",
            "adopt-transcript" => @"adopt-transcript --project demo\project.json --scene code --discrepancy-decision ""Retain the actual observed speech; mismatch flagged for final review"" --caption-exception ""Coarse observed segment retained; review reading speed and segmentation""",
            "bind-delivery-transcript" => @"bind-delivery-transcript --project demo\project.json --movie demo\output\candidate\HASH\movie.mp4 --transcript-project wholemaster\project.json",
            "import-narration" => @"import-narration --project demo\project.json --scene code --wav takes\human.wav --cues takes\cues.json --text-file takes\display.txt",
            "check" => @"check --project demo\project.json --movie demo\output\candidate\HASH\movie.mp4",
            "release" => @"release --project demo\project.json --candidate output/candidate/HASH --review accepted-review.json",
            "export" => @"export --project demo\project.json --output artifacts\source-bundle.zip",
            _ => command + @" --project demo\project.json"
        };
        return $"<skill_content name=\"video-production-{command}\">\nSkill directory: {AppContext.BaseDirectory}\nVersion: {SelfDescription.Manifest().Manifest.Version}\n# {command}\n"
            + Commands[command] + "\nAll arguments are strings/paths except enum-like scope/profile values shown above.\n"
            + globals + "\n"
            + $"Worked invocation: video-production {example}\n"
            + (command is "produce" or "audio-produce" or "media-produce"
                ? "Result: schemaVersion1 NDJSON accepted/progress/result/replay; exit0 success/read replay,1 failure,73 conflict,74 interrupted replay,75 busy,130 cancelled.\n"
                : "Result: JSON stdout; artifact-producing commands return paths/status. Errors are JSON stderr with nonzero exit (130 cancelled).\n")
            + "Use only explicit authorized local inputs. Read README.md for schemas, provenance, offline prerequisites, and human review gates.\n</skill_content>";
    }
}
