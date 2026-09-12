# GPT-Live 1 Spike Findings

Date: 2026-09-10

Scope: the standalone .NET Real-Time Speech Lab. This is an experiment only and does not change any production application.

## Evidence Labels

- **Documented**: verified against the official OpenAI pages linked below.
- **Observed locally**: verified by the no-network deterministic checks in this experiment.
- **Observed with Live**: requires an approved API-key test and is intentionally still pending.
- **Hypothesis**: a design direction that needs a real session to confirm.

## Documented Findings

The current model page identifies `gpt-live-1` as an audio/text input and output model. It supports the Live endpoint at `/v1/live/sessions`; the Realtime endpoint is not the endpoint for this model. The official WebSocket guide specifies a primary WebSocket at `wss://api.openai.com/v1/live/sessions`, an Authorization bearer header, `session.start`, and `session.started` before audio or application commands.

For WebSocket audio, the documented default and selected spike format is mono signed PCM16 at 24 kHz in both directions. Audio is base64 inside JSON events, with no WAV header. The input stream must be paced like a microphone; sending a whole file at once is not a live-microphone test.

GPT-Live voice sessions are documented at $0.05/minute, billed per second. Speaking, assistant speech, silence, and backend waiting are all active session time. Muting input does not close the session. There is no documented billing-free in-place pause; the documented long-task optimization is to close the voice session and create a new one with saved context. WebRTC initialization charges 15 seconds and credits that amount against the running duration; this spike uses WebSocket and does not incur that WebRTC initialization path.

Client delegation emits `session.delegation.created` with metadata including an opaque delegation ID and target. It does not contain task text or function arguments. The application must maintain transcript and task context, preserve the original ID, and return results with `session.thinking.append` or `session.commentary.append`. Appends are limited to 500 tokens. A spoken interruption does not cancel backend work.

Transcript events are timestamped fragments with `start_ms` and `end_ms`. They are not wall-clock timestamps, packet arrival times, exact word alignment, or an authoritative turn-completion signal. WebSocket output audio has no timing fields and there is no output-audio-done event. Playback completion must therefore be measured from the local playback queue, separately from transcript and backend completion.

Usage events report cumulative voice seconds. They are snapshots and must not be summed. `session.closed` is the final usage source; a socket close without that event leaves final usage unconfirmed. Backend usage is separate and must be counted from backend response events or backend API responses.

The current official OpenAI .NET README documents `ResponsesClient` and `RealtimeClient`, but no native Live client or Live WebSocket surface was found in the current C# package documentation. The NuGet feed currently lists `OpenAI` 2.13.0; this experiment remains on its existing 2.10.0 reference for Realtime compatibility and uses `ClientWebSocket` for Live. This is a rechecked SDK finding, not a claim that future releases cannot add Live support.

Sources checked:

- <https://developers.openai.com/api/docs/models/gpt-live-1>
- <https://developers.openai.com/api/docs/guides/live>
- <https://developers.openai.com/api/docs/guides/live-delegation>
- <https://developers.openai.com/api/docs/guides/live-conversations>
- <https://developers.openai.com/api/docs/guides/voice-websockets?api=live>
- <https://developers.openai.com/api/docs/guides/voice-latency-cost?api=live>
- <https://raw.githubusercontent.com/openai/openai-dotnet/main/README.md>

## What This Spike Implements

- Raw server-side WebSocket connection to `/v1/live/sessions` with `gpt-live-1` and 24 kHz PCM16.
- Safe event summaries by default. `SPEECH_LAB_LIVE_EVENTS=safe` includes redacted metadata; `full` permits transcript content for intentional inspection but still redacts audio bytes. No audio is written to disk.
- Fragment-preserving transcript history with source timestamps and explicit resume-history assembly.
- Separate local microphone gating, Live protocol mute, playback stop/clear, session close, and client-backend cancellation.
- Client delegation with explicit delegation ID preservation, a task revision, an application-owned `gpt-5.6-luna` Responses backend using low reasoning, a controllable slow tool, local tool execution, and stale-result suppression after cancellation or close.
- Live-managed Responses delegation via the separate Responses provider mode, currently configured with the same `gpt-5.6-luna` low-reasoning default, available function schemas, `tool_choice: auto`, and parallel tool calls. Completed nested Responses function calls are executed locally and returned with `response.item.create` followed by `response.create`.
- Per-mode tool policy: `UserTextTranscription` is retained only as an optional diagnostic tool. It is excluded from every mode by default and is not included in the shared system prompt. The persistent `UserTextTranscriptionModes` setting can opt it into `gemini`, `realtime`, `gpt-live-client`, `gpt-live-responses`, or `elevenlabs`.
- Session/delegation IDs, monotonic event timestamps, event type, command acknowledgment latency, input/output audio counts and durations, tool timings, cumulative usage snapshots, final usage, and close reason.
- `--self-test` deterministic checks for event payloads, transcript context, usage accounting, cancellation, stale results, and redaction.

