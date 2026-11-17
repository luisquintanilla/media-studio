using System.Text.Json;
using VideoProduction;
using VideoProduction.Providers;
using System.CommandLine;
using MediaStudio.Audio;

return await MainAsync(args);

static async Task<int> MainAsync(string[] args)
{
    StreamWriter? ownedOutput = null;
    StreamWriter? ownedError = null;
    using var cancel = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
    try
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine(Help.Root(args.Length == 0 || args[0] == "--help"));
            return 0;
        }
        if (args[0] is "-V" or "--version") { Console.WriteLine(SelfDescription.Manifest().Manifest.Version); return 0; }
        if (DefaultProductionCommand.Handles(args)) return DefaultProductionCommand.Run(args, Console.Out);
        if (MediaStudio.Configuration.ConfigureCommands.Handles(args))
            return await MediaStudio.Configuration.ConfigureCommands.RunAsync(args, Console.Out, cancel.Token,
                checks: [new MediaStudio.Sources.DocumentConfigurationCheck()]);
        if (MediaStudio.Sources.DocumentCommands.Handles(args))
            return await MediaStudio.Sources.DocumentCommands.RunAsync(args, Console.Out, DocumentCommandServices, cancel.Token);
        if (VideoProduction.Providers.Bench.ChatBenchCommands.Handles(args))
            return await VideoProduction.Providers.Bench.ChatBenchCommands.RunAsync(args, Console.Out, cancel.Token);
        if (MediaStudio.Speech.Bench.SpeechBenchCommands.Handles(args))
            return await MediaStudio.Speech.Bench.SpeechBenchCommands.RunAsync(args, Console.Out, cancel.Token);
        var command = args[0];
        if (args.Skip(1).Any(a => a is "--help" or "-h"))
        {
            Console.WriteLine(Help.Capability(command, args.Contains("--help")));
            return 0;
        }
        if (command == "manifest")
        {
            Guard.Require(args.Length == 1, "argument.syntax", "manifest takes no arguments.");
            Console.WriteLine(Json.Text(SelfDescription.Manifest()));
            return 0;
        }
        var allowed = command switch {
            "media-produce" => ["request", "production-config", "output-channel"],
            "audio-produce" => ["request", "approval", "speech-config", "state-root"],
            "audio-check" => ["directory"],
            "produce" => ["request", "production-config"],
            "status" or "cancel" => ["state-root", "attempt"],
            "draft-start" or "draft-edit" or "draft-snapshot" or "draft-fork" => ["state-root", "request"],
            "draft-read" => ["state-root", "draft"],
            "create-demo" => new[] { "directory", "font", "font-license" },
            "approve" => ["project", "scope", "by", "private-auth"],
            "plan" => ["project", "private-auth"],
            "run" => ["project", "private-auth"],
            "transcribe" or "analyze-speech" => ["project", "scene", "transcriber-config"],
            "adopt-transcript" => ["project", "scene", "discrepancy-decision", "caption-exception"],
            "bind-delivery-transcript" => ["project", "transcript-project", "movie"],
            "import-narration" => ["project", "scene", "wav", "cues", "text-file"],
            "render" => ["project", "profile"],
            "check" => ["project", "movie"],
            "release" => ["project", "candidate", "review"],
            "export" => ["project", "output"],
            "doctor" or "validate" or "narrate" or "resolve" => ["project"],
            _ => throw new VideoException("argument.command", "Unknown command. Run --help.")
        };
        return await Invoke(args.Skip(1).ToArray(), allowed, command, async options =>
        {
            Guard.Require(options.Keys.All(k => k is "config" or "source" || allowed.Contains(k)),
                "argument.unused", "An option is not supported by this capability; read its --help.");
            string Required(string key) => options.GetValueOrDefault(key)
                ?? throw new VideoException("argument.required", $"Missing --{key}. Read {command} --help.");
            if (command == "media-produce")
            {
                var configuration = PrivateFiles.Read<MediaProductionConfiguration>(Required("production-config"));
                var reviewed = PrivateFiles.Read<MediaReviewedRequest>(Required("request"));
                var channel = options.GetValueOrDefault("output-channel", "stdio");
                Guard.Require(channel is "stdio" or "file", "argument.channel", "Use stdio or the producer-owned bounded file event channel.");
                var store = new FileProductionStore(configuration.StateRoot);
                if (channel == "file")
                {
                    var saved = new MediaProjectStore(store).Read(reviewed.Project.Id, reviewed.Project.Revision);
                    Guard.Require(Files.Fingerprint(saved) == Files.Fingerprint(reviewed.Project),
                        "media.identity", "The producer log must bind a real saved project revision.");
                    var logRoot = Path.Combine(configuration.StateRoot, "requests", MediaProjectStore.SubmissionKey(saved));
                    Directory.CreateDirectory(logRoot);
                    ownedOutput = new StreamWriter(new FileStream(Path.Combine(logRoot, "events.ndjson"),
                        FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
                    ownedError = new StreamWriter(new FileStream(Path.Combine(logRoot, "errors.json"),
                        FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
                    Console.SetOut(ownedOutput);
                    Console.SetError(ownedError);
                }
                long outputBytes = 0;
                var channelFailed = false;
                void Emit(string kind, object value)
                {
                    if (channelFailed) return;
                    var line = JsonSerializer.Serialize(new { schemaVersion = 1, kind, value },
                        new JsonSerializerOptions(Json.Options) { WriteIndented = false });
                    try
                    {
                        if ((outputBytes = checked(outputBytes + System.Text.Encoding.UTF8.GetByteCount(line) + 2)) > 4_000_000)
                            throw new IOException("Producer event channel exceeded its explicit bound.");
                        Console.WriteLine(line);
                    }
                    catch (IOException)
                    {
                        channelFailed = true;
                        Atomic.Write(Path.Combine(configuration.StateRoot, "output-channel-failed.json"),
                            Json.Text(new { schemaVersion = 1, errorCode = "attempt.output-channel", productionContinues = true }));
                    }
                }
                var accepted = false;
                var mediaResult = await store.ProduceMedia(reviewed, configuration,
                    status => { Emit(accepted ? "progress" : "accepted", status); accepted = true; }, cancel.Token);
                Emit(mediaResult.Replay ? "replay" : "result", mediaResult);
                return mediaResult.Status switch { "cancelled" => 130, "failed" => 1, "interrupted" => 74, _ => 0 };
            }
            if (command == "audio-check")
            {
                Console.WriteLine(Json.Text(AudioProduction.Verify(Required("directory"))));
                return 0;
            }
            if (command == "audio-produce")
            {
                var request = Json.Read<AudioProductionRequest>(Required("request"));
                var approval = Json.Read<AudioApproval>(Required("approval"));
                var speech = Json.Read<SpeechConfig>(Required("speech-config"));
                Guard.Require(Files.Fingerprint(request) == approval.RequestSha256
                    && request.Voice == new SpeechRequest(speech.Voice, speech.Language, speech.Speed),
                    "audio.approval", "Audio approval/request/voice differs; no provider was initialized.");
                using var backend = new ConfiguredNarrationBackend(speech, request.Voice, request.Provider);
                var store = new FileProductionStore(Required("state-root"));
                var channelFailed = false;
                void Emit(string kind, object value)
                {
                    if (channelFailed) return;
                    try
                    {
                        Console.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, kind, value },
                        new JsonSerializerOptions(Json.Options) { WriteIndented = false }));
                    }
                    catch (IOException)
                    {
                        channelFailed = true;
                        Atomic.Write(Path.Combine(Required("state-root"), "output-channel-failed.json"),
                            Json.Text(new { schemaVersion = 1, errorCode = "attempt.output-channel", productionContinues = true }));
                    }
                }
                var accepted = false;
                var audioResult = await store.ProduceAudio(new(1, $"audio-{request.ProjectId}-r{request.Revision}", request, approval),
                    backend, status => { Emit(accepted ? "progress" : "accepted", status); accepted = true; }, cancel.Token);
                Emit(audioResult.Replay ? "replay" : "result", audioResult);
                return audioResult.Status switch { "completed" => 0, "cancelled" => 130, "interrupted" => 74, _ => 1 };
            }
            if (command is "produce" or "status" or "cancel" or "draft-start" or "draft-read" or "draft-edit" or "draft-snapshot" or "draft-fork")
            {
                Guard.Require(options.Keys.All(allowed.Contains), "argument.unused",
                    "File commands accept their explicit state/production configuration only; legacy renderer --config/--source are not used.");
                object response;
                if (command == "produce")
                {
                    var productionConfig = PrivateFiles.Read<FileProductionConfiguration>(Required("production-config"));
                    Guard.Require(productionConfig.SchemaVersion == 1, "attempt.config", "Expected file-production configuration schemaVersion1.");
                    var store = new FileProductionStore(productionConfig.StateRoot);
                    var request = Json.Read<ReviewedVideoRequest>(Required("request"));
                    var pipeline = new ProductionVideoPipeline(new ChatVideoPlanner(null, null), productionConfig.Generation);
                    var channelFailed = false;
                    void Emit(string kind, object value)
                    {
                        if (channelFailed) return;
                        try
                        {
                            Console.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, kind, value },
                            new JsonSerializerOptions(Json.Options) { WriteIndented = false }));
                        }
                        catch (IOException)
                        {
                            channelFailed = true;
                            Atomic.Write(Path.Combine(productionConfig.StateRoot, "output-channel-failed.json"),
                                Json.Text(new { schemaVersion = 1, errorCode = "attempt.output-channel", productionContinues = true }));
                        }
                    }
                    var accepted = false;
                    var attemptResult = await store.Produce(request, pipeline, snapshot =>
                    {
                        Emit(accepted ? "progress" : "accepted", snapshot);
                        accepted = true;
                    }, cancel.Token);
                    Emit(attemptResult.Replay ? "replay" : "result", attemptResult);
                    return (int)(attemptResult.Status switch {
                        "cancelled" => FileProductionExitCode.Cancelled,
                        "failed" => FileProductionExitCode.Failed,
                        "interrupted" => FileProductionExitCode.Interrupted,
                        _ => FileProductionExitCode.Success
                    });
                }
                else
                {
                    var store = new FileProductionStore(Required("state-root"));
                    var drafts = new FileDraftStore(store);
                    response = command switch {
                        "status" => store.Status(Required("attempt")),
                        "cancel" => store.Cancel(Required("attempt")),
                        "draft-start" => drafts.Start(Json.Read<FileDraftStart>(Required("request"))),
                        "draft-read" => drafts.Read(Required("draft")),
                        "draft-edit" => drafts.Edit(Json.Read<FileDraftEdit>(Required("request"))),
                        "draft-snapshot" => drafts.Snapshot(Json.Read<FileDraftSnapshotCommand>(Required("request"))),
                        "draft-fork" => drafts.Fork(Json.Read<FileDraftFork>(Required("request"))),
                        _ => throw new VideoException("argument.command", "Unknown file command.")
                    };
                }
                Console.WriteLine(Json.Text(response));
                return 0;
            }
            var source = Path.GetFullPath(options.GetValueOrDefault("source") ?? Environment.GetEnvironmentVariable("VIDEO_PRODUCTION_HOME") ?? ".");
            Tools tools = options.TryGetValue("config", out var config)
                ? Json.Read<Tools>(config)
                : new Tools(source, Files.FindExecutable("node"), Files.FindExecutable("ffmpeg"), Files.FindExecutable("ffprobe"), null);
            tools = tools with {
                SourceRoot = Path.GetFullPath(tools.SourceRoot),
                Node = Files.FindExecutable(tools.Node),
                Ffmpeg = Files.FindExecutable(tools.Ffmpeg),
                Ffprobe = Files.FindExecutable(tools.Ffprobe)
            };
            Guard.Require(Directory.Exists(tools.SourceRoot), "tool.source", "Source root does not exist; set --source or tools.sourceRoot.");
            object result;
            if (command == "create-demo")
            {
                var projectFile = Demo.Create(Path.GetFullPath(Required("directory")), options.GetValueOrDefault("font"), options.GetValueOrDefault("font-license"));
                var card = Path.Combine(Path.GetDirectoryName(projectFile)!, "card-import.png");
                await Processes.Run(tools.Ffmpeg, ["-hide_banner", "-nostdin", "-loglevel", "error", "-f", "lavfi", "-i",
                "color=c=0x173351:s=1920x1080", "-frames:v", "1", "-update", "1", card], tools.SourceRoot, cancel.Token);
                Demo.AttachImportedCard(projectFile, card);
                File.Delete(card);
                result = new { project = projectFile, status = "draft; run narrate or import-narration before validate/plan" };
            }
            else
            {
                var production = new Production(Required("project"), tools);
                switch (command)
                {
                    case "doctor": result = await production.Doctor(cancel.Token); break;
                    case "validate": Validation.Validate(production.Load(), production.Root); result = new { valid = true }; break;
                    case "plan": result = production.Plan(options.GetValueOrDefault("private-auth")); break;
                    case "approve":
                        Guard.Require(Required("scope") == JellyfinCapture.Scope(production.Load()), "approval.scope",
                            "Scope must match this exact graph: trusted-bundled-fixture-only or reviewed-jellyfin-loopback.");
                        result = production.Approve(Required("by"), options.GetValueOrDefault("private-auth")); break;
                    case "run": await production.Acquire(cancel.Token, options.GetValueOrDefault("private-auth")); result = new { acquired = true }; break;
                    case "narrate":
                        await production.Narrate(cancel.Token); result = new
                        {
                            narrated = true,
                            backend = tools.Speech is null ? "explicit-legacy-kokoro-cli" : "meai-ITextToSpeechClient",
                            humanAuditionRequired = true
                        }; break;
                    case "transcribe":
                        result = await production.Transcribe(Required("scene"), Json.Read<TranscriberConfig>(Required("transcriber-config")), cancel.Token);
                        break;
                    case "analyze-speech":
                        result = await production.AnalyzeSpeech(Required("scene"), Json.Read<TranscriberConfig>(Required("transcriber-config")), cancel.Token);
                        break;
                    case "adopt-transcript":
                        result = production.AdoptTranscript(Required("scene"), options.GetValueOrDefault("discrepancy-decision") ?? "",
                            options.GetValueOrDefault("caption-exception"));
                        break;
                    case "bind-delivery-transcript":
                        result = await production.BindDeliveryTranscript(Required("transcript-project"), Required("movie"), cancel.Token);
                        break;
                    case "import-narration":
                        var cues = Json.Read<Cue[]>(Required("cues"));
                        production.ImportNarration(Required("scene"), Required("wav"), cues, File.ReadAllText(Required("text-file")));
                        result = new { imported = true }; break;
                    case "resolve": result = await production.Resolve(cancel.Token); break;
                    case "render":
                        var profile = options.GetValueOrDefault("profile") ?? "candidate";
                        Guard.Require(profile is "candidate" or "preview", "argument.profile", "Use candidate or preview; release requires exact-build human approval.");
                        result = new { output = await production.Render(profile == "preview", cancel.Token), humanReviewRequired = true }; break;
                    case "check":
                        result = await production.CheckFile(Path.GetFullPath(Required("movie")),
                            Json.Read<Timeline>(Path.Combine(production.Root, "build", "timeline.lock.json")), cancel.Token); break;
                    case "release": result = new { output = production.Release(Required("candidate"), Required("review")) }; break;
                    case "export": result = new { bundle = production.Export(Path.GetFullPath(Required("output"))) }; break;
                    default: throw new VideoException("argument.command", "Unknown command. Run --help.");
                }
            }
            Console.WriteLine(Json.Text(result));
            return 0;
        }, cancel.Token);
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine(Json.Text(new { ok = false, code = "cancelled", message = "Cancelled; no incomplete stage is successful." }));
        return 130;
    }
    catch (Exception ex) when (ex is VideoException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or OverflowException)
    {
        Console.Error.WriteLine(Json.Text(new
        {
            ok = false, code = ex is VideoException v ? v.Code : "input-or-runtime",
            message = ex.Message, remedy = "Read capability --help; verify paths, tool prerequisites, hashes and scoped approvals."
        }));
        return ex is VideoException error ? (int)(error.Code switch {
            "attempt.busy" or "attempt.transaction-busy" => FileProductionExitCode.Busy,
            "attempt.idempotency-conflict" or "draft.revision" or "draft.operation-conflict" => FileProductionExitCode.Conflict,
            _ => FileProductionExitCode.Failed
        }) : 1;
    }
    finally
    {
        if (ownedOutput is not null) await ownedOutput.DisposeAsync();
        if (ownedError is not null) await ownedError.DisposeAsync();
    }
}

