using System.Buffers.Binary;
using System.Text;

namespace MediaStudio.Speech.Audio;

/// <summary>Decoded mono audio.</summary>
public sealed record PcmAudio(float[] Samples, int SampleRate)
{
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Samples.Length / SampleRate);
}

/// <summary>Minimal RIFF/WAVE reader and PCM16 mono writer.</summary>
public static class WavFile
{
    public const int HeaderSize = 44;

    /// <summary>Encodes float samples as a canonical 44-byte-header mono PCM16 WAV.</summary>
    public static byte[] ToPcm16Wav(ReadOnlySpan<float> samples, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        var pcm = new short[samples.Length];
        AudioMath.ToPcm16(samples, pcm);
        var bytes = new byte[HeaderSize + pcm.Length * 2];
        WriteHeader(bytes, sampleRate, pcm.Length);
        for (var i = 0; i < pcm.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(HeaderSize + i * 2), pcm[i]);
        return bytes;
    }

    /// <summary>Writes the RIFF header for <paramref name="sampleCount"/> mono PCM16 samples into the first 44 bytes.</summary>
    public static void WriteHeader(Span<byte> header, int sampleRate, long sampleCount)
    {
        var dataBytes = checked((uint)(sampleCount * 2));
        Encoding.ASCII.GetBytes("RIFF").CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked(36 + dataBytes));
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(header[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(header[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(header[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], sampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(header[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(header[34..], 16);
        Encoding.ASCII.GetBytes("data").CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], dataBytes);
    }

    /// <summary>Decodes PCM16 or IEEE float32 WAV (any channel count, downmixed to mono). Extra chunks such as LIST are skipped.</summary>
    public static PcmAudio Read(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav[8..12].SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Audio is not a RIFF/WAVE file.");
        int? format = null, channels = null, rate = null, bits = null;
        var offset = 12;
        while (offset + 8 <= wav.Length)
        {
            var id = wav.Slice(offset, 4);
            var size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(wav[(offset + 4)..]), (uint)(wav.Length - offset - 8));
            // Streamed RIFF output can carry a zero data length; the payload then runs to the end of the buffer.
            if (size == 0 && id.SequenceEqual("data"u8)) size = wav.Length - offset - 8;
            var body = wav.Slice(offset + 8, size);
            if (id.SequenceEqual("fmt "u8))
            {
                if (body.Length < 16) throw new InvalidDataException("WAV fmt chunk is truncated.");
                format = BinaryPrimitives.ReadUInt16LittleEndian(body);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
                rate = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(body[14..]);
                if (format == 0xFFFE && body.Length >= 26) format = BinaryPrimitives.ReadUInt16LittleEndian(body[24..]);
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (format is null) throw new InvalidDataException("WAV data chunk precedes its fmt chunk.");
                return new(Decode(body, format.Value, channels!.Value, bits!.Value), rate!.Value);
            }
            offset += 8 + size + (size & 1);
        }
        throw new InvalidDataException("WAV has no data chunk.");
    }

    /// <summary>
    /// Joins WAV chunks (all the same sample rate) into one mono PCM16 WAV with fresh RIFF/data lengths,
    /// cross-fading each boundary by <paramref name="crossFade"/>.
    /// </summary>
    public static byte[] Concatenate(IReadOnlyList<byte[]> wavs, TimeSpan crossFade)
    {
        if (wavs.Count == 0) throw new ArgumentException("Nothing to concatenate.", nameof(wavs));
        if (wavs.Count == 1) return wavs[0];
        var decoded = wavs.Select(w => Read(w)).ToArray();
        var rate = decoded[0].SampleRate;
        if (decoded.Any(d => d.SampleRate != rate)) throw new InvalidDataException("WAV chunks have different sample rates.");
        var joined = AudioMath.Concatenate(decoded.Select(d => d.Samples).ToArray(), (int)(rate * crossFade.TotalSeconds));
        return ToPcm16Wav(joined, rate);
    }

    private static float[] Decode(ReadOnlySpan<byte> data, int format, int channels, int bits)
    {
        if (channels <= 0) throw new InvalidDataException("WAV has no channels.");
        float[] interleaved;
        if (format == 1 && bits == 16)
        {
            var count = data.Length / 2;
            var pcm = new short[count];
            for (var i = 0; i < count; i++) pcm[i] = BinaryPrimitives.ReadInt16LittleEndian(data[(i * 2)..]);
            interleaved = new float[count];
            AudioMath.FromPcm16(pcm, interleaved);
        }
        else if (format == 3 && bits == 32)
        {
            var count = data.Length / 4;
            interleaved = new float[count];
            for (var i = 0; i < count; i++) interleaved[i] = BinaryPrimitives.ReadSingleLittleEndian(data[(i * 4)..]);
        }
        else throw new InvalidDataException($"Unsupported WAV encoding (format {format}, {bits}-bit). Use PCM16 or float32.");
        if (channels == 1) return interleaved;
        var frames = interleaved.Length / channels;
        var mono = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            float sum = 0;
            for (var c = 0; c < channels; c++) sum += interleaved[f * channels + c];
            mono[f] = sum / channels;
        }
        return mono;
    }
}