## Delegation Flow: The Important Difference

The two delegation modes have different ownership boundaries. In both modes, the application executes local tools; the difference is who interprets the conversation and creates the function call.

### Client delegation

```text
GPT-Live
	-> session.delegation.created
		 (delegation metadata and opaque ID only; no task text, transcript, tool name, or arguments)

Application
	-> snapshots the transcript fragments received so far
	-> sends that context to the application-owned Responses backend (Luna)

Luna
	-> interprets the transcript
	-> returns a function call and arguments, or a verified textual answer

Application
	-> executes the requested local tool
	-> sends the verified result back to GPT-Live with session.commentary.append

GPT-Live
	-> incorporates the result into the live conversation and speaks the response
```

The delegation event itself is only a request to perform delegated work. The application must infer the task from the separately arriving `session.input_transcript.delta` events and its retained transcript history. That history can contain fragmented, delayed, or overlapping speech, so client delegation requires context reconstruction and stale-result/cancellation handling.

### Responses delegation

```text
Application
	-> starts GPT-Live with delegation.type = responses
	-> supplies the Responses model, instructions, and tool schemas

GPT-Live / managed Responses backend
	-> uses the Live conversation context
	-> emits a structured function call through response events
		 (function name, call ID, and JSON arguments)

Application
	-> executes the requested local tool
	-> sends response.item.create with function_call_output
	-> sends response.create to continue the managed Responses turn

GPT-Live / managed Responses backend
	-> produces the final response, which GPT-Live speaks
```

Responses delegation therefore does not require the application to infer the task from the complete transcript. The application still owns tool permissions and execution, but receives the function name and arguments directly from the managed Responses flow. In this spike, the client and Responses branches are separate provider choices so their behavior can be compared independently.

## First Live Session: Observed 2026-09-10

The first paid WebSocket session reached `session.started` successfully after approximately **888 ms**. The server returned model `gpt-live-1`, voice `marin`, client delegation, and 24 kHz PCM audio. The initial implementation had serialized empty history as `session.input: null`; the API rejected that with `invalid_type`. Omitting the field fixed startup on the next run.

The session ran for **124 seconds** and ended with `session.closed` reason `close_requested`. It emitted output audio continuously in roughly 100 ms cadence while output transcript fragments arrived independently. The transcript fragments were commonly short words or partial phrases, carried 200 ms session intervals, and appeared while new input transcript fragments were still arriving. This confirmed full-duplex overlap in the transport; subjective echo, backchannel quality, and whether every result was heard remain listening-test questions.

An additional interactive test showed an important difference from providers where text transcription runs ahead of buffered speech: GPT-Live assistant transcript appeared approximately in sync with the assistant voice. When the user interrupted a count at **five**, Live stopped the spoken response, retained the useful transcript state, and correctly answered that the stopping point was five. This suggests the Live transcript is a useful near-real-time indicator of what has actually been spoken during barge-in, not merely an early prediction of buffered output. This is an observed listening result, not a protocol guarantee: transcript deltas still have no item ID, authoritative turn-complete event, or output-audio completion event.

The Live model can also be instructed to begin replies with an emoji. In the observed behavior, the leading emoji was not spoken aloud, while remaining available in the assistant response stream for the application to use as a UI/status signal, such as updating the listener emoji. This offers a lower-latency alternative to spending a tool call solely on `SetListenerEmoji`; the exact parsing and lifecycle rules still need to be defined before production use.

The same interactive testing showed that GPT-Live may emit a short spoken backchannel such as **“Hmm”** before or during delegation. Strong system and backend instructions to remain silent before tool work did not reliably suppress it. This is expected to remain a model behavior while GPT-Live owns the conversational speech stream; suppressing it deterministically would require buffering or filtering generated audio, which would trade away the low-latency behavior being evaluated.

Client delegation was triggered repeatedly for benign requests. The events contained opaque IDs such as `item_...` and metadata only; the task wording was reconstructed from separately arriving transcript fragments. The runner returned `session.thinking.append` and `session.commentary.append`, whose observed acknowledgments were approximately **575-595 ms**. The model spoke acknowledgments and later paraphrased backend-oriented commentary, but this first run did not produce a verified sandbox tool completion because the conversation continued through explanatory turns and the session was closed before a clean tool/result cycle.

