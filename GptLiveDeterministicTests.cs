using System.Text.Json.Nodes;

namespace DotnetRealtimeSpeechLab;

public static class GptLiveDeterministicTests
{
    public static void RunAll()
    {
        SessionStartContainsLiveContract();
        SessionStartContainsResponsesContract();
        ResponsesCommandsContainLiveToolFlow();
        ContextKeepsFragmentTiming();
        LatestSpeakerTextExcludesContextTimestamps();
        UsageSnapshotsAreNotSummed();
        CanceledDelegationRejectsLateResult();
        SafePayloadDoesNotExposeAudioOrTranscriptDelta();
    }

    private static void SessionStartContainsLiveContract()
    {
        var json = JsonNode.Parse(GptLiveProtocol.BuildSessionStart(
            "gpt-live-1",
            "short prompt",
            "marin",
            [("user", "hello")]))!.AsObject();

        Assert(json["type"]?.GetValue<string>() == "session.start", "session.start type missing");
        Assert(json["session"]?["model"]?.GetValue<string>() == "gpt-live-1", "Live model missing");
        Assert(json["session"]?["audio"]?["format"]?["rate"]?.GetValue<int>() == 24000, "24 kHz audio missing");
        Assert(json["session"]?["delegation"]?["type"]?.GetValue<string>() == "client", "client delegation missing");
    }

    private static void ContextKeepsFragmentTiming()
    {
        var context = GptLiveProtocol.BuildContext(
        [
            new GptLiveTranscriptFragment("user", "what is", 10, 40, 1),
            new GptLiveTranscriptFragment("user", " two plus two", 40, 90, 2),
            new GptLiveTranscriptFragment("assistant", "Four", 100, 140, 3)
        ]);

        Assert(context.Contains("[10-40ms] user: what is"), "first fragment timing was lost");
        Assert(context.Contains("[40-90ms] user:  two plus two"), "fragment spacing was changed");
        Assert(context.Contains("[100-140ms] assistant: Four"), "assistant fragment missing");
    }

    private static void SessionStartContainsResponsesContract()
    {
        var tools = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "function",
                ["name"] = "Add",
                ["description"] = "Adds two numbers",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject()
                }
            }
        };
        var json = JsonNode.Parse(GptLiveProtocol.BuildSessionStart(
            "gpt-live-1",
            "voice prompt",
            "marin",
            [],
            GptLiveDelegationMode.Responses,
            "gpt-5.6-luna",
            "backend prompt",
            tools,
            "low"))!.AsObject();

        var delegation = json["session"]?["delegation"];
        Assert(delegation?["type"]?.GetValue<string>() == "responses", "Responses delegation missing");
        Assert(delegation?["responses"]?["model"]?.GetValue<string>() == "gpt-5.6-luna", "Responses backend model missing");
        Assert(delegation?["responses"]?["reasoning"]?["effort"]?.GetValue<string>() == "low", "Responses reasoning effort missing");
        Assert(delegation?["responses"]?["tools"]?[0]?["name"]?.GetValue<string>() == "Add", "Responses tool schema missing");
    }

    private static void ResponsesCommandsContainLiveToolFlow()
    {
        var result = JsonNode.Parse(GptLiveProtocol.BuildResponsesToolResult("call_123", "42"))!.AsObject();
        var continuation = JsonNode.Parse(GptLiveProtocol.BuildResponsesContinue())!.AsObject();

        Assert(result["type"]?.GetValue<string>() == "response.item.create", "Responses result command type missing");
        Assert(result["item"]?["type"]?.GetValue<string>() == "function_call_output", "Responses result item type missing");
        Assert(result["item"]?["call_id"]?.GetValue<string>() == "call_123", "Responses call ID missing");
        Assert(continuation["type"]?.GetValue<string>() == "response.create", "Responses continuation command missing");
    }

    private static void UsageSnapshotsAreNotSummed()
    {
        var tracker = new GptLiveUsageTracker();
        tracker.Apply(new GptLiveUsageSnapshot(2, 0.1));
        tracker.Apply(new GptLiveUsageSnapshot(7, 0.2));

        Assert(tracker.LatestSeconds == 7, "usage snapshots were summed instead of replaced");
        Assert(tracker.SnapshotCount == 2, "usage snapshot count is incorrect");
    }

    private static void LatestSpeakerTextExcludesContextTimestamps()
    {
        var text = GptLiveProtocol.BuildLatestSpeakerText(
        [
            new GptLiveTranscriptFragment("assistant", "I am checking", 100, 300, 1),
            new GptLiveTranscriptFragment("user", "What is ", 400, 600, 2),
            new GptLiveTranscriptFragment("user", "25 plus 17", 600, 900, 3)
        ], "user");

        Assert(text == "What is 25 plus 17", "latest user text was not assembled correctly");
        Assert(!text.Contains("400") && !text.Contains("600") && !text.Contains("900"), "context timestamps leaked into user text");
    }

    private static void CanceledDelegationRejectsLateResult()
    {
        var state = new GptLiveDelegationState();
        var revision = state.Begin("item_test");
        state.Cancel("item_test");

        Assert(!state.AcceptResult("item_test", revision), "canceled result was accepted");
        var replacementRevision = state.Begin("item_test");
        Assert(state.AcceptResult("item_test", replacementRevision), "replacement result was rejected");
        Assert(!state.AcceptResult("item_test", revision), "stale result was accepted");
    }

    private static void SafePayloadDoesNotExposeAudioOrTranscriptDelta()
    {
        var safe = GptLiveProtocol.BuildSafePayload(new JsonObject
        {
            ["type"] = "session.output_audio.delta",
            ["audio"] = "base64-audio",
            ["delta"] = "private transcript",
            ["event_id"] = "event_test"
        });

        Assert(!safe.Contains("base64-audio"), "audio was included in safe diagnostics");
        Assert(!safe.Contains("private transcript"), "transcript delta was included in safe diagnostics");
        Assert(safe.Contains("event_test"), "non-sensitive event metadata was removed");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}