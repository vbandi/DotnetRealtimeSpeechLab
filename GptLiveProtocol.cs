using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;

namespace DotnetRealtimeSpeechLab;

public enum GptLiveDelegationMode
{
    Client,
    Responses
}

public sealed record GptLiveTranscriptFragment(
    string Speaker,
    string Delta,
    long? StartMs,
    long? EndMs,
    long ReceivedMonotonicMs);

public sealed record GptLiveEventInfo(
    string Type,
    string? EventId,
    string? SessionId,
    string? DelegationId,
    long MonotonicMs,
    string? SafePayload,
    long? AcknowledgmentLatencyMs);

public sealed record GptLiveDelegationRequestInfo(
    GptLiveDelegationMode Mode,
    string Stage,
    string Json);

public sealed record GptLiveUsageSnapshot(double Seconds, double? ContextUsageRatio);

public sealed class GptLiveUsageTracker
{
    public double? LatestSeconds { get; private set; }
    public double? ContextUsageRatio { get; private set; }
    public int SnapshotCount { get; private set; }

    public void Apply(GptLiveUsageSnapshot snapshot)
    {
        LatestSeconds = snapshot.Seconds;
        ContextUsageRatio = snapshot.ContextUsageRatio;
        SnapshotCount++;
    }
}

public sealed class GptLiveDelegationState
{
    private readonly Dictionary<string, int> _revisions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _cancelled = new(StringComparer.Ordinal);
    private int _nextRevision;

    public int Begin(string delegationId)
    {
        var revision = ++_nextRevision;
        _revisions[delegationId] = revision;
        _cancelled.Remove(delegationId);
        return revision;
    }

    public void Cancel(string delegationId)
    {
        _cancelled.Add(delegationId);
    }

    public bool AcceptResult(string delegationId, int revision)
    {
        return _revisions.TryGetValue(delegationId, out var currentRevision)
            && currentRevision == revision
            && !_cancelled.Contains(delegationId);
    }
}

public static class GptLiveProtocol
{
    public static string BuildSessionStart(
        string model,
        string instructions,
        string voice,
        IReadOnlyList<(string Role, string Text)> history,
        GptLiveDelegationMode delegationMode = GptLiveDelegationMode.Client,
        string? responsesModel = null,
        string? responsesInstructions = null,
        JsonArray? responseTools = null,
        string? responsesReasoningEffort = null)
    {
        var input = history
            .Where(item => !string.IsNullOrWhiteSpace(item.Text))
            .TakeLast(128)
            .Select(item => new
            {
                type = "message",
                role = item.Role is "assistant" or "developer" ? item.Role : "user",
                content = new[]
                {
                    new
                    {
                        type = item.Role == "assistant" ? "output_text" : "input_text",
                        text = item.Text
                    }
                }
            })
            .ToArray();

        var delegation = new JsonObject
        {
            ["type"] = delegationMode == GptLiveDelegationMode.Responses ? "responses" : "client"
        };
        if (delegationMode == GptLiveDelegationMode.Responses)
        {
            delegation["responses"] = new JsonObject
            {
                ["model"] = responsesModel ?? "gpt-5-mini",
                ["instructions"] = responsesInstructions ?? instructions,
                ["tools"] = responseTools ?? new JsonArray(),
                ["tool_choice"] = "auto",
                ["parallel_tool_calls"] = true,
                ["reasoning"] = new JsonObject
                {
                    ["effort"] = responsesReasoningEffort ?? "low"
                }
            };
        }

        var session = new JsonObject
        {
            ["model"] = model,
            ["instructions"] = instructions,
            ["audio"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "audio/pcm",
                    ["rate"] = 24000
                },
                ["output"] = new JsonObject
                {
                    ["voice"] = voice
                }
            },
            ["delegation"] = delegation
        };

        if (input.Length > 0)
        {
            session["input"] = JsonSerializer.SerializeToNode(input);
        }

