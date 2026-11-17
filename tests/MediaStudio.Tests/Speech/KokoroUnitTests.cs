using System.Text;
using MediaStudio.Speech;
using MediaStudio.Speech.Audio;
using MediaStudio.Speech.Kokoro;
using Microsoft.Extensions.AI;

namespace MediaStudio.Tests.Speech;

public sealed class KokoroUnitTests
{
    private static readonly Dictionary<char, int> Vocab = new() {
        ['$'] = 0, [';'] = 1, [':'] = 2, [','] = 3, ['.'] = 4, ['!'] = 5, ['?'] = 6, [' '] = 16,
        ['h'] = 50, ['ə'] = 83, ['l'] = 54, ['o'] = 57, ['ʊ'] = 135, ['w'] = 65, ['ɜ'] = 87, ['ː'] = 158, ['d'] = 46
    };

    private static KokoroPhonemeTokenizer Tokenizer() => new(Vocab);

    [Fact]
    public void TokenizerRoundTripsAndCountsUnknownSymbols()
    {
        var tokenizer = Tokenizer();
        var ids = tokenizer.EncodeToIds("həlˈoʊ wˈɜːld.");
        Assert.Equal(2, tokenizer.LastUnknownCount); // the stress mark ˈ is not in this test vocab
        Assert.Equal("həloʊ wɜːld.", tokenizer.Decode(ids));
        Assert.Equal([50, 83, 54, 57, 135, 16, 65, 87, 158, 54, 46, 4], ids);
    }

    [Fact]
    public void TokenizerCountsEveryDroppedSymbol()
    {
        var tokenizer = Tokenizer();
        _ = tokenizer.EncodeToIds("ˈhˌəʔ");
        Assert.Equal(3, tokenizer.LastUnknownCount);
    }

    [Fact]
    public void EncodeForModelPadsBothEnds()
    {
        var input = Tokenizer().EncodeForModel("hə.");
        Assert.Equal([0L, 50, 83, 4, 0], input);
    }

    [Fact]
    public void EncodeForModelRejectsMoreThan510Tokens() =>
        Assert.Throws<ArgumentException>(() => Tokenizer().EncodeForModel(new string('h', 511)));

