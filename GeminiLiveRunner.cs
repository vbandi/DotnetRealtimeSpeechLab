using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using GenerativeAI;
using GenerativeAI.Live;
using GenerativeAI.Live.Extensions;
using GenerativeAI.Types;
using Microsoft.Extensions.AI;
using WebRtcVadSharp;

namespace DotnetRealtimeSpeechLab;

/// <summary>
/// Gemini Live runner with tool support, designed for ultra-fast voice reactions.
/// Tracks timing metrics for latency analysis.
/// </summary>
public class GeminiLiveRunner : IVoiceRunner
{
    #region Constants

    public const int GeminiInputSampleRate = 16000;  // 16kHz mono 16-bit PCM
    public const int GeminiOutputSampleRate = 24000; // 24kHz mono 16-bit PCM
    private const int MaxBufferedChunks = 32;  // ~2 seconds
    private const int VadFrameBytes = 960;     // 30ms at 16kHz
    private const int MaxConnectAttempts = 2;

    private static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(15);

    #endregion

    #region Events

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

    #endregion

    #region IVoiceRunner Properties

    public string ProviderName => "Gemini Live";
    public int InputSampleRate => GeminiInputSampleRate;
    public int OutputSampleRate => GeminiOutputSampleRate;

    #endregion

    #region State

    private readonly string _apiKey;
    private readonly string _modelName;
    private readonly string _systemPrompt;
    private readonly List<Tool> _tools;
    private readonly IList<AIFunction> _aiFunctions;
    private readonly GenerationConfig _config;
    private readonly GenerativeModel _model;
    private readonly WebRtcVad _vad;
    private readonly string? _voiceName;

    private MultiModalLiveClient? _liveClient;
    private TaskCompletionSource<bool>? _setupCompleteTcs;
    private string? _sessionResumptionHandle;

    private readonly ConcurrentQueue<byte[]> _audioBuffer = new();
    private readonly List<(string role, string text)> _conversationHistory = new();
    private string _pendingUserMessage = "";
    private readonly System.Text.StringBuilder _pendingAiMessage = new();
    private bool _receivedOutputTranscriptionThisTurn;

    private bool _isConnected;
    private bool _isReconnecting;
    private bool _isBufferingAudio;
    private bool _needsReconnect;
    private bool _isUserSpeaking;
    private bool _isAiSpeaking;
    private bool _isDisposed;
    private DateTime _vadCooldownUntil = DateTime.MinValue;
    private int _consecutiveSpeechFrames;

    // Timing tracking
    private DateTime _userSpeechStartTime;
    private DateTime _userSpeechEndTime;

    #endregion

    #region Properties

    public bool IsConnected => _isConnected;
    public bool IsReconnecting => _isReconnecting;
    public bool AutoReconnectEnabled { get; set; } = true;
    public int ConversationHistoryCount => _conversationHistory.Count;

    #endregion

    public GeminiLiveRunner(
        string apiKey,
        string systemPrompt,
        IList<AIFunction> aiFunctions,
        string? modelName = null,
        string? voiceName = null)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _modelName = modelName ?? "models/gemini-3.1-flash-live-preview";
        _systemPrompt = systemPrompt;
        _aiFunctions = aiFunctions;
        _tools = ConvertToGeminiTools(aiFunctions);
        _voiceName = VoiceCatalog.NormalizeVoiceId(VoiceProvider.Gemini, voiceName);

        _config = new GenerationConfig
        {
            ResponseModalities = [Modality.AUDIO]
        };

        if (!string.IsNullOrWhiteSpace(_voiceName))
        {
            _config.SpeechConfig = new SpeechConfig
            {
                VoiceConfig = new VoiceConfig
                {
                    PrebuiltVoiceConfig = new PrebuiltVoiceConfig
                    {
                        VoiceName = _voiceName
                    }
                }
            };
        }

        _model = new GenerativeModel(_apiKey, _modelName);

