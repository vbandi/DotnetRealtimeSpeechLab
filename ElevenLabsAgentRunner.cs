using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace DotnetRealtimeSpeechLab;

/// <summary>
/// ElevenLabs conversational agent runner using the realtime WebSocket API.
/// </summary>
public sealed class ElevenLabsAgentRunner : IVoiceRunner
{
    private readonly string _apiKey;
    private readonly string _agentId;
    private readonly IList<AIFunction> _aiFunctions;

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private bool _isDisposed;
    private int _conversationTurns;
    private readonly bool _debugEvents;

    public string ProviderName => "ElevenLabs Agents";
    public int InputSampleRate => 16000;
    public int OutputSampleRate => 16000;
    public bool IsConnected => _socket?.State == WebSocketState.Open;
    public int ConversationHistoryCount => _conversationTurns;

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

    public ElevenLabsAgentRunner(string apiKey, string agentId, IList<AIFunction> aiFunctions)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("API key is required", nameof(apiKey));
        if (string.IsNullOrWhiteSpace(agentId)) throw new ArgumentException("Agent ID is required", nameof(agentId));
        _aiFunctions = aiFunctions ?? throw new ArgumentNullException(nameof(aiFunctions));

        _apiKey = apiKey;
        _agentId = agentId;
        _debugEvents = string.Equals(
            Environment.GetEnvironmentVariable("FASTVOICE_DEBUG_EVENTS"),
            "true",
            StringComparison.OrdinalIgnoreCase);
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(ElevenLabsAgentRunner));

        await DisconnectAsync();

        using var apiClient = new ElevenLabsAgentApiClient(_apiKey);
        var signedUrl = await apiClient.GetSignedConversationUrlAsync(_agentId, cancellationToken);

        _socket = new ClientWebSocket();
        await _socket.ConnectAsync(new Uri(signedUrl), cancellationToken);

        _receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token), _receiveCts.Token);

        await SendJsonAsync(new JsonObject
        {
            ["type"] = "conversation_initiation_client_data"
        }, cancellationToken);

        Connected?.Invoke(this, EventArgs.Empty);
    }

    public async Task DisconnectAsync()
    {
        _receiveCts?.Cancel();

        if (_socket != null)
        {
            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "disconnect", CancellationToken.None);
                }
            }
            catch
            {
            }

            _socket.Dispose();
            _socket = null;
        }

        if (_receiveTask != null)
        {
            try
            {
                await _receiveTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _receiveCts?.Dispose();
        _receiveCts = null;
        _receiveTask = null;
    }

    public async Task SendAudioAsync(byte[] audioData, CancellationToken cancellationToken = default)
    {
        if (_isDisposed || _socket?.State != WebSocketState.Open || audioData.Length == 0)
            return;

        try
        {
            var payload = new JsonObject
            {
                ["user_audio_chunk"] = Convert.ToBase64String(audioData)
            };

            await SendJsonAsync(payload, cancellationToken);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, ex);
        }
    }

    public Task SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        return Task.FromException(new NotSupportedException("ElevenLabs agent experiment does not support injected text turns."));
    }

    public void ClearHistory()
    {
        _conversationTurns = 0;
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        if (_socket == null)
            return;

        var buffer = new byte[8192];
        using var messageBuffer = new MemoryStream();

        try
        {
            while (_socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var segment = new ArraySegment<byte>(buffer);
                var result = await _socket.ReceiveAsync(segment, cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                messageBuffer.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                {
                    continue;
                }

                var messageText = Encoding.UTF8.GetString(messageBuffer.ToArray());
                messageBuffer.SetLength(0);

                await HandleServerMessageAsync(messageText, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, ex);
        }
        finally
        {
            if (_socket != null)
            {
                var wasConnected = _socket.State == WebSocketState.Open || _socket.State == WebSocketState.CloseSent;
                _socket.Dispose();
                _socket = null;
                if (wasConnected)
                {
                    Disconnected?.Invoke(this, EventArgs.Empty);
                }
            }
        }
    }

    private async Task HandleServerMessageAsync(string messageText, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(messageText))
            return;

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(messageText)?.AsObject();
        }
        catch
        {
            return;
        }

        if (root == null)
            return;

        var eventType = root["type"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(eventType))
            return;

        if (_debugEvents && ShouldLogDebugEvent(eventType))
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"[ELEVENLABS EVENT] {eventType}: {TrimForLog(messageText, 320)}");
            Console.ResetColor();
        }

        switch (eventType)
        {
            case "ping":
                await HandlePingAsync(root, cancellationToken);
                break;

            case "user_transcript":
                var userTranscript =
                    root["user_transcription_event"]?["user_transcript"]?.GetValue<string>()
                    ?? root["user_transcript_event"]?["user_transcript"]?.GetValue<string>()
                    ?? root["user_transcript"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(userTranscript))
                {
                    UserTranscriptReceived?.Invoke(this, userTranscript);
                }
                break;

            case "agent_response":
                var agentResponse =
                    root["agent_response_event"]?["agent_response"]?.GetValue<string>()
                    ?? root["agent_response"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(agentResponse))
                {
                    AiTranscriptReceived?.Invoke(this, agentResponse);
                    _conversationTurns++;
                    TurnCompleted?.Invoke(this, EventArgs.Empty);
                }
                break;

            case "agent_response_correction":
                var correctedResponse =
                    root["agent_response_correction_event"]?["corrected_agent_response"]?.GetValue<string>()
                    ?? root["agent_response_correction"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(correctedResponse))
                {
                    AiTranscriptReceived?.Invoke(this, correctedResponse);
                }
                break;

            case "audio":
                var audioBase64 = root["audio_event"]?["audio_base_64"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(audioBase64))
                {
                    var audioBytes = Convert.FromBase64String(audioBase64);
                    AudioReceived?.Invoke(this, audioBytes);
                }
                break;

            case "interruption":
                Interrupted?.Invoke(this, EventArgs.Empty);
                break;

            case "client_tool_call":
                await HandleClientToolCallAsync(root, cancellationToken);
                break;

            case "agent_tool_response":
                HandleAgentToolResponse(root);
                break;

            case "transcript":
                var role = root["transcript_event"]?["role"]?.GetValue<string>()
                    ?? root["role"]?.GetValue<string>();
                var text = root["transcript_event"]?["text"]?.GetValue<string>()
                    ?? root["text"]?.GetValue<string>();

                if (!string.IsNullOrWhiteSpace(text))
                {
                    if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
                    {
                        UserTranscriptReceived?.Invoke(this, text);
                    }
                    else
                    {
                        AiTranscriptReceived?.Invoke(this, text);
                    }
                }
                break;
        }
    }

    private async Task HandleClientToolCallAsync(JsonObject root, CancellationToken cancellationToken)
    {
        var toolCall = root["client_tool_call"]?.AsObject();
        if (toolCall == null)
            return;

        var toolName = toolCall["tool_name"]?.GetValue<string>();
        var toolCallId = toolCall["tool_call_id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(toolName) || string.IsNullOrWhiteSpace(toolCallId))
            return;

        var parametersNode = toolCall["parameters"];
        var argumentsJson = parametersNode?.ToJsonString() ?? "{}";

        var info = new ToolCallInfo
        {
            Name = toolName,
            Arguments = argumentsJson,
            StartTime = DateTime.UtcNow
        };
        ToolCallStarted?.Invoke(this, info);

        try
        {
            var result = await ExecuteToolAsync(toolName, parametersNode);
            info.Result = result;
            info.EndTime = DateTime.UtcNow;
            ToolCallCompleted?.Invoke(this, info);

            var payload = BuildClientToolResultPayload(toolCallId, result, isError: false);
            if (_debugEvents)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"[ELEVENLABS SEND] {TrimForLog(payload.ToJsonString(), 320)}");
                Console.ResetColor();
            }

            await SendJsonAsync(payload, cancellationToken);
        }
        catch (Exception ex)
        {
            info.Result = $"Error: {ex.Message}";
            info.EndTime = DateTime.UtcNow;
            ToolCallCompleted?.Invoke(this, info);

            var payload = BuildClientToolResultPayload(toolCallId, ex.Message, isError: true);
            if (_debugEvents)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"[ELEVENLABS SEND] {TrimForLog(payload.ToJsonString(), 320)}");
                Console.ResetColor();
            }

            await SendJsonAsync(payload, cancellationToken);
        }
    }

    private static JsonObject BuildClientToolResultPayload(string toolCallId, string result, bool isError)
    {
        // Canonical client_tool_result message shape from ElevenLabs examples.
        // Send result as plain string for maximal compatibility.
        return new JsonObject
        {
            ["type"] = "client_tool_result",
            ["tool_call_id"] = toolCallId,
            ["result"] = result,
            ["is_error"] = isError
        };
    }

    private void HandleAgentToolResponse(JsonObject root)
    {
        var response = root["agent_tool_response"]?.AsObject();
        if (response == null)
            return;

        var toolName = response["tool_name"]?.GetValue<string>() ?? "unknown";
        var isError = response["is_error"]?.GetValue<bool?>() ?? false;

        if (isError)
        {
            ErrorOccurred?.Invoke(this, new Exception($"Agent tool response error for '{toolName}'"));
            return;
        }

        if (_debugEvents)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"[ELEVENLABS ACK] agent_tool_response for {toolName}");
            Console.ResetColor();
        }
    }

    private async Task<string> ExecuteToolAsync(string toolName, JsonNode? parametersNode)
    {
        var function = _aiFunctions.FirstOrDefault(f =>
            string.Equals(f.Name, toolName, StringComparison.OrdinalIgnoreCase));

        if (function == null)
            throw new InvalidOperationException($"Unknown tool: {toolName}");

        Dictionary<string, object?> argsDict;

        if (parametersNode == null)
        {
            argsDict = new Dictionary<string, object?>();
        }
        else if (parametersNode is JsonValue jv
            && jv.TryGetValue<string>(out var paramsString)
            && !string.IsNullOrWhiteSpace(paramsString))
        {
            argsDict = JsonSerializer.Deserialize<Dictionary<string, object?>>(paramsString)
                ?? new Dictionary<string, object?>();
        }
        else
        {
            argsDict = JsonSerializer.Deserialize<Dictionary<string, object?>>(parametersNode.ToJsonString())
                ?? new Dictionary<string, object?>();
        }

        var result = await function.InvokeAsync(new AIFunctionArguments(argsDict));
        return result?.ToString() ?? "null";
    }

    private async Task HandlePingAsync(JsonObject root, CancellationToken cancellationToken)
    {
        var pingEvent = root["ping_event"]?.AsObject();
        var eventId = pingEvent?["event_id"]?.GetValue<int>() ?? 0;
        var pingMs = pingEvent?["ping_ms"]?.GetValue<int?>();
        var delayMs = pingMs ?? 0;

        if (delayMs > 0)
        {
            await Task.Delay(delayMs, cancellationToken);
        }

        var pong = new JsonObject
        {
            ["type"] = "pong",
            ["event_id"] = eventId
        };

        await SendJsonAsync(pong, cancellationToken);
    }

    private async Task SendJsonAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        if (_socket?.State != WebSocketState.Open)
            return;

        var json = payload.ToJsonString();
        var bytes = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (_socket?.State == WebSocketState.Open)
            {
                await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;

        try
        {
            DisconnectAsync().GetAwaiter().GetResult();
        }
        catch
        {
        }

        _sendLock.Dispose();

        GC.SuppressFinalize(this);
    }

    private static string TrimForLog(string value, int maxLength)
    {
        if (value.Length <= maxLength)
            return value;

        return value[..maxLength] + "...";
    }

    private static bool ShouldLogDebugEvent(string eventType)
    {
        return !eventType.Equals("audio", StringComparison.OrdinalIgnoreCase)
            && !eventType.Equals("ping", StringComparison.OrdinalIgnoreCase)
            && !eventType.Equals("user_transcript", StringComparison.OrdinalIgnoreCase)
            && !eventType.Equals("agent_response", StringComparison.OrdinalIgnoreCase)
            && !eventType.Equals("agent_response_correction", StringComparison.OrdinalIgnoreCase)
            && !eventType.Equals("transcript", StringComparison.OrdinalIgnoreCase);
    }
}
