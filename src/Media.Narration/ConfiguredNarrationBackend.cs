namespace VideoProduction;

public sealed class ConfiguredNarrationBackend(SpeechConfig configuration, SpeechRequest voice,
    SpeechClientIdentity approvedIdentity) : INarrationBackend, IDisposable
{
    private NarrationSpeechClient? speech;

    public Task<NarrationTake> Synthesize(string displayText, string outputDirectory, CancellationToken ct)
    {
        Guard.Require(voice == new SpeechRequest(configuration.Voice, configuration.Language, configuration.Speed),
            "audio.approval", "Configured voice differs from the approved voice.");
        Guard.Require(configuration.EndGuardMilliseconds == 0, "audio.guard",
            "Independent audio uses explicitly approved sample pauses; a video end-guard is not implicitly applied.");
        speech ??= NarrationSpeechClient.Create(configuration);
        Guard.Require(Files.Fingerprint(speech.Identity) == Files.Fingerprint(approvedIdentity),
            "audio.identity", "Configured synthesis provider differs from the approved identity.");
        return speech.CreateBackend(voice).Synthesize(displayText, outputDirectory, ct);
    }

    public void Dispose() => speech?.Dispose();
}
