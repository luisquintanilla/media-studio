namespace MediaStudio.Audio;

public sealed record AudioCompleted(string MasterSha256, long SampleCount);
public sealed record AudioFailed(string Code, string Message);
public sealed record AudioCancelled(string Reason);
public union AudioOutcome(AudioCompleted, AudioFailed, AudioCancelled);

public sealed record AudioOutcomeDto(string Status, string? MasterSha256 = null, long? SampleCount = null,
    string? Code = null, string? Message = null)
{
    public static AudioOutcomeDto From(AudioOutcome outcome) => outcome switch {
        AudioCompleted complete => new("completed", complete.MasterSha256, complete.SampleCount),
        AudioFailed failure => new("failed", Code: failure.Code, Message: failure.Message),
        AudioCancelled cancelled => new("cancelled", Message: cancelled.Reason),
        _ => throw new InvalidDataException("A default or unsupported audio outcome cannot be persisted as success.")
    };
}