The observed usage snapshots were cumulative (`58`, `73`, `88`, `104`, `119` seconds in the captured output) and the terminal snapshot was `124` seconds. The CLI initially printed the previous snapshot because diagnostics were emitted before state application; that ordering is now fixed so future `session.closed` logs report final usage and close reason. The first run also showed that application response-latency display is heuristic: without an authoritative Live turn-complete event, the existing CLI updated its speech-end marker on transcript fragments and displayed several latency values for one continuing exchange. Production metrics must calculate first-useful-audio latency from explicit application segment boundaries.

No mute, playback-stop, slow-tool billing comparison, close/resume, Responses delegation, or human-controlled echo/interruption test was completed in this session.

## Delegation Mode Smoke Tests: Observed 2026-09-10

These were bounded connection/configuration checks, not tool-behavior or listening tests. The Responses smoke test below used an earlier explicit `gpt-5-mini` override; current defaults use `gpt-5.6-luna` with low reasoning.

### Client delegation

- Command: `GPT_LIVE_DELEGATION=client`, `SPEECH_LAB_PROVIDER=gpt-live`, `SPEECH_LAB_LIVE_EVENTS=safe`.
- Live startup accepted `delegation.type: client` and returned `session.started` in approximately **601 ms**.
- Session `live_u0_EMfR5MaOS9uwm65nGiAZe` closed with `close_requested`; final usage was **16 seconds**.
- No user speech or delegation was attempted in this smoke test.

### Responses delegation

- Command: `GPT_LIVE_DELEGATION=responses`, `GPT_LIVE_RESPONSES_MODEL=gpt-5-mini`, `GPT_LIVE_RESPONSES_REASONING=low`, `SPEECH_LAB_PROVIDER=gpt-live`, `SPEECH_LAB_LIVE_EVENTS=safe`.
- Live startup accepted `delegation.type: responses` and returned `session.started` in approximately **753 ms**.
- The startup payload confirmed the explicitly configured `gpt-5-mini` backend model, concise backend instructions, all **12** discovered function schemas, `tool_choice: auto`, and `parallel_tool_calls: true`. This was before the current per-mode default tool filtering; current sessions expose **11** tools by default because `UserTextTranscription` is opt-in.
- Session `live_u1_EMfRdDbG6GStqZwcnP91D` closed with `close_requested`; final usage was **18 seconds**.
- No user speech or function call was attempted in this smoke test, so nested `response.output_item.done`, local execution, `response.item.create`, and `response.create` remain pending live verification.

## Evidence Ledger

| Scenario | Status | Evidence |
|---|---|---|
| Build the existing experiment with GPT-Live adapter | **Observed locally** | `dotnet build experiments/DotnetRealtimeSpeechLab/DotnetRealtimeSpeechLab.csproj --no-restore` succeeds. |
| Event payload, transcript context, usage snapshot, cancellation, late result, redaction checks | **Observed locally** | `dotnet run --project experiments/DotnetRealtimeSpeechLab/DotnetRealtimeSpeechLab.csproj -- --self-test` passes. |
| Authentication and `session.started` | **Observed with Live** | First session authenticated and started in approximately 888 ms. |
| Startup latency, audio pacing, silence behavior, graceful close, final usage | **Partially observed with Live** | Startup and graceful close measured; final usage was 124 seconds. Silence/mute comparison remains pending. |
| Duplex overlap, backchannels, echo, interruption, useful-answer latency | **Partially observed with Live** | Overlapping input/output event delivery observed. In an interactive barge-in test, assistant transcript tracked spoken output closely enough for GPT-Live to identify that the user stopped a count at five; echo, backchannel quality, and formal latency measurement remain pending. |
| Transcript fragmentation, timing, delayed delivery, overlap | **Observed with Live** | Short 200 ms timestamped fragments arrived independently while both speakers were active. |
| Client delegation trigger and actual result speech | **Partially observed with Live** | Delegation IDs and append acknowledgments observed; clean verified sandbox completion remains pending. |
| Slow sandbox tool, cancellation, stale result suppression | **Observed locally** | State and cancellation logic are deterministic; Live arrival and spoken result remain untested. |
| Silence, mute, and slow-tool billing comparison | **Observed with Live** | Usage snapshots exist, but controlled comparison was not completed. No billing pause is assumed. |
| Close/resume and work finishing while closed | **Hypothesis / implementation ready** | Backend task state is retained independently and stale results are suppressed; real resume reconciliation is unmeasured. |
| 8,192-token startup history and 128-message limit | **Documented** | The Live conversations guide states these limits; this spike does not attempt a paid boundary test. |
| Responses delegation session configuration | **Observed with Live** | Live accepted `delegation.type: responses`, the explicitly configured `gpt-5-mini`, and all 12 discovered function schemas; startup and close were clean. Current default configuration is Luna/low reasoning with 11 active tools unless the mode setting opts in the diagnostic tool. |
| Responses delegation tool execution | **Observed locally / Live pending** | Nested event batching, local execution, deduplication, result submission, and continuation are implemented and self-tested by protocol shape; a real spoken function call remains pending. |
| Client backend model | **Observed locally / Live pending** | Client delegation now calls the application-owned Responses backend with `gpt-5.6-luna` and low reasoning, then executes returned tools locally; a live client-mode tool call remains pending. |

