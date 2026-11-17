using MediaStudio.Speech.Bench;

namespace MediaStudio.Tests.Speech;

public sealed class WerTests
{
    [Theory]
    [InlineData("the cat sat on the mat", "the cat sat on the mat", 0, 0, 0)]
    [InlineData("the cat sat on the mat", "the cat sit on the mat", 1, 0, 0)]
    [InlineData("the cat sat on the mat", "the cat on the mat", 0, 1, 0)]
    [InlineData("the cat sat on the mat", "the black cat sat on the mat", 0, 0, 1)]
    [InlineData("a b c d", "", 0, 4, 0)]
    [InlineData("", "a b", 0, 0, 2)]
    [InlineData("kitten sitting", "sitting kitten", 2, 0, 0)]
    public void CountsSubstitutionsDeletionsAndInsertions(string reference, string hypothesis, int s, int d, int i) =>
        Assert.Equal(new WerResult(s, d, i, Wer.Normalize(reference).Length), Wer.Compute(reference, hypothesis));

    [Fact]
    public void NormalizesCaseAndPunctuation()
    {
        Assert.Equal(["dont", "stop", "well", "known", "café"], Wer.Normalize("Don\u2019t STOP! Well-known, café."));
        Assert.Equal(0, Wer.Compute("Hello, world.", "hello world").Errors);
    }

    [Fact]
    public void RateIsErrorsOverReferenceWords() => Assert.Equal(0.25, Wer.Compute("one two three four", "one two three for").Rate);

    [Fact]
    public void ResolvesWavReferencePairsFromDirectoryAndPattern()
    {
        var directory = Directory.CreateTempSubdirectory("wer-clips").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "b.wav"), []);
            File.WriteAllBytes(Path.Combine(directory, "a.wav"), []);
            File.WriteAllText(Path.Combine(directory, "a.txt"), " Reference text.\n");
            var clips = SpeechBenchCommands.ResolveClips(directory);
            Assert.Equal(["a.wav", "b.wav"], clips.Select(c => Path.GetFileName(c.Wav)));
            Assert.Equal("Reference text.", clips[0].Reference);
            Assert.Null(clips[1].Reference);
            Assert.Single(SpeechBenchCommands.ResolveClips(Path.Combine(directory, "a*.wav")));
            Assert.Empty(SpeechBenchCommands.ResolveClips(Path.Combine(directory, "missing", "*.wav")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(new[] { "bench", "tts" }, true)]
    [InlineData(new[] { "bench", "cloud-stt" }, true)]
    [InlineData(new[] { "bench", "local" }, false)]
    [InlineData(new[] { "render" }, false)]
    public void HandlesOnlySpeechBenchCommands(string[] args, bool expected) => Assert.Equal(expected, SpeechBenchCommands.Handles(args));
}
