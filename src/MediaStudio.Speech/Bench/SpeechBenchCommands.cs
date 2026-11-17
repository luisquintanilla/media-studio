using System.Diagnostics;
using System.Globalization;
using MediaStudio.Configuration;
using MediaStudio.Speech.Audio;
using Microsoft.Extensions.AI;

namespace MediaStudio.Speech.Bench;

#pragma warning disable MEAI001 // MEAI speech abstractions are experimental.
/// <summary>
/// <c>bench tts</c> and <c>bench stt</c> (alias <c>bench cloud-stt</c>): time every configured speech engine. Local engines
/// (Kokoro, Whisper) always run; Azure Speech (MAI) runs only when <c>privacy.allowCloud</c> is on and a Foundry connection exists.
/// </summary>
public static class SpeechBenchCommands
{
    public const string Usage = """
        Usage:
          bench tts [--text <text>] [--voice <kokoro-voice>] [--out <dir>] [--local-only] [--settings <file>] [--models <dir>]
          bench stt [--files <dir|dir/*.wav>] [--model base|large-v3-turbo] [--out <dir>] [--local-only] [--settings <file>] [--models <dir>]
          bench cloud-stt ...   (alias of bench stt)

        bench stt pairs every <name>.wav with a reference transcript <name>.txt. Without --files it uses samples/stt
        when present, otherwise it synthesizes short reference clips with Kokoro (a local round-trip, not a recording test).
        """;

    public const string DefaultText = "Media Studio turns a short script into a narrated video. Every scene is spoken, timed and captioned on this computer.";

    /// <summary>Sentences for synthetic STT clips when no recordings are supplied (written for this project).</summary>
    public static readonly string[] SyntheticSentences =
    [
        "The quick narration engine reads every scene aloud before the captions are timed.",
        "Rain forms when warm air rises, cools and the water vapor condenses into droplets.",
        "Please open the project folder and choose the second chapter of the outline.",
        "A small local model can transcribe clear speech with very few mistakes."
    ];

