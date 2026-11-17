namespace AudioTranscriber;

public static class RealtimePcm
{
    public const string MediaType = "audio/pcm";
    public const int SampleRate = 16000;
    public const int MaximumChunkBytes = 3200;
    public const int MaximumAudioBytes = 60 * SampleRate * 2;
}