static async Task<int> Invoke(string[] args, string[] allowed, string command,
    Func<Dictionary<string, string>, Task<int>> action, CancellationToken ct)
{
    var parser = new Command(command);
    var fileCommand = command is "media-produce" or "audio-produce" or "audio-check" or "produce"
        or "status" or "cancel" or "draft-start" or "draft-read" or "draft-edit" or "draft-snapshot" or "draft-fork";
    var options = allowed.Concat(fileCommand ? [] : new[] { "config", "source" }).Distinct(StringComparer.Ordinal)
        .ToDictionary(key => key, key => new Option<string?>("--" + key) { Arity = ArgumentArity.ExactlyOne },
            StringComparer.Ordinal);
    foreach (var option in options.Values)
    {
        option.Validators.Add(result =>
        {
            if (result.Tokens.Count > 1) result.AddError("Each scalar option must occur exactly once.");
        });
        parser.Options.Add(option);
    }
    parser.SetAction((result, token) =>
    {
        token.ThrowIfCancellationRequested();
        return action(options.Where(pair => result.GetResult(pair.Value) is not null)
            .ToDictionary(pair => pair.Key, pair => result.GetValue(pair.Value)!, StringComparer.Ordinal));
    });
    var parsed = parser.Parse(args, new ParserConfiguration { ResponseFileTokenReplacer = null });
    Guard.Require(parsed.Errors.Count == 0, "argument.syntax",
        string.Join("; ", parsed.Errors.Select(error => error.Message)));
    return await parsed.InvokeAsync(new InvocationConfiguration {
        Output = Console.Out, Error = Console.Error, EnableDefaultExceptionHandler = false,
        ProcessTerminationTimeout = null
    }, ct);
}

static MediaStudio.Sources.DocumentCommandServices DocumentCommandServices(MediaStudio.Configuration.ProviderSettings settings)
{
    Microsoft.Extensions.AI.IChatClient? vision = null;
    try { if (settings.Vision is { } role) vision = ProviderFactory.CreateRoleClient(settings, role); }
    catch (Exception error) when (error is InvalidOperationException or ArgumentException) { Console.Error.WriteLine($"Vision model unavailable: {error.Message}"); }
    var decider = VideoProduction.Providers.Decisions.DecisionClientFactory.Create(settings, new MediaStudio.Configuration.ModelAssets(MediaStudio.Configuration.ModelManifest.LoadDefault()));
    return new(vision, decider, settings.Vision is { } v && settings.IsCloud(v.Connection));
}
