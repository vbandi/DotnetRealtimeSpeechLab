using NAudio.Wave;

namespace DotnetRealtimeSpeechLab;

/// <summary>
/// Handles microphone audio capture at configurable sample rate.
/// </summary>
public class AudioCapture : IDisposable
{
    public const int BitsPerSample = 16;
    public const int Channels = 1;

    private readonly WaveInEvent _waveIn;
    private bool _isRecording;
    private bool _isDisposed;

    public int SampleRate { get; }

    /// <summary>
    /// Raised when audio data is available from the microphone.
    /// </summary>
    public event EventHandler<byte[]>? AudioDataAvailable;

    public AudioCapture(int sampleRate = 16000)
    {
        SampleRate = sampleRate;
        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(sampleRate, BitsPerSample, Channels),
            BufferMilliseconds = 50 // 50ms chunks for low latency
        };

        _waveIn.DataAvailable += OnDataAvailable;
    }

    public bool IsRecording => _isRecording;

    public void StartRecording()
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(AudioCapture));
        if (_isRecording) return;

        _waveIn.StartRecording();
        _isRecording = true;
    }

    public void StopRecording()
    {
        if (!_isRecording) return;

        _waveIn.StopRecording();
        _isRecording = false;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded > 0)
        {
            var buffer = new byte[e.BytesRecorded];
            Array.Copy(e.Buffer, buffer, e.BytesRecorded);
            AudioDataAvailable?.Invoke(this, buffer);
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        StopRecording();
        _waveIn.DataAvailable -= OnDataAvailable;
        _waveIn.Dispose();
    }
}
