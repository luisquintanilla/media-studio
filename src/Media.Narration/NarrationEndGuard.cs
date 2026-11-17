using System.Security.Cryptography;

namespace VideoProduction;

public static class NarrationEndGuard
{
    public static NarrationTake Apply(NarrationTake source, int milliseconds, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Guard.Require(milliseconds is > 0 and <= 1000, "narration.end-guard", "Explicit end guard must be 1..1000 milliseconds.");
        var clock = Wav.Inspect(source.WavPath);
        Guard.Require(Files.Hash(source.WavPath) == source.WavSha256 && clock.SampleRate == source.SampleRate
            && clock.Samples == source.SampleCount && (long)clock.SampleRate * milliseconds % 1000 == 0,
            "narration.end-guard", "Source identity/clock changed or the requested guard is not an exact sample count.");
        var addedSamples = (long)clock.SampleRate * milliseconds / 1000;
        var root = Path.GetDirectoryName(source.WavPath)!;
        var output = Path.Combine(root, "narration-guarded.wav");
        var metadata = Path.Combine(root, "narration-end-guard.json");
        Guard.Require(!File.Exists(output) && !File.Exists(metadata), "narration.immutable", "End-guard output already exists; preserve it.");
        using var input = File.OpenRead(source.WavPath);
        using var reader = new BinaryReader(input, System.Text.Encoding.UTF8, leaveOpen: true);
        input.Position = 12;
        long offset = 0, bytes = 0;
        while (input.Position + 8 <= input.Length)
        {
            var id = reader.ReadUInt32();
            var length = reader.ReadUInt32();
            if (id == 0x61746164)
            {
                Guard.Require(offset == 0, "narration.end-guard", "An end guard requires exactly one PCM data chunk.");
                offset = input.Position; bytes = length;
            }
            input.Position = checked(input.Position + length + (length & 1));
        }
        Guard.Require(offset > 0 && bytes == checked(clock.Samples * 2), "narration.end-guard", "Source PCM range differs from its clock.");
        var totalBytes = checked((uint)(bytes + addedSamples * 2));
        using var prefixHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("RIFF"u8); writer.Write(checked(totalBytes + 36)); writer.Write("WAVEfmt "u8);
            writer.Write(16); writer.Write((ushort)1); writer.Write((ushort)1); writer.Write(clock.SampleRate);
            writer.Write(checked(clock.SampleRate * 2)); writer.Write((ushort)2); writer.Write((ushort)16);
            writer.Write("data"u8); writer.Write(totalBytes);
            input.Position = offset;
            var buffer = new byte[65536];
            for (long copied = 0; copied < bytes;)
            {
                ct.ThrowIfCancellationRequested();
                var count = (int)Math.Min(buffer.Length, bytes - copied);
                input.ReadExactly(buffer.AsSpan(0, count));
                prefixHash.AppendData(buffer, 0, count);
                writer.Write(buffer, 0, count); copied += count;
            }
            writer.Write(new byte[checked((int)(addedSamples * 2))]);
            writer.Flush();
        }
        var actual = Wav.Inspect(output);
        Guard.Require(actual.SampleRate == clock.SampleRate && actual.Samples == clock.Samples + addedSamples
            && Files.Hash(source.WavPath) == source.WavSha256, "narration.end-guard", "Guarded PCM clock or retained source identity changed.");
        var hash = Files.Hash(output);
        Json.Write(metadata, new
        {
            schemaVersion = 1, mode = "explicit-real-digital-silence-end-guard", milliseconds, addedSamples,
            sourceWav = Path.GetFileName(source.WavPath), source.WavSha256,
            sourceMetadata = Path.GetFileName(source.MetadataPath), sourceMetadataSha256 = Files.Hash(source.MetadataPath),
            originalSamples = clock.Samples, originalPcmSha256 = Convert.ToHexStringLower(prefixHash.GetHashAndReset()),
            outputWav = Path.GetFileName(output), outputSha256 = hash, actual.SampleRate, sampleCount = actual.Samples,
            interpretation = "Original PCM retained byte-for-byte, followed by actual zero PCM. Included in playback, measured duration and export. No ASR timestamp normalization or exception."
        });
        return source with { WavPath = output, WavSha256 = hash, SampleCount = actual.Samples, MetadataPath = metadata };
    }
}
