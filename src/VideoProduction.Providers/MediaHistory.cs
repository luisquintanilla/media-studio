using System.Text.Json;
using MediaStudio.Audio;

namespace VideoProduction.Providers;

public sealed record MediaHistoryJob(MediaProject Project, FileAttempt Attempt, FileAttemptStatus Status,
    MediaVerifiedArtifacts? Artifacts);
public sealed record MediaLibraryArtifact(string AttemptId, string Kind, MediaProject Project, string Path,
    string Sha256, double? DurationSeconds, DateTimeOffset CreatedAt, string OverallStatus, bool HasCaptions = false);
public sealed record MediaHistoryIssue(string RecordId, string Code, string Message);
public sealed record MediaHistoryProjection(MediaHistoryJob[] Jobs, MediaLibraryArtifact[] Artifacts, MediaHistoryIssue[] Issues,
    MediaOutputDeletion[]? Deletions = null);

public sealed class MediaHistory(FileProductionStore production, MediaProjectStore projects)
{
    public MediaHistoryProjection Read()
    {
        var jobs = new List<MediaHistoryJob>();
        var artifacts = new List<MediaLibraryArtifact>();
        var issues = new List<MediaHistoryIssue>();
        var deletions = new List<MediaOutputDeletion>();
        var directories = Directory.GetDirectories(production.PathFor("attempts"));
        Guard.Require(directories.Length <= 1024, "history.bound", "Generation history exceeds the supported demo limit.");
        foreach (var directory in directories)
        {
            var id = Path.GetFileName(directory);
            if (!File.Exists(Path.Combine(directory, "attempt.json"))) continue;
            try
            {
                var attempt = production.Attempt(id);
                if (attempt.MediaKind != "media") continue;
                var request = Json.Read<MediaReviewedRequest>(Path.Combine(directory, "request.json"));
                var project = projects.Read(request.Project.Id, request.Project.Revision);
                Guard.Require(Files.Fingerprint(project) == Files.Fingerprint(request.Project), "history.identity", "Job does not bind its immutable project revision.");
                var status = production.ObserveStatus(id);
                deletions.AddRange(production.MediaDeletions(id));
                MediaVerifiedArtifacts? outputs = null;
                try { outputs = production.MediaArtifacts(id, observeOnly: true); }
                catch (Exception error) when (error is VideoException or IOException or JsonException or UnauthorizedAccessException)
                {
                    issues.Add(new(id, error is VideoException v ? v.Code : "history.verification",
                        "Generated output could not be verified. Open this job's technical details."));
                }
                jobs.Add(new(project, attempt, status, outputs));
                foreach (var artifact in outputs?.Outputs.SelectMany(o => o.Artifacts) ?? [])
                {
                    if (artifact.Kind is not ("audio-master" or "candidate-mp4")) continue;
                    double? duration = null;
                    if (artifact.Kind == "audio-master")
                    {
                        var master = AudioProduction.Verify(Path.GetDirectoryName(Files.Under(directory, artifact.Path))!);
                        duration = (double)master.SampleCount / master.SampleRate;
                    }
                    else
                    {
                        var checks = outputs!.Outputs.SelectMany(o => o.Artifacts).FirstOrDefault(a => a.Kind == "checks");
                        if (checks is not null)
                        {
                            var graph = Json.Read<DurationObservation>(Files.Under(directory, checks.Path));
                            Guard.Require(graph.MovieSha256 == artifact.Sha256 && graph.Ok
                                && graph.Probe is { Duration: var measured } && double.IsFinite(measured) && measured > 0,
                                "history.duration", "Encoded duration observation does not bind the verified movie.");
                            duration = graph.Probe!.Duration;
                        }
                        else if (outputs!.Outputs.SelectMany(o => o.Artifacts).FirstOrDefault(a => a.Kind == "offline-verification") is { } fixture)
                        {
                            var graph = Json.Read<DurationObservation>(Files.Under(directory, fixture.Path));
                            var measured = graph.ActualDurationSeconds ?? double.NaN;
                            Guard.Require(double.IsFinite(measured) && measured > 0, "history.duration", "Invalid fixture's encoded duration observation.");
                            duration = measured;
                        }
                    }
                    artifacts.Add(new(id, artifact.Kind, project, artifact.Path, artifact.Sha256, duration,
                        attempt.UpdatedAt, status.Status,
                        outputs!.Outputs.SelectMany(o => o.Artifacts).Any(a => a.Kind == "captions-vtt")));
                }
            }
            catch (Exception error) when (error is VideoException or IOException or JsonException or UnauthorizedAccessException)
            {
                issues.Add(new(id, error is VideoException v ? v.Code : "history.record",
                    "A generation record is damaged or unavailable. It was not retried or repaired."));
            }
        }
        return new(jobs.OrderByDescending(j => j.Attempt.CreatedAt).ToArray(),
            artifacts.OrderByDescending(a => a.CreatedAt).ToArray(), issues.ToArray(), deletions.ToArray());
    }
    [System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip)]
    private sealed record DurationObservation(bool Ok = false, string? MovieSha256 = null,
        DurationProbe? Probe = null, double? ActualDurationSeconds = null);
    [System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip)]
    private sealed record DurationProbe(double Duration = 0);
}
