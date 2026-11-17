using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using MediaStudio.Audio;
using VideoProduction;
using VideoProduction.Providers;

namespace MediaStudio.Tests.Agents;

using MediaStudio.Agents;

public static class MediaDraftToolProof
{
    public static async Task Run()
    {
        var frame = MediaDraftRules.FromContent(new("Bounded tool fixture", "", "", "",
            MediaOutput.Video, 300, null, null));
        var sourceId = "source-fixture";
        frame = frame with { Sources = [new(sourceId, new('a', 64), new('b', 64), new('c', 64))] };
        var state = new MediaDraftState(1, "draft-" + new string('a', 32), null, 0, 3,
            Files.Fingerprint(frame), frame, [], [], "run-fixture", "thread-fixture");
        var tool = new MediaDraftTool();
        var prompt = tool.Begin(state, "Create two cited inert scenes.", default);
        AdvertisesBoundedIllustrationPrompt(tool.Function);
        AdvertisesRequiredSceneIdentity(tool.Function);
        DeclaresCompleteCitationReplacement(prompt);
        var original = new MediaDraftTransaction("op-fixture", state.DraftId, state.ThreadId!, state.ActiveRunId!,
            state.Version, state.ContentSha256,
            [new(MediaDraftOperationKind.AddScene, "scene-one",
                Scene: new(TemplateKind.Diagram, "One", "Inert text", "Self-contained narration.", new string('x', 2001)))],
            "Add an inert scene.");
        foreach (var invalidPrompt in new[] { new string('x', 2001), "", "   ", "bad\0prompt" })
            await RejectsSerializedIllustrationWithoutAdvancingBase(() => Invoke(original with {
                Operations = [original.Operations[0] with { Scene = original.Operations[0].Scene! with { IllustrationPrompt = invalidPrompt } }]
            }));
        await RejectsSerializedIllustrationWithoutAdvancingBase(() => Invoke(original with {
            Operations = [original.Operations[0] with { Scene = original.Operations[0].Scene! with
            {
                IllustrationPrompt = "Bounded illustration",
                Visual = new(VisualPreset.ArchitectureFlowEmphasis, [], [])
            } }]
        }));
        var missingId = original with {
            Operations = [original.Operations[0] with { ItemId = null, Scene = original.Operations[0].Scene! with { IllustrationPrompt = null } }]
        };
        await RejectsMissingSceneIdentityWithoutAdvancingBase(() => Invoke(missingId), tool);
        var secondCitation = new MediaCitation("scene-two", sourceId, "section-2");
        var firstCitation = new MediaCitation("scene-one", sourceId, "section-1");
        var valid = original with {
            Operations =
            [
                new(MediaDraftOperationKind.AddScene, "scene-one",
                    Scene: original.Operations[0].Scene! with { IllustrationPrompt = new string('x', 2000) }),
                new(MediaDraftOperationKind.AddScene, "scene-two",
                    Scene: new(TemplateKind.Title, "Two", "Approval", "Approval remains separate."),
                    Citations: [firstCitation, secondCitation])
            ]
        };
        var misplaced = JsonSerializer.SerializeToNode(valid, Json.Options)!.AsObject();
        misplaced["citations"] = JsonSerializer.SerializeToNode(new[] { firstCitation, secondCitation }, Json.Options);
        await RejectsMisplacedTransactionCitationsWithoutAdvancingBase();
        var response = await Invoke(valid);
        var acknowledgment = JsonSerializer.SerializeToElement(response, Json.Options)
            .Deserialize<MediaDraftAcknowledgement>(Json.Options)!;
        SerializedAtomicScenesRetainCompleteCitations(acknowledgment, state, firstCitation, secondCitation);
        var next = valid with {
            OperationId = "op-next",
            BaseVersion = acknowledgment.NextVersion,
            BaseSha256 = acknowledgment.NextSha256,
            Operations = [new(MediaDraftOperationKind.SetItemTarget, "scene-one", TargetSeconds: 30)]
        };
        var nextResponse = JsonSerializer.SerializeToElement(await Invoke(next), Json.Options)
            .Deserialize<MediaDraftAcknowledgement>(Json.Options)!;
        SerializedContinuationUsesActualAcknowledgment(nextResponse, acknowledgment);
        tool.End();
        await OwnedLargeAcknowledgementProof(state);
        await OutputBoundAudioProof();
        await NativeFunctionsProof();
        await NativeMotionProof();

        async Task<object?> Invoke(MediaDraftTransaction transaction) =>
            await tool.Function.InvokeAsync(new AIFunctionArguments {
                ["transaction"] = JsonSerializer.SerializeToElement(transaction, Json.Options)
            });
        async Task RejectsMisplacedTransactionCitationsWithoutAdvancingBase()
        {
            try
            {
                await tool.Function.InvokeAsync(new AIFunctionArguments {
                    ["transaction"] = JsonSerializer.SerializeToElement(misplaced, Json.Options)
                });
            }
            catch (JsonException)
            {
                Require(tool.Function.JsonSchema.GetRawText().Contains("never transaction.citations", StringComparison.Ordinal)
                    && prompt.Contains("transaction.operations[i].citations", StringComparison.Ordinal),
                    nameof(RejectsMisplacedTransactionCitationsWithoutAdvancingBase));
                return;
            }
            throw new InvalidOperationException("Misplaced transaction citations were accepted.");
        }
    }
    private static async Task OwnedLargeAcknowledgementProof(MediaDraftState initial)
    {
        var tool = new MediaDraftTool();
        var state = initial with {
            Messages = Enumerable.Range(0, 3)
            .Select(_ => new MediaDraftMessage("user", new string('x', 7000), DateTimeOffset.UnixEpoch)).ToArray()
        };
        tool.Begin(state, "Create one cited scene.", default);
        var transaction = new MediaDraftTransaction("op-large-owned", state.DraftId, state.ThreadId!, state.ActiveRunId!,
            state.Version, state.ContentSha256,
            [new(MediaDraftOperationKind.AddScene, "scene-large-owned", Scene: new(TemplateKind.Title, "Owned state",
                "Only the actual callback can advance the draft.", "A preview notice is not serialized application state."),
                Citations: [new("scene-large-owned", state.Frame.Sources[0].Id, "section-1")])], "Add cited scene.");
        var args = new AIFunctionArguments { ["transaction"] = JsonSerializer.SerializeToElement(transaction, Json.Options) };
        var pending = new Dictionary<string, string>(StringComparer.Ordinal);
        var call = new AgentResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("large-call", MediaDraftTool.ToolName, args)]);
        CopilotAuthoringAgent.BindOwnedAcknowledgements(call, tool, pending);
        var actual = JsonSerializer.SerializeToElement(await tool.Function.InvokeAsync(args), Json.Options)
            .Deserialize<MediaDraftAcknowledgement>(Json.Options)!;
        if (System.Text.Encoding.UTF8.GetByteCount(Json.Text(actual)) <= 20_000)
            throw new Exception("Large acknowledgment regression did not reproduce the observed output-size condition.");
        var result = new AgentResponseUpdate(ChatRole.Tool, [new FunctionResultContent("large-call",
            "Output too large to read at once (22.6 KB). Saved to: unavailable-sdk-preview.txt")]);
        CopilotAuthoringAgent.BindOwnedAcknowledgements(result, tool, pending);
        var bound = ((FunctionResultContent)result.Contents[0]).Result as MediaDraftAcknowledgement;
        if (bound is null || Json.Text(bound) != Json.Text(actual) || pending.Count != 0
            || bound.Transaction.Operations[0].Citations?.Single().SectionId != "section-1")
            throw new Exception("The complete callback-owned state/citations were not retained independently of the SDK display preview.");
        foreach (var operationId in new[] { transaction.OperationId, "op-never-executed" })
        {
            var freshCall = "unowned-" + operationId;
            pending.Add(freshCall, operationId);
            try
            {
                var fake = new AgentResponseUpdate(ChatRole.Tool, [new FunctionResultContent(freshCall, Json.Text(actual))]);
                CopilotAuthoringAgent.BindOwnedAcknowledgements(fake, tool, pending);
                throw new Exception("Unexecuted or already-consumed callback acknowledgment was accepted.");
            }
            catch (VideoException error) when (error.Code == "draft.tool-result") { }
        }
        pending.Add("failed-call", "op-failed");
        var failure = new AgentResponseUpdate(ChatRole.Tool,
            [new FunctionResultContent("failed-call", "Error: actual tool failure")]);
        CopilotAuthoringAgent.BindOwnedAcknowledgements(failure, tool, pending);
        if (((FunctionResultContent)failure.Contents[0]).Result is not string)
            throw new Exception("An actual callback failure was converted into success.");
        tool.End();
        Console.WriteLine("PASS oversized Copilot display-preview regression: complete application-owned acknowledgment/citations, exact call/operation binding, consumed/unexecuted rejection and preserved failure; no SDK file read or model call.");
    }
    private static async Task NativeMotionProof()
    {
        var motion = JsonSerializer.Deserialize<MotionVisual>(
            File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "fixtures", "rain-motion.json")), Json.Options)!;
        var content = new MediaProjectContent("Rain fixture", "", "", "", MediaOutput.Both, 60,
            [new("part-one", "Water evaporates; moist air rises and cools. Clouds contain liquid droplets.", 0)],
            new("Rain fixture", [new(TemplateKind.Diagram, "Rain", "Mechanism", "Original narration."),
                new(TemplateKind.Title, "Unchanged", "Unchanged text", "Unchanged narration.")]));
        var frame = MediaDraftRules.FromContent(content);
        var state = new MediaDraftState(1, "draft-" + new string('d', 32), null, 0, 0,
            Files.Fingerprint(frame), frame, [], [], "run-motion", "thread-motion");
        var tool = new MediaDraftTool();
        tool.Begin(state, "Animate only the rain mechanism.", default, nativeFunctions: true);
        Require(!tool.FunctionsForOutput(MediaOutput.Audio).Any(t => t.Name is "set_scene_motion" or "clear_scene_motion"),
            "IndependentAudioDoesNotAcquireMotionTools");
        var set = (AIFunction)tool.FunctionsForOutput(MediaOutput.Both).Single(t => t.Name == "set_scene_motion");
        Require(set.JsonSchema.GetRawText().Contains("entities") && !set.JsonSchema.GetRawText().Contains("baseSha256"),
            "MotionSchemaIsFrameworkGeneratedAndHostBindsAuthority");
        var id = frame.SceneIds[0];
        var result = JsonSerializer.SerializeToElement(await set.InvokeAsync(new AIFunctionArguments { ["sceneId"] = id, ["motion"] = JsonSerializer.SerializeToElement(motion, Json.Options) }), Json.Options)
            .Deserialize<MediaDraftAcknowledgement>(Json.Options)!;
        Require(Json.Text(result.CurrentDraft.Frame.Content.AudioScript) == Json.Text(content.AudioScript)
            && result.CurrentDraft.Frame.Content.Storyboard!.Scenes[0].Script == "Original narration."
            && result.CurrentDraft.Frame.Content.Storyboard.Scenes[1] == content.Storyboard!.Scenes[1]
            && Json.Text(result.CurrentDraft.Frame.Content.Storyboard.Scenes[0].Motion) == Json.Text(motion)
            && result.Transaction.BaseSha256 == state.ContentSha256, "MotionPreservesAudioOtherScenesAndNarration");
        var clear = (AIFunction)tool.FunctionsForOutput(MediaOutput.Both).Single(t => t.Name == "clear_scene_motion");
        var cleared = JsonSerializer.SerializeToElement(await clear.InvokeAsync(new AIFunctionArguments { ["sceneId"] = id }), Json.Options)
            .Deserialize<MediaDraftAcknowledgement>(Json.Options)!;
        Require(cleared.Transaction.BaseVersion == result.NextVersion
            && Json.Text(cleared.CurrentDraft.Frame.Content) == Json.Text(content), "ClearMotionRestoresExactOriginalContent");
        tool.End();
        var events = new List<AGUI.Abstractions.BaseEvent>();
        var input = new AGUI.Abstractions.RunAgentInput {
            ThreadId = state.ThreadId!, RunId = state.ActiveRunId!,
            State = JsonSerializer.SerializeToElement(state, Json.Options), Messages = [], Tools = [], Context = []
        };
        await foreach (var item in new MediaAgentTransport(new MediaCanvasFixtureAgent { MotionEdit = motion, ClearMotionAfterSet = true }, state.ThreadId!).SendAsync(input)) events.Add(item);
        var snapshots = events.OfType<AGUI.Abstractions.StateSnapshotEvent>()
            .Select(e => e.Snapshot.Deserialize<MediaDraftAcknowledgement>(Json.Options)!).ToArray();
        Require(events.Last() is AGUI.Abstractions.RunFinishedEvent && snapshots.Length == 2
            && snapshots[0].CurrentDraft.Frame.Content.Storyboard!.Scenes[0].Motion is not null
            && snapshots[1].Transaction.BaseVersion == snapshots[0].NextVersion
            && Json.Text(snapshots[1].CurrentDraft.Frame.Content) == Json.Text(content),
            "NativeMotionSetAndClearReachActualTransportSnapshotsWithExactContinuation");
        foreach (var invalid in new[]
        {
            new MediaCanvasFixtureAgent { MotionEdit = motion, MotionCallNameOverride = "unregistered_motion_tool" },
            new MediaCanvasFixtureAgent { MotionEdit = motion, OmitMotionAcknowledgment = true }
        })
        {
            var refused = new List<AGUI.Abstractions.BaseEvent>();
            await foreach (var item in new MediaAgentTransport(invalid, state.ThreadId!).SendAsync(input)) refused.Add(item);
            Require(refused.Last() is AGUI.Abstractions.RunErrorEvent
                && !refused.OfType<AGUI.Abstractions.RunFinishedEvent>().Any()
                && !refused.OfType<AGUI.Abstractions.StateSnapshotEvent>().Any(),
                "UnknownOrUnacknowledgedMotionCannotFinishAsSuccessfulNoOp");
        }
    }
    private static async Task NativeFunctionsProof()
    {
        var frame = MediaDraftRules.FromContent(new("Native functions", "", "", "", MediaOutput.Audio, 300,
            [new("part-one", "Original first narration.", 24000), new("part-two", "Unchanged second narration.", 12000)], null));
        frame = frame with {
            Sources = [new("source-fixture", new('a', 64), new('b', 64), new('c', 64))],
            Citations = [new("part-one", "source-fixture", "section-1"), new("part-two", "source-fixture", "section-2")]
        };
        var state = new MediaDraftState(1, "draft-" + new string('c', 32), null, 0, 0,
            Files.Fingerprint(frame), frame, [], [], "run-native", "thread-native");
        var tool = new MediaDraftTool();
        Require(tool.FunctionsForOutput(MediaOutput.Both).Select(t => t.Name).Order()
            .SequenceEqual(MediaDraftTool.NativeToolNames.Order()), "EveryAdvertisedNativeToolHasTransportStateAndFailureMapping");
        tool.Begin(state, "Simplify only the first part.", default, nativeFunctions: true);
        foreach (var output in new[] { MediaOutput.Audio, MediaOutput.Video, MediaOutput.Both })
        {
            var names = tool.FunctionsForOutput(output).Select(t => t.Name).ToArray();
            Require(names.Contains(MediaDraftTool.AddAudioName) == (output != MediaOutput.Video)
                && names.Contains(MediaDraftTool.AddVideoName) == (output != MediaOutput.Audio)
                && !names.Contains(MediaDraftTool.ToolName), "NativeFunctionsAreOutputSpecific");
        }
        var update = (AIFunction)tool.FunctionsForOutput(MediaOutput.Audio).Single(t => t.Name == MediaDraftTool.UpdateAudioName);
        Require(!update.JsonSchema.GetRawText().Contains("transaction")
            && !update.JsonSchema.GetRawText().Contains("allOf"), "NativeSchemaIsFrameworkGenerated");
        var acknowledgment = JsonSerializer.SerializeToElement(await update.InvokeAsync(new AIFunctionArguments { ["partId"] = "part-one", ["narration"] = "Simpler first narration.", ["sources"] = Array.Empty<DraftSourceReference>() }), Json.Options)
            .Deserialize<MediaDraftAcknowledgement>(Json.Options)!;
        Require(acknowledgment.Transaction.RunId == state.ActiveRunId
            && acknowledgment.Transaction.BaseVersion == state.Version
            && acknowledgment.Transaction.BaseSha256 == state.ContentSha256
            && acknowledgment.CurrentDraft.Frame.Content.AudioScript![0].PauseAfterSamples == 24000
            && acknowledgment.CurrentDraft.Frame.Content.AudioScript[1] == frame.Content.AudioScript![1]
            && acknowledgment.CurrentDraft.Frame.Citations.SequenceEqual(frame.Citations.Where(c => c.ItemId == "part-two")),
            "NativeHostBindsAuthorityAndPreservesPauseOtherPartsAndCitations");
        var add = (AIFunction)tool.FunctionsForOutput(MediaOutput.Audio).Single(t => t.Name == MediaDraftTool.AddAudioName);
        var added = JsonSerializer.SerializeToElement(await add.InvokeAsync(new AIFunctionArguments { ["narration"] = "New narration." }), Json.Options).Deserialize<MediaDraftAcknowledgement>(Json.Options)!;
        Require(added.Transaction.BaseVersion == acknowledgment.NextVersion
            && added.CurrentDraft.Frame.Content.AudioScript![2].Id.StartsWith("part-")
            && added.Transaction.OperationId.StartsWith("op-"), "NativeServiceGeneratesIdentitiesAndBindsContinuation");
        tool.End();
    }
    private static async Task OutputBoundAudioProof()
    {
        var frame = MediaDraftRules.FromContent(new("Output fixture", "", "", "", MediaOutput.Video, 300, null, null));
        var state = new MediaDraftState(1, "draft-" + new string('b', 32), null, 0, 0,
            Files.Fingerprint(frame), frame, [], [], "run-audio-bound", "thread-audio-bound");
        var tool = new MediaDraftTool();
        var prompt = tool.Begin(state, "Create an audio explanation.", default);
        using var promptDocument = JsonDocument.Parse(prompt);
        Require(promptDocument.RootElement.GetProperty("selectedOutput").GetString() == "Video"
            && prompt.Contains("overrides medium wording"),
            "ExplicitSelectedOutputOverridesConflictingBrief");
        foreach (var output in new[] { MediaOutput.Video, MediaOutput.Audio, MediaOutput.Both })
        {
            var schema = tool.ForOutput(output).JsonSchema;
            var operation = OperationSchemas(schema).Single();
            var kinds = operation.GetProperty("properties").GetProperty("kind").GetProperty("enum")
                .EnumerateArray().Select(v => v.GetString()).ToArray();
            Require(kinds.Contains("AddPart") == (output != MediaOutput.Video)
                && kinds.Contains("AddScene") == (output != MediaOutput.Audio)
                && kinds.Contains("UpdateBrief"), "ActualFunctionSchemaRestricts" + output);
        }
        var transaction = new MediaDraftTransaction("op-audio-bound", state.DraftId, state.ThreadId!, state.ActiveRunId!,
            state.Version, state.ContentSha256, [new(MediaDraftOperationKind.AddPart, Part: new("part-one", "Owned text.", 0))],
            "Synthetic audio edit.");
        try
        {
            await tool.Function.InvokeAsync(new AIFunctionArguments { ["transaction"] = JsonSerializer.SerializeToElement(transaction, Json.Options) });
            throw new InvalidOperationException("Video accepted audio.");
        }
        catch (VideoException error) when (error.Code == "draft.part-output")
        { Require(tool.LastRejection?.Code == error.Code, "VideoAudioMismatchHasExactDiagnosis"); }
        var repaired = transaction with {
            Operations = [new(MediaDraftOperationKind.AddScene, "scene-repaired",
            Scene: new(TemplateKind.Title, "Owned title", "Owned text", "Owned narration."))]
        };
        var reply = JsonSerializer.SerializeToElement(await tool.Function.InvokeAsync(new AIFunctionArguments {
            ["transaction"] = JsonSerializer.SerializeToElement(repaired, Json.Options)
        }), Json.Options)
            .Deserialize<MediaDraftAcknowledgement>(Json.Options)!;
        Require(reply.NextVersion == state.Version + 1 && reply.CurrentDraft.Frame.Content.AudioScript is null
            && tool.LastRejection is null, "RejectedWrongOutputIsAtomicAndSameBaseCanRecover");
        tool.End();
        foreach (var (part, code) in new (AudioScriptPart?, string)[] {
            (null, "draft.part-missing"), (new("Part_One", "Owned text.", 0), "draft.part-id"),
            (new("part-one", "Owned text.", 0), "draft.part-duplicate") })
        {
            var audio = MediaDraftRules.FromContent(new("Audio fixture", "", "", "", MediaOutput.Audio, 300,
                [new("part-one", "Original owned text.", 0)], null));
            try { MediaDraftRules.Apply(audio, [new(MediaDraftOperationKind.AddPart, Part: part)]); throw new Exception("Invalid audio edit accepted."); }
            catch (VideoException error) when (error.Code == code) { }
        }
        Console.WriteLine("PASS distinct missing/invalid/duplicate audio identity errors; original content and output preserved.");
    }
    private static IEnumerable<JsonElement> OperationSchemas(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("properties", out var fields) && fields.TryGetProperty("kind", out _)
                && fields.TryGetProperty("part", out _)) yield return node;
            foreach (var property in node.EnumerateObject())
                foreach (var child in OperationSchemas(property.Value)) yield return child;
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray())
                foreach (var child in OperationSchemas(item)) yield return child;
    }

    private static void SerializedAtomicScenesRetainCompleteCitations(MediaDraftAcknowledgement acknowledgment,
        MediaDraftState state, MediaCitation firstCitation, MediaCitation secondCitation)
    {
        Require(acknowledgment.NextVersion == state.Version + 1
            && acknowledgment.CurrentDraft.Frame.SceneIds.SequenceEqual(new[] { "scene-one", "scene-two" })
            && acknowledgment.CurrentDraft.Frame.Citations.SequenceEqual(new[] { firstCitation, secondCitation })
            && acknowledgment.CurrentDraft.Frame.Content.Storyboard!.Scenes[0].IllustrationPrompt?.Length == 2000
            && acknowledgment.NextSha256 == Files.Fingerprint(acknowledgment.CurrentDraft.Frame),
            nameof(SerializedAtomicScenesRetainCompleteCitations));
    }
    private static void SerializedContinuationUsesActualAcknowledgment(MediaDraftAcknowledgement nextResponse,
        MediaDraftAcknowledgement acknowledgment)
    {
        Require(nextResponse.NextVersion == acknowledgment.NextVersion + 1
            && nextResponse.CurrentDraft.Frame.ItemTargets["scene-one"] == 30
            && nextResponse.CurrentDraft.Frame.Citations.SequenceEqual(acknowledgment.CurrentDraft.Frame.Citations),
            nameof(SerializedContinuationUsesActualAcknowledgment));
    }

    private static IEnumerable<JsonElement> IllustrationSchemas(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals("illustrationPrompt", StringComparison.OrdinalIgnoreCase))
                    yield return property.Value;
                else
                    foreach (var schema in IllustrationSchemas(property.Value)) yield return schema;
            }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray())
                foreach (var schema in IllustrationSchemas(child)) yield return schema;
    }
    private static void Require(bool value, string name)
    {
        if (!value) throw new InvalidOperationException(name);
        Console.WriteLine("PASS " + name);
    }
    private static void AdvertisesBoundedIllustrationPrompt(AIFunction function)
    {
        var schemas = IllustrationSchemas(function.JsonSchema).ToArray();
        Require(schemas.Length > 0 && schemas.All(s => s.GetProperty("type").EnumerateArray().Select(t => t.GetString()).SequenceEqual(["string", "null"])
            && s.GetProperty("minLength").GetInt32() == 1 && s.GetProperty("maxLength").GetInt32() == 2000),
            nameof(AdvertisesBoundedIllustrationPrompt));
    }
    private static void AdvertisesRequiredSceneIdentity(AIFunction function)
    {
        var rules = Conditions(function.JsonSchema).ToArray();
        var addScene = rules.Single(rule => rule.GetProperty("if").GetProperty("properties").GetProperty("kind").GetProperty("enum")
            .EnumerateArray().Any(kind => kind.GetString() == "AddScene")).GetProperty("then");
        Require(addScene.GetProperty("required").EnumerateArray().Select(field => field.GetString()).SequenceEqual(["itemId", "scene"])
            && addScene.GetProperty("properties").GetProperty("itemId").GetProperty("type").GetString() == "string"
            && addScene.GetProperty("properties").GetProperty("itemId").GetProperty("minLength").GetInt32() == 1,
            nameof(AdvertisesRequiredSceneIdentity));
    }
    private static IEnumerable<JsonElement> Conditions(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("if", out _)) yield return node;
            else foreach (var property in node.EnumerateObject())
                foreach (var child in Conditions(property.Value)) yield return child;
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray())
                foreach (var child in Conditions(item)) yield return child;
    }
    private static async Task RejectsMissingSceneIdentityWithoutAdvancingBase(Func<Task<object?>> invoke, MediaDraftTool tool)
    {
        try { await invoke(); throw new InvalidOperationException("A missing scene identity was accepted."); }
        catch (VideoException error) when (error.Code == "draft.scene-id")
        {
            Require(tool.LastRejection?.Code == error.Code && tool.LastRejection.Message.Contains("not applied")
                && tool.LastRejection.Message.Contains("no generation"), nameof(RejectsMissingSceneIdentityWithoutAdvancingBase));
        }
    }
    private static void DeclaresCompleteCitationReplacement(string prompt) =>
        Require(prompt.Contains("complete draft citation set", StringComparison.Ordinal),
            nameof(DeclaresCompleteCitationReplacement));
    private static async Task RejectsSerializedIllustrationWithoutAdvancingBase(Func<Task<object?>> invoke)
    {
        try
        {
            await invoke();
            throw new InvalidOperationException("An invalid illustration prompt was accepted.");
        }
        catch (VideoException error) when (error.Code == "draft.items")
        {
            Console.WriteLine("PASS " + nameof(RejectsSerializedIllustrationWithoutAdvancingBase));
        }
    }
}
