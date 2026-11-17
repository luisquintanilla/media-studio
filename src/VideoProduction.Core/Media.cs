using System.Globalization;
using System.Text.Json;

namespace VideoProduction;

public sealed record MediaInfo(string Codec, int Width, int Height, string PixelFormat, string FrameRate,
    long Frames, double Duration, string AudioCodec, int AudioRate, int AudioChannels, double StartTime,
    string ColorSpace, string ColorTransfer, string ColorPrimaries);

public sealed record Tools(string SourceRoot, string Node, string Ffmpeg, string Ffprobe, KokoroConfig? Kokoro = null,
    SpeechConfig? Speech = null);
public sealed record KokoroConfig(string Dotnet, string Assembly, string Assets, string Espeak, string FrontendLock,
    string Voice = "af_heart", string Language = "en-us", string? Execution = null);

public static class Media
{
    public static long VideoFrames(MediaInfo info, Rate fps)
    {
        fps.Validate();
        var rate = info.FrameRate.Split('/');
        // Container duration is rounded and may include a longer audio tail.
        if (info.Frames > 0 && rate.Length == 2
            && long.TryParse(rate[0], out var numerator) && numerator > 0
            && long.TryParse(rate[1], out var denominator) && denominator > 0)
            return Rate.Divide((System.Numerics.BigInteger)info.Frames * denominator * fps.Numerator,
                (System.Numerics.BigInteger)numerator * fps.Denominator, Rounding.Floor);
        return (long)Math.Floor(info.Duration * fps.Numerator / fps.Denominator + 0.00001);
    }

    public static bool HasFastStart(string file)
    {
        using var stream = File.OpenRead(file);
        using var reader = new BinaryReader(stream);
        long moov = -1, mdat = -1;
        while (stream.Position + 8 <= stream.Length)
        {
            var position = stream.Position;
            var size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(reader.ReadBytes(4));
            var type = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4));
            long length = size;
            if (size == 1)
            {
                Guard.Require(stream.Position + 8 <= stream.Length, "mp4.atom", "Truncated extended MP4 atom.");
                length = checked((long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(reader.ReadBytes(8)));
            }
            else if (size == 0) length = stream.Length - position;
            Guard.Require(length >= (size == 1 ? 16 : 8) && length <= stream.Length - position, "mp4.atom", "Invalid MP4 atom length.");
            if (type == "moov") moov = position;
            if (type == "mdat") mdat = position;
            stream.Position = position + length;
        }
        return moov >= 0 && mdat > moov;
    }

    public static async Task<MediaInfo> Probe(string ffprobe, string file, CancellationToken ct)
    {
        var result = await Processes.Run(ffprobe,
            ["-v", "error", "-protocol_whitelist", "file,pipe", "-count_frames", "-show_streams", "-show_format", "-of", "json", file],
            Path.GetDirectoryName(file)!, ct);
        using var json = JsonDocument.Parse(result.Stdout);
        var streams = json.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.FirstOrDefault(s => Text(s, "codec_type") == "video");
        var audio = streams.FirstOrDefault(s => Text(s, "codec_type") == "audio");
        var format = json.RootElement.GetProperty("format");
        return new MediaInfo(Text(video, "codec_name"), Int(video, "width"), Int(video, "height"), Text(video, "pix_fmt"),
            Text(video, "avg_frame_rate"), Long(video, "nb_read_frames"), Double(format, "duration"),
            Text(audio, "codec_name"), Int(audio, "sample_rate"), Int(audio, "channels"), Double(format, "start_time"),
            Text(video, "color_space"), Text(video, "color_transfer"), Text(video, "color_primaries"));
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value.ToString() : "";
    private static int Int(JsonElement element, string name) => int.TryParse(Text(element, name), out var value) ? value : 0;
    private static long Long(JsonElement element, string name) => long.TryParse(Text(element, name), out var value) ? value : 0;
    private static double Double(JsonElement element, string name) =>
        double.TryParse(Text(element, name), CultureInfo.InvariantCulture, out var value) ? value : 0;

    public static async Task<(double Integrated, double Peak, JsonElement Measurement)> Loudness(Tools tools,
        string file, CancellationToken ct)
    {
        var result = await Processes.Run(tools.Ffmpeg, ["-hide_banner", "-nostdin", "-protocol_whitelist", "file,pipe", "-i", file, "-vn",
            "-af", "loudnorm=I=-16:TP=-1.5:LRA=11:print_format=json", "-f", "null", "-"],
            Path.GetDirectoryName(file)!, ct, 300);
        var begin = result.Stderr.LastIndexOf('{');
        var end = result.Stderr.LastIndexOf('}');
        Guard.Require(begin >= 0 && end > begin, "media.loudness", "FFmpeg did not return a loudness measurement.");
        using var json = JsonDocument.Parse(result.Stderr[begin..(end + 1)]);
        var root = json.RootElement;
        var integrated = double.Parse(root.GetProperty("input_i").GetString()!, CultureInfo.InvariantCulture);
        var peak = double.Parse(root.GetProperty("input_tp").GetString()!, CultureInfo.InvariantCulture);
        Guard.Require(double.IsFinite(integrated) && double.IsFinite(peak), "media.silent", "Audio is silent or measurement is not finite.");
        return (integrated, peak, root.Clone());
    }

    public static string Number(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture);
}