## Reproducible Local Checks

```powershell
dotnet build DotnetRealtimeSpeechLab.csproj --no-restore
dotnet run -- --self-test
```

For a future Live run, set `OPENAI_API_KEY` in the process environment without putting it in chat, source, or diagnostic files. Then use:

```powershell
$env:SPEECH_LAB_PROVIDER = "gpt-live"
$env:SPEECH_LAB_LIVE_EVENTS = "safe"
dotnet run
```

The CLI has separate `M` microphone mute, `P` playback stop/resume, `X` backend cancellation, and `Q` graceful close controls. `SPEECH_LAB_LIVE_EVENTS=full` is an intentional transcript-content inspection mode; the default is summary-only. The provider screen exposes GPT-Live Client and GPT-Live Responses as separate choices, and the `V` voice picker is available for both branches.

## Paid-Test Gate

The recorded first session and the two delegation startup smoke tests were paid Live tests. Before any additional test, agree a spend cap. At the documented voice rate, a 3-minute session is approximately $0.15 of voice cost before backend usage; backend model/tool calls are additional. A bounded follow-up should use at most three sessions of three minutes each, close every session explicitly, and stop after the first authentication or protocol failure. The test operator should perform the actual speaker/microphone checks for overlap, interruption, echo, backchannels, and whether a delegated result was really heard.

Suggested order after approval:

1. Connect each mode, speak one short benign question, verify `session.started`, first useful transcript, first audio, and `session.closed` final usage.
2. In Responses mode, say **"What is 25 plus 17?"** and verify a nested function call with `name: Add`, arguments `a: 25`, `b: 17`, one local tool execution, `response.item.create`, `response.create`, and a spoken answer of 42.
3. In client mode, repeat the calculation and verify the application-owned backend path, delegation ID preservation, and one local tool execution.
4. Compare a short speaking interval, silence, local mute, and a 1.5-second slow sandbox task using usage snapshots. Do not infer a pause from omitted frames.
5. Interrupt a harmless delegated calculation with a correction, cancel it, close during work, then resume with saved context. Verify delegation IDs, task revisions, no duplicate tool action, and actual playback separately.

## Limitations and Unanswered Questions

- No natural microphone, speaker, echo, overlap, backchannel, or interruption result is claimed until a human listens and speaks through the CLI.
- The client backend is an experiment-only Responses adapter, not a production application service or application manager. Production integration remains out of scope.
- The Responses path now uses Live-managed Responses delegation and needs a real spoken tool-call test. The account accepted its session configuration, but nested function-call delivery and continuation remain unverified with live audio.
- WebRTC and sideband were researched but not implemented because the server-side CLI's WebSocket path is the cheapest way to answer the initial protocol questions. A browser WebRTC comparison remains a production design question.
- The Live protocol's absence of authoritative transcript turn completion and output-audio-done means any UI turn grouping and playback completion signal will remain application-level heuristics.

## Production Recommendation

1. **Do not integrate directly from this spike yet.** First collect the bounded Live measurements above, especially useful spoken-answer latency, interruption behavior, actual delegation result delivery, and voice usage during silence/mute/backend waits.
2. **Prefer client delegation for an existing application architecture** if the requirement is exact ownership of context, permissions, confirmations, task revisions, and application services. Keep delegation IDs separate from durable operation IDs and reconcile uncertain outcomes before retrying.
3. **Use Responses delegation only for workflows that fit its managed context and tool contract.** It reduces connection plumbing but gives the host application less control over context and result review.
4. **For a browser production path, evaluate WebRTC with server-side controls or sideband** after the WebSocket evidence is complete. Keep credentials server-side, keep audio playback state separate from Live events, and finalize usage only from `session.closed`.
5. **Preserve the existing Realtime and Gemini providers during rollout.** Add Live behind an explicit provider capability and feature flag until the human audio and billing evidence supports a production decision.