using System.Text.Json;

namespace DotnetRealtimeSpeechLab;

public sealed class FastVoiceSettings
{
    public string? OpenAIVoice { get; set; }
    public string? GeminiVoice { get; set; }
    public string? GptLiveClientVoice { get; set; }
    public string? GptLiveResponsesVoice { get; set; }
    public string? LastProvider { get; set; }
    public List<string> UserTextTranscriptionModes { get; set; } = new();
}

public sealed class FastVoiceSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public FastVoiceSettingsStore(string? settingsPath = null)
    {
        SettingsPath = settingsPath ?? GetDefaultSettingsPath();
    }

    public string SettingsPath { get; }

    public FastVoiceSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new FastVoiceSettings();
            }

            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<FastVoiceSettings>(json, JsonOptions) ?? new FastVoiceSettings();
        }
        catch
        {
            return new FastVoiceSettings();
        }
    }

    public void Save(FastVoiceSettings settings)
    {
        var directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsPath, json);
    }

    private static string GetDefaultSettingsPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DotnetRealtimeSpeechLab",
            "settings.json");
    }
}