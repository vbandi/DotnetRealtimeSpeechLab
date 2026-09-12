using System.ClientModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Realtime;

#pragma warning disable OPENAI002 // OpenAI Realtime API is experimental

namespace DotnetRealtimeSpeechLab;

/// <summary>
/// OpenAI Realtime runner with tool support, designed for ultra-fast voice reactions.
/// </summary>
public class OpenAIRealtimeRunner : IVoiceRunner
{
    private readonly string _apiKey;
    private readonly string _modelName;
    private readonly string _systemPrompt;
    private readonly IList<AIFunction> _aiFunctions;
    private readonly string? _voiceName;

    private RealtimeClient? _realtimeClient;
    private RealtimeSessionClient? _session;
    private CancellationTokenSource? _sessionCts;
    private Task? _receiveTask;
    private bool _isDisposed;

    // Transcript tracking
    private readonly System.Text.StringBuilder _currentUserTranscript = new();
    private readonly System.Text.StringBuilder _currentAiTranscript = new();
    private int _conversationTurns;

    // Parallel tool call support
    private readonly List<RealtimeServerUpdateResponseFunctionCallArgumentsDone> _pendingToolCalls = new();
    private bool _isCollectingToolCalls;

    public string ProviderName => "OpenAI Realtime";
    public int InputSampleRate => 24000;  // OpenAI uses 24kHz
    public int OutputSampleRate => 24000;
    public bool IsConnected => _session != null;
    public int ConversationHistoryCount => _conversationTurns;

    #region Events

#pragma warning disable CS0067
    public event EventHandler? Connected;
    public event EventHandler? Disconnected;
    public event EventHandler<Exception>? ErrorOccurred;
    public event EventHandler<byte[]>? AudioReceived;
    public event EventHandler<string>? UserTranscriptReceived;
    public event EventHandler<string>? AiTranscriptReceived;
    public event EventHandler? TurnCompleted;
    public event EventHandler? Interrupted;
    public event EventHandler<string>? ThinkingReceived;
    public event EventHandler? ReconnectionStarted;
    public event EventHandler? ReconnectionCompleted;
    public event EventHandler<ToolCallInfo>? ToolCallStarted;
    public event EventHandler<ToolCallInfo>? ToolCallCompleted;
#pragma warning restore CS0067

    #endregion