    [Fact]
    public void TokenizerReadsSingleCharacterVocabFromTokenizerJson()
    {
        var json = """{"model":{"vocab":{"$":0,"a":43,"ab":99,"ˈ":156}}}""";
        var tokenizer = KokoroPhonemeTokenizer.FromJson(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        Assert.Equal(3, tokenizer.Vocabulary.Count);
        Assert.Equal([156, 43], tokenizer.EncodeToIds("ˈa"));
    }

    [Fact]
    public void ChunkKeepsEveryPieceWithin510AndPrefersPunctuation()
    {
        var tokenizer = Tokenizer();
        var phonemes = string.Join(" ", Enumerable.Repeat("həloʊ wɜːld,", 120));
        var ids = tokenizer.EncodeToIds(phonemes);
        var chunks = tokenizer.Chunk(ids);
        Assert.True(chunks.Count >= 3);
        Assert.All(chunks, c => Assert.InRange(c.Length, 1, KokoroPhonemeTokenizer.MaxTokens));
        Assert.All(chunks.SkipLast(1), c => Assert.Equal(Vocab[','], c[^1]));
        Assert.All(chunks, c => Assert.NotEqual(Vocab[' '], c[0]));
        Assert.Equal(ids.Count(i => i != Vocab[' ']), chunks.Sum(c => c.Count(i => i != Vocab[' '])));
    }

    [Fact]
    public void ChunkFallsBackToHardSplitWithoutBreaks()
    {
        var chunks = Tokenizer().Chunk(Enumerable.Repeat(50, 1100).ToArray());
        Assert.Equal([510, 510, 80], chunks.Select(c => c.Length));
    }

    [Theory]
    [InlineData("həlˈoʊ\u200d wˈɜːld_\r\n", "həlˈoʊ wˈɜːld")]
    [InlineData("  ðɪs\nɪz\t mˈiːdiə  ", "ðɪs ɪz mˈiːdiə")]
    [InlineData("_\u200d_", "")]
    public void EspeakOutputIsCleaned(string raw, string expected) => Assert.Equal(expected, EspeakPhonemizer.Clean(raw));

    [Theory]
    [InlineData(null, "en-us")]
    [InlineData("en", "en-us")]
    [InlineData("en-US", "en-us")]
    [InlineData("en-GB", "en-gb")]
    public void EspeakVoiceMapsLanguages(string? language, string voice) => Assert.Equal(voice, EspeakPhonemizer.VoiceFor(language));

    [Fact]
    public void MissingEspeakGivesInstallHint()
    {
        var phonemizer = new EspeakPhonemizer(Path.Combine(Path.GetTempPath(), "no-such-dir", "espeak-ng.exe"));
        var error = Assert.Throws<InvalidOperationException>(() => phonemizer.ExecutablePath);
        Assert.Contains("espeak-ng", error.Message);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(40, 40)]
    [InlineData(509, 509)]
    [InlineData(510, 509)]
    [InlineData(2000, 509)]
    public void VoiceRowIsTokenCountClampedTo509(int tokens, int row)
    {
        Assert.Equal(row, KokoroVoice.RowFor(tokens));
        var voice = TestVoice();
        Assert.Equal(row, voice.GetStyle(tokens)[0]);
        Assert.Equal(KokoroVoice.StyleDimensions, voice.GetStyle(tokens).Length);
        Assert.Equal(row, voice.Styles[row, 0, 5]);
    }

    [Fact]
    public void VoiceRejectsWrongSize() => Assert.Throws<InvalidDataException>(() => new KokoroVoice("bad", new float[100]));

    [Fact]
    public void VoicesResolveNestedAndFlattenedFilesAndRejectTraversal()
    {
        var dir = Directory.CreateTempSubdirectory("kokoro-voices");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir.FullName, "voices"));
            File.WriteAllBytes(Path.Combine(dir.FullName, "voices", "af_heart.bin"), new byte[4]);
            File.WriteAllBytes(Path.Combine(dir.FullName, "voices_bf_emma.bin"), new byte[4]);
            var voices = new KokoroVoices(dir.FullName);
            Assert.NotNull(voices.FindFile("af_heart"));
            Assert.NotNull(voices.FindFile("bf_emma"));
            Assert.Equal(["af_heart", "bf_emma"], voices.ListVoices().Order());
            Assert.ThrowsAny<ArgumentException>(() => voices.Get("..\\secrets"));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public async Task ClientChunksSelectsStyleRowAndKeepsContiguousLedger()
    {
        var calls = new List<(long[] Ids, float Row, float Speed)>();
        float[] Run(long[] ids, ReadOnlyMemory<float> style, float speed)
        {
            calls.Add((ids, style.Span[0], speed));
            return Enumerable.Range(0, 2400).Select(i => MathF.Sin(i / 10f) * 0.5f).ToArray();
        }
        var phonemizer = new FakePhonemizer(text => text.StartsWith("Hello world", StringComparison.Ordinal) ? "həloʊ wɜːld" : "həloʊ");
        using var client = new KokoroTextToSpeechClient(Tokenizer(), phonemizer, _ => TestVoice(), Run);

        var response = await client.GetAudioAsync("Hello world. Hello again!", new TextToSpeechOptions { Speed = 1.25f });

        Assert.Equal(2, calls.Count);
        Assert.Equal([0L, 50, 83, 54, 57, 135, 16, 65, 87, 158, 54, 46, 4, 0], calls[0].Ids);
        Assert.Equal(12, calls[0].Row);
        Assert.Equal(1.25f, calls[0].Speed);
        Assert.Equal([0L, 50, 83, 54, 57, 135, 5, 0], calls[1].Ids);
        Assert.Equal(6, calls[1].Row);
        Assert.Equal(["en-us", "en-us"], phonemizer.Languages);

        var audio = Assert.IsType<DataContent>(Assert.Single(response.Contents));
        Assert.Equal("audio/wav", audio.MediaType);
        var pcm = WavFile.Read(audio.Data.Span);
        Assert.Equal(24000, pcm.SampleRate);
        Assert.Equal(2400 * 2 - 240, pcm.Samples.Length); // 10 ms cross-fade overlap

        var raw = Assert.IsType<KokoroSynthesisResult>(response.RawRepresentation);
        Assert.Equal(pcm.Samples.Length, raw.SampleCount);
        Assert.Equal(0, raw.Chunks[0].StartSample);
        Assert.Equal(raw.Chunks[0].SampleCount, raw.Chunks[1].StartSample);
        Assert.Equal(raw.SampleCount, raw.Chunks.Sum(c => c.SampleCount));
        Assert.Equal("af_heart", raw.Provenance.VoiceId);
        Assert.InRange(pcm.Samples.Max(MathF.Abs), 0.93f, 0.96f);
    }

