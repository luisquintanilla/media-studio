using System.Diagnostics;

namespace VideoProduction.Providers;

public sealed record MediaProducerLaunch(int SchemaVersion, ProducerIdentity Producer, string ReviewedRequestSha256);

public sealed class MediaProducerLauncher(FileProductionStore production, string dotnetHost, string cliDll)
{
    public async Task<FileAttemptStatus> AcceptAsync(MediaReviewedRequest request, string configurationFile)
    {
        var projects = new MediaProjectStore(production);
        var key = MediaProjectStore.SubmissionKey(request.Project);
        var directory = production.PathFor("requests/" + key);
        var launchPath = Path.Combine(directory, "launch.json");
        var requestPath = Path.Combine(directory, "reviewed.json");
        using (var transaction = production.Admission())
        {
            if (production.Lookup(key) is { } existing)
            {
                Guard.Require(existing.ReviewedRequestSha256 == Files.Fingerprint(request),
                    "attempt.idempotency-conflict", "This saved revision already binds a different generation approval.");
            }
            else
            {
                Directory.CreateDirectory(directory);
                if (File.Exists(requestPath))
                    Guard.Require(Files.Fingerprint(PrivateFiles.Read<MediaReviewedRequest>(requestPath)) == Files.Fingerprint(request),
                        "attempt.idempotency-conflict", "This saved revision already has a different pending approval.");
                else { Json.Write(requestPath, request); PrivateFiles.Protect(requestPath); }
                var previous = File.Exists(launchPath) ? Json.Read<MediaProducerLaunch>(launchPath) : null;
                Guard.Require(previous is null || previous.SchemaVersion == 1
                    && previous.ReviewedRequestSha256 == Files.Fingerprint(request),
                    "media.launch", "Pending producer identity differs from this exact approval.");
                if (previous is null)
                {
                    Guard.Require(File.Exists(dotnetHost) && File.Exists(cliDll)
                        && Path.GetFileNameWithoutExtension(dotnetHost).Equals("dotnet", StringComparison.OrdinalIgnoreCase),
                        "media.host", "Build and configure the matching private dotnet host and production CLI before approval.");
                    var start = new ProcessStartInfo(Path.GetFullPath(dotnetHost)) {
                        UseShellExecute = false, CreateNoWindow = true,
                        WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(configurationFile))!
                    };
                    foreach (var argument in new[] { Path.GetFullPath(cliDll), "media-produce", "--request", requestPath,
                        "--production-config", Path.GetFullPath(configurationFile), "--output-channel", "file" })
                        start.ArgumentList.Add(argument);
                    using var child = Process.Start(start) ?? throw new VideoException("media.host", "Producer process did not start.");
                    Json.Write(launchPath, new MediaProducerLaunch(1,
                        new(child.Id, child.StartTime.ToUniversalTime().Ticks), Files.Fingerprint(request)));
                }
                else Guard.Require(IsLive(previous.Producer), "media.start-failed",
                    "The pending producer exited before admission. Inspect the retained errors.json; save a new reviewed revision to try again, never automatically retry.");
            }
        }
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            if (projects.Submission(request.Project) is { } accepted) return accepted;
            var launch = Json.Read<MediaProducerLaunch>(launchPath);
            Guard.Require(IsLive(launch.Producer), "media.start-failed",
                "The producer exited before durable acceptance. Inspect its retained requests/<revision>/errors.json; no job is claimed accepted.");
            Guard.Require(elapsed.Elapsed < TimeSpan.FromSeconds(20), "media.start-pending",
                "The producer is still starting but no attempt has been accepted yet. Reattach to the pending request; do not start another producer.");
            await Task.Delay(100);
        }
    }

    private static bool IsLive(ProducerIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == identity.StartedUtcTicks;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
