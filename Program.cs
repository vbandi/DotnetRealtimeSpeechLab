using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.AI;

namespace DotnetRealtimeSpeechLab;

public enum VoiceProvider { Gemini, OpenAI, GptLiveClient, GptLiveResponses, ElevenLabs }

class Program
{
    private const string DefaultElevenLabsAgentName = "DotnetRealtimeSpeechLabAgent";
    private const string DefaultVoicePreviewText = "This is a voice preview for the Fast Voice CLI experiment.";

    private static string _currentEmoji = "😊";
    private static readonly StringBuilder _userTranscript = new();
    private static readonly StringBuilder _aiTranscript = new();
    private static readonly Stopwatch _sessionStopwatch = new();
    private static DateTime _lastUserSpeechEnd;
    private static DateTime _lastAiResponseStart;
    private static bool _isUserSpeaking;
    private static bool _isAiSpeaking;
    private static readonly object _consoleLock = new();

    // Current state
    private static VoiceProvider _currentProvider = VoiceProvider.Gemini;
    private static IVoiceRunner? _runner;
    private static AudioCapture? _capture;
    private static AudioPlayback? _playback;
    private static IList<AIFunction>? _aiFunctions;
    private static string? _systemPrompt;
    private static string? _geminiApiKey;
    private static string? _openaiApiKey;
    private static string? _elevenLabsApiKey;
    private static string? _elevenLabsAgentId;
    private static string? _elevenLabsVoiceId;
    private static string _elevenLabsAgentName = DefaultElevenLabsAgentName;
    private static SpeechLabSettingsStore? _settingsStore;
    private static SpeechLabSettings _settings = new();
    private static bool _playbackStopped;

    static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.Title = "DotnetRealtimeSpeechLab - Ultra-Fast Voice Reactions";

