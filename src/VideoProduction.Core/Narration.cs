using System.Text.Json;
using System.Text.Json.Nodes;

namespace VideoProduction;

public sealed class KokoroBackend(KokoroConfig config) : INarrationBackend
{
    public async Task<NarrationTake> Synthesize(string displayText, string outputDirectory, CancellationToken ct)
    {
        Guard.Require(config.Execution == "legacy-cli", "kokoro.legacy-explicit",
            "Direct Kokoro CLI requires explicit execution:'legacy-cli'. Configure speech for the MEAI-first path; no fallback.");
        Guard.Require(!Directory.Exists(outputDirectory), "narration.immutable", "Take directory already exists; choose a new take.");
        Directory.CreateDirectory(outputDirectory);
        var input = Path.Combine(outputDirectory, "display.txt");
        var wav = Path.Combine(outputDirectory, "narration.wav");
        var metadata = Path.Combine(outputDirectory, "kokoro.json");
        Atomic.Write(input, displayText);
        await Processes.Run(config.Dotnet,
            [config.Assembly, "synthesize", "--assets", config.Assets, "--espeak", config.Espeak,
                "--lock", config.FrontendLock, "--text-file", input, "--voice", config.Voice,
                "--language", config.Language, "--output", wav, "--result", metadata],
            outputDirectory, ct, 600);
        using var json = JsonDocument.Parse(File.ReadAllText(metadata));
        var result = json.RootElement;
        Guard.Require(result.GetProperty("schemaVersion").GetInt32() == 1 && result.GetProperty("ok").GetBoolean(),
            "kokoro.contract", "Kokoro did not return a successful schemaVersion1 result.");
        var info = Wav.Inspect(wav);
        var hash = Files.Hash(wav);
        Guard.Require(info.SampleRate == 24000 && info.Samples == result.GetProperty("sampleCount").GetInt64()
            && info.SampleRate == result.GetProperty("sampleRate").GetInt32()
            && hash == result.GetProperty("wavSha256").GetString()
            && result.GetProperty("displayText").GetString() == displayText,
            "kokoro.integrity", "Kokoro WAV/text/result mismatch.");
        var provenance = result.GetProperty("provenance");
        Guard.Require(provenance.GetProperty("voiceId").GetString() == config.Voice,
            "kokoro.voice", "Backend returned a different voice.");
        long cursor = 0;
        foreach (var chunk in result.GetProperty("chunks").EnumerateArray())
        {
            Guard.Require(chunk.GetProperty("startSample").GetInt64() == cursor
                && chunk.GetProperty("sampleCount").GetInt64() > 0, "kokoro.chunks", "Noncontiguous or empty chunk ledger.");
            cursor += chunk.GetProperty("sampleCount").GetInt64();
        }
        Guard.Require(cursor == info.Samples, "kokoro.chunks", "Chunk ledger does not cover complete WAV.");
        var portableMetadata = JsonNode.Parse(File.ReadAllText(metadata))!.AsObject();
        portableMetadata["wavPath"] = "narration.wav";
        Atomic.Write(metadata, portableMetadata.ToJsonString(Json.Options));
        return new NarrationTake(wav, hash, info.SampleRate, info.Samples, displayText, metadata);
    }
}

public static class NarrationImport
{
    public static Narration Import(string root, string sourceWav, string id, string displayText,
        Cue[] cues, out Asset asset)
    {
        var info = Wav.Inspect(sourceWav);
        foreach (var cue in cues) Captions.ValidateCue(cue, info.SampleRate);
        var hash = Files.Hash(sourceWav);
        var relative = $"assets/narration/{id}-{hash}.wav";
        var target = Files.Under(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (!File.Exists(target)) File.Copy(sourceWav, target);
        Guard.Require(Files.Hash(target) == hash, "import.hash", "Imported take hash mismatch.");
        asset = new Asset(id, relative, hash, "Explicitly imported narration; ownership/consent asserted by caller", "Caller-supplied; review required");
        return new Narration(id, info.SampleRate, info.Samples, displayText, cues);
    }
}
