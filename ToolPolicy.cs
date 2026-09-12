using Microsoft.Extensions.AI;

namespace DotnetRealtimeSpeechLab;

/// <summary>
/// Chooses the tools exposed to a specific voice mode.
/// </summary>
internal static class ToolPolicy
{
    private const string UserTextTranscriptionToolName = "UserTextTranscription";

    public static IList<AIFunction> SelectForMode(
        string modeId,
        IEnumerable<AIFunction> discoveredTools,
        IEnumerable<string>? userTextTranscriptionModes)
    {
        ArgumentNullException.ThrowIfNull(modeId);
        ArgumentNullException.ThrowIfNull(discoveredTools);

        var allowlist = (userTextTranscriptionModes ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (allowlist.Contains(modeId))
        {
            return discoveredTools.ToList();
        }

        return discoveredTools
            .Where(tool => !string.Equals(tool.Name, UserTextTranscriptionToolName, StringComparison.Ordinal))
            .ToList();
    }
}