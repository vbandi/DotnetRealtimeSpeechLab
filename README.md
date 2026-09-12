# .NET Real-Time Speech Lab

A .NET 10 console lab for experimenting with real-time speech APIs, streaming audio, voice activity, tool calls, interruption handling, and provider-specific live-agent protocols. It currently exercises **Gemini Live**, **OpenAI Realtime**, **GPT-Live 1**, and **ElevenLabs Agents**. Switch between providers at runtime, preview voices, and persist your preferred voice choices locally.

This is an educational comparison project. It keeps provider adapters side by side so you can study audio capture, streaming events, function calling, cancellation, playback, and provider-specific tradeoffs in a small codebase.

## Providers

- **Google Gemini Live** for bidirectional native-audio sessions
- **OpenAI Realtime** for realtime speech and function calls
- **OpenAI GPT-Live 1** for client-owned and Responses-managed delegation experiments
- **ElevenLabs Agents** for conversational-agent WebSocket sessions

## Learning Goals

1. **Minimal latency**: Measure time from end-of-utterance to first AI response token
2. **Concurrent tool calls**: Fire tools immediately when intent is detected
3. **Streaming transcripts**: Display user and AI speech in real-time
4. **Audio feedback**: Play AI audio response while streaming text
5. **Provider comparison**: Switch between Gemini, OpenAI, and GPT-Live to compare behavior
6. **Voice selection spike**: Browse available Gemini/OpenAI/GPT-Live voices, preview them, and save per-provider defaults

## Prerequisites

- Windows with a working microphone and speakers or headphones
- .NET 10.0 SDK
- API keys for the providers you want to use:
  - `GEMINI_API_KEY` - for Gemini Live
  - `OPENAI_API_KEY` - for OpenAI Realtime and both GPT-Live 1 delegation options
  - `ELEVENLABS_API_KEY` (or `XI_API_KEY`) - for ElevenLabs Agents
- Optional ElevenLabs settings:
  - `ELEVENLABS_AGENT_ID` - update existing agent; if omitted, app creates one
  - `ELEVENLABS_VOICE_ID` - voice for ElevenLabs TTS
- Microphone for audio input
- Speakers/headphones for audio output

Live provider sessions require credentials and may incur provider charges. The local protocol self-test does not use credentials or network access.

## Setup

```powershell
# Set your API keys (one or both)
$env:GEMINI_API_KEY = "your-gemini-key"
$env:OPENAI_API_KEY = "your-openai-key"
$env:ELEVENLABS_API_KEY = "your-elevenlabs-key"

# Optional ElevenLabs agent controls
$env:ELEVENLABS_AGENT_ID = "agent_xxx"
$env:ELEVENLABS_VOICE_ID = "voice_xxx"

# Build
dotnet build

# Run
dotnet run
```

Run the local protocol checks without credentials or network access:

```powershell
dotnet run -- --self-test
```

Start the lab after setting the provider key in the process environment. The key is never printed or written to diagnostics:

```powershell
$env:FASTVOICE_PROVIDER = "gpt-live"
$env:FASTVOICE_LIVE_EVENTS = "safe" # safe (milestones), full (protocol events)
dotnet run
```

GPT-Live has two separate provider choices: client delegation and Responses delegation. Both currently default to the Luna backend (`gpt-5.6-luna`) with low reasoning. Client delegation lets the application own the backend request and context; Responses delegation lets GPT-Live manage the Responses request while the application still executes the custom tools. The mode is selected from the provider screen or with a provider argument.

```powershell
# Optional tuning for client delegation. The interactive provider screen is
# preferred for normal use.
$env:GPT_LIVE_DELEGATION = "client"
$env:GPT_LIVE_CLIENT_MODEL = "gpt-5.6-luna"
$env:GPT_LIVE_CLIENT_REASONING = "low"

# Responses delegation: GPT-Live configures and drives the Responses backend;
# the application still executes the custom tools and returns function results.
$env:GPT_LIVE_DELEGATION = "responses"
$env:GPT_LIVE_RESPONSES_MODEL = "gpt-5.6-luna"
$env:GPT_LIVE_RESPONSES_REASONING = "low"
```

The interactive provider screen exposes these as separate entries: `[L] GPT-Live 1 / Client delegation` and `[R] GPT-Live 1 / Responses delegation`. Command-line aliases are `gpt-live-client` and `gpt-live-responses`; the legacy `gpt-live` and `live` aliases select client delegation. `GPT_LIVE_SLOW_TOOL_MS` remains available for the optional slow-tool experiment.

## Controls

| Key | Action |
|-----|--------|
| S | Return to provider selection |
| V | Open voice selection / preview for the active Gemini, OpenAI, or GPT-Live provider |
| M | Toggle microphone mute |
| P | Stop/resume playback and clear queued audio when stopping |
| X | Cancel active GPT-Live backend sandbox work |
| C | Clear conversation history |
| H | Show history turn count |
| Q | Quit |

On startup, the app waits for explicit provider selection (`G`, `O`, `L`, `R`, `E`, or `Q`) before voice mode starts.

After you choose Gemini, OpenAI, or either GPT-Live mode, the experiment opens a voice picker:

- `Up` / `Down` moves through the available voice catalog
- `P` previews the highlighted voice with a canned sample line
- `Enter` or `S` saves that voice as the default for the provider
- `D` clears the saved choice and falls back to the provider default voice
- `Q` keeps the current saved choice and continues

