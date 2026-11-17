namespace VideoProduction;

public sealed record WavInfo(int SampleRate, int Channels, long Samples);

public static class Wav
{
    public static (long Offset, long Bytes) PcmRange(string file)
    {
        var clock = Inspect(file);
        using var stream = File.OpenRead(file);
        using var reader = new BinaryReader(stream);
        stream.Position = 12;
        long offset = 0, bytes = 0;
        var formats = 0;
        while (stream.Position + 8 <= stream.Length)
        {
            var id = reader.ReadUInt32();
            var length = reader.ReadUInt32();
            if (id == 0x20746d66) formats++;
            if (id == 0x61746164)
            {
                Guard.Require(offset == 0, "wav.data", "Composition requires exactly one PCM data chunk.");
                offset = stream.Position;
                bytes = length;
            }
            stream.Position = checked(stream.Position + length + (length & 1));
        }
        Guard.Require(formats == 1 && offset > 0 && bytes == checked(clock.Samples * 2), "wav.data", "Composition requires one unambiguous format/data range matching the measured clock.");
        return (offset, bytes);
    }

    public static void WriteHeader(BinaryWriter writer, int sampleRate, long samples)
    {
        Guard.Require(sampleRate is >= 8000 and <= 192000 && samples > 0, "wav.profile", "Invalid mono PCM16 composition clock.");
        var bytes = checked((uint)(samples * 2));
        writer.Write("RIFF"u8); writer.Write(checked(bytes + 36)); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((ushort)1); writer.Write((ushort)1); writer.Write(sampleRate);
        writer.Write(checked(sampleRate * 2)); writer.Write((ushort)2); writer.Write((ushort)16);
        writer.Write("data"u8); writer.Write(bytes);
    }

    public static WavInfo Inspect(string file)
    {
        using var stream = File.OpenRead(file);
        using var reader = new BinaryReader(stream);
        Guard.Require(stream.Length >= 44 && new string(reader.ReadChars(4)) == "RIFF", "wav.header", "Expected PCM16 RIFF WAV.");
        var size = reader.ReadUInt32();
        Guard.Require(size + 8L == stream.Length && new string(reader.ReadChars(4)) == "WAVE", "wav.size", "WAV length/header mismatch.");
        int rate = 0, channels = 0;
        long dataBytes = 0;
        while (stream.Position + 8 <= stream.Length)
        {
            var id = new string(reader.ReadChars(4));
            var length = reader.ReadUInt32();
            var next = checked(stream.Position + length + (length & 1));
            Guard.Require(next <= stream.Length, "wav.chunk", "Truncated WAV chunk.");
            if (id == "fmt ")
            {
                Guard.Require(length >= 16 && reader.ReadUInt16() == 1, "wav.encoding", "Only uncompressed integer PCM16 WAV is accepted.");
                channels = reader.ReadUInt16();
                rate = reader.ReadInt32();
                var byteRate = reader.ReadInt32();
                var alignment = reader.ReadUInt16();
                var bits = reader.ReadUInt16();
                Guard.Require(channels == 1 && rate is >= 8000 and <= 192000 && bits == 16
                    && alignment == 2 && byteRate == rate * 2, "wav.profile", "Narration must be mono PCM16 with a valid sample rate.");
            }
            if (id == "data") dataBytes = length;
            stream.Position = next;
        }
        Guard.Require(rate > 0 && channels == 1 && dataBytes > 0 && dataBytes % 2 == 0,
            "wav.data", "Missing/invalid PCM WAV data.");
        return new WavInfo(rate, channels, dataBytes / 2);
    }
}
