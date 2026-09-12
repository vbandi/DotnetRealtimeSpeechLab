namespace DotnetRealtimeSpeechLab;

/// <summary>
/// Common interface for voice providers (Gemini Live, OpenAI Realtime).
/// </summary>
public interface IVoiceRunner : IDisposable
{
    string ProviderName { get; }
    int InputSampleRate { get; }
    int OutputSampleRate { get; }
    bool IsConnected { get; }
    int ConversationHistoryCount { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync();
    Task SendAudioAsync(byte[] audioData, CancellationToken cancellationToken = default);
    Task SendTextAsync(string text, CancellationToken cancellationToken = default);
    void ClearHistory();

    event EventHandler? Connected;
    event EventHandler? Disconnected;
    event EventHandler<Exception>? ErrorOccurred;
    event EventHandler<byte[]>? AudioReceived;
    event EventHandler<string>? UserTranscriptReceived;
    event EventHandler<string>? AiTranscriptReceived;
    event EventHandler? TurnCompleted;
    event EventHandler? Interrupted;
    event EventHandler<string>? ThinkingReceived;
    event EventHandler? ReconnectionStarted;
    event EventHandler? ReconnectionCompleted;
    event EventHandler<ToolCallInfo>? ToolCallStarted;
    event EventHandler<ToolCallInfo>? ToolCallCompleted;
}
