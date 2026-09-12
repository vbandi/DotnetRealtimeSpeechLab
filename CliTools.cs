using System.ComponentModel;

namespace DotnetRealtimeSpeechLab;

/// <summary>
/// CLI tools exposed to the AI agent for testing fast reactions.
/// Methods with [Description] are auto-discovered as AI tools.
/// </summary>
public class CliTools
{
    private readonly List<string> _notes = new();
    private string _currentEmoji = "😊";
    private readonly Action<string> _onEmojiChanged;

    public CliTools(Action<string> onEmojiChanged)
    {
        _onEmojiChanged = onEmojiChanged;
    }

    public string CurrentEmoji => _currentEmoji;

    [Description("Records the assistant's understanding of what the user said.")]
    [ElevenLabsToolOptions(ExpectsResponse = false)]
    public Task<string> UserTextTranscription(
        [Description("Your transcription/understanding of what the user just said")] string transcription)
    {
        // Optional diagnostic tool. Provider/model policy decides whether it is exposed.
        return Task.FromResult($"Understood: {transcription}");
    }

    [Description("Adds two numbers together. Use this for any math calculation.")]
    [ElevenLabsToolOptions(ForcePreToolSpeech = true)]
    public Task<string> Add(
        [Description("First number")] double a,
        [Description("Second number")] double b)
    {
        var result = a + b;
        return Task.FromResult($"{result}");
    }

    [Description("Subtracts the second number from the first.")]
    public Task<string> Subtract(
        [Description("First number")] double a,
        [Description("Second number")] double b)
    {
        var result = a - b;
        return Task.FromResult($"{result}");
    }

    [Description("Multiplies two numbers.")]
    public Task<string> Multiply(
        [Description("First number")] double a,
        [Description("Second number")] double b)
    {
        var result = a * b;
        return Task.FromResult($"{result}");
    }

    [Description("Divides the first number by the second.")]
    public Task<string> Divide(
        [Description("First number (dividend)")] double a,
        [Description("Second number (divisor)")] double b)
    {
        if (b == 0)
            return Task.FromResult("Error: Cannot divide by zero");
        var result = a / b;
        return Task.FromResult($"{result}");
    }

    [Description("Gets the current time in HH:mm:ss format.")]
    public Task<string> GetCurrentTime()
    {
        return Task.FromResult(DateTime.Now.ToString("HH:mm:ss"));
    }

    [Description("Gets the current date.")]
    public Task<string> GetCurrentDate()
    {
        return Task.FromResult(DateTime.Now.ToString("yyyy-MM-dd dddd"));
    }

    [Description("Saves a note to memory. Returns the note number.")]
    public Task<string> SaveNote(
        [Description("The note content to save")] string content)
    {
        _notes.Add(content);
        return Task.FromResult($"Note #{_notes.Count} saved: \"{content}\"");
    }

    [Description("Lists all saved notes.")]
    public Task<string> ListNotes()
    {
        if (_notes.Count == 0)
            return Task.FromResult("No notes saved yet.");

        var list = string.Join("\n", _notes.Select((n, i) => $"  {i + 1}. {n}"));
        return Task.FromResult($"Notes:\n{list}");
    }

    [Description("Clears all saved notes.")]
    public Task<string> ClearNotes()
    {
        var count = _notes.Count;
        _notes.Clear();
        return Task.FromResult($"Cleared {count} notes.");
    }

    [Description("Sets the listener emoji to reflect your mood or state. Use emojis like 😊 😎 🤔 😴 🎉 etc.")]
    [ElevenLabsToolOptions(ExpectsResponse = false)]
    public Task<string> SetListenerEmoji(
        [Description("The emoji to display (e.g., 😊, 🤔, 🎉)")] string emoji)
    {
        _currentEmoji = emoji;
        _onEmojiChanged(emoji);
        return Task.FromResult($"Emoji set to {emoji}");
    }

    [Description("Generates a random number between min and max (inclusive).")]
    public Task<string> RandomNumber(
        [Description("Minimum value")] int min,
        [Description("Maximum value")] int max)
    {
        var random = new Random();
        var result = random.Next(min, max + 1);
        return Task.FromResult($"{result}");
    }
}