Saved voice and tool settings are written to a local per-user file at `%LOCALAPPDATA%\DotnetRealtimeSpeechLab\settings.json`. The optional `UserTextTranscriptionModes` setting can contain voice modes that should receive the diagnostic `UserTextTranscription` tool; it is empty by default, so the tool is not exposed to any provider or included in the system prompt. Supported mode keys are `gemini`, `realtime`, `gpt-live-client`, `gpt-live-responses`, and `elevenlabs`. For example:

```json
{
  "UserTextTranscriptionModes": [ "realtime", "gpt-live-responses" ]
}
```

For scripted/non-interactive runs, you can explicitly select provider up front:

```powershell
$env:FASTVOICE_PROVIDER = "gpt-live-responses" # gemini | openai | gpt-live-client | gpt-live-responses | elevenlabs
dotnet run

# or pass provider as argument
dotnet run -- elevenlabs
```

## Provider Differences

| Feature | Gemini Live | OpenAI Realtime | GPT-Live 1 | ElevenLabs Agents |
|---------|-------------|-----------------|-------------------|
| Input sample rate | 16kHz | 24kHz | 24kHz | 16kHz |
| Output sample rate | 24kHz | 24kHz | 24kHz | 16kHz (configured) |
| Voice catalog | 30 documented Gemini native audio voices | Fixed SDK voice set (`alloy`, `ash`, `ballad`, `cedar`, `coral`, `echo`, `marin`, `sage`, `shimmer`, `verse`) | Explicit Live `BuiltInVoice` catalog, including Live-specific voices such as `quartz`, `ripple`, `vesper`, `willow`, `stone`, `gleam`, and `meridian` | Existing env-configured voice ID |
| Voice preview | Yes | Yes | Yes, over the selected delegation branch | No change in this spike |
| Saved default voice | Yes | Yes | Yes, separately for Client and Responses; `GPT_LIVE_VOICE` remains a fallback | No change in this spike |
| VAD | WebRTC local + server | Server-side semantic | Live full-duplex server behavior | Server-side |
| Auto-reconnect | Yes (speech-triggered) | No | No | No |
| Barge-in | Automatic | Manual interrupt | Interruption events | Interruption events |
| Delegation | Provider tools | Realtime function calls | Separate Client or Responses mode; local tool execution | Provider agent |

## Available Tools

The AI has access to these tools for fast reactions. `UserTextTranscription` is retained as an optional diagnostic tool, but is disabled for every mode by default and is not part of the shared system prompt.

| Tool | Description |
|------|-------------|
| `Add(a, b)` | Add two numbers |
| `Subtract(a, b)` | Subtract b from a |
| `Multiply(a, b)` | Multiply two numbers |
| `Divide(a, b)` | Divide a by b |
| `GetCurrentTime()` | Get current time (HH:mm:ss) |
| `GetCurrentDate()` | Get current date |
| `SaveNote(content)` | Save a note to memory |
| `ListNotes()` | List all saved notes |
| `ClearNotes()` | Clear all notes |
| `SetListenerEmoji(emoji)` | Update console title emoji |
| `RandomNumber(min, max)` | Generate random number |

## Example Utterances

Try saying:
- "What's 25 plus 17?"
- "What time is it?"
- "Save a note: buy milk"
- "Set your emoji to thinking"
- "Give me a random number between 1 and 100"
- "List my notes"

## Output Format

```
[00:05.234] 🎤 User: what's twenty five plus seventeen
[00:06.100] 🔧 Tool: Add({"a":25,"b":17})
[00:06.112] ✅ Result: 42 (12ms)
[00:06.150] 🤖 AI: Twenty-five plus seventeen equals forty-two.
```

## Timing Metrics

The experiment tracks:
- **Tool execution time**: Duration of each tool call
- **Barge-in detection**: When user interrupts AI response
- **Protocol acknowledgment latency**: For supported GPT-Live commands

The old response-latency display is intentionally disabled because Live transcript fragments do not provide an authoritative speech-turn boundary.

## Architecture

```
┌─────────────────┐     ┌──────────────────┐     ┌─────────────────┐
│  AudioCapture   │────▶│  GeminiLiveRunner │────▶│  AudioPlayback  │
│  (16kHz PCM)    │     │  (WebSocket)      │     │  (24kHz PCM)    │
└─────────────────┘     └──────────────────┘     └─────────────────┘
                               │
                               ▼
                        ┌──────────────────┐
                        │    CliTools      │
                        │  (AI Functions)  │
                        └──────────────────┘
```

## Files

- `Program.cs` - Main entry point, provider switching, console UI
- `IVoiceRunner.cs` - Common interface for voice providers
- `GeminiLiveRunner.cs` - Gemini Live client with tool support
- `OpenAIRealtimeRunner.cs` - OpenAI Realtime client with tool support
- `GptLiveRunner.cs` - GPT-Live WebSocket client, client delegation, diagnostics, and sandbox backend
- `GptLiveProtocol.cs` - GPT-Live event, transcript, context, and usage helpers
- `GptLiveDeterministicTests.cs` - no-network protocol and lifecycle checks
- `ElevenLabsAgentRunner.cs` - ElevenLabs Conversational AI websocket runner
- `ElevenLabsAgentApiClient.cs` - Creates/updates ElevenLabs agent and fetches signed websocket URL
- `CliTools.cs` - AI-callable tools with `[Description]` attributes
- `ToolDiscovery.cs` - Reflection-based tool discovery
- `AudioCapture.cs` - NAudio microphone input (configurable rate)
- `AudioPlayback.cs` - NAudio speaker output (configurable rate)

See [GPT-LIVE-SPIKE-FINDINGS.md](GPT-LIVE-SPIKE-FINDINGS.md) for the evidence ledger, protocol findings, paid-test gate, and open research questions.