        if (args.Any(arg => arg.Equals("--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            GptLiveDeterministicTests.RunAll();
            Console.WriteLine("GPT-Live deterministic tests passed.");
            return 0;
        }

        // Get API keys
        _geminiApiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        _openaiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        _elevenLabsApiKey = Environment.GetEnvironmentVariable("ELEVENLABS_API_KEY")
            ?? Environment.GetEnvironmentVariable("XI_API_KEY");
        _elevenLabsAgentId = Environment.GetEnvironmentVariable("ELEVENLABS_AGENT_ID");
        _elevenLabsVoiceId = Environment.GetEnvironmentVariable("ELEVENLABS_VOICE_ID");
        _elevenLabsAgentName = DefaultElevenLabsAgentName;
        _settingsStore = new SpeechLabSettingsStore();
        _settings = _settingsStore.Load();

        if (string.IsNullOrEmpty(_geminiApiKey) && string.IsNullOrEmpty(_openaiApiKey) && string.IsNullOrEmpty(_elevenLabsApiKey))
        {
            WriteError("No provider API key found. Set one or more of: GEMINI_API_KEY, OPENAI_API_KEY, ELEVENLABS_API_KEY");
            return 1;
        }

        WriteHeader();

        // Create tools
        var tools = new CliTools(emoji =>
        {
            _currentEmoji = emoji;
            Console.Title = $"{emoji} DotnetRealtimeSpeechLab - {_currentProvider}";
        });

        _aiFunctions = ToolDiscovery.DiscoverTools(tools);
        WriteInfo($"Discovered {_aiFunctions.Count} tools: {string.Join(", ", _aiFunctions.Select(f => f.Name))}");

        // System prompt optimized for fast reactions (tool-capable providers)
        _systemPrompt = """
            You are a fast, responsive voice assistant in a CLI experiment.

            CRITICAL BEHAVIORS:
            - Be EXTREMELY concise. One sentence max for simple queries.
            - Use tools AGGRESSIVELY and IMMEDIATELY when relevant.
            - For math, ALWAYS use the calculator tools (Add, Subtract, Multiply, Divide).
            - React to partial intent - if you hear "what's 2 plus" you can start preparing.
            - Use SetListenerEmoji to reflect your state: 🤔 when thinking, 😊 when ready, 🎉 for success, 😴 if bored.
            - For time/date questions, use GetCurrentTime/GetCurrentDate tools.

            AVAILABLE TOOLS:
            - Add(a, b), Subtract(a, b), Multiply(a, b), Divide(a, b) - Math operations
            - GetCurrentTime(), GetCurrentDate() - Time queries
            - SaveNote(content), ListNotes(), ClearNotes() - Note management
            - SetListenerEmoji(emoji) - Update your displayed state
            - RandomNumber(min, max) - Generate random numbers

            Keep responses SHORT and FAST. Speed is everything.
            """;

        try
        {
            var selectedProvider = TryResolveExplicitProvider(args);
            if (selectedProvider != null)
            {
                WriteInfo($"Provider explicitly selected: {selectedProvider}");
            }
            else
            {
                selectedProvider = PromptForStartupProvider();
            }

            if (selectedProvider == null)
            {
                WriteInfo("No provider selected. Exiting.");
                return 0;
            }

            await ConfigureVoiceForProviderAsync(selectedProvider.Value, isStartup: true);

            if (!await StartProviderAsync(selectedProvider.Value))
            {
                WriteError("Could not start the selected provider.");
                return 1;
            }

            _sessionStopwatch.Start();

            WriteInstructions();

            // Main loop
            var running = true;
            while (running)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);
                    switch (key.Key)
                    {
                        case ConsoleKey.Q:
                            running = false;
                            break;

                        case ConsoleKey.C:
                            _runner?.ClearHistory();
                            WriteInfo("Conversation history cleared");
                            break;

                        case ConsoleKey.H:
                            WriteInfo($"History: {_runner?.ConversationHistoryCount ?? 0} turns");
                            break;

                        case ConsoleKey.N:
                            await InjectNotificationAsync();
                            break;

                        case ConsoleKey.M:
                            if (_runner is IGptLiveControls liveControls)
                            {
                                var muted = !liveControls.IsMicrophoneMuted;
                                await liveControls.SetMicrophoneMutedAsync(muted);
                                if (muted)
                                {
                                    _capture?.StopRecording();
                                }
                                else
                                {
                                    _capture?.StartRecording();
                                }

                                WriteInfo(muted ? "Microphone muted (local gate + Live protocol)" : "Microphone unmuted");
                                break;
                            }

                            if (_capture?.IsRecording == true)
                            {
                                _capture.StopRecording();
                                WriteInfo("🔇 Microphone muted");
                            }
                            else if (_capture != null)
                            {
                                _capture.StartRecording();
                                WriteInfo("🎤 Microphone unmuted");
                            }
                            break;

                        case ConsoleKey.P:
                            if (_playback != null)
                            {
                                _playbackStopped = !_playbackStopped;
                                if (_playbackStopped)
                                {
                                    _playback.ClearBuffer();
                                    _playback.Stop();
                                }
                                else
                                {
                                    _playback.Start();
                                }

                                WriteInfo(_playbackStopped ? "Playback stopped and buffer cleared" : "Playback resumed");
                            }
                            break;

                        case ConsoleKey.X:
                            if (_runner is IGptLiveControls liveBackendControls)
                            {
                                await liveBackendControls.CancelBackendAsync();
                                WriteInfo("Requested cancellation of active Live backend tasks");
                            }
                            else
                            {
                                WriteInfo("Backend cancellation is only available for GPT-Live in this experiment");
                            }
                            break;

                        case ConsoleKey.S:
                            running = await ReturnToProviderSelectionAsync();
                            break;

                        case ConsoleKey.V:
                            await ChangeVoiceAsync();
                            break;
                    }
                }
                await Task.Delay(50);
            }
        }
        catch (Exception ex)
        {
            WriteError($"Fatal error: {ex.Message}");
            return 1;
        }
        finally
        {
            await CleanupAsync();
        }

        WriteInfo("Session ended");
        return 0;
    }

    private static IList<AIFunction> SelectToolsForMode(string modeId)
    {
        return ToolPolicy.SelectForMode(modeId, _aiFunctions!, _settings.UserTextTranscriptionModes);
    }

    private static string GetToolModeKey(VoiceProvider provider)
    {
        return provider switch
        {
            VoiceProvider.Gemini => "gemini",
            VoiceProvider.OpenAI => "realtime",
            VoiceProvider.GptLiveClient => "gpt-live-client",
            VoiceProvider.GptLiveResponses => "gpt-live-responses",
            VoiceProvider.ElevenLabs => "elevenlabs",
            _ => provider.ToString()
        };
    }

    private static async Task<bool> StartProviderAsync(VoiceProvider provider)
    {
        // Cleanup existing
        await CleanupAsync();

        _currentProvider = provider;
        Console.Title = $"{_currentEmoji} DotnetRealtimeSpeechLab - {provider}";

        var modelTools = SelectToolsForMode(GetToolModeKey(provider));

        // Create appropriate runner
        _runner = await CreateRunnerAsync(
            provider,
            GetSystemPromptForProvider(provider),
            modelTools,
            GetConfiguredVoiceId(provider));

        if (_runner == null)
        {
            return false;
        }

        // Create audio I/O with correct sample rates
        _capture = new AudioCapture(_runner.InputSampleRate);
        _playback = new AudioPlayback(_runner.OutputSampleRate);
        _playbackStopped = false;

        // Wire up events
        WireUpEvents(_runner, _playback);

        // Wire up audio capture
        _capture.AudioDataAvailable += async (s, data) =>
        {
            if (_runner != null)
                await _runner.SendAudioAsync(data);
        };

        WriteInfo($"Connecting to {_runner.ProviderName}...");
        WriteInfo($"  Input: {_runner.InputSampleRate}Hz, Output: {_runner.OutputSampleRate}Hz");
        if (_runner is GptLiveRunner liveRunner)
        {
            WriteInfo($"  Delegation: {liveRunner.DelegationMode}");
        }
        if (provider != VoiceProvider.ElevenLabs)
        {
            WriteInfo($"  Voice: {GetConfiguredVoiceLabel(provider)}");
        }

        await _runner.ConnectAsync();
        WriteSuccess($"Connected to {_runner.ProviderName}!");

        _playback.Start();
        _capture.StartRecording();

        // Reset transcript state
        _userTranscript.Clear();
        _aiTranscript.Clear();
        _isUserSpeaking = false;
        _isAiSpeaking = false;
        SaveLastProvider(provider);

        return true;
    }

    private static async Task<IVoiceRunner?> CreateRunnerAsync(
        VoiceProvider provider,
        string systemPrompt,
        IList<AIFunction> aiFunctions,
        string? configuredVoiceId)
    {
        if (provider == VoiceProvider.Gemini)
        {
            if (string.IsNullOrEmpty(_geminiApiKey))
            {
                WriteError("GEMINI_API_KEY not set, cannot switch to Gemini");
                return null;
            }

            return new GeminiLiveRunner(_geminiApiKey, systemPrompt, aiFunctions, voiceName: configuredVoiceId);
        }

        if (provider == VoiceProvider.OpenAI)
        {
            if (string.IsNullOrEmpty(_openaiApiKey))
            {
                WriteError("OPENAI_API_KEY not set, cannot switch to OpenAI");
                return null;
            }

            return new OpenAIRealtimeRunner(_openaiApiKey, systemPrompt, aiFunctions, voiceName: configuredVoiceId);
        }

        if (provider is VoiceProvider.GptLiveClient or VoiceProvider.GptLiveResponses)
        {
            if (string.IsNullOrEmpty(_openaiApiKey))
            {
                WriteError("OPENAI_API_KEY not set, cannot start GPT-Live 1");
                return null;
            }

            var delegationMode = provider == VoiceProvider.GptLiveResponses
                ? GptLiveDelegationMode.Responses
                : GptLiveDelegationMode.Client;
            return new GptLiveRunner(
                _openaiApiKey,
                systemPrompt,
                aiFunctions,
                voiceName: configuredVoiceId,
                delegationMode: delegationMode);
        }

        if (string.IsNullOrEmpty(_elevenLabsApiKey))
        {
            WriteError("ELEVENLABS_API_KEY (or XI_API_KEY) not set, cannot switch to ElevenLabs");
            return null;
        }

        using var elevenLabsApi = new ElevenLabsAgentApiClient(_elevenLabsApiKey);
        var upsertResult = await elevenLabsApi.UpsertAgentAsync(
            systemPrompt,
            _elevenLabsAgentId,
            _elevenLabsAgentName,
            _elevenLabsVoiceId,
            aiFunctions);

        _elevenLabsAgentId = upsertResult.AgentId;
        WriteInfo($"ElevenLabs agent {upsertResult.Operation.ToString().ToLowerInvariant()}d: {_elevenLabsAgentId}");

        if (upsertResult.FallbackUsed)
        {
            WriteInfo("Previous ELEVENLABS_AGENT_ID was not found. A new agent was created.");
        }

        return new ElevenLabsAgentRunner(_elevenLabsApiKey, _elevenLabsAgentId, aiFunctions);
    }

    private static async Task<bool> ReturnToProviderSelectionAsync()
    {
        WriteInfo("Returning to provider selection...");
        await CleanupAsync();

        var selectedProvider = PromptForStartupProvider();
        if (selectedProvider == null)
        {
            return false;
        }

        try
        {
            await ConfigureVoiceForProviderAsync(selectedProvider.Value, isStartup: false);
            if (!await StartProviderAsync(selectedProvider.Value))
            {
                WriteError($"Could not start {selectedProvider.Value}.");
                return false;
            }

            WriteInstructions();
            return true;
        }
        catch (Exception ex)
        {
            WriteError($"Failed to start {selectedProvider.Value}: {ex.Message}");
            return false;
        }
    }

    private static async Task ChangeVoiceAsync()
    {
        if (_currentProvider == VoiceProvider.ElevenLabs)
        {
            WriteInfo("Voice selection is currently implemented for Gemini Live, OpenAI Realtime, and GPT-Live only.");
            return;
        }

        WriteInfo($"Opening voice selection for {_currentProvider}...");
        await CleanupAsync();
        await ConfigureVoiceForProviderAsync(_currentProvider, isStartup: false);
        await StartProviderAsync(_currentProvider);
    }

    private static List<VoiceProvider> GetAvailableProviders()
    {
        var providers = new List<VoiceProvider>();

        if (!string.IsNullOrWhiteSpace(_geminiApiKey))
            providers.Add(VoiceProvider.Gemini);

        if (!string.IsNullOrWhiteSpace(_openaiApiKey))
            providers.Add(VoiceProvider.OpenAI);

        if (!string.IsNullOrWhiteSpace(_openaiApiKey))
        {
            providers.Add(VoiceProvider.GptLiveClient);
            providers.Add(VoiceProvider.GptLiveResponses);
        }

        if (!string.IsNullOrWhiteSpace(_elevenLabsApiKey))
            providers.Add(VoiceProvider.ElevenLabs);

        return providers;
    }

    private static VoiceProvider? PromptForStartupProvider()
    {
        var availableProviders = GetAvailableProviders();
        if (availableProviders.Count == 0)
        {
            return null;
        }

        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("Select provider to start voice mode:");
        if (availableProviders.Contains(VoiceProvider.Gemini))
            Console.WriteLine($"  [G] Gemini Live ({GetConfiguredVoiceLabel(VoiceProvider.Gemini)})");
        if (availableProviders.Contains(VoiceProvider.OpenAI))
            Console.WriteLine($"  [O] OpenAI Realtime ({GetConfiguredVoiceLabel(VoiceProvider.OpenAI)})");
        if (availableProviders.Contains(VoiceProvider.GptLiveClient))
            Console.WriteLine($"  [L] GPT-Live 1 / Client delegation ({GetConfiguredVoiceLabel(VoiceProvider.GptLiveClient)})");
        if (availableProviders.Contains(VoiceProvider.GptLiveResponses))
            Console.WriteLine($"  [R] GPT-Live 1 / Responses delegation ({GetConfiguredVoiceLabel(VoiceProvider.GptLiveResponses)})");
        if (availableProviders.Contains(VoiceProvider.ElevenLabs))
            Console.WriteLine("  [E] ElevenLabs Agents");
        Console.WriteLine("  [Q] Quit");
        Console.ResetColor();

        while (true)
        {
            var key = Console.ReadKey(true).Key;
            switch (key)
            {
                case ConsoleKey.G when availableProviders.Contains(VoiceProvider.Gemini):
                    return VoiceProvider.Gemini;

                case ConsoleKey.O when availableProviders.Contains(VoiceProvider.OpenAI):
                    return VoiceProvider.OpenAI;

                case ConsoleKey.L when availableProviders.Contains(VoiceProvider.GptLiveClient):
                    return VoiceProvider.GptLiveClient;

                case ConsoleKey.R when availableProviders.Contains(VoiceProvider.GptLiveResponses):
                    return VoiceProvider.GptLiveResponses;

                case ConsoleKey.E when availableProviders.Contains(VoiceProvider.ElevenLabs):
                    return VoiceProvider.ElevenLabs;

                case ConsoleKey.Q:
                    return null;

                default:
                    WriteInfo("Invalid selection. Press G, O, L, R, E, or Q.");
                    break;
            }
        }
    }

    private static VoiceProvider? TryResolveExplicitProvider(string[] args)
    {
        var providerToken = args.FirstOrDefault(arg =>
            arg.Equals("gemini", StringComparison.OrdinalIgnoreCase)
            || arg.Equals("openai", StringComparison.OrdinalIgnoreCase)
            || arg.Equals("gpt-live", StringComparison.OrdinalIgnoreCase)
            || arg.Equals("live", StringComparison.OrdinalIgnoreCase)
            || arg.Equals("gpt-live-client", StringComparison.OrdinalIgnoreCase)
            || arg.Equals("live-client", StringComparison.OrdinalIgnoreCase)
            || arg.Equals("gpt-live-responses", StringComparison.OrdinalIgnoreCase)
            || arg.Equals("live-responses", StringComparison.OrdinalIgnoreCase)
            || arg.Equals("elevenlabs", StringComparison.OrdinalIgnoreCase)
            || arg.Equals("eleven", StringComparison.OrdinalIgnoreCase));

        providerToken ??= Environment.GetEnvironmentVariable("SPEECH_LAB_PROVIDER");

        if (string.IsNullOrWhiteSpace(providerToken))
        {
            return null;
        }

        var provider = providerToken.ToLowerInvariant() switch
        {
            "gemini" => VoiceProvider.Gemini,
            "openai" => VoiceProvider.OpenAI,
            "gpt-live" => VoiceProvider.GptLiveClient,
            "live" => VoiceProvider.GptLiveClient,
            "gpt-live-client" => VoiceProvider.GptLiveClient,
            "live-client" => VoiceProvider.GptLiveClient,
            "gpt-live-responses" => VoiceProvider.GptLiveResponses,
            "live-responses" => VoiceProvider.GptLiveResponses,
            "elevenlabs" => VoiceProvider.ElevenLabs,
            "eleven" => VoiceProvider.ElevenLabs,
            _ => (VoiceProvider?)null
        };

        if (provider == null)
        {
            WriteError($"Unknown provider override '{providerToken}'. Use gemini, openai, gpt-live-client, gpt-live-responses, or elevenlabs.");
            return null;
        }

        var availableProviders = GetAvailableProviders();
        if (!availableProviders.Contains(provider.Value))
        {
            WriteError($"Provider override '{provider}' is not available with current environment variables.");
            return null;
        }

        return provider;
    }

    private static async Task CleanupAsync()
    {
        _capture?.StopRecording();
        _capture?.Dispose();
        _capture = null;

        if (_runner != null)
        {
            await _runner.DisconnectAsync();
            _runner.Dispose();
            _runner = null;
        }

        _playback?.Stop();
        _playback?.Dispose();
        _playback = null;
    }

    private static void WireUpEvents(IVoiceRunner runner, AudioPlayback playback)
    {
        runner.Connected += (s, e) =>
        {
            WriteTimestamped("🟢", $"Connected to {runner.ProviderName}", ConsoleColor.Green);
        };

        runner.Disconnected += (s, e) =>
        {
            WriteTimestamped("🔴", "Disconnected", ConsoleColor.DarkYellow);
        };

        runner.ReconnectionStarted += (s, e) =>
        {
            WriteTimestamped("🔄", "Speech detected - reconnecting...", ConsoleColor.Yellow);
        };

        runner.ReconnectionCompleted += (s, e) =>
        {
            WriteTimestamped("🟢", "Reconnected", ConsoleColor.Green);
        };

        runner.ErrorOccurred += (s, ex) =>
        {
            WriteTimestamped("❌", $"Error: {ex.Message}", ConsoleColor.Red);
        };

        runner.UserTranscriptReceived += (s, text) =>
        {
            lock (_consoleLock)
            {
                if (_isAiSpeaking)
                {
                    _isAiSpeaking = false;
                    Console.WriteLine();
                }

                if (!_isUserSpeaking)
                {
                    _isUserSpeaking = true;
                    _userTranscript.Clear();
                    Console.WriteLine();
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Write($"[{GetTimestamp()}] 🎤 User: ");
                    Console.ResetColor();
                }

                _userTranscript.Append(text);
                _lastUserSpeechEnd = DateTime.UtcNow;

                // Just append the new chunk
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write(text);
                Console.ResetColor();
            }
        };

        runner.AiTranscriptReceived += (s, text) =>
        {
            lock (_consoleLock)
            {
                if (_isUserSpeaking)
                {
                    _isUserSpeaking = false;
                    Console.WriteLine();
                }

                if (!_isAiSpeaking)
                {
                    _isAiSpeaking = true;
                    _lastAiResponseStart = DateTime.UtcNow;

                    // Disabled: this was calculated from session-level transcript state,
                    // not an authoritative Live turn boundary, so the displayed value
                    // was misleading.
                    // if (_lastUserSpeechEnd != default)
                    // {
                    //     var latency = (_lastAiResponseStart - _lastUserSpeechEnd).TotalMilliseconds;
                    //     Console.ForegroundColor = ConsoleColor.DarkGray;
                    //     Console.WriteLine($"[{GetTimestamp()}] ⏱️  Response latency: {latency:F0}ms");
                    //     Console.ResetColor();
                    // }

                    _aiTranscript.Clear();
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.Write($"[{GetTimestamp()}] 🤖 AI: ");
                    Console.ResetColor();
                }

                _aiTranscript.Append(text);

                // Just append the new chunk
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write(text);
                Console.ResetColor();
            }
        };

        runner.TurnCompleted += (s, e) =>
        {
            lock (_consoleLock)
            {
                if (_isAiSpeaking)
                {
                    _isAiSpeaking = false;
                    Console.WriteLine();
                }
            }
        };

        runner.Interrupted += (s, e) =>
        {
            lock (_consoleLock)
            {
                _isAiSpeaking = false;
                playback.ClearBuffer();
                WriteTimestamped("⚡", "Interrupted (barge-in)", ConsoleColor.Magenta);
            }
        };

        runner.ThinkingReceived += (s, text) =>
        {
            // Optionally show thinking
        };

        runner.ToolCallStarted += (s, info) =>
        {
            WriteTimestamped("🔧", $"Tool: {info.Name}({info.Arguments})", ConsoleColor.Blue);
        };

        runner.ToolCallCompleted += (s, info) =>
        {
            WriteTimestamped("✅", $"Result: {info.Result} ({info.Duration.TotalMilliseconds:F0}ms)", ConsoleColor.Green);
        };

        runner.AudioReceived += (s, buffer) =>
        {
            if (!_playbackStopped)
            {
                playback.AddSamples(buffer);
            }
        };

        if (runner is GptLiveRunner liveRunner)
        {
            // Verbose delegation request logging is intentionally disabled for normal runs.
            // Re-enable this handler when inspecting the complete client/Responses LLM request:
            // liveRunner.DelegationRequestPrepared += (s, request) =>
            // {
            //     lock (_consoleLock)
            //     {
            //         Console.ForegroundColor = ConsoleColor.Magenta;
            //         Console.WriteLine($"[{GetTimestamp()}] 🔧 Delegation request ({request.Mode}, {request.Stage}):");
            //         Console.WriteLine(request.Json);
            //         Console.ResetColor();
            //     }
            // };

            liveRunner.EventObserved += (s, info) =>
            {
                if (!ShouldShowLiveEvent(info.Type))
                {
                    return;
                }

                var suffix = info.SafePayload == null ? "" : $" {info.SafePayload}";
                var acknowledgment = info.AcknowledgmentLatencyMs.HasValue
                    ? $" ack={info.AcknowledgmentLatencyMs.Value}ms"
                    : "";
                // The complete session.delegation.created payload is retained in GptLiveRunner,
                // but the verbose CLI dump is intentionally disabled for normal runs.
                // Re-enable the commented branch below for protocol inspection.
                // if (info.Type == "session.delegation.created" && info.SafePayload != null)
                // {
                //     WriteTimestamped("LIVE", $"Event {info.Type}{acknowledgment}:", ConsoleColor.DarkCyan);
                //     lock (_consoleLock)
                //     {
                //         Console.ForegroundColor = ConsoleColor.DarkCyan;
                //         Console.WriteLine(info.SafePayload);
                //         Console.ResetColor();
                //     }
                // }
                // else
                // {
                WriteTimestamped("LIVE", $"Event {info.Type}{acknowledgment}{suffix}", ConsoleColor.DarkCyan);
                // }
                if (info.Type is "session.usage.updated" or "session.closed")
                {
                    WriteTimestamped("COST", $"Voice usage snapshot: {liveRunner.LatestVoiceUsageSeconds?.ToString("F2") ?? "unconfirmed"}s", ConsoleColor.DarkYellow);
                }
                if (info.Type == "session.closed")
                {
                    WriteTimestamped(
                        "AUDIO",
                        $"Input={liveRunner.InputAudioDuration.TotalSeconds:F2}s Output={liveRunner.OutputAudioDuration.TotalSeconds:F2}s Close={liveRunner.CloseReason ?? "unconfirmed"}",
                        ConsoleColor.DarkGray);
                }
            };
        }
    }

    private static bool ShouldShowLiveEvent(string eventType)
    {
        if (string.Equals(
            Environment.GetEnvironmentVariable("SPEECH_LAB_LIVE_EVENTS"),
            "full",
            StringComparison.OrdinalIgnoreCase))
        {
            return eventType != "session.output_audio.delta";
        }

        return eventType is
            "session.started" or
            "session.delegation.created" or
            "session.usage.updated" or
            "session.interrupted" or
            "response.interrupted" or
            "error" or
            "session.closed";
    }

    private static string GetTimestamp()
    {
        return _sessionStopwatch.Elapsed.ToString(@"mm\:ss\.fff");
    }

    private static void WriteTimestamped(string emoji, string message, ConsoleColor color)
    {
        lock (_consoleLock)
        {
            Console.ForegroundColor = color;
            Console.WriteLine($"[{GetTimestamp()}] {emoji} {message}");
            Console.ResetColor();
        }
    }

    private static void WriteHeader()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════╗