    public OpenAIRealtimeRunner(
        string apiKey,
        string systemPrompt,
        IList<AIFunction> aiFunctions,
        string? modelName = null,
        string? voiceName = null)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _modelName = modelName ?? "gpt-realtime-mini";
        _systemPrompt = systemPrompt;
        _aiFunctions = aiFunctions;
        _voiceName = VoiceCatalog.NormalizeVoiceId(VoiceProvider.OpenAI, voiceName);
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(OpenAIRealtimeRunner));

        _realtimeClient = new RealtimeClient(new ApiKeyCredential(_apiKey));

        _session = await _realtimeClient.StartConversationSessionAsync(
            model: _modelName,
            cancellationToken: cancellationToken);

        var sessionOptions = new RealtimeConversationSessionOptions
        {
            Instructions = _systemPrompt,
            AudioOptions = new RealtimeConversationSessionAudioOptions
            {
                InputAudioOptions = new RealtimeConversationSessionInputAudioOptions
                {
                    AudioFormat = new RealtimePcmAudioFormat(),
                    AudioTranscriptionOptions = new RealtimeAudioTranscriptionOptions
                    {
                        Model = "whisper-1"
                    },
                    TurnDetection = new RealtimeSemanticVadTurnDetection
                    {
                        EagernessLevel = RealtimeSemanticVadEagernessLevel.Medium,
                        CreateResponseEnabled = true,
                        InterruptResponseEnabled = true
                    }
                },
                OutputAudioOptions = new RealtimeConversationSessionOutputAudioOptions
                {
                    AudioFormat = new RealtimePcmAudioFormat(),
                    Voice = ResolveVoice(_voiceName)
                }
            }
        };

        sessionOptions.OutputModalities.Add(RealtimeOutputModality.Audio);

        // Add tools
        foreach (var tool in CreateToolDefinitions(_aiFunctions))
        {
            sessionOptions.Tools.Add(tool);
        }

        await _session.ConfigureConversationSessionAsync(sessionOptions, cancellationToken);

        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _receiveTask = ReceiveUpdatesAsync(_sessionCts.Token);

        Connected?.Invoke(this, EventArgs.Empty);
    }

    public async Task DisconnectAsync()
    {
        _sessionCts?.Cancel();

        if (_receiveTask != null)
        {
            try { await _receiveTask; }
            catch (OperationCanceledException) { }
        }

        _session?.Dispose();
        _session = null;
        _realtimeClient = null;
        _sessionCts?.Dispose();
        _sessionCts = null;
        _receiveTask = null;

        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    public async Task SendAudioAsync(byte[] audioData, CancellationToken cancellationToken = default)
    {
        if (_isDisposed || _session == null) return;

        try
        {
            var binaryData = BinaryData.FromBytes(audioData);
            await _session.SendInputAudioAsync(binaryData, cancellationToken);
        }
        catch (ObjectDisposedException) { _session = null; }
        catch (Exception ex) { ErrorOccurred?.Invoke(this, ex); }
    }

    public async Task SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_isDisposed || _session == null || string.IsNullOrWhiteSpace(text))
            return;

        try
        {
            await _session.AddItemAsync(
                RealtimeItem.CreateUserMessageItem(text),
                cancellationToken);

            await _session.StartResponseAsync(cancellationToken);
        }
        catch (ObjectDisposedException)
        {
            _session = null;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, ex);
        }
    }

    public void ClearHistory()
    {
        _conversationTurns = 0;
        _currentUserTranscript.Clear();
        _currentAiTranscript.Clear();
    }

    private async Task ReceiveUpdatesAsync(CancellationToken cancellationToken)
    {
        if (_session == null) return;

        try
        {
            await foreach (var update in _session.ReceiveUpdatesAsync(cancellationToken))
            {
                await HandleUpdateAsync(update, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ErrorOccurred?.Invoke(this, ex); }
    }

    private async Task HandleUpdateAsync(RealtimeServerUpdate update, CancellationToken cancellationToken)
    {
        switch (update)
        {
            case RealtimeServerUpdateSessionCreated:
                break;

            case RealtimeServerUpdateInputAudioBufferSpeechStarted:
                _currentUserTranscript.Clear();
                break;

            case RealtimeServerUpdateInputAudioBufferSpeechStopped:
                break;

            case RealtimeServerUpdateConversationItemInputAudioTranscriptionCompleted transcription:
                // OpenAI sends final transcription at once
                UserTranscriptReceived?.Invoke(this, transcription.Transcript);
                _conversationTurns++;
                break;

            case RealtimeServerUpdateResponseOutputItemAdded:
                _currentAiTranscript.Clear();
                break;

            case RealtimeServerUpdateResponseOutputAudioDelta audioDelta:
                var audioBytes = audioDelta.Delta.ToArray();
                if (audioBytes.Length > 0)
                {
                    AudioReceived?.Invoke(this, audioBytes);
                }
                break;

            case RealtimeServerUpdateResponseOutputAudioTranscriptDelta transcriptDelta:
                if (!string.IsNullOrEmpty(transcriptDelta.Delta))
                {
                    _currentAiTranscript.Append(transcriptDelta.Delta);
                    AiTranscriptReceived?.Invoke(this, transcriptDelta.Delta);
                }
                break;

            case RealtimeServerUpdateResponseFunctionCallArgumentsDone functionCallDone:
                // Collect tool calls - don't execute yet, wait for RealtimeServerUpdateResponseDone
                _pendingToolCalls.Add(functionCallDone);
                _isCollectingToolCalls = true;
                break;

            case RealtimeServerUpdateResponseDone:
                _conversationTurns++;
                TurnCompleted?.Invoke(this, EventArgs.Empty);

                // Process all collected tool calls in parallel
                if (_isCollectingToolCalls && _pendingToolCalls.Count > 0)
                {
                    await HandleParallelToolCallsAsync(_pendingToolCalls.ToList(), cancellationToken);
                    _pendingToolCalls.Clear();
                    _isCollectingToolCalls = false;
                }
                break;

            case RealtimeServerUpdateError error:
                ErrorOccurred?.Invoke(this, new Exception(error.Error.Message ?? "Unknown error"));
                break;
        }
    }

    private async Task HandleParallelToolCallsAsync(List<RealtimeServerUpdateResponseFunctionCallArgumentsDone> toolCalls, CancellationToken cancellationToken)
    {
        if (_session == null || toolCalls.Count == 0) return;

        // Create tool info objects and fire started events
        var toolInfos = toolCalls.Select(tc => new ToolCallInfo
        {
            Name = tc.FunctionName ?? "unknown",
            Arguments = tc.FunctionArguments.ToString() ?? "{}",
            StartTime = DateTime.UtcNow
        }).ToList();

        foreach (var info in toolInfos)
        {
            ToolCallStarted?.Invoke(this, info);
        }

        // Execute all tool calls in parallel
        var tasks = toolCalls.Select(async (toolCall, index) =>
        {
            var info = toolInfos[index];
            try
            {
                var result = await ExecuteToolAsync(toolCall.FunctionName!, toolCall.FunctionArguments.ToString()!);
                info.Result = result;
                info.EndTime = DateTime.UtcNow;
                return (toolCall.CallId!, result, (Exception?)null);
            }
            catch (Exception ex)
            {
                info.Result = $"Error: {ex.Message}";
                info.EndTime = DateTime.UtcNow;
                return (toolCall.CallId!, $"Error: {ex.Message}", ex);
            }
        }).ToList();

        var results = await Task.WhenAll(tasks);

        // Fire completed events
        foreach (var info in toolInfos)
        {
            ToolCallCompleted?.Invoke(this, info);
        }

        // Submit all results to the session
        try
        {
            foreach (var (functionCallId, result, _) in results)
            {
                await _session.AddItemAsync(
                    RealtimeItem.CreateFunctionCallOutputItem(functionCallId, result),
                    cancellationToken);
            }

            // Now trigger the response after all tool results are submitted
            await _session.StartResponseAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, ex);
        }
    }

    private async Task<string> ExecuteToolAsync(string toolName, string argsJson)
    {
        var function = _aiFunctions.FirstOrDefault(f => f.Name == toolName);
        if (function == null) return $"Unknown tool: {toolName}";

        try
        {
            var argsDict = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson)
                ?? new Dictionary<string, object?>();
            var aiArgs = new AIFunctionArguments(argsDict);
            var result = await function.InvokeAsync(aiArgs);
            return result?.ToString() ?? "null";
        }
        catch (Exception ex)
        {
            return $"Tool execution failed: {ex.Message}";
        }
    }

    private static IEnumerable<RealtimeFunctionTool> CreateToolDefinitions(IList<AIFunction> aiFunctions)
    {
        foreach (var aiFunction in aiFunctions)
        {
            var schema = aiFunction.JsonSchema;
            var properties = schema.TryGetProperty("properties", out var props) ? props : default;
            var required = schema.TryGetProperty("required", out var req) ? req : default;

            var parametersSchema = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = JsonSerializer.Deserialize<Dictionary<string, object>>(properties.GetRawText())
                    ?? new Dictionary<string, object>()
            };

            if (required.ValueKind == JsonValueKind.Array)
            {
                parametersSchema["required"] = JsonSerializer.Deserialize<List<string>>(required.GetRawText())
                    ?? new List<string>();
            }

            yield return new RealtimeFunctionTool(aiFunction.Name)
            {
                FunctionDescription = aiFunction.Description,
                FunctionParameters = BinaryData.FromString(JsonSerializer.Serialize(parametersSchema))
            };
        }
    }

    private static RealtimeVoice ResolveVoice(string? voiceName)
    {
        return voiceName?.ToLowerInvariant() switch
        {
            "alloy" => RealtimeVoice.Alloy,
            "ash" => RealtimeVoice.Ash,
            "ballad" => RealtimeVoice.Ballad,
            "cedar" => RealtimeVoice.Cedar,
            "coral" => RealtimeVoice.Coral,
            "echo" => RealtimeVoice.Echo,
            "marin" => RealtimeVoice.Marin,
            "sage" => RealtimeVoice.Sage,
            "shimmer" => RealtimeVoice.Shimmer,
            "verse" => RealtimeVoice.Verse,
            _ => RealtimeVoice.Alloy
        };
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _sessionCts?.Cancel();
        _session?.Dispose();
        _sessionCts?.Dispose();

        GC.SuppressFinalize(this);
    }
}
