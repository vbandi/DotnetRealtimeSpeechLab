using NAudio.Wave;

namespace DotnetRealtimeSpeechLab;

/// <summary>
/// Handles audio playback at configurable sample rate.
/// </summary>
public class AudioPlayback : IDisposable
{
    public const int BitsPerSample = 16;
    public const int Channels = 1;

    private readonly BufferedWaveProvider _waveProvider;
    private readonly WaveOutEvent _waveOut;
    private bool _isDisposed;

    public int SampleRate { get; }
    public TimeSpan BufferedDuration => _waveProvider.BufferedDuration;

    public AudioPlayback(int sampleRate = 24000)
    {
        SampleRate = sampleRate;
        _waveProvider = new BufferedWaveProvider(new WaveFormat(sampleRate, BitsPerSample, Channels))
        {
            BufferLength = sampleRate * 2 * 60, // 60 seconds buffer for long responses
            DiscardOnBufferOverflow = false     // Don't drop audio, let it queue
        };

        _waveOut = new WaveOutEvent
        {
            DesiredLatency = 100 // Lower latency for faster start
        };
        _waveOut.Init(_waveProvider);
    }

    public void Start()
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(AudioPlayback));
        _waveOut.Play();
    }

    public void Stop()
    {
        _waveOut.Stop();
    }

    public void AddSamples(byte[] buffer)
    {
        if (_isDisposed) return;
        _waveProvider.AddSamples(buffer, 0, buffer.Length);
    }

    public void ClearBuffer()
    {
        _waveProvider.ClearBuffer();
    }

    public async Task WaitForDrainAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (_isDisposed)
        {
            return;
        }

        var timeoutAt = DateTime.UtcNow + timeout;
        while (!_isDisposed && _waveProvider.BufferedBytes > 0 && DateTime.UtcNow < timeoutAt)
        {
            await Task.Delay(25, cancellationToken);
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _waveOut.Stop();
        _waveOut.Dispose();
    }
}