║  .NET Real-Time Speech Lab                                  ║
║  Gemini + OpenAI + GPT-Live + ElevenLabs | Tools + Timing   ║
╚═══════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }

    private static void WriteInstructions()
    {
        var geminiStatus = string.IsNullOrEmpty(_geminiApiKey) ? "❌ not set" : "✅ available";
        var openaiStatus = string.IsNullOrEmpty(_openaiApiKey) ? "❌ not set" : "✅ available";
        var elevenLabsStatus = string.IsNullOrEmpty(_elevenLabsApiKey) ? "❌ not set" : "✅ available";
        var availableProviders = string.Join(", ", GetAvailableProviders().Select(GetProviderLabel));
        if (string.IsNullOrWhiteSpace(availableProviders))
            availableProviders = "none";

        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine($@"
┌─────────────────────────────────────────────────────────────┐
│  Provider: {_currentProvider,-16}                              │
│  Available: {availableProviders,-44}│
│  Gemini voice: {GetConfiguredVoiceLabel(VoiceProvider.Gemini),-40}│
│  OpenAI voice: {GetConfiguredVoiceLabel(VoiceProvider.OpenAI),-40}│
│  GPT-Live voice: {GetConfiguredVoiceLabel(_currentProvider is VoiceProvider.GptLiveResponses ? VoiceProvider.GptLiveResponses : VoiceProvider.GptLiveClient),-37}│
│  GEMINI_API_KEY: {geminiStatus,-15}                          │
│  OPENAI_API_KEY: {openaiStatus,-15}                          │
│  ELEVENLABS_API_KEY: {elevenLabsStatus,-11}                      │
│  ELEVENLABS_AGENT_ID: {(_elevenLabsAgentId ?? "(auto-create)"),-30}│
├─────────────────────────────────────────────────────────────┤
│  Controls:                                                  │
│    S = Return to provider selection                          │
│    V = Select / preview voice                               │
│    M = Toggle microphone mute                               │
│    P = Stop / resume playback                               │
│    X = Cancel GPT-Live backend task                         │
│    C = Clear conversation history                           │
│    H = Show history count                                   │
│    N = Inject test notification                             │
│    Q = Quit                                                 │
├─────────────────────────────────────────────────────────────┤
│  Try saying:                                                │
│    ""What's 25 plus 17?""                                     │
│    ""What time is it?""                                       │
│    ""Save a note: buy milk""                                  │
│    ""Set your emoji to thinking""                             │
└─────────────────────────────────────────────────────────────┘
");
        Console.ResetColor();
        Console.WriteLine($"🎤 Listening via {_currentProvider}... (speak into your microphone)\n");
    }

    private static async Task ConfigureVoiceForProviderAsync(VoiceProvider provider, bool isStartup)
    {
        if (provider == VoiceProvider.ElevenLabs)
        {
            return;
        }

        var voices = VoiceCatalog.GetVoices(provider).ToList();
        if (voices.Count == 0)
        {
            WriteInfo($"No selectable voice catalog found for {provider}.");
            return;
        }

        var configuredVoiceId = GetConfiguredVoiceId(provider);
        var selectedIndex = configuredVoiceId == null
            ? 0
            : Math.Max(0, voices.FindIndex(voice => string.Equals(voice.Id, configuredVoiceId, StringComparison.OrdinalIgnoreCase)));
        int? menuTop = null;

        while (true)
        {
            RenderVoiceMenu(provider, voices, selectedIndex, configuredVoiceId, isStartup, ref menuTop);

            var key = Console.ReadKey(true);
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                case ConsoleKey.K:
                    selectedIndex = (selectedIndex - 1 + voices.Count) % voices.Count;
                    break;

                case ConsoleKey.DownArrow:
                case ConsoleKey.J:
                    selectedIndex = (selectedIndex + 1) % voices.Count;
                    break;

                case ConsoleKey.P:
                    await PreviewVoiceAsync(provider, voices[selectedIndex]);
                    break;

                case ConsoleKey.Enter:
                case ConsoleKey.S:
                    configuredVoiceId = SetConfiguredVoiceId(provider, voices[selectedIndex].Id);
                    WriteSuccess($"Saved {GetProviderLabel(provider)} voice: {voices[selectedIndex].DisplayName}");
                    return;

                case ConsoleKey.D:
                    configuredVoiceId = SetConfiguredVoiceId(provider, null);
                    WriteInfo($"Cleared saved {GetProviderLabel(provider)} voice. Provider default will be used.");
                    return;

                case ConsoleKey.Escape:
                case ConsoleKey.Q:
                    WriteInfo(isStartup
                        ? $"Keeping {GetProviderLabel(provider)} voice: {GetConfiguredVoiceLabel(provider)}"
                        : "Voice selection cancelled.");
                    return;
            }
        }
    }

    private static void RenderVoiceMenu(
        VoiceProvider provider,
        IReadOnlyList<VoiceOption> voices,
        int selectedIndex,
        string? configuredVoiceId,
        bool isStartup,
        ref int? menuTop)
    {
        lock (_consoleLock)
        {
            if (CanRenderMenuInPlace())
            {
                PrepareVoiceMenuRender(ref menuTop, GetVoiceMenuLineCount(voices));
            }

            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine();
            Console.WriteLine($"Voice selection for {GetProviderLabel(provider)}");
            Console.WriteLine(isStartup
                ? "  Up/Down = move  P = preview  Enter/S = save and continue  D = use provider default  Q = keep current"
                : "  Up/Down = move  P = preview  Enter/S = save and restart  D = use provider default  Q = cancel");
            Console.ResetColor();

            for (var index = 0; index < voices.Count; index++)
            {
                var voice = voices[index];
                var cursor = index == selectedIndex ? ">" : " ";
                var marker = string.Equals(voice.Id, configuredVoiceId, StringComparison.OrdinalIgnoreCase) ? "*" : " ";

                Console.ForegroundColor = index == selectedIndex ? ConsoleColor.Cyan : ConsoleColor.Gray;
                Console.WriteLine($" {cursor}{marker} {voice.DisplayName,-18} {voice.Description}");
                Console.ResetColor();
            }

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  * saved selection. Current: {GetConfiguredVoiceLabel(provider)}");
            Console.ResetColor();
        }
    }

    private static bool CanRenderMenuInPlace()
    {
        return !Console.IsOutputRedirected && !Console.IsErrorRedirected;
    }

    private static int GetVoiceMenuLineCount(IReadOnlyList<VoiceOption> voices)
    {
        return voices.Count + 4;
    }

    private static void PrepareVoiceMenuRender(ref int? menuTop, int lineCount)
    {
        var safeBufferHeight = GetSafeConsoleBufferHeight();
        if (lineCount >= safeBufferHeight)
        {
            Console.Clear();
            menuTop = 0;
            return;
        }

        menuTop ??= Console.CursorTop;
        if (menuTop.Value < 0 || menuTop.Value + lineCount >= safeBufferHeight)
        {
            Console.Clear();
            menuTop = 0;
            return;
        }

        ClearConsoleRegion(menuTop.Value, lineCount);
        Console.SetCursorPosition(0, menuTop.Value);
    }

    private static int GetSafeConsoleBufferHeight()
    {
        try
        {
            return Math.Max(1, Console.BufferHeight);
        }
        catch (IOException)
        {
            return Math.Max(1, Console.WindowHeight);
        }
    }

    private static void ClearConsoleRegion(int top, int lineCount)
    {
        var width = Math.Max(1, Console.WindowWidth - 1);
        for (var lineIndex = 0; lineIndex < lineCount; lineIndex++)
        {
            Console.SetCursorPosition(0, top + lineIndex);
            Console.Write(new string(' ', width));
        }
    }

    private static async Task PreviewVoiceAsync(VoiceProvider provider, VoiceOption voice)
    {
        WriteInfo($"Previewing {voice.DisplayName} on {GetProviderLabel(provider)}...");

        var previewRunner = await CreateRunnerAsync(
            provider,
            GetVoicePreviewSystemPrompt(voice),
            Array.Empty<AIFunction>(),
            voice.Id);

        if (previewRunner == null)
        {
            return;
        }

        using var playback = new AudioPlayback(previewRunner.OutputSampleRate);
        var previewComplete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lastAudioReceivedAt = DateTime.MinValue;

        previewRunner.AudioReceived += (_, buffer) =>
        {
            lastAudioReceivedAt = DateTime.UtcNow;
            playback.AddSamples(buffer);
        };
        previewRunner.AiTranscriptReceived += (_, text) =>
        {
            lock (_consoleLock)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write(text);
                Console.ResetColor();
            }
        };
        previewRunner.TurnCompleted += (_, _) =>
        {
            lock (_consoleLock)
            {
                Console.WriteLine();
            }

            previewComplete.TrySetResult(true);
        };
        previewRunner.ErrorOccurred += (_, ex) => previewComplete.TrySetException(ex);

        try
        {
            playback.Start();
            await previewRunner.ConnectAsync();
            await previewRunner.SendTextAsync($"Say exactly: {DefaultVoicePreviewText}");

            var completed = await Task.WhenAny(previewComplete.Task, Task.Delay(TimeSpan.FromSeconds(12)));
            if (completed != previewComplete.Task)
            {
                WriteError("Voice preview timed out.");
            }
            else
            {
                await previewComplete.Task;
                await WaitForPreviewPlaybackAsync(playback, () => lastAudioReceivedAt);
            }
        }
        catch (Exception ex)
        {
            WriteError($"Voice preview failed: {ex.Message}");
        }
        finally
        {
            playback.Stop();
            await previewRunner.DisconnectAsync();
            previewRunner.Dispose();
        }
    }

    private static async Task WaitForPreviewPlaybackAsync(AudioPlayback playback, Func<DateTime> getLastAudioReceivedAt)
    {
        var timeoutAt = DateTime.UtcNow + TimeSpan.FromSeconds(4);

        while (DateTime.UtcNow < timeoutAt)
        {
            var lastAudioReceivedAt = getLastAudioReceivedAt();
            var silenceWindowReached = lastAudioReceivedAt == DateTime.MinValue
                || DateTime.UtcNow - lastAudioReceivedAt >= TimeSpan.FromMilliseconds(200);

            if (silenceWindowReached && playback.BufferedDuration <= TimeSpan.FromMilliseconds(40))
            {
                return;
            }

            await playback.WaitForDrainAsync(TimeSpan.FromMilliseconds(50));
        }
    }

    private static string GetConfiguredVoiceLabel(VoiceProvider provider)
    {
        if (provider == VoiceProvider.ElevenLabs)
        {
            return _elevenLabsVoiceId ?? "provider default";
        }

        return VoiceCatalog.FindVoice(provider, GetConfiguredVoiceId(provider))?.DisplayName ?? "provider default";
    }

    private static string? GetConfiguredVoiceId(VoiceProvider provider)
    {
        return provider switch
        {
            VoiceProvider.Gemini => VoiceCatalog.NormalizeVoiceId(provider, _settings.GeminiVoice),
            VoiceProvider.OpenAI => VoiceCatalog.NormalizeVoiceId(provider, _settings.OpenAIVoice),
            VoiceProvider.GptLiveClient => GetConfiguredGptLiveVoice(_settings.GptLiveClientVoice),
            VoiceProvider.GptLiveResponses => GetConfiguredGptLiveVoice(_settings.GptLiveResponsesVoice),
            _ => null
        };
    }

    private static string? GetConfiguredGptLiveVoice(string? savedVoiceId)
    {
        var configuredVoiceId = savedVoiceId
            ?? Environment.GetEnvironmentVariable("GPT_LIVE_VOICE");

        return VoiceCatalog.NormalizeVoiceId(VoiceProvider.GptLiveClient, configuredVoiceId);
    }

    private static string? SetConfiguredVoiceId(VoiceProvider provider, string? voiceId)
    {
        var normalizedVoiceId = VoiceCatalog.NormalizeVoiceId(provider, voiceId);

        switch (provider)
        {
            case VoiceProvider.Gemini:
                _settings.GeminiVoice = normalizedVoiceId;
                break;

            case VoiceProvider.OpenAI:
                _settings.OpenAIVoice = normalizedVoiceId;
                break;

            case VoiceProvider.GptLiveClient:
                _settings.GptLiveClientVoice = normalizedVoiceId;
                break;

            case VoiceProvider.GptLiveResponses:
                _settings.GptLiveResponsesVoice = normalizedVoiceId;
                break;
        }

        _settingsStore?.Save(_settings);
        return normalizedVoiceId;
    }

    private static void SaveLastProvider(VoiceProvider provider)
    {
        _settings.LastProvider = provider.ToString();
        _settingsStore?.Save(_settings);
    }

    private static string GetVoicePreviewSystemPrompt(VoiceOption voice)
    {
        return $"""
            You are previewing the {voice.DisplayName} voice for a CLI experiment.
            Speak exactly the sentence you are given.
            Do not call tools.
            Do not add extra words.
            Keep the delivery natural.
            """;
    }

    private static string GetProviderLabel(VoiceProvider provider)
    {
        return provider switch
        {
            VoiceProvider.Gemini => "Gemini",
            VoiceProvider.OpenAI => "OpenAI",
            VoiceProvider.GptLiveClient => "GPT-Live 1 / Client",
            VoiceProvider.GptLiveResponses => "GPT-Live 1 / Responses",
            VoiceProvider.ElevenLabs => "ElevenLabs",
            _ => provider.ToString()
        };
    }

    private static string GetSystemPromptForProvider(VoiceProvider provider)
    {
        if (provider == VoiceProvider.ElevenLabs)
        {
            return """
                You are a fast, responsive voice assistant in a CLI experiment.

                BEHAVIOR:
                - Be concise and clear.
                - Use one sentence for simple requests.
                - Acknowledge complex requests briefly before answering.
                - Use available tools whenever they can provide a precise answer.
                - For arithmetic, call calculator tools instead of mental math.
                - Never mention internal tool names or implementation details.
                - If speech recognition is uncertain, ask a quick clarifying question.

                Keep responses short and natural for real-time voice conversation.
                """;
        }

        if (provider is VoiceProvider.GptLiveClient or VoiceProvider.GptLiveResponses)
        {
            return """
                You are GPT-Live 1 in a .NET real-time speech lab.
                Keep conversational replies concise and natural. Listen and speak concurrently.
                Delegate requests that need backend work, calculations, or the slow sandbox to the client backend.
                The client backend maintains transcript context and returns only verified sandbox results.
                Never claim that a consequential action happened. This experiment has no mail, calendar, or external side effects.
                """;
        }

        return _systemPrompt!;
    }

    private static void WriteInfo(string message)
    {
        lock (_consoleLock)
        {
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine($"[INFO] {message}");
            Console.ResetColor();
        }
    }

    private static void WriteSuccess(string message)
    {
        lock (_consoleLock)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[OK] {message}");
            Console.ResetColor();
        }
    }

    private static void WriteError(string message)
    {
        lock (_consoleLock)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERROR] {message}");
            Console.ResetColor();
        }
    }

    private static async Task InjectNotificationAsync()
    {
        if (_runner == null)
        {
            WriteError("No active runner");
            return;
        }

        const string notification = "[System notification: Response from app creator for 'draw-analyzer': It looks like the user drew a simple red triangular outline resembling a tent or mountain. Briefly tell the user what was drawn. Do NOT call any tools.]";

        WriteInfo("Injecting app-creator-style notification into the active provider...");

        try
        {
            await _runner.SendTextAsync(notification);
        }
        catch (NotSupportedException ex)
        {
            WriteError(ex.Message);
        }
        catch (Exception ex)
        {
            WriteError($"Notification injection failed: {ex.Message}");
        }
    }
}