        _vad = new WebRtcVad
        {
            OperatingMode = OperatingMode.VeryAggressive,
            FrameLength = FrameLength.Is30ms,
            SampleRate = SampleRate.Is16kHz
        };
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(GeminiLiveRunner));

        Exception? lastException = null;

        for (var attempt = 1; attempt <= MaxConnectAttempts; attempt++)
        {
            try
            {
                await ConnectCoreAsync(cancellationToken);
                return;
            }
            catch (Exception ex) when (attempt < MaxConnectAttempts && ShouldRetryConnect(ex, cancellationToken))
            {
                lastException = ex;
                ResetConnectionAfterFailedSetup();
            }
            catch
            {
                ResetConnectionAfterFailedSetup();
                throw;
            }
        }

        ResetConnectionAfterFailedSetup();
        throw lastException ?? new TimeoutException($"Setup did not complete within {SetupTimeout.TotalSeconds:0} seconds");
    }

    public async Task DisconnectAsync()
    {
        if (_liveClient != null && _isConnected)
        {
            try { await _liveClient.DisconnectAsync(); }
            catch { }
        }
    }

    public async Task SendAudioAsync(byte[] audioData, CancellationToken cancellationToken = default)
    {
        if (_isDisposed) return;

        bool hasSpeech = DetectSpeech(audioData);

        if (hasSpeech)
        {
            _consecutiveSpeechFrames++;

            // Track speech start time
            if (_consecutiveSpeechFrames == 3 && !_isUserSpeaking)
            {
                _userSpeechStartTime = DateTime.UtcNow;
            }
        }
        else
        {
            _consecutiveSpeechFrames = 0;
        }

        if (_needsReconnect && _consecutiveSpeechFrames >= 8 && AutoReconnectEnabled && !_isReconnecting && DateTime.UtcNow > _vadCooldownUntil)
        {
            _needsReconnect = false;
            _isReconnecting = true;
            ReconnectionStarted?.Invoke(this, EventArgs.Empty);

            _ = Task.Run(async () =>
            {
                try
                {
                    await ConnectAsync(cancellationToken);
                    ReconnectionCompleted?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke(this, ex);
                    _needsReconnect = true;
                }
                finally
                {
                    _isReconnecting = false;
                }
            }, cancellationToken);
        }

        if (_isBufferingAudio || !_isConnected)
        {
            BufferAudio(audioData);
            return;
        }

        try
        {
            if (_liveClient != null && _isConnected && _setupCompleteTcs?.Task.IsCompletedSuccessfully == true)
            {
                await SendAudioFrameAsync(audioData, cancellationToken);
            }
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, ex);
        }
    }

    public async Task SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_isDisposed || string.IsNullOrWhiteSpace(text))
            return;

        if (!await EnsureConnectedForTextTurnAsync(cancellationToken))
            return;

        try
        {
            var liveClient = _liveClient;
            if (liveClient?.Client == null)
                return;

            var payload = JsonSerializer.Serialize(new
            {
                realtimeInput = new
                {
                    text
                }
            });

            cancellationToken.ThrowIfCancellationRequested();
            await liveClient.Client.SendInstant(payload);
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, ex);
        }
    }

    public void ClearHistory()
    {
        _conversationHistory.Clear();
        _pendingUserMessage = "";
        _pendingAiMessage.Clear();
    }

    private void WireUpEventHandlers()
    {
        if (_liveClient == null) return;

        _liveClient.Connected += (s, e) =>
        {
            _isConnected = true;
            Connected?.Invoke(this, EventArgs.Empty);
        };

        _liveClient.Disconnected += (s, e) =>
        {
            _isConnected = false;

            if (_setupCompleteTcs?.Task.IsCompleted != true)
            {
                TryFailPendingSetup(new InvalidOperationException("Gemini disconnected before setup completed."));
            }

            SavePendingMessages();
            _isBufferingAudio = true;
            _needsReconnect = true;
            Disconnected?.Invoke(this, EventArgs.Empty);
        };

        _liveClient.ErrorOccurred += (s, e) =>
        {
            var ex = e.GetException();
            if (ex is not ObjectDisposedException && ex is not InvalidOperationException)
            {
                if (_setupCompleteTcs?.Task.IsCompleted != true)
                {
                    TryFailPendingSetup(ex ?? new Exception("Unknown Gemini setup error."));
                }

                ErrorOccurred?.Invoke(this, ex ?? new Exception("Unknown error"));
            }
        };

        _liveClient.AudioChunkReceived += (s, e) =>
        {
            if (e.Buffer != null && e.Buffer.Length > 0)
            {
                AudioReceived?.Invoke(this, e.Buffer);
            }
        };

        _liveClient.InputTranscriptionReceived += (s, e) =>
        {
            if (!string.IsNullOrEmpty(e.Text))
            {
                if (!_isUserSpeaking)
                {
                    _isUserSpeaking = true;
                    _pendingUserMessage = "";
                }
                _pendingUserMessage += e.Text;
                UserTranscriptReceived?.Invoke(this, e.Text);
            }
        };

        _liveClient.OutputTranscriptionReceived += (s, e) =>
        {
            if (!string.IsNullOrEmpty(e.Text))
            {
                if (!_isAiSpeaking)
                {
                    _isAiSpeaking = true;
                    if (_isUserSpeaking && !string.IsNullOrWhiteSpace(_pendingUserMessage))
                    {
                        _conversationHistory.Add(("user", _pendingUserMessage));
                        _pendingUserMessage = "";
                        _userSpeechEndTime = DateTime.UtcNow;
                    }
                    _isUserSpeaking = false;
                    _pendingAiMessage.Clear();
                }
                _receivedOutputTranscriptionThisTurn = true;
                _pendingAiMessage.Append(e.Text);
                AiTranscriptReceived?.Invoke(this, e.Text);
            }
        };

        _liveClient.AudioReceiveCompleted += (s, e) =>
        {
            CompleteAiTurn();
        };

        _liveClient.TextChunkReceived += (s, e) =>
        {
            if (!string.IsNullOrEmpty(e.Text))
            {
                if (_receivedOutputTranscriptionThisTurn)
                {
                    ThinkingReceived?.Invoke(this, e.Text);
                    return;
                }

                if (!_isAiSpeaking)
                {
                    _isAiSpeaking = true;
                    if (_isUserSpeaking && !string.IsNullOrWhiteSpace(_pendingUserMessage))
                    {
                        _conversationHistory.Add(("user", _pendingUserMessage));
                        _pendingUserMessage = "";
                        _userSpeechEndTime = DateTime.UtcNow;
                    }
                    _isUserSpeaking = false;
                    _pendingAiMessage.Clear();
                }

                _pendingAiMessage.Append(e.Text);
                AiTranscriptReceived?.Invoke(this, e.Text);

                if (e.IsTurnFinish)
                {
                    CompleteAiTurn();
                }
            }
        };

        _liveClient.MessageReceived += (s, e) =>
        {
            if (e.Payload?.SetupComplete != null)
            {
                _setupCompleteTcs?.TrySetResult(true);
            }

            // Handle function calls
            var functionCalls = e.Payload?.ToolCall?.FunctionCalls;
            if (functionCalls != null && functionCalls.Any())
            {
                _ = HandleToolCallsAsync(functionCalls.ToList());
            }

            if (e.Payload?.ServerContent?.Interrupted == true)
            {
                _isAiSpeaking = false;
                _receivedOutputTranscriptionThisTurn = false;
                Interrupted?.Invoke(this, EventArgs.Empty);
            }
        };

        _liveClient.SessionResumableUpdateReceived += (s, update) =>
        {
            if (update != null && !string.IsNullOrEmpty(update.ResumptionToken))
            {
                _sessionResumptionHandle = update.ResumptionToken;
            }
        };

        _liveClient.GoAwayReceived += (s, goAway) =>
        {
            if (_setupCompleteTcs?.Task.IsCompleted != true)
            {
                TryFailPendingSetup(new InvalidOperationException("Gemini server requested disconnect before setup completed."));
            }
        };
    }

    private async Task HandleToolCallsAsync(List<FunctionCall> functionCalls)
    {
        if (_liveClient == null) return;

        var toolResponses = new List<FunctionResponse>();

        foreach (var call in functionCalls)
        {
            var toolInfo = new ToolCallInfo
            {
                Name = call.Name ?? "unknown",
                Arguments = call.Args?.ToJsonString() ?? "{}",
                StartTime = DateTime.UtcNow
            };

            ToolCallStarted?.Invoke(this, toolInfo);

            try
            {
                var result = await ExecuteToolAsync(call.Name!, call.Args);
                toolInfo.Result = result;
                toolInfo.EndTime = DateTime.UtcNow;

                toolResponses.Add(new FunctionResponse
                {
                    Name = call.Name ?? "unknown",
                    Id = call.Id ?? "",
                    Response = JsonNode.Parse($"{{\"result\": {JsonSerializer.Serialize(result)}}}")
                });
            }
            catch (Exception ex)
            {
                toolInfo.Result = $"Error: {ex.Message}";
                toolInfo.EndTime = DateTime.UtcNow;

                toolResponses.Add(new FunctionResponse
                {
                    Name = call.Name ?? "unknown",
                    Id = call.Id ?? "",
                    Response = JsonNode.Parse($"{{\"error\": {JsonSerializer.Serialize(ex.Message)}}}")
                });
            }

            ToolCallCompleted?.Invoke(this, toolInfo);
        }

        try
        {
            var response = new BidiGenerateContentToolResponse
            {
                FunctionResponses = toolResponses.ToArray()
            };
            await _liveClient.SendToolResponseAsync(response, CancellationToken.None);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, ex);
        }
    }

    private async Task<string> ExecuteToolAsync(string toolName, JsonNode? args)
    {
        var function = _aiFunctions.FirstOrDefault(f => f.Name == toolName);
        if (function == null)
        {
            return $"Unknown tool: {toolName}";
        }

        try
        {
            var argsDict = args != null
                ? JsonSerializer.Deserialize<Dictionary<string, object?>>(args.ToJsonString())
                : new Dictionary<string, object?>();

            var aiArgs = new AIFunctionArguments(argsDict!);
            var result = await function.InvokeAsync(aiArgs);
            return result?.ToString() ?? "null";
        }
        catch (Exception ex)
        {
            return $"Tool execution failed: {ex.Message}";
        }
    }

    private void SavePendingMessages()
    {
        if (_isUserSpeaking && !string.IsNullOrWhiteSpace(_pendingUserMessage))
        {
            _conversationHistory.Add(("user", _pendingUserMessage));
            _pendingUserMessage = "";
        }
        if (_isAiSpeaking && _pendingAiMessage.Length > 0)
        {
            _conversationHistory.Add(("model", _pendingAiMessage.ToString()));
            _pendingAiMessage.Clear();
        }

        _receivedOutputTranscriptionThisTurn = false;
    }

    private async Task<bool> EnsureConnectedForTextTurnAsync(CancellationToken cancellationToken)
    {
        if (_liveClient != null && _isConnected && _setupCompleteTcs?.Task.IsCompletedSuccessfully == true)
        {
            return true;
        }

        if (_isReconnecting)
        {
            while (_isReconnecting && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(50, cancellationToken);
            }

            return _liveClient != null && _isConnected && _setupCompleteTcs?.Task.IsCompletedSuccessfully == true;
        }

        if (!AutoReconnectEnabled)
        {
            return false;
        }

        _needsReconnect = false;
        _isReconnecting = true;
        ReconnectionStarted?.Invoke(this, EventArgs.Empty);

        try
        {
            await ConnectAsync(cancellationToken);
            ReconnectionCompleted?.Invoke(this, EventArgs.Empty);
            return _liveClient != null && _isConnected && _setupCompleteTcs?.Task.IsCompletedSuccessfully == true;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, ex);
            _needsReconnect = true;
            return false;
        }
        finally
        {
            _isReconnecting = false;
        }
    }

    private void CompleteAiTurn()
    {
        _isAiSpeaking = false;

        var aiMsg = _pendingAiMessage.ToString();
        if (!string.IsNullOrWhiteSpace(aiMsg))
        {
            _conversationHistory.Add(("model", aiMsg));
            _pendingAiMessage.Clear();
        }

        _receivedOutputTranscriptionThisTurn = false;
        TurnCompleted?.Invoke(this, EventArgs.Empty);
    }

    private async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        _liveClient = _model.CreateMultiModalLiveClient(_config);
        WireUpEventHandlers();

        _setupCompleteTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await _liveClient.ConnectAsync(false, cancellationToken);

        var systemInstruction = BuildSystemPromptWithHistory();

        var setup = new BidiGenerateContentSetup
        {
            Model = _modelName,
            GenerationConfig = _config,
            SystemInstruction = new Content
            {
                Parts = [new Part { Text = systemInstruction }]
            },
            Tools = _tools.ToArray(),
            SessionResumption = new SessionResumptionConfig
            {
                Handle = _sessionResumptionHandle
            },
            ContextWindowCompression = new ContextWindowCompressionConfig
            {
                SlidingWindow = new SlidingWindow()
            },
            InputAudioTranscription = new AudioTranscriptionConfig(),
            OutputAudioTranscription = new AudioTranscriptionConfig()
        };

        await _liveClient.SendSetupAsync(setup, cancellationToken);

        var completedTask = await Task.WhenAny(
            _setupCompleteTcs.Task,
            Task.Delay(SetupTimeout, cancellationToken));

        if (completedTask != _setupCompleteTcs.Task)
        {
            throw new TimeoutException($"Setup did not complete within {SetupTimeout.TotalSeconds:0} seconds");
        }

        await _setupCompleteTcs.Task;

        await Task.Delay(100, cancellationToken);

        _isBufferingAudio = false;
        _needsReconnect = false;
        await FlushAudioBufferAsync(cancellationToken);
    }

    private static bool ShouldRetryConnect(Exception ex, CancellationToken cancellationToken)
    {
        return !cancellationToken.IsCancellationRequested && ex is not ObjectDisposedException && ex is not OperationCanceledException;
    }

    private void ResetConnectionAfterFailedSetup()
    {
        _isConnected = false;
        _isBufferingAudio = false;
        _needsReconnect = false;
        _setupCompleteTcs = null;

        if (_liveClient != null)
        {
            try
            {
                _liveClient.Dispose();
            }
            catch
            {
            }

            _liveClient = null;
        }
    }

    private void TryFailPendingSetup(Exception ex)
    {
        _setupCompleteTcs?.TrySetException(ex);
    }

    private string BuildSystemPromptWithHistory()
    {
        if (_conversationHistory.Count == 0)
            return _systemPrompt;

        var sb = new System.Text.StringBuilder(_systemPrompt);
        sb.AppendLine("\n\n--- CONVERSATION HISTORY (for context) ---\n");

        foreach (var (role, text) in _conversationHistory)
        {
            var roleName = role == "user" ? "User" : "Assistant";
            sb.AppendLine($"{roleName}: {text}");
        }

        sb.AppendLine("\n--- END HISTORY ---\n");
        return sb.ToString();
    }

    private bool DetectSpeech(byte[] audioData)
    {
        if (audioData.Length < VadFrameBytes) return false;
        try { return _vad.HasSpeech(audioData[..VadFrameBytes]); }
        catch { return false; }
    }

    private void BufferAudio(byte[] audioData)
    {
        while (_audioBuffer.Count >= MaxBufferedChunks)
            _audioBuffer.TryDequeue(out _);

        var copy = new byte[audioData.Length];
        Array.Copy(audioData, copy, audioData.Length);
        _audioBuffer.Enqueue(copy);
    }

    private async Task FlushAudioBufferAsync(CancellationToken cancellationToken)
    {
        while (_audioBuffer.TryDequeue(out var chunk))
        {
            try
            {
                if (_liveClient != null && _isConnected)
                    await SendAudioFrameAsync(chunk, cancellationToken);
            }
            catch { }
        }
    }

    private async Task SendAudioFrameAsync(byte[] audioData, CancellationToken cancellationToken)
    {
        if (_liveClient?.Client == null)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            realtimeInput = new
            {
                audio = new
                {
                    data = Convert.ToBase64String(audioData),
                    mimeType = "audio/pcm"
                }
            }
        });

        cancellationToken.ThrowIfCancellationRequested();
        await _liveClient.Client.SendInstant(payload);
    }

    private static List<Tool> ConvertToGeminiTools(IList<AIFunction> aiFunctions)
    {
        var tools = new List<Tool>();

        foreach (var aiFunction in aiFunctions)
        {
            var schema = aiFunction.JsonSchema;
            var geminiSchema = ConvertJsonSchemaToGeminiSchema(schema);

            tools.Add(new Tool
            {
                FunctionDeclarations =
                [
                    new FunctionDeclaration
                    {
                        Name = aiFunction.Name,
                        Description = aiFunction.Description,
                        Parameters = geminiSchema
                    }
                ]
            });
        }

        return tools;
    }

    private static Schema ConvertJsonSchemaToGeminiSchema(JsonElement schema)
    {
        var geminiSchema = new Schema
        {
            Type = "OBJECT",
            Properties = new Dictionary<string, Schema>()
        };

        if (schema.TryGetProperty("properties", out var properties))
        {
            foreach (var prop in properties.EnumerateObject())
            {
                var propSchema = new Schema();

                if (prop.Value.TryGetProperty("type", out var typeEl))
                    propSchema.Type = typeEl.GetString()?.ToUpperInvariant() ?? "STRING";

                if (prop.Value.TryGetProperty("description", out var descEl))
                    propSchema.Description = descEl.GetString();

                geminiSchema.Properties[prop.Name] = propSchema;
            }
        }

        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            geminiSchema.Required = required.EnumerateArray()
                .Select(r => r.GetString()!)
                .ToList();
        }

        return geminiSchema;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try { _liveClient?.Dispose(); } catch { }
        _vad.Dispose();

        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Information about a tool call for timing analysis.
/// </summary>
public class ToolCallInfo
{
    public string Name { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string? Result { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public TimeSpan Duration => EndTime - StartTime;
}
