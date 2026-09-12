using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace DotnetRealtimeSpeechLab;

public interface IGptLiveControls
{
    bool IsMicrophoneMuted { get; }
    Task SetMicrophoneMutedAsync(bool muted, CancellationToken cancellationToken = default);
    Task CancelBackendAsync();
    double? LatestVoiceUsageSeconds { get; }
    string? SessionId { get; }
    string? CloseReason { get; }
}

public sealed class GptLiveRunner : IVoiceRunner, IGptLiveControls
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };
    private const string LiveWebSocketUrl = "wss://api.openai.com/v1/live/sessions";
    private const string DefaultVoice = "marin";
    private const string DefaultBackendModel = "gpt-5.6-luna";
    private const string DefaultBackendReasoning = "low";
    private const int MaxTranscriptFragments = 512;
    private static readonly TimeSpan TranscriptTurnQuietPeriod = TimeSpan.FromMilliseconds(1800);

    private readonly string _apiKey;
    private readonly string _systemPrompt;
    private readonly IList<AIFunction> _aiFunctions;
    private readonly string _voiceName;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _stateLock = new();
    private readonly List<GptLiveTranscriptFragment> _transcript = new();
    private readonly GptLiveUsageTracker _usage = new();
    private readonly GptLiveDelegationState _delegationState = new();
    private readonly Dictionary<string, CancellationTokenSource> _delegations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ResponsesDelegationWork> _responsesDelegations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _sentEventTimes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _handledResponseToolCalls = new(StringComparer.Ordinal);
    private readonly HttpClient _backendHttpClient = new();
    private readonly CancellationTokenSource _backendLifetimeCts = new();

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private TaskCompletionSource<bool>? _startedTcs;
    private TaskCompletionSource<bool>? _closedTcs;
    private bool _isDisposed;
    private bool _started;
    private bool _closeRequested;
    private bool _microphoneMuted;
    private string? _sessionId;
    private string? _closeReason;
    private string? _activeOutputSpeaker;
    private int _turnGeneration;
    private readonly GptLiveDelegationMode _delegationMode;

    private sealed class ResponsesDelegationWork
    {
        public required CancellationTokenSource Cancellation { get; init; }
        public string? ResponseId { get; set; }
        public List<JsonObject> FunctionCalls { get; } = new();
        public bool CompletionSeen { get; set; }
        public bool CompletionStarted { get; set; }
    }

    public string ProviderName => "GPT-Live 1";
    public int InputSampleRate => 24000;
    public int OutputSampleRate => 24000;
    public bool IsConnected => _started && _socket?.State == WebSocketState.Open;
    public int ConversationHistoryCount => BuildResumeHistory().Count;
    public bool IsMicrophoneMuted => _microphoneMuted;
    public double? LatestVoiceUsageSeconds => _usage.LatestSeconds;
    public string? SessionId => _sessionId;
    public string? CloseReason => _closeReason;
    public GptLiveDelegationMode DelegationMode => _delegationMode;
    public long InputAudioBytes { get; private set; }
    public long OutputAudioBytes { get; private set; }
    public TimeSpan InputAudioDuration => TimeSpan.FromSeconds(InputAudioBytes / 2d / InputSampleRate);
    public TimeSpan OutputAudioDuration => TimeSpan.FromSeconds(OutputAudioBytes / 2d / OutputSampleRate);
    public GptLiveUsageTracker Usage => _usage;

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
    public event EventHandler<GptLiveEventInfo>? EventObserved;
    public event EventHandler<GptLiveDelegationRequestInfo>? DelegationRequestPrepared;

    public GptLiveRunner(
        string apiKey,
        string systemPrompt,
        IList<AIFunction> aiFunctions,
        string? voiceName = null,
        GptLiveDelegationMode? delegationMode = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("OpenAI API key is required", nameof(apiKey));
        }

        _apiKey = apiKey;
        _systemPrompt = systemPrompt;
        _aiFunctions = aiFunctions ?? throw new ArgumentNullException(nameof(aiFunctions));
        _delegationMode = delegationMode ?? ReadDelegationMode();
        _voiceName = VoiceCatalog.NormalizeVoiceId(
                VoiceProvider.GptLiveClient,
                voiceName ?? Environment.GetEnvironmentVariable("GPT_LIVE_VOICE"))
            ?? DefaultVoice;
        _backendHttpClient.Timeout = TimeSpan.FromSeconds(45);
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(GptLiveRunner));
        }

        await DisconnectAsync().ConfigureAwait(false);

        _socket = new ClientWebSocket
        {
            Options = { KeepAliveInterval = TimeSpan.FromSeconds(20) }
        };
        _socket.Options.SetRequestHeader("Authorization", $"Bearer {_apiKey}");
        _receiveCts = new CancellationTokenSource();
        _startedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _closedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _started = false;
        _closeRequested = false;
        _closeReason = null;

        await _socket.ConnectAsync(new Uri(LiveWebSocketUrl), cancellationToken).ConfigureAwait(false);
        _receiveTask = ReceiveLoopAsync(_receiveCts.Token);

        var sessionStart = GptLiveProtocol.BuildSessionStart(
                "gpt-live-1",
                _systemPrompt,
                _voiceName,
                BuildResumeHistory(),
                _delegationMode,
                Environment.GetEnvironmentVariable("GPT_LIVE_RESPONSES_MODEL") ?? DefaultBackendModel,
                BuildResponsesInstructions(),
                BuildResponseTools(),
                Environment.GetEnvironmentVariable("GPT_LIVE_RESPONSES_REASONING") ?? "low");

        if (_delegationMode == GptLiveDelegationMode.Responses
            && JsonNode.Parse(sessionStart) is JsonObject sessionStartJson
            && sessionStartJson["session"]?["delegation"] is JsonNode delegation)
        {
            DelegationRequestPrepared?.Invoke(this, new GptLiveDelegationRequestInfo(
                _delegationMode,
                "session.start delegation configuration",
                delegation.ToJsonString(PrettyJson)));
        }

        await SendJsonAsync(sessionStart, cancellationToken).ConfigureAwait(false);

        var completed = await Task.WhenAny(
            _startedTcs.Task,
            Task.Delay(TimeSpan.FromSeconds(20), cancellationToken)).ConfigureAwait(false);
        if (completed != _startedTcs.Task)
        {
            throw new TimeoutException("GPT-Live did not emit session.started within 20 seconds.");
        }

        await _startedTcs.Task.ConfigureAwait(false);
    }

    public async Task DisconnectAsync()
    {
        var socket = _socket;
        if (socket == null)
        {
            return;
        }

        if (IsConnected && !_closeRequested)
        {
            _closeRequested = true;
            try
            {
                await SendJsonAsync("{\"type\":\"session.close\",\"event_id\":\"fastvoice_close\"}", CancellationToken.None)
                    .ConfigureAwait(false);
                if (_closedTcs != null)
                {
                    await Task.WhenAny(_closedTcs.Task, Task.Delay(TimeSpan.FromSeconds(15))).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, ex);
            }
        }

        _receiveCts?.Cancel();
        if (_receiveTask != null)
        {
            try { await _receiveTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        socket.Dispose();
        _socket = null;
        _receiveTask = null;
        _receiveCts?.Dispose();
        _receiveCts = null;
        _started = false;
    }

    public async Task SendAudioAsync(byte[] audioData, CancellationToken cancellationToken = default)
    {
        if (_isDisposed || !IsConnected || _microphoneMuted || audioData.Length == 0)
        {
            return;
        }

        if ((audioData.Length & 1) != 0)
        {
            audioData = audioData[..^1];
        }

        if (audioData.Length == 0)
        {
            return;
        }

        InputAudioBytes += audioData.Length;
        await SendJsonAsync(GptLiveProtocol.BuildAudioAppend(audioData), cancellationToken).ConfigureAwait(false);
    }

    public async Task SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_isDisposed || !IsConnected || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        await SendAppendAsync("session.commentary.append", text, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetMicrophoneMutedAsync(bool muted, CancellationToken cancellationToken = default)
    {
        _microphoneMuted = muted;
        if (IsConnected)
        {
            await SendJsonAsync(GptLiveProtocol.BuildInputMute(muted), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task CancelBackendAsync()
    {
        KeyValuePair<string, CancellationTokenSource>[] active;
        ResponsesDelegationWork[] responseActive;
        lock (_stateLock)
        {
            active = _delegations.ToArray();
            foreach (var delegation in active)
            {
                _delegationState.Cancel(delegation.Key);
                delegation.Value.Cancel();
            }

            responseActive = _responsesDelegations.Values.ToArray();
            foreach (var delegation in responseActive)
            {
                delegation.Cancellation.Cancel();
            }
        }

        foreach (var delegation in active)
        {
            if (IsConnected)
            {
                try
                {
                    await SendAppendAsync(
                        "session.thinking.append",
                        "The sandbox backend task was canceled by the operator. Do not report a result for it.",
                        delegation.Key,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke(this, ex);
                }
            }
        }

        foreach (var delegation in responseActive)
        {
            if (IsConnected)
            {
                try
                {
                    await SendJsonAsync(
                        JsonSerializer.Serialize(new
                        {
                            type = "session.thinking.append",
                            event_id = $"fastvoice_responses_cancel_{Guid.NewGuid():N}",
                            delegation_id = (string?)null,
                            content = "The backend task was canceled by the operator. Do not report a result for it."
                        }),
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke(this, ex);
                }
            }
        }
    }

    public void ClearHistory()
    {
        lock (_stateLock)
        {
            _transcript.Clear();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        if (_socket == null)
        {
            return;
        }

        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();

        try
        {
            while (_socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                {
                    continue;
                }

                var json = Encoding.UTF8.GetString(message.ToArray());
                message.SetLength(0);
                await HandleEventAsync(json, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, ex);
            _startedTcs?.TrySetException(ex);
        }
        finally
        {
            _closedTcs?.TrySetResult(true);
            if (_started)
            {
                _started = false;
                Disconnected?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private async Task HandleEventAsync(string json, CancellationToken cancellationToken)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json)?.AsObject();
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, new InvalidOperationException("GPT-Live returned invalid JSON.", ex));
            return;
        }

        if (root == null)
        {
            return;
        }

        var type = GptLiveProtocol.GetString(root, "type") ?? "unknown";
        var eventRoot = root;
        if (type == "response.event" && root["event"] is JsonObject nestedEvent)
        {
            eventRoot = nestedEvent;
            type = GptLiveProtocol.GetString(nestedEvent, "type") ?? "response.event.unknown";
        }
        var eventSessionId = root["session"]?["id"]?.GetValue<string>();
        if (eventSessionId != null)
        {
            _sessionId = eventSessionId;
        }

        var delegationId = root["delegation"]?["id"]?.GetValue<string>()
            ?? GptLiveProtocol.GetString(root, "delegation_id");
        var clientEventId = GptLiveProtocol.GetString(root, "client_event_id");
        long? acknowledgmentLatencyMs = null;
        if (clientEventId != null)
        {
            lock (_stateLock)
            {
                if (_sentEventTimes.Remove(clientEventId, out var sentAt))
                {
                    acknowledgmentLatencyMs = _clock.ElapsedMilliseconds - sentAt;
                }
            }
        }

        switch (type)
        {
            case "session.started":
                _started = true;
                _startedTcs?.TrySetResult(true);
                Connected?.Invoke(this, EventArgs.Empty);
                break;

            case "session.input_transcript.delta":
                HandleTranscript("user", root, UserTranscriptReceived);
                break;

            case "session.output_transcript.delta":
                HandleTranscript("assistant", root, AiTranscriptReceived);
                ScheduleTurnCompleted();
                break;

            case "session.output_audio.delta":
                var audio = root["delta"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(audio))
                {
                    var audioBytes = Convert.FromBase64String(audio);
                    OutputAudioBytes += audioBytes.Length;
                    AudioReceived?.Invoke(this, audioBytes);
                }
                break;

            case "session.interrupted":
            case "response.interrupted":
                Interrupted?.Invoke(this, EventArgs.Empty);
                break;

            case "session.delegation.created":
                if (delegationId != null && _delegationMode == GptLiveDelegationMode.Client)
                {
                    _ = HandleDelegationAsync(delegationId);
                }
                else if (delegationId != null && _delegationMode == GptLiveDelegationMode.Responses)
                {
                    lock (_stateLock)
                    {
                        if (!_responsesDelegations.ContainsKey(delegationId))
                        {
                            _responsesDelegations[delegationId] = new ResponsesDelegationWork
                            {
                                Cancellation = CancellationTokenSource.CreateLinkedTokenSource(_backendLifetimeCts.Token)
                            };
                        }
                    }
                }
                break;

            case "response.created":
                if (_delegationMode == GptLiveDelegationMode.Responses && delegationId != null)
                {
                    lock (_stateLock)
                    {
                        if (_responsesDelegations.TryGetValue(delegationId, out var work))
                        {
                            work.ResponseId = eventRoot["response"]?["id"]?.GetValue<string>();
                        }
                    }
                }
                break;

            case "response.output_item.done":
                if (_delegationMode == GptLiveDelegationMode.Responses
                    && delegationId != null
                    && eventRoot["item"] is JsonObject item
                    && item["type"]?.GetValue<string>() == "function_call")
                {
                    lock (_stateLock)
                    {
                        if (!_responsesDelegations.TryGetValue(delegationId, out var work))
                        {
                            work = new ResponsesDelegationWork
                            {
                                Cancellation = CancellationTokenSource.CreateLinkedTokenSource(_backendLifetimeCts.Token)
                            };
                            _responsesDelegations[delegationId] = work;
                        }

                        work.FunctionCalls.Add(item.DeepClone().AsObject());
                    }
                }
                break;

            case "response.completed":
                if (_delegationMode == GptLiveDelegationMode.Responses && delegationId != null)
                {
                    ResponsesDelegationWork? work = null;
                    lock (_stateLock)
                    {
                        if (_responsesDelegations.TryGetValue(delegationId, out work))
                        {
                            work.CompletionSeen = true;
                            if (work.CompletionStarted)
                            {
                                work = null;
                            }
                            else
                            {
                                work.CompletionStarted = true;
                            }
                        }
                    }

                    if (work != null)
                    {
                        _ = CompleteResponsesDelegationAsync(delegationId, work);
                    }
                }
                break;

            case "session.usage.updated":
                if (GptLiveProtocol.TryReadUsage(root, out var usage))
                {
                    _usage.Apply(usage);
                }
                break;

            case "session.closed":
                _closeReason = GptLiveProtocol.GetString(root, "reason");
                if (GptLiveProtocol.TryReadUsage(root, out var finalUsage))
                {
                    _usage.Apply(finalUsage);
                }
                _closedTcs?.TrySetResult(true);
                break;

            case "error":
                var error = eventRoot["error"] as JsonObject ?? root["error"] as JsonObject;
                var message = error?["message"]?.GetValue<string>() ?? "Unknown GPT-Live error";
                ErrorOccurred?.Invoke(this, new InvalidOperationException(message));
                if (!_started)
                {
                    _startedTcs?.TrySetException(new InvalidOperationException(message));
                }
                break;
        }

        EventObserved?.Invoke(this, new GptLiveEventInfo(
            type,
            GptLiveProtocol.GetString(root, "event_id"),
            _sessionId,
            delegationId,
            _clock.ElapsedMilliseconds,
            GetEventPayload(eventRoot),
            acknowledgmentLatencyMs));
    }

    private async Task CompleteResponsesDelegationAsync(
        string delegationId,
        ResponsesDelegationWork work)
    {
        JsonObject[] functionCalls;
        lock (_stateLock)
        {
            functionCalls = work.FunctionCalls.ToArray();
        }

        if (functionCalls.Length == 0)
        {
            lock (_stateLock)
            {
                _responsesDelegations.Remove(delegationId);
            }
            work.Cancellation.Dispose();
            return;
        }

        try
        {
            var callsToRun = functionCalls
                .Select(item =>
                {
                    var callId = item["call_id"]?.GetValue<string>();
                    var name = item["name"]?.GetValue<string>();
                    return (item, callId, name);
                })
                .Where(call => !string.IsNullOrWhiteSpace(call.callId) && !string.IsNullOrWhiteSpace(call.name))
                .Where(call =>
                {
                    lock (_stateLock)
                    {
                        return _handledResponseToolCalls.Add(call.callId!);
                    }
                })
                .ToArray();

            var results = await Task.WhenAll(callsToRun.Select(async call =>
            {
                var argumentsJson = call.item["arguments"]?.GetValue<string>() ?? "{}";
                var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson)
                    ?? new Dictionary<string, object?>();
                try
                {
                    return (call.callId!, Output: await InvokeToolAsync(call.name!, arguments, work.Cancellation.Token).ConfigureAwait(false));
                }
                catch (OperationCanceledException)
                {
                    return (call.callId!, Output: "Tool execution canceled by the operator.");
                }
                catch (Exception ex)
                {
                    return (call.callId!, Output: $"Tool failed: {ex.Message}");
                }
            })).ConfigureAwait(false);

            if (!IsConnected || work.Cancellation.IsCancellationRequested)
            {
                return;
            }

            foreach (var (callId, output) in results)
            {
                var toolResult = GptLiveProtocol.BuildResponsesToolResult(callId, output);
                EmitDelegationRequest("response.item.create tool result", toolResult);
                await SendJsonAsync(toolResult, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            var continuation = GptLiveProtocol.BuildResponsesContinue();
            EmitDelegationRequest("response.create continuation", continuation);
            await SendJsonAsync(continuation, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, ex);
        }
        finally
        {
            lock (_stateLock)
            {
                _responsesDelegations.Remove(delegationId);
            }
            work.Cancellation.Dispose();
        }
    }

    private void HandleTranscript(
        string speaker,
        JsonObject root,
        EventHandler<string>? transcriptEvent)
    {
        var delta = root["delta"]?.GetValue<string>();
        if (string.IsNullOrEmpty(delta))
        {
            return;
        }

        var fragment = new GptLiveTranscriptFragment(
            speaker,
            delta,
            TryGetInt64(root, "start_ms"),
            TryGetInt64(root, "end_ms"),
            _clock.ElapsedMilliseconds);
        lock (_stateLock)
        {
            _transcript.Add(fragment);
            if (_transcript.Count > MaxTranscriptFragments)
            {
                _transcript.RemoveRange(0, _transcript.Count - MaxTranscriptFragments);
            }
        }

        if (speaker == "assistant" && _activeOutputSpeaker != "assistant")
        {
            _activeOutputSpeaker = "assistant";
            ThinkingReceived?.Invoke(this, "assistant output transcript started");
        }

        transcriptEvent?.Invoke(this, delta);
    }

    private void ScheduleTurnCompleted()
    {
        var generation = ++_turnGeneration;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TranscriptTurnQuietPeriod).ConfigureAwait(false);
            if (generation == _turnGeneration)
            {
                _activeOutputSpeaker = null;
                TurnCompleted?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    private async Task HandleDelegationAsync(string delegationId)
    {
        var revision = _delegationState.Begin(delegationId);
        using var taskCts = CancellationTokenSource.CreateLinkedTokenSource(_backendLifetimeCts.Token);
        lock (_stateLock)
        {
            _delegations[delegationId] = taskCts;
        }

        try
        {
            await SendAppendAsync(
                "session.thinking.append",
                "I am checking the sandbox task. No consequential action is being taken.",
                delegationId,
                CancellationToken.None).ConfigureAwait(false);

            var result = await RunClientBackendAsync(taskCts.Token).ConfigureAwait(false);

            if (!_delegationState.AcceptResult(delegationId, revision) || !IsConnected)
            {
                ThinkingReceived?.Invoke(this, $"Ignored stale or closed result for delegation {delegationId}.");
                return;
            }

            await SendAppendAsync("session.commentary.append", result, delegationId, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ThinkingReceived?.Invoke(this, $"Backend task canceled for delegation {delegationId}.");
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, ex);
            if (_delegationState.AcceptResult(delegationId, revision) && IsConnected)
            {
                await SendAppendAsync(
                    "session.commentary.append",
                    "The sandbox backend could not complete that task.",
                    delegationId,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_stateLock)
            {
                _delegations.Remove(delegationId);
            }
        }
    }

    private async Task<string> RunClientBackendAsync(CancellationToken cancellationToken)
    {
        List<GptLiveTranscriptFragment> fragments;
        lock (_stateLock)
        {
            fragments = _transcript.ToList();
        }

        var latestUserText = GptLiveProtocol.BuildLatestSpeakerText(fragments, "user");
        if (latestUserText.Contains("slow", StringComparison.OrdinalIgnoreCase))
        {
            var toolInfo = StartTool("SlowSandbox", "{\"milliseconds\":1500}");
            try
            {
                await Task.Delay(GetSlowToolDuration(), cancellationToken).ConfigureAwait(false);
                CompleteTool(toolInfo, "Slow sandbox completed");
            }
            catch
            {
                CompleteTool(toolInfo, "Canceled");
                throw;
            }
        }

        var model = Environment.GetEnvironmentVariable("GPT_LIVE_CLIENT_MODEL") ?? DefaultBackendModel;
        var reasoning = Environment.GetEnvironmentVariable("GPT_LIVE_CLIENT_REASONING") ?? DefaultBackendReasoning;
        var context = GptLiveProtocol.BuildContext(fragments);
        string? previousResponseId = null;
        JsonNode input = JsonValue.Create(context)!;

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var request = new JsonObject
            {
                ["model"] = model,
                ["instructions"] = BuildClientBackendInstructions(),
                ["input"] = input,
                ["tools"] = BuildResponseTools(),
                ["tool_choice"] = "auto",
                ["parallel_tool_calls"] = true,
                ["reasoning"] = new JsonObject
                {
                    ["effort"] = reasoning
                }
            };
            if (previousResponseId != null)
            {
                request["previous_response_id"] = previousResponseId;
            }

            DelegationRequestPrepared?.Invoke(this, new GptLiveDelegationRequestInfo(
                _delegationMode,
                previousResponseId == null
                    ? "client Responses request"
                    : "client Responses tool-follow-up request",
                request.ToJsonString(PrettyJson)));

            using var content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await SendClientBackendRequestAsync(content, cancellationToken).ConfigureAwait(false);
            var responseJson = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))?.AsObject()
                ?? throw new InvalidOperationException("Client backend returned invalid JSON.");
            previousResponseId = responseJson["id"]?.GetValue<string>();

            var toolOutputs = new JsonArray();
            var answer = new StringBuilder();
            if (responseJson["output"] is JsonArray output)
            {
                foreach (var item in output.OfType<JsonObject>())
                {
                    var type = item["type"]?.GetValue<string>();
                    if (type == "function_call")
                    {
                        var name = item["name"]?.GetValue<string>() ?? "unknown";
                        var argumentsJson = item["arguments"]?.GetValue<string>() ?? "{}";
                        var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson)
                            ?? new Dictionary<string, object?>();
                        var result = await InvokeToolAsync(name, arguments, cancellationToken).ConfigureAwait(false);
                        toolOutputs.Add(new JsonObject
                        {
                            ["type"] = "function_call_output",
                            ["call_id"] = item["call_id"]?.GetValue<string>(),
                            ["output"] = result
                        });
                    }
                    else if (type == "message" && item["content"] is JsonArray contentItems)
                    {
                        foreach (var contentItem in contentItems.OfType<JsonObject>())
                        {
                            if (contentItem["type"]?.GetValue<string>() == "output_text")
                            {
                                answer.Append(contentItem["text"]?.GetValue<string>());
                            }
                        }
                    }
                }
            }

            if (toolOutputs.Count == 0)
            {
                return answer.Length == 0
                    ? "The client backend completed without a spoken result."
                    : answer.ToString();
            }

            input = toolOutputs;
        }

        throw new InvalidOperationException("Client backend exceeded the sandbox tool-call limit.");
    }

    private string BuildClientBackendInstructions()
    {
        return "You are the application-owned backend for a live voice assistant. Use only the harmless sandbox tools provided. Voice transcripts may be fragmented or mistaken; use the latest context and verified tool results. Return concise verified facts for speech. Never claim an action succeeded unless a tool result confirms it.";
    }

    private async Task<HttpResponseMessage> SendClientBackendRequestAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses")
        {
            Content = content
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
        var response = await _backendHttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            response.Dispose();
            throw new InvalidOperationException($"Client backend returned {(int)response.StatusCode}: {body[..Math.Min(body.Length, 240)]}");
        }

        return response;
    }

    private JsonArray BuildResponseTools()
    {
        var tools = new JsonArray();
        foreach (var function in _aiFunctions)
        {
            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["name"] = function.Name,
                ["description"] = function.Description,
                ["parameters"] = JsonNode.Parse(function.JsonSchema.GetRawText())
            });
        }

        return tools;
    }

    private string BuildResponsesInstructions()
    {
        return "Use only harmless sandbox tools. Return verified facts, never claim an action succeeded unless the tool result says so. Keep answers short for speech.";
    }

    private void EmitDelegationRequest(string stage, string json)
    {
        if (JsonNode.Parse(json) is JsonNode parsed)
        {
            json = parsed.ToJsonString(PrettyJson);
        }

        DelegationRequestPrepared?.Invoke(this, new GptLiveDelegationRequestInfo(
            _delegationMode,
            stage,
            json));
    }

    private static GptLiveDelegationMode ReadDelegationMode()
    {
        return string.Equals(
            Environment.GetEnvironmentVariable("GPT_LIVE_DELEGATION"),
            "responses",
            StringComparison.OrdinalIgnoreCase)
            ? GptLiveDelegationMode.Responses
            : GptLiveDelegationMode.Client;
    }

    private async Task<string> InvokeToolAsync(
        string name,
        IDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        var function = _aiFunctions.FirstOrDefault(item => item.Name == name);
        var info = StartTool(name, JsonSerializer.Serialize(arguments));
        try
        {
            if (function == null)
            {
                CompleteTool(info, $"Unknown sandbox tool: {name}");
                return $"Unknown sandbox tool: {name}";
            }

            cancellationToken.ThrowIfCancellationRequested();
            var result = await function.InvokeAsync(new AIFunctionArguments(arguments), cancellationToken).ConfigureAwait(false);
            var text = result?.ToString() ?? "null";
            CompleteTool(info, text);
            return text;
        }
        catch (Exception ex)
        {
            CompleteTool(info, $"Error: {ex.Message}");
            throw;
        }
    }

    private ToolCallInfo StartTool(string name, string arguments)
    {
        var info = new ToolCallInfo
        {
            Name = name,
            Arguments = arguments,
            StartTime = DateTime.UtcNow
        };
        ToolCallStarted?.Invoke(this, info);
        return info;
    }

    private void CompleteTool(ToolCallInfo info, string result)
    {
        info.Result = result;
        info.EndTime = DateTime.UtcNow;
        ToolCallCompleted?.Invoke(this, info);
    }

    private async Task SendAppendAsync(
        string type,
        string content,
        string? delegationId,
        CancellationToken cancellationToken)
    {
        var boundedContent = content.Length > 1800 ? content[..1800] : content;
        var eventId = $"fastvoice_{Guid.NewGuid():N}";
        await SendJsonAsync(JsonSerializer.Serialize(new
        {
            type,
            event_id = eventId,
            delegation_id = delegationId,
            content = boundedContent
        }), cancellationToken).ConfigureAwait(false);
    }

    private async Task SendJsonAsync(string json, CancellationToken cancellationToken)
    {
        var socket = _socket;
        if (socket?.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("GPT-Live WebSocket is not open.");
        }

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                var outbound = JsonNode.Parse(json)?.AsObject();
                var eventId = outbound?["event_id"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(eventId))
                {
                    lock (_stateLock)
                    {
                        _sentEventTimes[eventId] = _clock.ElapsedMilliseconds;
                    }
                }
            }
            catch (JsonException)
            {
            }

            await socket.SendAsync(
                Encoding.UTF8.GetBytes(json),
                WebSocketMessageType.Text,
                true,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private List<(string Role, string Text)> BuildResumeHistory()
    {
        lock (_stateLock)
        {
            var history = new List<(string Role, string Text)>();
            foreach (var fragment in _transcript)
            {
                var role = fragment.Speaker == "assistant" ? "assistant" : "user";
                if (history.Count > 0 && history[^1].Role == role)
                {
                    history[^1] = (role, history[^1].Text + fragment.Delta);
                }
                else
                {
                    history.Add((role, fragment.Delta));
                }
            }

            return history.TakeLast(128).ToList();
        }
    }

    private string? GetEventPayload(JsonObject root)
    {
        // Retain the complete delegation-event dump for later diagnostics, but do not emit
        // it during normal runs. Uncomment this block when protocol inspection is needed:
        // if (GptLiveProtocol.GetString(root, "type") == "session.delegation.created")
        // {
        //     return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        // }

        var mode = Environment.GetEnvironmentVariable("FASTVOICE_LIVE_EVENTS")?.ToLowerInvariant();
        return mode switch
        {
            "full" => GptLiveProtocol.BuildFullPayload(root),
            "safe" => GptLiveProtocol.BuildSafePayload(root),
            _ => null
        };
    }

    private TimeSpan GetSlowToolDuration()
    {
        return int.TryParse(Environment.GetEnvironmentVariable("GPT_LIVE_SLOW_TOOL_MS"), out var milliseconds)
            ? TimeSpan.FromMilliseconds(Math.Clamp(milliseconds, 100, 10000))
            : TimeSpan.FromMilliseconds(1500);
    }

    private static long? TryGetInt64(JsonObject root, string propertyName)
    {
        return long.TryParse(root[propertyName]?.ToString(), out var value) ? value : null;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        lock (_stateLock)
        {
            foreach (var cancellation in _delegations.Values)
            {
                cancellation.Cancel();
                cancellation.Dispose();
            }
            _delegations.Clear();
            foreach (var work in _responsesDelegations.Values)
            {
                work.Cancellation.Cancel();
                work.Cancellation.Dispose();
            }
            _responsesDelegations.Clear();
        }

        _backendLifetimeCts.Cancel();
        _backendLifetimeCts.Dispose();
        _backendHttpClient.Dispose();
        _sendLock.Dispose();
        _receiveCts?.Dispose();
        _socket?.Dispose();
        GC.SuppressFinalize(this);
    }
}