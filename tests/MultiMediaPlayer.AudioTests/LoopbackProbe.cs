using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace MultiMediaPlayer.AudioTests;

internal sealed class LoopbackProbe : IDisposable
{
    private readonly WasapiRecorder _capture;
    private readonly object _sync = new();
    private List<float>? _window;
    private Exception? _error;
    public int SampleRate => _capture.WaveFormat.SampleRate;
    public static async Task<LoopbackProbe> CreateAsync()
    {
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                throw new PlatformNotSupportedException("Process loopback requires Windows 10 build 19041 or newer.");
            var recorder = await new WasapiRecorderBuilder()
                .WithProcessLoopback((uint)Environment.ProcessId, ProcessLoopbackMode.IncludeTargetProcessTree)
                .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)).BuildAsync();
            return new(recorder);
        }
        catch (Exception ex) { throw new NotSupportedException("WASAPI process loopback unavailable; audio output was not verified.", ex); }
    }
    private LoopbackProbe(WasapiRecorder capture)
    {
        _capture = capture;
        _capture.DataAvailable += (buffer, flags, devicePosition, qpcPosition) =>
        {
            lock (_sync)
            {
                if (_window == null) return;
                var format = _capture.WaveFormat;
                if (format.BitsPerSample != 32) { _error = new Exception("Loopback requires 32-bit float samples."); return; }
                for (int offset = 0; offset + format.BlockAlign <= buffer.Length; offset += format.BlockAlign)
                {
                    float sample = 0;
                    if ((flags & AudioClientBufferFlags.Silent) != 0) { _window.Add(0); continue; }
                    for (int c = 0; c < format.Channels; c++) sample += BitConverter.ToSingle(buffer.Slice(offset + c * 4, 4));
                    _window.Add(sample / format.Channels);
                }
            }
        };
        _capture.RecordingStopped += (_, e) => { if (e.Exception != null) _error = e.Exception; };
        _capture.StartRecording();
    }
    public async Task<double[]> MeasureAsync(int[] frequencies, int milliseconds = 250)
    {
        lock (_sync) _window = [];
        await Task.Delay(milliseconds);
        float[] samples;
        lock (_sync) { samples = _window!.ToArray(); _window = null; }
        if (_error != null) throw new InvalidOperationException("Loopback capture stopped.", _error);
        return frequencies.Select(f => Enumerable.Range(-2, 5).Max(offset => Magnitude(samples, f + offset))).ToArray();
    }
    private double Magnitude(float[] samples, double frequency)
    {
        if (samples.Length < 64) return 0;
        var coefficient = 2 * Math.Cos(2 * Math.PI * frequency / SampleRate);
        double previous = 0, beforePrevious = 0, weightSum = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            var weight = .5 - .5 * Math.Cos(2 * Math.PI * i / (samples.Length - 1));
            var next = samples[i] * weight + coefficient * previous - beforePrevious;
            beforePrevious = previous; previous = next; weightSum += weight;
        }
        return Math.Sqrt(Math.Max(0, previous * previous + beforePrevious * beforePrevious - coefficient * previous * beforePrevious)) / weightSum * 2;
    }
    public void Dispose() { _capture.StopRecording(); _capture.Dispose(); }
}
