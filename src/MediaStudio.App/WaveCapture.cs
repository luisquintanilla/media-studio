using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MediaStudio.App;

public interface IVoiceCapture : IDisposable
{
    bool ReachedLimit { get; }
    Exception? Error { get; }
}

public sealed class WaveCapture : IVoiceCapture
{
    public const int MaximumSeconds = 60;
    private readonly int sampleRate;
    private readonly int maximumBytes;
    private readonly object gate = new();
    private readonly List<(IntPtr Header, IntPtr Data)> buffers = [];
    private readonly WaveCallback callback;
    private readonly FileStream output;
    private readonly Action<ReadOnlyMemory<byte>>? pcm;
    private IntPtr handle;
    private bool recording;
    private bool disposed;
    private int bytes;
    public bool ReachedLimit { get; private set; }
    public Exception? Error { get; private set; }
    public static uint DeviceCount => waveInGetNumDevs();
    public static uint QueryFormat(int sampleRate = 16000)
    {
        var format = CaptureFormat(sampleRate);
        // WAVE_FORMAT_QUERY checks support without opening or recording the device.
        return waveInOpen(out _, uint.MaxValue, ref format, null, IntPtr.Zero, 1);
    }

    private static WaveFormat CaptureFormat(int sampleRate) => sampleRate is 16000 or 24000
        ? new() {
            Format = 1, Channels = 1, SamplesPerSecond = (uint)sampleRate,
            AverageBytesPerSecond = (uint)sampleRate * 2, BlockAlign = 2, BitsPerSample = 16
        }
        : throw new InvalidDataException("Speech capture supports explicit16000Hz or24000Hz mono PCM16 only.");

    public WaveCapture(string file, Action<ReadOnlyMemory<byte>>? pcm = null, int sampleRate = 16000)
    {
        _ = CaptureFormat(sampleRate);
        this.sampleRate = sampleRate;
        maximumBytes = sampleRate * 2 * MaximumSeconds;
        this.pcm = pcm;
        callback = OnData;
        output = new(file, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        WriteHeader(0);
        try
        {
            var format = CaptureFormat(sampleRate);
            Require(waveInOpen(out handle, uint.MaxValue, ref format, callback, IntPtr.Zero, 0x30000), "open microphone");
            for (var index = 0; index < 6; index++)
            {
                var data = Marshal.AllocHGlobal(4096);
                var header = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
                buffers.Add((header, data));
                Marshal.StructureToPtr(new WaveHeader { Data = data, BufferLength = 4096 }, header, false);
                Require(waveInPrepareHeader(handle, header, (uint)Marshal.SizeOf<WaveHeader>()), "prepare microphone buffer");
                Require(waveInAddBuffer(handle, header, (uint)Marshal.SizeOf<WaveHeader>()), "queue microphone buffer");
            }
            recording = true;
            Require(waveInStart(handle), "start microphone");
        }
        catch { Dispose(); throw; }
    }

    private void OnData(IntPtr device, uint message, IntPtr instance, IntPtr header, IntPtr unused)
    {
        if (message != 0x3C0) return;
        lock (gate)
        {
            if (disposed || Error is not null) return;
            try
            {
                var value = Marshal.PtrToStructure<WaveHeader>(header);
                var count = Math.Min(checked((int)value.BytesRecorded), maximumBytes - bytes);
                count -= count % 2;
                var data = new byte[count];
                Marshal.Copy(value.Data, data, 0, count);
                output.Write(data);
                pcm?.Invoke(data);
                bytes += count;
                ReachedLimit = bytes >= maximumBytes;
                if (recording && !ReachedLimit)
                    Require(waveInAddBuffer(device, header, (uint)Marshal.SizeOf<WaveHeader>()), "reuse microphone buffer");
            }
            catch (Exception error) { Error = error; }
        }
    }

    public void Stop()
    {
        lock (gate) recording = false;
        if (handle != IntPtr.Zero)
        {
            Require(waveInReset(handle), "stop microphone");
            foreach (var (header, _) in buffers)
                Require(waveInUnprepareHeader(handle, header, (uint)Marshal.SizeOf<WaveHeader>()), "release microphone buffer");
            Require(waveInClose(handle), "close microphone");
            handle = IntPtr.Zero;
        }
        lock (gate)
        {
            if (disposed) return;
            WriteHeader(bytes);
            output.Flush(flushToDisk: true);
        }
        if (Error is { } error) throw new IOException("Microphone recording failed.", error);
    }

    private void WriteHeader(int count)
    {
        var position = output.Position;
        output.Position = 0;
        using var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8); writer.Write(36 + count); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(sampleRate);
        writer.Write(sampleRate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(count);
        output.Position = Math.Max(44, position);
    }

    public static void ValidateWave(string file, int sampleRate = 16000)
    {
        _ = CaptureFormat(sampleRate);
        using var stream = File.OpenRead(file);
        using var reader = new BinaryReader(stream);
        if (stream.Length < sampleRate / 50 + 44 || stream.Length > sampleRate * 2 * MaximumSeconds + 44
            || Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF"
            || reader.ReadInt32() != stream.Length - 8
            || Encoding.ASCII.GetString(reader.ReadBytes(8)) != "WAVEfmt "
            || reader.ReadInt32() != 16 || reader.ReadInt16() != 1 || reader.ReadInt16() != 1
            || reader.ReadInt32() != sampleRate || reader.ReadInt32() != sampleRate * 2
            || reader.ReadInt16() != 2 || reader.ReadInt16() != 16
            || Encoding.ASCII.GetString(reader.ReadBytes(4)) != "data"
            || reader.ReadInt32() != stream.Length - 44 || stream.Length % 2 != 0)
            throw new InvalidDataException($"Voice requires a bounded {sampleRate}Hz mono PCM16 WAV (0.01..60 seconds).");
    }

    public void Dispose()
    {
        if (disposed) return;
        try { Stop(); }
        finally
        {
            lock (gate)
            {
                disposed = true;
                output.Dispose();
                if (handle == IntPtr.Zero)
                {
                    foreach (var (header, data) in buffers) { Marshal.FreeHGlobal(header); Marshal.FreeHGlobal(data); }
                    buffers.Clear();
                }
            }
        }
    }

    private static void Require(uint result, string action)
    {
        if (result != 0) throw new IOException($"Could not {action} (Windows multimedia error {result}). Check microphone availability, privacy permission and another app's exclusive use.");
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormat
    {
        public ushort Format, Channels;
        public uint SamplesPerSecond, AverageBytesPerSecond;
        public ushort BlockAlign, BitsPerSample, ExtraSize;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public uint BufferLength, BytesRecorded;
        public UIntPtr User;
        public uint Flags, Loops;
        public IntPtr Next;
        public UIntPtr Reserved;
    }
    private delegate void WaveCallback(IntPtr device, uint message, IntPtr instance, IntPtr header, IntPtr unused);
    [DllImport("winmm.dll")] private static extern uint waveInGetNumDevs();
    [DllImport("winmm.dll")] private static extern uint waveInOpen(out IntPtr handle, uint device, ref WaveFormat format, WaveCallback? callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint waveInPrepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveInUnprepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveInAddBuffer(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveInStart(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveInReset(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveInClose(IntPtr handle);
}
