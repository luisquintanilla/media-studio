namespace VideoProduction.Providers;

public sealed record MediaOutputDeletion(int SchemaVersion, string AttemptId, string Output, string Token,
    string ReviewedRequestSha256, string ReceiptSha256, string PrimarySha256, string State, DateTimeOffset RequestedAt);

public sealed partial class FileProductionStore
{
    private string DeletionPath(string id, string output) => Path.Combine(AttemptRoot(id), output + "-deletion.json");
    private MediaOutputReceipt DeletionReceipt(FileAttempt attempt, string output)
    {
        Guard.Require(output is "audio" or "video", "artifact.output", "Select an audio or video output, not a path.");
        var receipt = Json.Read<MediaOutputReceipt>(Path.Combine(AttemptRoot(attempt.Id), output + "-result.json"));
        Guard.Require(receipt.SchemaVersion == 1 && receipt.AttemptId == attempt.Id && receipt.Output == output
            && receipt.Token == attempt.Token && receipt.ReviewedRequestSha256 == attempt.ReviewedRequestSha256
            && receipt.Artifacts.Length is > 0 and <= 64
            && receipt.Artifacts.Count(a => a.Kind == (output == "audio" ? "audio-master" : "candidate-mp4")) == 1
            && receipt.Artifacts.Select(a => a.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == receipt.Artifacts.Length
            && receipt.Artifacts.All(a => Files.ValidHash(a.Sha256)
                && a.Path is not ("attempt.json" or "request.json" or "result.json" or "cancel.json"
                    or "audio-result.json" or "video-result.json" or "audio-deletion.json" or "video-deletion.json"
                    or "output-outcomes.json")),
            "artifact.receipt", "Deletion must bind the exact committed output receipt and cannot delete job records.");
        foreach (var artifact in receipt.Artifacts) _ = Files.Under(AttemptRoot(attempt.Id), artifact.Path);
        return receipt;
    }
    private MediaOutputDeletion? ReadDeletion(FileAttempt attempt, string output)
    {
        var file = DeletionPath(attempt.Id, output);
        if (!File.Exists(file)) return null;
        var deletion = Json.Read<MediaOutputDeletion>(file);
        var receipt = DeletionReceipt(attempt, output);
        Guard.Require(Terminal(attempt.Status) && deletion.SchemaVersion == 1 && deletion.AttemptId == attempt.Id
            && deletion.Output == output && deletion.Token == attempt.Token
            && deletion.ReviewedRequestSha256 == attempt.ReviewedRequestSha256
            && deletion.State is "deleting" or "deleted"
            && deletion.ReceiptSha256 == Files.Hash(Path.Combine(AttemptRoot(attempt.Id), output + "-result.json"))
            && receipt.Artifacts.Single(a => a.Kind == (output == "audio" ? "audio-master" : "candidate-mp4")).Sha256 == deletion.PrimarySha256,
            "artifact.deletion-identity", "Deletion record does not bind this exact terminal output.");
        foreach (var artifact in receipt.Artifacts)
        {
            var path = Files.Under(AttemptRoot(attempt.Id), artifact.Path);
            Guard.Require(!File.Exists(path) || deletion.State == "deleting" && Files.Hash(path) == artifact.Sha256,
                "artifact.deletion-files", "A deleted output was replaced or changed; it is not silently trusted.");
        }
        return deletion;
    }
    private HashSet<VideoArtifact> RetiredArtifacts(FileAttempt attempt)
    {
        var retired = new HashSet<VideoArtifact>();
        if (attempt.MediaKind != "media") return retired;
        foreach (var output in new[] { "audio", "video" })
            if (ReadDeletion(attempt, output) is not null)
                retired.UnionWith(DeletionReceipt(attempt, output).Artifacts);
        return retired;
    }
    public MediaOutputDeletion[] MediaDeletions(string id)
    {
        using var transaction = Admission();
        var attempt = ReadAttempt(id);
        return new[] { ReadDeletion(attempt, "audio"), ReadDeletion(attempt, "video") }
            .OfType<MediaOutputDeletion>().ToArray();
    }
    public void DeleteMediaOutput(string id, string output, string expectedPrimarySha256)
    {
        using var transaction = Admission();
        var attempt = ReadAttempt(id);
        Guard.Require(attempt.MediaKind == "media" && Terminal(attempt.Status),
            "artifact.running", "A running or cancelling job cannot be deleted. Wait for its terminal outcome.");
        var receipt = DeletionReceipt(attempt, output);
        var primary = receipt.Artifacts.Single(a => a.Kind == (output == "audio" ? "audio-master" : "candidate-mp4"));
        Guard.Require(primary.Sha256 == expectedPrimarySha256, "artifact.stale",
            "The selected artifact changed. Refresh before deleting.");
        var other = output == "audio" ? "video" : "audio";
        if (File.Exists(Path.Combine(AttemptRoot(id), other + "-result.json")))
        {
            var otherPaths = DeletionReceipt(attempt, other).Artifacts.Select(a => a.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Guard.Require(!receipt.Artifacts.Any(a => otherPaths.Contains(a.Path)), "artifact.shared",
                "These outputs share a file; deleting it would damage the other output.");
        }
        var deletion = ReadDeletion(attempt, output);
        if (deletion?.State == "deleted") return;
        if (deletion is null)
        {
            VerifyArtifacts(AttemptRoot(id), receipt.Artifacts);
            _ = Manifest(attempt);
            deletion = new(1, id, output, attempt.Token, attempt.ReviewedRequestSha256,
                Files.Hash(Path.Combine(AttemptRoot(id), output + "-result.json")), primary.Sha256, "deleting", DateTimeOffset.UtcNow);
            Json.Write(DeletionPath(id, output), deletion);
        }
        foreach (var artifact in receipt.Artifacts)
        {
            var path = Files.Under(AttemptRoot(id), artifact.Path);
            if (!File.Exists(path)) continue;
            Guard.Require(Files.Hash(path) == artifact.Sha256, "artifact.stale", "An output file changed during deletion.");
            File.Delete(path);
        }
        Json.Write(DeletionPath(id, output), deletion with { State = "deleted" });
    }
}