    [Fact]
    public async Task ClientStreamsOneUpdatePerChunkThenCompletes()
    {
        using var client = new KokoroTextToSpeechClient(Tokenizer(), new FakePhonemizer(_ => "hə"), _ => TestVoice(), (_, _, _) => new float[480]);
        var kinds = new List<TextToSpeechResponseUpdateKind>();
        await foreach (var update in client.GetStreamingAudioAsync("One. Two. Three.")) kinds.Add(update.Kind);
        Assert.Equal([TextToSpeechResponseUpdateKind.AudioUpdating, TextToSpeechResponseUpdateKind.AudioUpdating,
            TextToSpeechResponseUpdateKind.AudioUpdating, TextToSpeechResponseUpdateKind.AudioUpdated], kinds);
    }

    [Theory]
    [InlineData(0.25f)]
    [InlineData(3f)]
    public async Task ClientRejectsOutOfRangeSpeed(float speed)
    {
        using var client = new KokoroTextToSpeechClient(Tokenizer(), new FakePhonemizer(_ => "hə"), _ => TestVoice(), (_, _, _) => new float[10]);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GetAudioAsync("Hi.", new TextToSpeechOptions { Speed = speed }));
    }

    [Fact]
    public void BritishVoicesUseEnGb()
    {
        Assert.Equal("en-gb", KokoroTextToSpeechClient.LanguageForVoice("bf_emma"));
        Assert.Equal("en-us", KokoroTextToSpeechClient.LanguageForVoice("af_heart"));
    }

    internal static KokoroVoice TestVoice()
    {
        var data = new float[KokoroVoice.Rows * KokoroVoice.StyleDimensions];
        for (var row = 0; row < KokoroVoice.Rows; row++) data.AsSpan(row * KokoroVoice.StyleDimensions, KokoroVoice.StyleDimensions).Fill(row);
        return new KokoroVoice("af_heart", data);
    }

    internal sealed class FakePhonemizer(Func<string, string> map) : IPhonemizer
    {
        public List<string> Languages { get; } = [];
        public Task<string> PhonemizeAsync(string text, string language, CancellationToken cancellationToken = default)
        {
            Languages.Add(language);
            return Task.FromResult(map(text));
        }
    }
}

public sealed class TextChunkerTests
{
    [Fact]
    public void SplitsSentencesButNotAbbreviationsOrInitials()
    {
        var sentences = TextChunker.SplitSentences("Dr. Smith met J. Doe at noon today!  Was it fun?\n\nYes… Next paragraph");
        Assert.Equal(["Dr. Smith met J. Doe at noon today!", "Was it fun?", "Yes…", "Next paragraph"], sentences);
    }

    [Fact]
    public void PackRespectsLimitAndKeepsAllWords()
    {
        var text = string.Join(" ", Enumerable.Range(0, 400).Select(i => $"Sentence number {i} is here."));
        var chunks = TextChunker.Pack(TextChunker.SplitSentences(text), 5000);
        Assert.True(chunks.Count >= 2);
        Assert.All(chunks, c => Assert.InRange(c.Length, 1, 5000));
        Assert.Equal(text, string.Join(" ", chunks));
    }

