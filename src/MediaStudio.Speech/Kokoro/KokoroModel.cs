using Microsoft.ML.OnnxRuntime;

namespace MediaStudio.Speech.Kokoro;

/// <summary>
/// The Kokoro-82M ONNX graph: inputs <c>input_ids int64[1,n]</c>, <c>style float[1,256]</c>, <c>speed float[1]</c>;
/// output <c>waveform float[1,samples]</c> at 24 kHz.
/// </summary>
public sealed class KokoroModel : IDisposable
{
    public const int SampleRate = 24000;
    private readonly InferenceSession session;
    private readonly object gate = new();

    public KokoroModel(string modelPath, bool useDirectML = false)
    {
        if (!File.Exists(modelPath)) throw new FileNotFoundException("Kokoro ONNX model not found.", modelPath);
        using var options = new SessionOptions {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2)
        };
        if (useDirectML) options.AppendExecutionProvider("DML");
        session = new InferenceSession(modelPath, options);
        foreach (var input in new[] { "input_ids", "style", "speed" })
            if (!session.InputMetadata.ContainsKey(input))
                throw new InvalidDataException($"'{Path.GetFileName(modelPath)}' is not a Kokoro v1.0 model (missing input '{input}').");
        OutputName = session.OutputMetadata.ContainsKey("waveform") ? "waveform" : session.OutputMetadata.Keys.First();
        ModelPath = modelPath;
    }

    public string ModelPath { get; }
    public string OutputName { get; }

    /// <summary>Runs one chunk. <paramref name="ids"/> must already include the leading/trailing pad.</summary>
    public float[] Run(long[] ids, ReadOnlySpan<float> style, float speed)
    {
        if (style.Length != KokoroVoice.StyleDimensions) throw new ArgumentException("Style vector must have 256 floats.", nameof(style));
        var styleBuffer = style.ToArray();
        using var idsValue = OrtValue.CreateTensorValueFromMemory(ids, [1, ids.Length]);
        using var styleValue = OrtValue.CreateTensorValueFromMemory(styleBuffer, [1, KokoroVoice.StyleDimensions]);
        using var speedValue = OrtValue.CreateTensorValueFromMemory(new[] { speed }, [1]);
        var inputs = new Dictionary<string, OrtValue> { ["input_ids"] = idsValue, ["style"] = styleValue, ["speed"] = speedValue };
        lock (gate)
        {
            using var runOptions = new RunOptions();
            using var outputs = session.Run(runOptions, inputs, [OutputName]);
            return outputs[0].GetTensorDataAsSpan<float>().ToArray();
        }
    }

    public void Dispose() => session.Dispose();
}
