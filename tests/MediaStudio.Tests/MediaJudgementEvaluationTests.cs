using VideoProduction;

internal static class MediaJudgementEvaluationTests
{
    public static async Task<int> Run(string root)
    {
        var datasetRoot = TestPaths.Evaluation;
        var sets = new[] { "visual-development-v1.json", "visual-heldout-v1.json",
            "triage-development-v1.json", "triage-heldout-v1.json" }
            .Select(n => Json.Read<JudgementDataset>(Path.Combine(datasetRoot, n))).ToArray();
        await DevelopmentThenHeldoutBindsFrozenPolicy(datasetRoot, root);
        InvalidTaskSplitAndLabelVersionRejected(sets);
        CrossSplitIdFamilyAndContentOverlapRejected(sets);
        IneligibleVisualLabelsAndAbsentTranscriptRejected(sets);
        await HeldoutRequiresExactDevelopmentReceipt(datasetRoot, root);
        await FrozenDatasetAndPolicyTamperingRejected(datasetRoot, root);
        return 6;
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
    }
    private static void Reject(Action action, string code)
    {
        try { action(); throw new Exception("Expected " + code); }
        catch (VideoException error) when (error.Code == code) { }
    }
    private static async Task RejectAsync(Func<Task> action, string code)
    {
        try { await action(); throw new Exception("Expected " + code); }
        catch (VideoException error) when (error.Code == code) { }
    }
    private static JudgementDataset[] Replace(JudgementDataset[] sets, int index, JudgementDataset replacement)
    {
        var changed = sets.ToArray();
        changed[index] = replacement;
        return changed;
    }
    private static JudgementDataset WithFirst(JudgementDataset set, JudgementExample example) =>
        set with { Cases = [example, .. set.Cases.Skip(1)] };

