using System.IO;
using System.Text.Json;
using VideoProduction;
using VideoProduction.Providers;

namespace MediaStudio.App;

// The foreground CLI owns execution; the shared file store owns admission, status and cancellation.
public sealed class FileProductionSession
{
    public static string CliDll => Path.Combine(AppContext.BaseDirectory, "producer", "video-production.dll");
    private readonly FileDraftGateway store;
    private readonly string configurationFile;
    private bool starting;
    public FileAttemptStatus? Status { get; private set; }
    public string? Error { get; private set; }
    public bool Busy => starting || Status?.Status is "running" or "cancelling";
    public string? MoviePath => ArtifactPath("candidate-mp4");
    public string? MovieUri => MoviePath is null ? null : $"/hybrid-result/{Status!.AttemptId}/movie.mp4";
    public string? CaptionsUri => ArtifactPath("captions-vtt") is null ? null : $"/hybrid-result/{Status!.AttemptId}/captions.vtt";

    public FileProductionSession(FileDraftGateway store, string configurationFile)
    {
        this.store = store;
        this.configurationFile = Path.GetFullPath(configurationFile);
        var config = PrivateFiles.Read<FileProductionConfiguration>(this.configurationFile);
        if (config.SchemaVersion != 1
            || !string.Equals(Path.GetFullPath(config.StateRoot), store.StateRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Production configuration must bind this exact private draft state root.");
        Refresh();
    }

    public void Refresh()
    {
        var draft = store.Drafts.Read(store.DraftId);
        Status = draft.Submission is { } submission ? store.Production.Status(submission.AttemptId) : null;
    }

    public void Cancel()
    {
        Refresh();
        if (Status is null || Status.Status is not ("running" or "cancelling"))
            throw new InvalidOperationException("There is no accepted running production to cancel.");
        Status = store.Production.Cancel(Status.AttemptId);
    }

    public async Task GenerateAsync(StoryboardState editing, bool hasProposal, Func<Task>? changed = null)
    {
        Error = null;
        if (Busy) throw new InvalidOperationException("Production is busy; no work was queued.");
        store.EnsureCurrent(editing);
        if (hasProposal || StoryboardRules.Fingerprint(editing) != StoryboardRules.Fingerprint(store.Read()))
            throw new InvalidOperationException("Accept/reject proposals and save or reload edits before generating the exact reviewed saved revision.");
        var reviewed = store.Drafts.Snapshot(new(1, editing.DraftId, editing.Revision));
        var requestFile = Path.Combine(Path.GetDirectoryName(configurationFile)!,
            $"reviewed-{editing.DraftId}-r{editing.Revision}.json");
        if (File.Exists(requestFile))
        {
            if (Files.Fingerprint(Json.Read<ReviewedVideoRequest>(requestFile)) != Files.Fingerprint(reviewed))
                throw new InvalidOperationException("The retained reviewed request differs; no attempt was started.");
        }
        else Json.Write(requestFile, reviewed);
        starting = true;
        try
        {
            if (changed is not null) await changed();
            if (!File.Exists(CliDll))
                throw new FileNotFoundException("Build Studio with its matching packaged producer.", CliDll);
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) != "dotnet")
                throw new InvalidOperationException("Launch Hybrid with the explicit private dotnet host to run its matching foreground CLI.");
            // No view-lifetime cancellation token: closing a window must not kill its accepted producer.
            var work = Processes.Run(Environment.ProcessPath!, ["--roll-forward", "Major", CliDll,
                "produce", "--request", requestFile, "--production-config", configurationFile],
                Path.GetDirectoryName(configurationFile)!, CancellationToken.None, timeoutSeconds: 3600, requireSuccess: false);
            while (!work.IsCompleted)
            {
                Refresh();
                if (changed is not null) await changed();
                await Task.Delay(250);
            }
            var result = await work;
            Refresh();
            var terminal = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonSerializer.Deserialize<Envelope>(line, Json.Options)
                    ?? throw new JsonException("Missing foreground production response."))
                .LastOrDefault();
            if (result.ExitCode != 0)
                throw new VideoException(Status?.ErrorCode ?? "production.cli",
                    $"Foreground production exited {result.ExitCode}. {result.Stderr.Trim()}");
            if (terminal is not { SchemaVersion: 1, Kind: "result" or "replay" }
                || Status?.Status != "completed"
                || Files.Fingerprint(terminal.Value with { Replay = false }) != Files.Fingerprint(Status)
                || MoviePath is null)
                throw new InvalidOperationException("Production did not commit a verified candidate manifest; no partial output is presented as success.");
        }
        catch (Exception error)
        {
            Error = error.Message;
            throw;
        }
        finally
        {
            starting = false;
            if (changed is not null) await changed();
        }
    }

    private string? ArtifactPath(string kind) => Status is { Status: "completed" } completed
        && completed.Artifacts.SingleOrDefault(a => a.Kind == kind) is { } artifact
            ? Files.Under(Path.Combine(store.StateRoot, "attempts", completed.AttemptId), artifact.Path) : null;

    public Stream? OpenPreview(Uri uri, out string contentType)
    {
        contentType = "application/octet-stream";
        if (Status is not { Status: "completed" } completed) return null;
        var kind = uri.AbsolutePath == $"/hybrid-result/{completed.AttemptId}/movie.mp4" ? "candidate-mp4"
            : uri.AbsolutePath == $"/hybrid-result/{completed.AttemptId}/captions.vtt" ? "captions-vtt" : null;
        if (kind is null) return null;
        var artifact = completed.Artifacts.Single(a => a.Kind == kind);
        var stream = File.Open(ArtifactPath(kind)!, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
            if (!hash.Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new VideoException("attempt.artifact-hash", "Preview bytes differ from the committed manifest.");
            stream.Position = 0;
            contentType = kind == "candidate-mp4" ? "video/mp4" : "text/vtt; charset=utf-8";
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    private sealed record Envelope(int SchemaVersion, string Kind, FileAttemptStatus Value);
}
