#pragma warning disable OPENAI002

using System.Reflection;
using OpenAI.Realtime;

namespace DotnetRealtimeSpeechLab;

public sealed record VoiceOption(string Id, string DisplayName, string Description);

public static class VoiceCatalog
{
    private static readonly Lazy<IReadOnlyList<VoiceOption>> OpenAIVoices = new(CreateOpenAiVoices);

    private static readonly IReadOnlyList<VoiceOption> GeminiVoices =
    [
        new("Zephyr", "Zephyr", "Bright"),
        new("Puck", "Puck", "Upbeat"),
        new("Charon", "Charon", "Informative"),
        new("Kore", "Kore", "Firm"),
        new("Fenrir", "Fenrir", "Excitable"),
        new("Leda", "Leda", "Youthful"),
        new("Orus", "Orus", "Firm"),
        new("Aoede", "Aoede", "Breezy"),
        new("Callirrhoe", "Callirrhoe", "Easy-going"),
        new("Autonoe", "Autonoe", "Bright"),
        new("Enceladus", "Enceladus", "Breathy"),
        new("Iapetus", "Iapetus", "Clear"),
        new("Umbriel", "Umbriel", "Easy-going"),
        new("Algieba", "Algieba", "Smooth"),
        new("Despina", "Despina", "Smooth"),
        new("Erinome", "Erinome", "Clear"),
        new("Algenib", "Algenib", "Gravelly"),
        new("Rasalgethi", "Rasalgethi", "Informative"),
        new("Laomedeia", "Laomedeia", "Upbeat"),
        new("Achernar", "Achernar", "Soft"),
        new("Alnilam", "Alnilam", "Firm"),
        new("Schedar", "Schedar", "Even"),
        new("Gacrux", "Gacrux", "Mature"),
        new("Pulcherrima", "Pulcherrima", "Forward"),
        new("Achird", "Achird", "Friendly"),
        new("Zubenelgenubi", "Zubenelgenubi", "Casual"),
        new("Vindemiatrix", "Vindemiatrix", "Gentle"),
        new("Sadachbia", "Sadachbia", "Lively"),
        new("Sadaltager", "Sadaltager", "Knowledgeable"),
        new("Sulafat", "Sulafat", "Warm")
    ];

    // GPT-Live has its own BuiltInVoice contract. It includes several names
    // also accepted by Realtime, plus Live-specific voices; keep this catalog
    // independent so changes to the Realtime SDK cannot silently change Live.
    private static readonly IReadOnlyList<VoiceOption> GptLiveVoices =
    [
        new("marin", "Marin", "Default GPT-Live voice"),
        new("alloy", "Alloy", "Built-in GPT-Live voice"),
        new("ash", "Ash", "Built-in GPT-Live voice"),
        new("ballad", "Ballad", "Built-in GPT-Live voice"),
        new("beacon", "Beacon", "Filipino influence; generated"),
        new("bossa", "Bossa", "Brazilian Portuguese influence; feminine"),
        new("cedar", "Cedar", "Built-in GPT-Live voice"),
        new("cinder", "Cinder", "Southern U.S. influence; generated"),
        new("coral", "Coral", "Built-in GPT-Live voice"),
        new("delta", "Delta", "Southern U.S. influence; feminine"),
        new("echo", "Echo", "Built-in GPT-Live voice"),
        new("gleam", "Gleam", "North American influence; feminine"),
        new("meridian", "Meridian", "North American influence; masculine"),
        new("quartz", "Quartz", "Australian influence; feminine; generated"),
        new("ripple", "Ripple", "Australian influence; masculine"),
        new("sage", "Sage", "Built-in GPT-Live voice"),
        new("shimmer", "Shimmer", "Built-in GPT-Live voice"),
        new("stone", "Stone", "Irish influence; masculine"),
        new("tempo", "Tempo", "Brazilian Portuguese influence; masculine"),
        new("verse", "Verse", "Built-in GPT-Live voice"),
        new("vesper", "Vesper", "British influence; masculine"),
        new("willow", "Willow", "Irish influence; feminine")
    ];

    public static IReadOnlyList<VoiceOption> GetVoices(VoiceProvider provider)
    {
        return provider switch
        {
            VoiceProvider.OpenAI => OpenAIVoices.Value,
            VoiceProvider.GptLiveClient or VoiceProvider.GptLiveResponses => GptLiveVoices,
            VoiceProvider.Gemini => GeminiVoices,
            _ => Array.Empty<VoiceOption>()
        };
    }

    public static VoiceOption? FindVoice(VoiceProvider provider, string? voiceId)
    {
        if (string.IsNullOrWhiteSpace(voiceId))
        {
            return null;
        }

        return GetVoices(provider).FirstOrDefault(voice =>
            string.Equals(voice.Id, voiceId, StringComparison.OrdinalIgnoreCase));
    }

    public static string? NormalizeVoiceId(VoiceProvider provider, string? voiceId)
    {
        return FindVoice(provider, voiceId)?.Id;
    }

    private static IReadOnlyList<VoiceOption> CreateOpenAiVoices()
    {
        return typeof(RealtimeVoice)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(property => property.PropertyType == typeof(RealtimeVoice))
            .Select(property => property.GetValue(null)?.ToString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Select(value => new VoiceOption(
                value!,
                ToDisplayName(value!),
                "Built-in OpenAI Realtime voice"))
            .ToList();
    }

    private static string ToDisplayName(string voiceId)
    {
        if (string.IsNullOrWhiteSpace(voiceId))
        {
            return voiceId;
        }

        return char.ToUpperInvariant(voiceId[0]) + voiceId[1..];
    }
}