        return JsonSerializer.Serialize(new
        {
            type = "session.start",
            event_id = "fastvoice_session_start",
            session
        });
    }

    public static string BuildAudioAppend(byte[] pcm16)
    {
        return JsonSerializer.Serialize(new
        {
            type = "session.input_audio.append",
            audio = Convert.ToBase64String(pcm16)
        });
    }

    public static string BuildInputMute(bool muted)
    {
        return JsonSerializer.Serialize(new
        {
            type = muted ? "session.input_audio.mute" : "session.input_audio.unmute",
            event_id = $"fastvoice_mute_{(muted ? "on" : "off")}_{Guid.NewGuid():N}"
        });
    }

    public static string BuildResponsesToolResult(string callId, string output)
    {
        return JsonSerializer.Serialize(new
        {
            type = "response.item.create",
            event_id = $"fastvoice_tool_result_{Guid.NewGuid():N}",
            item = new
            {
                type = "function_call_output",
                call_id = callId,
                output
            }
        });
    }

    public static string BuildResponsesContinue()
    {
        return JsonSerializer.Serialize(new
        {
            type = "response.create",
            event_id = $"fastvoice_response_continue_{Guid.NewGuid():N}"
        });
    }

    public static string BuildContext(IReadOnlyList<GptLiveTranscriptFragment> fragments, int maxCharacters = 12000)
    {
        var builder = new StringBuilder();
        foreach (var fragment in fragments.TakeLast(128))
        {
            var start = fragment.StartMs?.ToString() ?? "?";
            var end = fragment.EndMs?.ToString() ?? "?";
            builder.Append('[').Append(start).Append('-').Append(end).Append("ms] ")
                .Append(fragment.Speaker).Append(": ").AppendLine(fragment.Delta);
        }

        if (builder.Length <= maxCharacters)
        {
            return builder.ToString();
        }

        return builder.ToString()[^maxCharacters..];
    }

    public static string BuildLatestSpeakerText(
        IReadOnlyList<GptLiveTranscriptFragment> fragments,
        string speaker)
    {
        var builder = new StringBuilder();
        for (var index = fragments.Count - 1; index >= 0; index--)
        {
            var fragment = fragments[index];
            if (!string.Equals(fragment.Speaker, speaker, StringComparison.OrdinalIgnoreCase))
            {
                if (builder.Length > 0)
                {
                    break;
                }

                continue;
            }

            builder.Insert(0, fragment.Delta);
        }

        return builder.ToString();
    }

    public static bool TryReadUsage(JsonObject root, out GptLiveUsageSnapshot snapshot)
    {
        snapshot = new GptLiveUsageSnapshot(0, null);
        var usage = root["usage"] as JsonObject;
        if (usage == null || !usage.TryGetPropertyValue("seconds", out var secondsNode)
            || !double.TryParse(secondsNode?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        double? ratio = null;
        if (root["context_window"] is JsonObject context
            && double.TryParse(context["usage_ratio"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedRatio))
        {
            ratio = parsedRatio;
        }

        snapshot = new GptLiveUsageSnapshot(seconds, ratio);
        return true;
    }

    public static string BuildSafePayload(JsonObject root)
    {
        var safe = new JsonObject();
        foreach (var property in root)
        {
            if (property.Key is "audio" or "delta")
            {
                if (property.Value is JsonValue value && value.TryGetValue<string>(out var text))
                {
                    safe[property.Key] = $"[redacted:{text.Length}]";
                }

                continue;
            }

            if (property.Key == "event")
            {
                safe[property.Key] = "[nested event omitted]";
                continue;
            }

            safe[property.Key] = property.Value?.DeepClone();
        }

        return safe.ToJsonString();
    }

    public static string BuildFullPayload(JsonObject root)
    {
        var full = root.DeepClone().AsObject();
        if (full["type"]?.GetValue<string>() == "session.output_audio.delta")
        {
            var delta = full["delta"]?.GetValue<string>();
            full["delta"] = delta == null ? null : $"[audio-redacted:{delta.Length}]";
        }

        full.Remove("audio");
        return full.ToJsonString();
    }

    public static string? GetString(JsonObject root, string propertyName)
    {
        return root[propertyName]?.GetValue<string>();
    }
}