    [Fact]
    public void PackSplitsAnOverlongSentenceAtClauses()
    {
        var chunks = TextChunker.Pack(["alpha beta, gamma delta; epsilon zeta"], 12);
        Assert.All(chunks, c => Assert.True(c.Length <= 12, c));
        Assert.Equal("alpha beta, gamma delta; epsilon zeta", string.Join(" ", chunks));
    }
}

public sealed class AudioTests
{
    [Fact]
    public void ToPcm16ProducesExactValuesAndClamps()
    {
        var pcm = new short[5];
        AudioMath.ToPcm16([-1f, 0f, 1f, 1.5f, -2f], pcm);
        Assert.Equal([(short)-32767, (short)0, (short)32767, (short)32767, (short)-32767], pcm);
    }

    [Fact]
    public void PeakNormalizeScalesToTargetAndIgnoresSilence()
    {
        float[] samples = [0.1f, -0.5f, 0.25f];
        AudioMath.PeakNormalize(samples, 0.95f);
        Assert.Equal(0.95f, samples.Max(MathF.Abs), 4);
        Assert.Equal(0.19f, samples[0], 4);
        float[] silence = [0f, 0f];
        AudioMath.PeakNormalize(silence);
        Assert.Equal([0f, 0f], silence);
    }

    [Fact]
    public void CrossFadeRampsLinearly()
    {
        var down = new float[4];
        AudioMath.CrossFade([1f, 1f, 1f, 1f], [0f, 0f, 0f, 0f], down);
        Assert.True(down[0] > down[1] && down[1] > down[2] && down[2] > down[3]);
        var flat = new float[4];
        AudioMath.CrossFade([1f, 1f, 1f, 1f], [1f, 1f, 1f, 1f], flat);
        Assert.All(flat, v => Assert.Equal(1f, v, 5));
    }

    [Fact]
    public void ConcatenateOverlapsByFadeLength()
    {
        var joined = AudioMath.Concatenate([new float[100], new float[50], new float[30]], 10);
        Assert.Equal(100 + 50 + 30 - 20, joined.Length);
    }

    [Fact]
    public void WavHeaderHasCorrectRiffAndDataLengths()
    {
        var wav = WavFile.ToPcm16Wav(new float[1000], 24000);
        Assert.Equal(44 + 2000, wav.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(wav.Length - 8, BitConverter.ToInt32(wav, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal(1, BitConverter.ToInt16(wav, 20));
        Assert.Equal(1, BitConverter.ToInt16(wav, 22));
        Assert.Equal(24000, BitConverter.ToInt32(wav, 24));
        Assert.Equal(48000, BitConverter.ToInt32(wav, 28));
        Assert.Equal(16, BitConverter.ToInt16(wav, 34));
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(2000, BitConverter.ToInt32(wav, 40));
    }

    [Fact]
    public void WavRoundTripsWithinPcmPrecision()
    {
        float[] samples = [0f, 0.5f, -0.5f, 0.25f];
        var read = WavFile.Read(WavFile.ToPcm16Wav(samples, 16000));
        Assert.Equal(16000, read.SampleRate);
        for (var i = 0; i < samples.Length; i++) Assert.Equal(samples[i], read.Samples[i], 3);
    }

    [Fact]
    public void WavConcatenateRewritesLengths()
    {
        var a = WavFile.ToPcm16Wav(Enumerable.Repeat(0.1f, 2400).ToArray(), 24000);
        var b = WavFile.ToPcm16Wav(Enumerable.Repeat(0.2f, 1200).ToArray(), 24000);
        var joined = WavFile.Concatenate([a, b], TimeSpan.FromMilliseconds(10));
        var samples = 2400 + 1200 - 240;
        Assert.Equal(44 + samples * 2, joined.Length);
        Assert.Equal(joined.Length - 8, BitConverter.ToInt32(joined, 4));
        Assert.Equal(samples * 2, BitConverter.ToInt32(joined, 40));
        Assert.Equal(24000, WavFile.Read(joined).SampleRate);
    }

    [Fact]
    public void ResampleChangesLength() => Assert.Equal(16000, AudioMath.Resample(new float[24000], 24000, 16000).Length);
}