    private static async Task DevelopmentThenHeldoutBindsFrozenPolicy(string datasetRoot, string root)
    {
        var developmentPath = Path.Combine(root, "evaluation-development.json");
        var dev = await MediaJudgementEvaluation.Run(datasetRoot, "development", developmentPath);
        var held = await MediaJudgementEvaluation.Run(datasetRoot, "locked-heldout",
            Path.Combine(root, "evaluation-heldout.json"), developmentPath);
        Require(dev.Cases.Length == 12 && held.Cases.Length == 12
            && dev.Cases.Select(c => c.Id).Intersect(held.Cases.Select(c => c.Id)).Count() == 0
            && held.DevelopmentReportSha256 == Files.Hash(developmentPath)
            && dev.LockSha256 == held.LockSha256 && dev.PolicySha256 == held.PolicySha256
            && !dev.RealModelQualityMeasured && !held.RealModelQualityMeasured
            && dev.ProviderUsage is null && held.ProviderUsage is null,
            nameof(DevelopmentThenHeldoutBindsFrozenPolicy));
        foreach (var report in new[] { dev, held })
        {
            Require(report.Metrics.Length == 2 && report.Metrics.All(m => m.Cases == 6 && m.DecisionCalls == 5
                    && m.ReviewInvariantErrors == 0 && m.UncertainCases == 1 && m.LabelErrors > 0)
                && report.Cases.All(c => c.ObservedHarnessTicks >= 0 && Files.ValidHash(c.InputSha256))
                && report.Cases.Where(c => c.Id.EndsWith("-title", StringComparison.Ordinal)).All(c => c.DecisionCalls == 0 && c.SelectedId == "plain")
                && report.Cases.Where(c => c.Id.EndsWith("-exact", StringComparison.Ordinal)).All(c => c.DecisionCalls == 0 && c.MeaningChangeProbability is null)
                && report.Cases.Where(c => c.Task == "transcript-triage" && c.DecisionCalls > 0).All(c => c.RequiresReview),
                nameof(DevelopmentThenHeldoutBindsFrozenPolicy));
        }
    }
    private static void InvalidTaskSplitAndLabelVersionRejected(JudgementDataset[] sets)
    {
        Reject(() => MediaJudgementEvaluation.ValidateDatasets(Replace(sets, 0, sets[0] with { Task = "storyboard-planning" })), "evaluation.dataset");
        Reject(() => MediaJudgementEvaluation.ValidateDatasets(Replace(sets, 0, sets[0] with { Split = "unlocked" })), "evaluation.dataset");
        Reject(() => MediaJudgementEvaluation.ValidateDatasets(Replace(sets, 0, sets[0] with { LabelsVersion = "unknown" })), "evaluation.dataset");
    }
    private static void CrossSplitIdFamilyAndContentOverlapRejected(JudgementDataset[] sets)
    {
        Reject(() => MediaJudgementEvaluation.ValidateDatasets(Replace(sets, 1,
            WithFirst(sets[1], sets[1].Cases[0] with { Id = sets[0].Cases[0].Id }))), "evaluation.overlap");
        Reject(() => MediaJudgementEvaluation.ValidateDatasets(Replace(sets, 1,
            WithFirst(sets[1], sets[1].Cases[0] with { Family = sets[0].Cases[0].Family }))), "evaluation.overlap");
        Reject(() => MediaJudgementEvaluation.ValidateDatasets(Replace(sets, 1,
            WithFirst(sets[1], sets[0].Cases[0] with { Id = "different-id", Family = "different-family" }))), "evaluation.overlap");
        Reject(() => MediaJudgementEvaluation.ValidateDatasets(Replace(sets, 3,
            WithFirst(sets[3], sets[2].Cases[0] with { Id = "different-text-id", Family = "different-text-family" }))), "evaluation.overlap");
    }
    private static void IneligibleVisualLabelsAndAbsentTranscriptRejected(JudgementDataset[] sets)
    {
        Reject(() => MediaJudgementEvaluation.ValidateDatasets(Replace(sets, 0,
            WithFirst(sets[0], sets[0].Cases[0] with { AcceptedVisuals = ["progressiveCodeFocus"] }))), "evaluation.label");
        Reject(() => MediaJudgementEvaluation.ValidateDatasets(Replace(sets, 0,
            WithFirst(sets[0], sets[0].Cases[0] with { AcceptedVisuals = ["unknown"] }))), "evaluation.label");
        Reject(() => MediaJudgementEvaluation.ValidateDatasets(Replace(sets, 2,
            WithFirst(sets[2], sets[2].Cases[0] with { Observed = "" }))), "evaluation.label");
        Reject(() => MediaJudgementEvaluation.ValidateDatasets(Replace(sets, 2,
            WithFirst(sets[2], sets[2].Cases[0] with { MeaningChange = true }))), "evaluation.label");
    }
    private static async Task HeldoutRequiresExactDevelopmentReceipt(string datasetRoot, string root)
    {
        await RejectAsync(() => MediaJudgementEvaluation.Run(datasetRoot, "locked-heldout",
            Path.Combine(root, "no-development.json")), "evaluation.development");
        var originalPath = Path.Combine(root, "evaluation-development.json");
        var incorrectPath = Path.Combine(root, "incorrect-development.json");
        var original = Json.Read<JudgementEvaluationReport>(originalPath);
        Json.Write(incorrectPath, original with { PolicySha256 = new string('0', 64) });
        await RejectAsync(() => MediaJudgementEvaluation.Run(datasetRoot, "locked-heldout",
            Path.Combine(root, "wrong-development.json"), incorrectPath), "evaluation.development");
    }
    private static async Task FrozenDatasetAndPolicyTamperingRejected(string datasetRoot, string root)
    {
        var clone = Path.Combine(root, "evaluation-lock-tamper");
        var data = Path.Combine(clone, "Evaluation");
        Directory.CreateDirectory(data);
        File.Copy(Path.Combine(datasetRoot, "..", "MediaJudgementEvaluation.cs"), Path.Combine(clone, "MediaJudgementEvaluation.cs"));
        foreach (var source in Directory.GetFiles(datasetRoot, "*.json"))
            File.Copy(source, Path.Combine(data, Path.GetFileName(source)));
        var policy = Path.Combine(data, "policy-v1.json");
        var original = File.ReadAllText(policy);
        File.AppendAllText(policy, "\n ");
        await RejectAsync(() => MediaJudgementEvaluation.Run(data, "development", Path.Combine(root, "tampered-policy.json")), "evaluation.lock");
        File.WriteAllText(policy, original);
        File.AppendAllText(Path.Combine(data, "visual-heldout-v1.json"), "\n ");
        await RejectAsync(() => MediaJudgementEvaluation.Run(data, "development", Path.Combine(root, "tampered-dataset.json")), "evaluation.lock");
    }
}