    public static bool Handles(string[] args) => args.Length > 0 && args[0] == "bench"
        && (args.Length == 1 || args[1] is "tts" or "stt" or "cloud-stt" or "-h" or "--help");

    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken cancellationToken = default)
    {
        if (args.Length < 2 || args[1] is "-h" or "--help") { output.WriteLine(Usage); return args.Length < 2 ? 2 : 0; }
        Options options;
        try { options = Options.Parse(args.Skip(2)); }
        catch (ArgumentException error) { output.WriteLine(error.Message); output.WriteLine(Usage); return 2; }
        var settings = (options.Value("--settings") is { } file ? new ProviderSettingsStore(file) : new ProviderSettingsStore()).TryLoad()
            ?? new ProviderSettings();
        var assets = new ModelAssets(ModelManifest.LoadDefault(), options.Value("--models"));
        var outDirectory = Path.GetFullPath(options.Value("--out")
            ?? Path.Combine(MediaStudioPaths.Root, "bench", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)));
        Directory.CreateDirectory(outDirectory);
        var cloud = !options.Has("--local-only") && CloudConfigured(settings);
        if (!cloud) output.WriteLine(options.Has("--local-only") ? "Cloud engines skipped (--local-only)."
            : "Cloud engines skipped: enable privacy.allowCloud and add a Foundry connection (run `configure azd`) to include Azure Speech.");
        return args[1] == "tts"
            ? await TtsAsync(settings, assets, options, outDirectory, cloud, output, cancellationToken).ConfigureAwait(false)
            : await SttAsync(settings, assets, options, outDirectory, cloud, output, cancellationToken).ConfigureAwait(false);
    }

    public static bool CloudConfigured(ProviderSettings settings) =>
        settings.Privacy.AllowCloud && settings.Connections.Any(c => c.Kind == ConnectionKind.Foundry);

    private static async Task<int> TtsAsync(ProviderSettings settings, ModelAssets assets, Options options, string outDirectory,
        bool cloud, TextWriter output, CancellationToken cancellationToken)
    {
        var text = options.Value("--text") ?? DefaultText;
        var engines = new List<(string Name, TtsSettings Tts)>
        {
            ("kokoro", settings.Speech.Tts with { Provider = TtsProvider.Kokoro, Voice = options.Value("--voice") ?? KokoroVoice(settings.Speech.Tts.Voice) })
        };
        if (cloud) engines.Add(("azure-speech (MAI-Voice)", settings.Speech.Tts with { Provider = TtsProvider.AzureSpeech, Voice = AzureVoice(settings.Speech.Tts.Voice) }));
        output.WriteLine($"Text ({text.Length} chars): {text}");
        output.WriteLine($"{"engine",-26} {"load s",7} {"synth s",8} {"audio s",8} {"RTF",6}  output");
        var failures = 0;
        foreach (var (name, tts) in engines)
        {
            try
            {
                var load = Stopwatch.StartNew();
                using var client = new SpeechClientFactory(settings with { Speech = settings.Speech with { Tts = tts } }, assets).CreateTextToSpeech();
                load.Stop();
                var synth = Stopwatch.StartNew();
                var response = await client.GetAudioAsync(text, cancellationToken: cancellationToken).ConfigureAwait(false);
                synth.Stop();
                var data = response.Contents.OfType<DataContent>().Single();
                var audio = WavFile.Read(data.Data.Span);
                var path = Path.Combine(outDirectory, $"tts-{Slug(name)}.wav");
                await File.WriteAllBytesAsync(path, data.Data.ToArray(), cancellationToken).ConfigureAwait(false);
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{name,-26} {load.Elapsed.TotalSeconds,7:0.00} {synth.Elapsed.TotalSeconds,8:0.00} {audio.Duration.TotalSeconds,8:0.00} {synth.Elapsed.TotalSeconds / Math.Max(audio.Duration.TotalSeconds, 1e-6),6:0.00}  {path}"));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                failures++;
                output.WriteLine($"{name,-26} failed: {error.Message}");
            }
        }
        output.WriteLine("RTF = synthesis time / audio duration (lower is faster; < 1 is faster than real time).");
        return failures == engines.Count ? 1 : 0;
    }

    private static async Task<int> SttAsync(ProviderSettings settings, ModelAssets assets, Options options, string outDirectory,
        bool cloud, TextWriter output, CancellationToken cancellationToken)
    {
        var files = options.Value("--files");
        var clips = ResolveClips(files);
        if (clips.Count == 0)
        {
            output.WriteLine(files is null
                ? "No recordings supplied (--files) and samples/stt not found; synthesizing reference clips with Kokoro."
                : $"No .wav files found at '{files}'; synthesizing reference clips with Kokoro.");
            clips = await SynthesizeClipsAsync(settings, assets, Path.Combine(outDirectory, "clips"), cancellationToken).ConfigureAwait(false);
        }
        var missing = clips.Where(c => c.Reference is null).Select(c => c.Wav).ToArray();
        if (missing.Length > 0)
        {
            output.WriteLine("Every clip needs a reference transcript next to it (<name>.txt). Missing for: " + string.Join(", ", missing.Select(Path.GetFileName)));
            return 2;
        }
        var whisperModel = options.Value("--model") ?? settings.Speech.Stt.Model;
        var engines = new List<(string Name, SttSettings Stt)>
        {
            ($"whisper ({WhisperAsset(whisperModel)})", settings.Speech.Stt with { Provider = SttProvider.Whisper, Model = whisperModel })
        };
        if (cloud) engines.Add(("azure-speech (MAI-Transcribe)", settings.Speech.Stt with { Provider = SttProvider.AzureSpeech, Model = null }));
        output.WriteLine($"{clips.Count} clip(s).");
        output.WriteLine($"{"engine",-30} {"WER",7} {"errors",7} {"words",6} {"RTF",6} {"word times",10}");
        var failures = 0;
        foreach (var (name, stt) in engines)
        {
            try
            {
                using var client = new SpeechClientFactory(settings with { Speech = settings.Speech with { Stt = stt } }, assets).CreateSpeechToText();
                int errors = 0, words = 0;
                double audioSeconds = 0, wallSeconds = 0;
                var allTimed = true;
                foreach (var clip in clips)
                {
                    var bytes = await File.ReadAllBytesAsync(clip.Wav, cancellationToken).ConfigureAwait(false);
                    audioSeconds += WavFile.Read(bytes).Duration.TotalSeconds;
                    var watch = Stopwatch.StartNew();
                    var response = await client.GetTextAsync(new MemoryStream(bytes), new SpeechToTextOptions { SpeechLanguage = "en" }, cancellationToken).ConfigureAwait(false);
                    wallSeconds += watch.Elapsed.TotalSeconds;
                    var wer = Wer.Compute(clip.Reference!, response.Text);
                    errors += wer.Errors;
                    words += wer.ReferenceWords;
                    allTimed &= TimedWord.From(response).Count > 0;
                    output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {Path.GetFileName(clip.Wav),-28} {wer.Rate,7:P1}  \"{response.Text.Trim()}\""));
                }
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{name,-30} {(words == 0 ? 0 : (double)errors / words),7:P1} {errors,7} {words,6} {wallSeconds / Math.Max(audioSeconds, 1e-6),6:0.00} {(allTimed ? "yes" : "no"),10}"));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                failures++;
                output.WriteLine($"{name,-30} failed: {error.Message}");
            }
        }
        output.WriteLine("WER = word-level edit distance / reference words after lower-casing and stripping punctuation. RTF = transcription time / audio duration.");
        return failures == engines.Count ? 1 : 0;
    }

    public sealed record Clip(string Wav, string? Reference);

    /// <summary>Accepts a directory, a <c>dir/*.wav</c> pattern or a single WAV; defaults to <c>samples/stt</c> under the current directory.</summary>
    public static IReadOnlyList<Clip> ResolveClips(string? files)
    {
        string directory, pattern = "*.wav";
        if (files is null) directory = Path.Combine(Environment.CurrentDirectory, "samples", "stt");
        else if (Directory.Exists(files)) directory = files;
        else if (File.Exists(files)) return [ClipFor(Path.GetFullPath(files))];
        else
        {
            directory = Path.GetDirectoryName(Path.GetFullPath(files)) ?? Environment.CurrentDirectory;
            pattern = Path.GetFileName(files);
        }
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, pattern).Where(f => f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal).Select(ClipFor).ToArray()
            : [];
    }

    private static Clip ClipFor(string wav)
    {
        var txt = Path.ChangeExtension(wav, ".txt");
        return new(wav, File.Exists(txt) ? File.ReadAllText(txt).Trim() : null);
    }

    private static async Task<IReadOnlyList<Clip>> SynthesizeClipsAsync(ProviderSettings settings, ModelAssets assets, string directory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var tts = settings.Speech.Tts with { Provider = TtsProvider.Kokoro, Voice = KokoroVoice(settings.Speech.Tts.Voice) };
        using var client = new SpeechClientFactory(settings with { Speech = settings.Speech with { Tts = tts } }, assets).CreateTextToSpeech();
        var clips = new List<Clip>();
        for (var i = 0; i < SyntheticSentences.Length; i++)
        {
            var response = await client.GetAudioAsync(SyntheticSentences[i], cancellationToken: cancellationToken).ConfigureAwait(false);
            var wav = Path.Combine(directory, $"kokoro-{i + 1:00}.wav");
            await File.WriteAllBytesAsync(wav, response.Contents.OfType<DataContent>().Single().Data.ToArray(), cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.ChangeExtension(wav, ".txt"), SyntheticSentences[i], cancellationToken).ConfigureAwait(false);
            clips.Add(new(wav, SyntheticSentences[i]));
        }
        return clips;
    }

    private static string KokoroVoice(string? voice) => voice is { Length: > 0 } && !voice.Contains('-') ? voice : "af_heart";
    private static string? AzureVoice(string? voice) => voice is not null && voice.Contains('-') ? voice : null;
    private static string WhisperAsset(string? model) => Whisper.WhisperNetSpeechToTextClient.AssetIdFor(model);
    private static string Slug(string name) => new(name.Where(char.IsLetterOrDigit).ToArray());

    private sealed class Options
    {
        private static readonly string[] Flags = ["--local-only"];
        private static readonly string[] Valued = ["--text", "--voice", "--out", "--files", "--model", "--settings", "--models"];
        private readonly Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase);

        public static Options Parse(IEnumerable<string> args)
        {
            var options = new Options();
            using var enumerator = args.GetEnumerator();
            while (enumerator.MoveNext())
            {
                var name = enumerator.Current;
                if (Flags.Contains(name, StringComparer.OrdinalIgnoreCase)) options.values[name] = null;
                else if (Valued.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    if (!enumerator.MoveNext()) throw new ArgumentException($"{name} needs a value.");
                    options.values[name] = enumerator.Current;
                }
                else throw new ArgumentException($"Unknown option '{name}'.");
            }
            return options;
        }

        public bool Has(string name) => values.ContainsKey(name);
        public string? Value(string name) => values.TryGetValue(name, out var value) ? value : null;
    }
}
#pragma warning restore MEAI001
