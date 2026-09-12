using System.Text.Json;

namespace DotnetRealtimeSpeechLab;

public sealed class SpeechLabSettings
{
    public string? OpenAIVoice { get; set; }
    public string? GeminiVoice { get; set; }
    public string? GptLiveClientVoice { get; set; }
    public string? GptLiveResponsesVoice { get; set; }
    public string? LastProvider { get; set; }
    public List<string> UserTextTranscriptionModes { get; set; } = new();
}

public sealed class SpeechLabSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public SpeechLabSettingsStore(string? settingsPath = null)
    {
        SettingsPath = settingsPath ?? GetDefaultSettingsPath();
    }

    public string SettingsPath { get; }

    public SpeechLabSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new SpeechLabSettings();
            }

            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<SpeechLabSettings>(json, JsonOptions) ?? new SpeechLabSettings();
        }
        catch
        {
            return new SpeechLabSettings();
        }
    }

    public void Save(SpeechLabSettings settings)
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