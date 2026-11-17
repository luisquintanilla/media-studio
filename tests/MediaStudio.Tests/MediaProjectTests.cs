using MediaStudio.Audio;
using VideoProduction;
using VideoProduction.Providers;

internal static class MediaProjectTests
{
    public static async Task<int> Run(string root, OfflineMediaConfiguration? videoFixture = null)
    {
        var assertions = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); assertions++; }
        var state = Path.Combine(root, "media-project-state");
        var production = new FileProductionStore(state);
        var projects = new MediaProjectStore(production);
        var content = new MediaProjectContent("Revision-bound explanation", "Explain the exact saved revision boundary.",
            "Developers", "Distinguish editing from production approval.", MediaOutput.Audio, 10,
            [new("explanation", "Edits become a saved revision before exact production approval.", 2000)], null,
            new("Distinguish authority", "Editing text", "Exact saved data owns approval", "Compare two revisions", "Synthetic fixture, not speech quality"));
        var saved = projects.Save(null, 0, content);
        content.AudioScript![0] = content.AudioScript[0] with { Text = "Unapproved caller mutation" };
        Check(projects.Read(saved.Id).Content.AudioScript![0].Text != content.AudioScript[0].Text,
            "Catalog persistence snapshots caller arrays.");
        var config = new MediaProductionConfiguration(1, state, null, null,
            new("deliberately-no-ffmpeg", "deliberately-no-ffprobe", "no-font", "no-font-hash"));
        var reviewed = MediaApprovals.Approve(saved, config, "explicit-test-reviewer");
        Check(projects.Submission(saved) is null && projects.List().Length == 1, "Save and navigation do not accept production.");
        foreach (var grants in new[] { Array.Empty<MediaStageGrant>(),
            new[] { reviewed.Grants[0] with { PayloadSha256 = new string('a', 64) } },
            new[] { reviewed.Grants[0] with { Operation = "release" } } })
        {
            try { await production.ProduceMedia(reviewed with { Grants = grants }, config); throw new Exception("Expected grant rejection"); }
            catch (VideoException error)
            {
                Check(error.Code == "media.stage-approval" && projects.Submission(saved) is null,
                "Missing/changed/wrong-operation grants never accept production.");
            }
        }
        var result = await production.ProduceMedia(reviewed, config);
        Check(result.Status == "completed", "Independent audio commits with no visual setup or executable resolution.");
        var resolved = production.MediaArtifacts(result.AttemptId);
        Check(resolved.Outputs is [{ Output: "audio" }] && resolved.Outputs[0].Artifacts.Any(a => a.Kind == "audio-master"),
            "Playback resolver exposes only receipt-bound committed audio.");
        Check(new MediaProjectStore(new(state)).Submission(saved)?.AttemptId == result.AttemptId,
            "Reopening resolves the real attempt without replay.");
        Check((await production.ProduceMedia(reviewed, config)).Replay, "Exact approved revision is durably accepted once.");
        var newer = projects.Save(saved.Id, saved.Revision, saved.Content with { Outcome = "Newly reviewed objective" });
        try { projects.Save(saved.Id, saved.Revision, saved.Content); throw new Exception("Expected revision conflict"); }
        catch (VideoException error) { Check(error.Code == "media.revision", "Stale edits do not overwrite a newer saved revision."); }
        try
        {
            await production.ProduceMedia(MediaApprovals.Approve(newer, config, "reviewer"), config with { Verification = null });
            throw new Exception("Expected exact-config rejection");
        }
        catch (VideoException error) { Check(error.Code == "media.approval", "Configuration changes require a new explicit review."); }
        var privateConfig = Path.Combine(root, "audio-only-production.json");
        Json.Write(privateConfig, config);
        PrivateFiles.Protect(privateConfig);
        var cli = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "VideoProduction.Cli", "bin", new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name,
            new DirectoryInfo(AppContext.BaseDirectory).Name, "video-production.dll"));
        var accepted = await new MediaProducerLauncher(production, Files.FindExecutable("dotnet"), cli)
            .AcceptAsync(MediaApprovals.Approve(newer, config, "explicit-subprocess-reviewer"), privateConfig);
        while (accepted.Status is "running" or "cancelling")
        {
            await Task.Delay(50);
            accepted = production.Status(accepted.AttemptId);
        }
        Check(accepted.Status == "completed" && production.MediaArtifacts(accepted.AttemptId).Outputs.Length == 1,
            "Actual producer CLI owns admission/output beyond the initiating call; no pipes or view task own its lifetime.");
        var history = new MediaHistory(production, projects).Read();
        Check(history.Jobs.Length == 2 && history.Artifacts.Length == 2 && history.Issues.Length == 0
            && history.Jobs.Select(j => j.Project.Revision).Order().SequenceEqual(new[] { saved.Revision, newer.Revision })
            && history.Artifacts.All(a => a.Kind == "audio-master" && a.DurationSeconds > 0),
            "GlobalHistoryPreservesAttemptBoundHistoricalRevisionsAndMeasuredAudio");
        if (videoFixture is not null)
        {
            config = config with { Verification = videoFixture };
            var both = projects.Save(null, 0, saved.Content with {
                Outputs = MediaOutput.Both,
                Storyboard = new("A real bounded card", [new(TemplateKind.Diagram, "Saved revision",
                    "Working edits\nSaved revision\nExact approval", "Only the exact saved revision authorizes production.")])
            });
            var complete = await production.ProduceMedia(MediaApprovals.Approve(both, config, "offline-both-reviewer"), config);
            Check(complete.Status == "completed" && production.MediaArtifacts(complete.AttemptId).Outputs.Length == 2,
                "Both mode commits actual independent audio plus a decoded H264/AAC video through the tracked producer.");
            history = new MediaHistory(production, projects).Read();
            Check(history.Issues.Length == 0 && history.Artifacts.Any(a => a.AttemptId == complete.AttemptId
                && a.Kind == "candidate-mp4" && a.DurationSeconds > 0 && a.HasCaptions),
                "GlobalHistoryUsesVerifiedEncodedDurationAndCaptionsWithoutDiscardingVideo");
            var failing = projects.Save(null, 0, both.Content with {
                Storyboard = both.Content.Storyboard! with {
                    Scenes = [both.Content.Storyboard.Scenes[0] with { Text = new string('x', 100) }]
                }
            });
            var partial = await production.ProduceMedia(MediaApprovals.Approve(failing, config, "partial-output-reviewer"), config);
            Check(partial.Status == "failed" && partial.ErrorCode == "verification.card" && partial.Artifacts.Length == 0,
                "Video failure preserves failed overall Both status and frozen all-or-nothing status wire semantics.");
            var reopened = new FileProductionStore(state);
            var partialArtifacts = reopened.MediaArtifacts(partial.AttemptId);
            Check(partialArtifacts.Outputs is [{ Output: "audio" }]
                && partialArtifacts.Outputs[0].Artifacts.All(a => a.Kind != "candidate-mp4"),
                "Fresh playback resolution recovers only the committed successful audio receipt after video failure.");
            history = new MediaHistory(production, projects).Read();
            Check(history.Jobs.Single(j => j.Attempt.Id == partial.AttemptId).Status.Status == "failed"
                && history.Artifacts.Where(a => a.AttemptId == partial.AttemptId).All(a => a.Kind == "audio-master" && a.OverallStatus == "failed"),
                "GlobalHistoryRetainsFailedBothJobWithoutPromotingPartialAudioToSuccess");
            var coverConfig = config with {
                Cover = new(new("https://fixture.openai.azure.com/openai/v1",
                "fixture-model", null, AllowRemote: true, Responses: true),
                new(TestPaths.RepoRoot, Files.FindExecutable("node"),
                    Path.Combine(state, "cover-scratch"), videoFixture.FontFile, videoFixture.FontSha256))
            };
            var coverProject = projects.Save(null, 0, saved.Content with { Title = "Cover failure preserves audio" });
            var coverReview = MediaApprovals.Approve(coverProject, coverConfig, "cover-reviewer");
            Check(coverReview.Grants.Any(g => g.Operation == "generate-library-cover"),
                "Optional cover is an exact configuration-bound approved stage, not an unapproved UI task.");
            try
            {
                await production.ProduceMedia(coverReview, coverConfig with { Cover = coverConfig.Cover! with { Connection = coverConfig.Cover.Connection with { Model = "changed-model" } } },
                    coverFactory: _ => throw new Exception("Changed-model factory invoked"));
                throw new Exception("Expected changed cover model refusal");
            }
            catch (VideoException error)
            {
                Check(error.Code == "media.approval" && projects.Submission(coverProject) is null,
                "Changing the cover model invalidates the exact request before admission or any provider operation.");
            }
            try
            {
                await production.ProduceMedia(coverReview with { Grants = coverReview.Grants.Where(g => g.Operation != "generate-library-cover").ToArray() },
                    coverConfig, coverFactory: _ => throw new Exception("Unauthorized cover factory invoked"));
                throw new Exception("Expected missing cover grant refusal");
            }
            catch (VideoException error)
            {
                Check(error.Code == "media.stage-approval" && projects.Submission(coverProject) is null,
                "Missing cover approval refuses the entire request before admission or image creation.");
            }
            try
            {
                await production.ProduceMedia(coverReview, coverConfig);
                throw new Exception("Expected offline live-provider refusal");
            }
            catch (VideoException error)
            {
                Check(error.Code == "verification.cover" && projects.Submission(coverProject) is null,
                "Offline production cannot resolve a live image client without an explicit fixture.");
            }
            var coverFailure = await production.ProduceMedia(coverReview, coverConfig,
                coverFactory: _ => throw new VideoException("cover.fixture", "Explicit synthetic image failure"));
            var coverOutcomes = Json.Read<MediaOutputReport>(Path.Combine(state, "attempts", coverFailure.AttemptId, "output-outcomes.json"));
            Check(coverFailure.Status == "failed" && coverFailure.ErrorCode == "cover.fixture"
                && production.MediaArtifacts(coverFailure.AttemptId).Outputs is [{ Output: "audio" }]
                && coverOutcomes.Outputs.Any(o => o.Output == "cover" && o.Status == "failed"),
                "Requested cover failure is not overall success and cannot discard independently committed audio.");
            var nullFactoryProject = projects.Save(null, 0, saved.Content with { Title = "No live image fallback" });
            var nullFactory = await production.ProduceMedia(MediaApprovals.Approve(nullFactoryProject, coverConfig, "null-factory-reviewer"),
                coverConfig, coverFactory: _ => null!);
            Check(nullFactory.Status == "failed" && nullFactory.ErrorCode == "cover.factory"
                && production.MediaArtifacts(nullFactory.AttemptId).Outputs is [{ Output: "audio" }],
                "An invalid injected image factory fails explicitly and never resolves a live provider as fallback.");
            var partialCoverProject = projects.Save(null, 0, failing.Content with { Title = "Two failures preserve the original video error" });
            var partialCover = await production.ProduceMedia(MediaApprovals.Approve(partialCoverProject, coverConfig, "partial-cover-reviewer"),
                coverConfig, coverFactory: _ => throw new VideoException("cover.fixture", "Explicit second-stage failure"));
            Check(partialCover.Status == "failed" && partialCover.ErrorCode == "verification.card"
                && partialCover.Stage == partial.Stage
                && production.MediaArtifacts(partialCover.AttemptId).Outputs is [{ Output: "audio" }],
                "A later cover failure never hides the original failed video stage or destroys its verified audio.");
            using (var cancel = new CancellationTokenSource())
            {
                var cancelledProject = projects.Save(null, 0, saved.Content with { Title = "Cancellation retains committed media" });
                var cancelledCover = await production.ProduceMedia(MediaApprovals.Approve(cancelledProject, coverConfig, "cancel-cover-reviewer"),
                    coverConfig, ct: cancel.Token, coverFactory: _ =>
                    {
                        cancel.Cancel();
                        throw new OperationCanceledException(cancel.Token);
                    });
                Check(cancelledCover.Status == "cancelled"
                    && production.MediaArtifacts(cancelledCover.AttemptId).Outputs is [{ Output: "audio" }],
                    "Cancellation during the optional cover cannot become overall success or delete the committed audio.");
            }
            var master = partialArtifacts.Outputs[0].Artifacts.Single(a => a.Kind == "audio-master");
            var path = Files.Under(Path.Combine(state, "attempts", partial.AttemptId), master.Path);
            var bytes = File.ReadAllBytes(path);
            File.AppendAllText(path, "tampered");
            try { reopened.MediaArtifacts(partial.AttemptId); throw new Exception("Expected integrity rejection"); }
            catch (VideoException error) { Check(error.Code == "attempt.artifact-hash", "Partial audio tampering cannot become playable after reopening."); }
            File.WriteAllBytes(path, bytes);
            var retainedVideo = production.MediaArtifacts(complete.AttemptId).Outputs.Single(o => o.Output == "video");
            var audioToDelete = production.MediaArtifacts(complete.AttemptId).Outputs.Single(o => o.Output == "audio")
                .Artifacts.Single(a => a.Kind == "audio-master");
            var attemptRoot = Path.Combine(state, "attempts", complete.AttemptId);
            var immutable = new[] { "attempt.json", "request.json", "result.json", "audio-result.json", "video-result.json" }
                .ToDictionary(f => f, f => Files.Hash(Path.Combine(attemptRoot, f)));
            try { production.DeleteMediaOutput(complete.AttemptId, "audio", new string('0', 64)); throw new Exception("Expected stale deletion rejection"); }
            catch (VideoException error) { Check(error.Code == "artifact.stale", "Wrong artifact identity cannot authorize deletion."); }
            using (var held = new FileStream(Path.Combine(attemptRoot, audioToDelete.Path), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { production.DeleteMediaOutput(complete.AttemptId, "audio", audioToDelete.Sha256); throw new Exception("Expected busy-file deletion failure"); }
                catch (IOException)
                {
                    Check(production.MediaDeletions(complete.AttemptId).Single().State == "deleting",
                    "Interrupted filesystem deletion has an explicit durable retry state, not false success.");
                }
            }
            var freshDeletionStore = new FileProductionStore(state);
            freshDeletionStore.DeleteMediaOutput(complete.AttemptId, "audio", audioToDelete.Sha256);
            Check(freshDeletionStore.MediaArtifacts(complete.AttemptId).Outputs is [{ Output: "video" }]
                && retainedVideo.Artifacts.All(a => Files.Hash(Files.Under(attemptRoot, a.Path)) == a.Sha256)
                && immutable.All(pair => Files.Hash(Path.Combine(attemptRoot, pair.Key)) == pair.Value),
                "PermanentAudioDeletionPreservesVideoAndEveryImmutableJobReceiptAfterReopening");
            freshDeletionStore.DeleteMediaOutput(complete.AttemptId, "audio", audioToDelete.Sha256);
            Check(!File.Exists(Path.Combine(attemptRoot, audioToDelete.Path))
                && new MediaHistory(freshDeletionStore, projects).Read().Artifacts.Where(a => a.AttemptId == complete.AttemptId)
                    .All(a => a.Kind == "candidate-mp4"), "DeletedAudioNeverReappearsInTheLibraryAndRetryIsIdempotent");
            var videoToDelete = retainedVideo.Artifacts.Single(a => a.Kind == "candidate-mp4");
            freshDeletionStore.DeleteMediaOutput(complete.AttemptId, "video", videoToDelete.Sha256);
            Check(freshDeletionStore.Status(complete.AttemptId).Status == "completed"
                && freshDeletionStore.MediaArtifacts(complete.AttemptId).Outputs.Length == 0
                && Json.Text(projects.Read(both.Id).Content) == Json.Text(both.Content),
                "DeleteBothOutputsKeepsHistoricalCompletionAndSavedProjectWithoutReplaying");
            Console.WriteLine("PASS permanent per-output deletion: stale refusal, busy-file retry, untouched other output/immutable receipts, fresh reopen and idempotent completion.");
        }
        var damagedId = "attempt-11111111111111111111111111111111";
        var damaged = Path.Combine(state, "attempts", damagedId);
        Directory.CreateDirectory(damaged);
        File.WriteAllText(Path.Combine(damaged, "attempt.json"), "{broken");
        history = new MediaHistory(production, projects).Read();
        Check(history.Jobs.Length >= 2 && history.Issues.Single(i => i.RecordId == damagedId).Code == "history.record"
            && File.ReadAllText(Path.Combine(damaged, "attempt.json")) == "{broken",
            "MalformedHistoryRecordProducesExplicitIssueWithoutRetryRepairOrDroppingValidJobs");
        return assertions;
    }
}
