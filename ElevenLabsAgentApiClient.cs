using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace DotnetRealtimeSpeechLab;

public sealed class ElevenLabsAgentApiClient : IDisposable
{
    private readonly HttpClient _httpClient;

    public ElevenLabsAgentApiClient(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("ElevenLabs API key is required", nameof(apiKey));

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.elevenlabs.io")
        };

        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.DefaultRequestHeaders.Add("xi-api-key", apiKey);
    }

    public async Task<ElevenLabsAgentUpsertResult> UpsertAgentAsync(
        string systemPrompt,
        string? existingAgentId,
        string? agentName,
        string? voiceId,
        IList<AIFunction>? aiFunctions = null,
        CancellationToken cancellationToken = default)
    {
        var payload = BuildAgentPayload(systemPrompt, agentName, voiceId, aiFunctions);
        var candidateAgentId = existingAgentId;

        if (string.IsNullOrWhiteSpace(candidateAgentId) && !string.IsNullOrWhiteSpace(agentName))
        {
            candidateAgentId = await FindAgentIdByNameAsync(agentName, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(candidateAgentId))
        {
            var updateResponse = await SendPatchAsync($"/v1/convai/agents/{candidateAgentId}", payload, cancellationToken);
            if (updateResponse.IsSuccessStatusCode)
            {
                var fallbackUsed = !string.IsNullOrWhiteSpace(existingAgentId)
                    && !string.Equals(existingAgentId, candidateAgentId, StringComparison.Ordinal);

                return new ElevenLabsAgentUpsertResult(candidateAgentId, AgentUpsertOperation.Update, fallbackUsed);
            }

            if (updateResponse.StatusCode != HttpStatusCode.NotFound)
            {
                var errorBody = await updateResponse.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException($"ElevenLabs agent update failed ({(int)updateResponse.StatusCode}): {errorBody}");
            }
        }

        var createResponse = await _httpClient.PostAsync(
            "/v1/convai/agents/create",
            new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
            cancellationToken);

        var createBody = await createResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!createResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"ElevenLabs agent create failed ({(int)createResponse.StatusCode}): {createBody}");
        }

        var createJson = JsonNode.Parse(createBody)?.AsObject();
        var createdAgentId = createJson?["agent_id"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(createdAgentId))
        {
            throw new InvalidOperationException("ElevenLabs create response did not include agent_id.");
        }

        return new ElevenLabsAgentUpsertResult(
            createdAgentId,
            AgentUpsertOperation.Create,
            !string.IsNullOrWhiteSpace(existingAgentId));
    }

    private async Task<string?> FindAgentIdByNameAsync(string agentName, CancellationToken cancellationToken)
    {
        // Best-effort lookup to avoid creating duplicate agents across runs.
        // The response shape may vary by API version, so parse defensively.
        var response = await _httpClient.GetAsync("/v1/convai/agents?page_size=100", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var root = JsonNode.Parse(body);
        if (root == null)
        {
            return null;
        }

        static IEnumerable<JsonObject> EnumerateAgents(JsonNode node)
        {
            if (node is JsonArray array)
            {
                foreach (var item in array)
                {
                    if (item is JsonObject obj)
                        yield return obj;
                }

                yield break;
            }

            if (node is JsonObject objNode)
            {
                JsonNode? candidates = objNode["agents"]
                    ?? objNode["items"]
                    ?? objNode["data"]
                    ?? objNode["results"];

                if (candidates is JsonArray list)
                {
                    foreach (var item in list)
                    {
                        if (item is JsonObject obj)
                            yield return obj;
                    }
                }
            }
        }

        foreach (var agent in EnumerateAgents(root))
        {
            var name = agent["name"]?.GetValue<string>();
            if (!string.Equals(name, agentName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = agent["agent_id"]?.GetValue<string>()
                ?? agent["agentId"]?.GetValue<string>()
                ?? agent["id"]?.GetValue<string>();

            if (!string.IsNullOrWhiteSpace(id))
            {
                return id;
            }
        }

        return null;
    }

    public async Task<string> GetSignedConversationUrlAsync(string agentId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            throw new ArgumentException("Agent ID is required", nameof(agentId));

        var response = await _httpClient.GetAsync(
            $"/v1/convai/conversation/get-signed-url?agent_id={Uri.EscapeDataString(agentId)}",
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"ElevenLabs signed URL request failed ({(int)response.StatusCode}): {body}");
        }

        var json = JsonNode.Parse(body)?.AsObject();
        var signedUrl = json?["signed_url"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(signedUrl))
        {
            throw new InvalidOperationException("ElevenLabs signed URL response did not include signed_url.");
        }

        return signedUrl;
    }

    private async Task<HttpResponseMessage> SendPatchAsync(string path, JsonObject payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, path)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };

        return await _httpClient.SendAsync(request, cancellationToken);
    }

    private static JsonObject BuildAgentPayload(
        string systemPrompt,
        string? agentName,
        string? voiceId,
        IList<AIFunction>? aiFunctions)
    {
        var promptText = string.IsNullOrWhiteSpace(systemPrompt)
            ? "You are a helpful and concise voice assistant."
            : systemPrompt;

        var tts = new JsonObject
        {
            ["model_id"] = "eleven_flash_v2",
            ["agent_output_audio_format"] = "pcm_16000",
            ["optimize_streaming_latency"] = 3
        };

        if (!string.IsNullOrWhiteSpace(voiceId))
        {
            tts["voice_id"] = voiceId;
        }

        var payload = new JsonObject
        {
            ["conversation_config"] = new JsonObject
            {
                ["asr"] = new JsonObject
                {
                    // ElevenLabs Original ASR was removed; Scribe v2 Realtime is current.
                    ["provider"] = "scribe_realtime",
                    ["quality"] = "high",
                    ["user_input_audio_format"] = "pcm_16000"
                },
                ["turn"] = new JsonObject
                {
                    // Use the documented/default turn behavior rather than eagerly
                    // responding to short pauses as empty user turns.
                    ["turn_timeout"] = 7.0,
                    ["initial_wait_time"] = 2.0,
                    ["silence_end_call_timeout"] = -1,
                    ["turn_eagerness"] = "normal",
                    ["speculative_turn"] = false
                },
                ["tts"] = tts,
                ["conversation"] = new JsonObject
                {
                    ["max_duration_seconds"] = 1800,
                    ["client_events"] = new JsonArray(
                        "audio",
                        "interruption",
                        "user_transcript",
                        "agent_response",
                        "agent_response_correction")
                },
                ["agent"] = new JsonObject
                {
                    ["language"] = "en",
                    ["first_message"] = "Hey! Ready when you are.",
                    ["prompt"] = new JsonObject
                    {
                        ["prompt"] = promptText,
                        ["temperature"] = 0.2,
                        ["max_tokens"] = 256,
                        ["tools"] = BuildClientTools(aiFunctions)
                    }
                }
            }
        };

        if (!string.IsNullOrWhiteSpace(agentName))
        {
            payload["name"] = agentName;
        }

        return payload;
    }

    private static JsonArray BuildClientTools(IList<AIFunction>? aiFunctions)
    {
        var tools = new JsonArray();
        if (aiFunctions == null)
        {
            return tools;
        }

        foreach (var aiFunction in aiFunctions)
        {
            var toolOptions = ResolveToolOptions(aiFunction.Name);

            var tool = new JsonObject
            {
                ["type"] = "client",
                ["name"] = aiFunction.Name,
                ["description"] = aiFunction.Description ?? $"Executes {aiFunction.Name}",
                ["expects_response"] = toolOptions.ExpectsResponse,
                ["response_timeout_secs"] = 10,
                ["force_pre_tool_speech"] = toolOptions.ForcePreToolSpeech
            };

            var schema = aiFunction.JsonSchema;
            if (schema.ValueKind != JsonValueKind.Undefined && schema.ValueKind != JsonValueKind.Null)
            {
                tool["parameters"] = JsonNode.Parse(schema.GetRawText());
            }

            tools.Add(tool);
        }

        return tools;
    }

    private static ElevenLabsToolOptionsAttribute ResolveToolOptions(string functionName)
    {
        var method = typeof(CliTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => string.Equals(m.Name, functionName, StringComparison.OrdinalIgnoreCase));

        return method?.GetCustomAttribute<ElevenLabsToolOptionsAttribute>()
            ?? new ElevenLabsToolOptionsAttribute();
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

public enum AgentUpsertOperation
{
    Create,
    Update
}

public sealed record ElevenLabsAgentUpsertResult(string AgentId, AgentUpsertOperation Operation, bool FallbackUsed);
