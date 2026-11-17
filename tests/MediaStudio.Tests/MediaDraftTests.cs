using VideoProduction;
using VideoProduction.Providers;

internal static class MediaDraftTests
{
    public static int Run(string root)
    {
        var count = 0;
        void Check(bool test, string name) { if (!test) throw new Exception(name); count++; }
        void Reject(Action action, string code)
        {
            try { action(); throw new Exception("Expected " + code); }
            catch (VideoException error) when (error.Code == code) { count++; }
        }
        var production = new FileProductionStore(Path.Combine(root, "draft-state"));
        var store = new MediaDraftStore(production);
        var draft = store.New(new("", "", "", "", MediaOutput.Both, 120, null, null));
        var longScript = string.Join(" ", Enumerable.Repeat("attention", 869));
        var timingContent = new MediaProjectContent("Paper", "Explain the paper.", "", "", MediaOutput.Both,
            300, [new("part-paper", longScript, 0)],
            new("Paper", [new(TemplateKind.Title, "Paper", "", "Explain attention.")]));
        var estimates = MediaDraftRules.EstimateNarration(timingContent);
        Check(estimates is [{ Output: "Audio", Words: 869, Seconds: 348 }, { Output: "Video narration", Words: 2, Seconds: 1 }],
            "IndependentAudioAndVideoWordEstimatesExposePaperOverrunWithoutChangingMaximum");
        Check(MediaDraftRules.EstimateNarration(timingContent with { Outputs = MediaOutput.Audio, Storyboard = null })
                is [{ Output: "Audio", Words: 869 }]
            && MediaDraftRules.EstimateNarration(timingContent with { Outputs = MediaOutput.Video, AudioScript = null })
                is [{ Output: "Video narration", Words: 2 }],
            "TimingEstimatesRespectIndependentOutputIntent");
        Check(timingContent.MaximumSeconds == 300
            && MediaDraftRules.EstimateNarration(timingContent with { MaximumSeconds = 600 })[0].Seconds == 348,
            "ExplicitMaximumChangesDoNotAlterNarrationOrPretendEstimateIsMeasured");
        draft = store.Edit(draft, MediaDraftRules.Apply(draft.Frame, [
            new(MediaDraftOperationKind.AddScene, "scene-intro", Scene: new(TemplateKind.Title, "", "", "")),
            new(MediaDraftOperationKind.AddPart, Part: new("part-explanation", "", 0))
        ]), "Added incomplete video and audio");
        Check(draft.Frame.Content.Storyboard!.Scenes[0].Script == "" && draft.Frame.Content.AudioScript![0].Text == "",
            "IncompleteTypingPersistsWithoutProductionReadiness");
        Check(store.Read(draft.DraftId).ContentSha256 == draft.ContentSha256, "RestartRestoresDraftAndStableIds");
        var collisionRun = store.BeginRun(draft, "run-collisions", "thread-collisions", "Reject cross-medium identity collisions.");
        var missingIdentity = new MediaDraftTransaction("missing-scene-identity", collisionRun.DraftId,
            collisionRun.ThreadId!, collisionRun.ActiveRunId!, collisionRun.Version, collisionRun.ContentSha256,
            [new(MediaDraftOperationKind.UpdateScene, "scene-intro", Scene: new(TemplateKind.Title, "Must not persist", "", "")),
             new(MediaDraftOperationKind.AddScene, Scene: new(TemplateKind.Title, "No identity", "", ""))],
            "Reject the whole transaction, including its otherwise valid first edit.");
        Reject(() => store.Apply(collisionRun, missingIdentity), "draft.scene-id");
        Check(store.Read(draft.DraftId).ContentSha256 == collisionRun.ContentSha256
            && store.Read(draft.DraftId).Version == collisionRun.Version
            && store.Read(draft.DraftId).Frame.Content.Storyboard!.Scenes[0].Title == "",
            "AddSceneMissingIdentityRejectedBeforePartialEdits");
        MediaDraftOperation[] collisions =
        [
            new(MediaDraftOperationKind.AddScene, "part-explanation", Scene: new(TemplateKind.Title, "", "", "")),
            new(MediaDraftOperationKind.AddPart, Part: new("scene-intro", "", 0)),
            new(MediaDraftOperationKind.ReplaceVideo, Storyboard: new("Replacement", [new(TemplateKind.Title, "", "", "")]),
                SceneIds: ["part-explanation"]),
            new(MediaDraftOperationKind.ReplaceAudio, Parts: [new("scene-intro", "", 0)])
        ];
        for (var index = 0; index < collisions.Length; index++)
        {
            var transaction = new MediaDraftTransaction("collision-" + index, collisionRun.DraftId,
                collisionRun.ThreadId!, collisionRun.ActiveRunId!, collisionRun.Version, collisionRun.ContentSha256,
                [collisions[index]], "Cross-medium collision fixture");
            Reject(() => store.Apply(collisionRun, transaction), "draft.items");
        }
        Check(store.Read(draft.DraftId).Version == collisionRun.Version
            && store.Read(draft.DraftId).ContentSha256 == collisionRun.ContentSha256,
            "BothAddSceneAddPartAndReplacementCollisionsAreRejectedAtomically");
        draft = store.FinishRun(draft.DraftId, collisionRun.ActiveRunId!, "failed");
        Reject(() => MediaProjectStore.Validate(draft.Frame.Content, ready: true), "media.content");
        var stale = draft;
        draft = store.Edit(draft, MediaDraftRules.Apply(draft.Frame, [
            new(MediaDraftOperationKind.UpdateScene, "scene-intro", Scene: new(TemplateKind.Title, "Intro", "", "Explain revisions.")),
            new(MediaDraftOperationKind.AddScene, "scene-final", Scene: new(TemplateKind.Title, "Closing", "", "Keep approvals explicit.")),
            new(MediaDraftOperationKind.SetItemTarget, "scene-intro", TargetSeconds: 40),
            new(MediaDraftOperationKind.ReviseDirection, Direction: new(180))
        ]), "Edited scenes and targets");
        Check(draft.Frame.Content.MaximumSeconds == 120 && draft.Frame.Direction.TargetSeconds == 180
            && draft.Frame.ItemTargets["scene-intro"] == 40 && draft.Frame.Content.AudioScript![0].Text == "",
            "CreativeTargetsDoNotChangeSafetyMaximumOrIndependentAudio");
        Reject(() => store.Edit(stale, stale.Frame, "Stale"), "draft.stale");
        var moved = store.Edit(draft, MediaDraftRules.Apply(draft.Frame,
            [new(MediaDraftOperationKind.MoveScene, "scene-final", "scene-intro")]), "Moved closing");
        Check(moved.Frame.SceneIds.SequenceEqual(new[] { "scene-final", "scene-intro" }), "StableIdReorderPreservesContent");
        var undone = store.Undo(moved);
        Check(undone.Frame.SceneIds.SequenceEqual(draft.Frame.SceneIds), "UndoRestoresAtomicFrame");
        draft = store.Undo(undone, redo: true);
        Check(draft.Frame.SceneIds.SequenceEqual(moved.Frame.SceneIds), "RedoRestoresStableIdentities");
        var run = store.BeginRun(draft, "run-one", "thread-one", "Add a scene without generating.");
        var selected = store.Select(run, "scene-final");
        Check(selected.SelectedItemId == "scene-final" && store.Read(run.DraftId).SelectedItemId == "scene-final"
            && selected.Version == run.Version && selected.ContentSha256 == run.ContentSha256 && selected.ActiveRunId == run.ActiveRunId,
            "SelectionPersistsWhileAuthoringWithoutChangingTransactionIdentity");
        MediaDraftTransaction tx = new("operation-one", run.DraftId, "thread-one", "run-one", run.Version,
            run.ContentSha256, [new(MediaDraftOperationKind.DeleteScene, "scene-final")], "Deleted closing scene");
        Reject(() => store.Apply(run, tx with { ThreadId = "foreign" }), "draft.transaction");
        Reject(() => store.Apply(run, tx with { BaseSha256 = new string('0', 64) }), "draft.transaction");
        var changed = store.Apply(run, tx);
        Check(changed.Version == run.Version + 1 && changed.Frame.SceneIds.SequenceEqual(new[] { "scene-intro" }),
            "CommittedAgentTransactionUpdatesBeforeRunFinishes");
        Check(changed.SelectedItemId is null, "DeletingSelectedItemClearsNavigationWithoutStaleIdentity");
        Reject(() => store.Apply(changed, tx), "draft.transaction");
        var recovered = store.Recover(changed.DraftId);
        Check(recovered.RunStatus == "interrupted" && recovered.ActiveRunId is null
            && recovered.Frame.SceneIds.SequenceEqual(changed.Frame.SceneIds), "InterruptedRunRetainsCommittedEditsWithoutReplay");
        Reject(() => store.Apply(recovered, tx with {
            OperationId = "operation-two",
            BaseVersion = recovered.Version,
            BaseSha256 = recovered.ContentSha256
        }), "draft.transaction");
        Reject(() => MediaDraftRules.Apply(recovered.Frame, [new(MediaDraftOperationKind.UpdateScene,
            "scene-intro", Scene: new(TemplateKind.Title, "Unsafe", "", "", IllustrationPrompt: "external\0"))]), "draft.items");
        Reject(() => MediaDraftRules.Apply(recovered.Frame, [new(MediaDraftOperationKind.DeleteScene, "foreign")]), "draft.operation");
        var revisionContent = recovered.Frame.Content with {
            Title = "Saved",
            Prompt = "Explain revisions.",
            AudioScript = [new("part-explanation", "Independent audio explanation.", 0)]
        };
        var prepared = store.Edit(recovered, recovered.Frame with { Content = revisionContent }, "Completed brief");
        prepared = store.Edit(prepared, prepared.Frame with {
            Sources = [new("source-owned", new string('1', 64), new string('2', 64), new string('3', 64))],
            Citations = [new("scene-intro", "source-owned", "section-page-1"),
                new("part-explanation", "source-owned", "section-page-2")]
        }, "Bound source provenance and both-medium citations");
        prepared = store.Edit(prepared, prepared.Frame with { Content = MediaDraftRules.ProductionContent(prepared.Frame) }, "Flushed production content");
        var projects = new MediaProjectStore(production);
        var saved = projects.Save(null, 0, prepared.Frame.Content);
        prepared = store.BindSaved(prepared, saved);
        var hydrated = store.New(saved.Content, saved);
        Check(hydrated.DraftId != prepared.DraftId && hydrated.Frame.SceneIds.SequenceEqual(prepared.Frame.SceneIds)
            && hydrated.Frame.Direction == prepared.Frame.Direction && hydrated.ContentSha256 == prepared.ContentSha256,
            "SavedAuthoringSidecarRestoresStableIdsAndDirectionWithoutWorkingJournalDependency");
        Check(hydrated.Frame.Sources.SequenceEqual(prepared.Frame.Sources)
            && hydrated.Frame.Citations.SequenceEqual(prepared.Frame.Citations)
            && hydrated.Frame.ItemTargets.OrderBy(p => p.Key).SequenceEqual(prepared.Frame.ItemTargets.OrderBy(p => p.Key)),
            "SavedAuthoringSidecarPreservesSourceHashesBothMediumCitationsAndTargetsWithoutJournal");
        var afterSaveUndo = store.Undo(prepared);
        Check(afterSaveUndo.ProjectId == saved.Id && afterSaveUndo.BaseRevision == saved.Revision
            && Files.Fingerprint(afterSaveUndo.Frame.Content) != Files.Fingerprint(saved.Content),
            "UndoAfterSaveCreatesWorkingChangesNotHistoricMutation");
        Check(projects.Read(saved.Id).Content == saved.Content || Files.Fingerprint(projects.Read(saved.Id)) == Files.Fingerprint(saved),
            "SavedProductionRevisionRemainsImmutable");
        Check(Directory.GetDirectories(Path.Combine(root, "draft-state", "attempts")).Length == 0,
            "DraftAndSourceInstructionsCannotApproveOrStartProduction");
        var typing = store.New(new("Typing", "", "", "", MediaOutput.Video, 120, null, null));
        var initialHash = typing.ContentSha256;
        typing = store.Edit(typing, typing.Frame with { Content = typing.Frame.Content with { Prompt = "A" } }, "Edited brief", coalesceKey: "prompt");
        var firstTypingVersion = typing.Version;
        typing = store.Edit(typing, typing.Frame with { Content = typing.Frame.Content with { Prompt = "AB" } }, "Edited brief", coalesceKey: "prompt");
        Check(typing.Version == firstTypingVersion + 1 && store.Read(typing.DraftId).Frame.Content.Prompt == "AB"
            && typing.Messages.Length == 1 && store.Undo(typing).ContentSha256 == initialHash,
            "TypingPersistsEveryVersionButCoalescesOneFieldUndoAndChangeSummary");
        return count;
    }
}
