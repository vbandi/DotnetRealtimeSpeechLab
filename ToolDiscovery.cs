using System.ComponentModel;
using System.Reflection;
using Microsoft.Extensions.AI;

namespace DotnetRealtimeSpeechLab;

/// <summary>
/// Utility class for discovering AI tools via reflection.
/// Tools are methods decorated with [Description] attribute.
/// </summary>
public static class ToolDiscovery
{
    /// <summary>
    /// Discovers all AI tools from an instance's public methods.
    /// A method is considered a tool if it has a [Description] attribute.
    /// </summary>
    public static IList<AIFunction> DiscoverTools(object instance) =>
        [.. instance.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttributes(typeof(DescriptionAttribute), false).Any())
            .Where(m => m.GetCustomAttributes(typeof(ObsoleteAttribute), false).Length == 0)
            .Select(m => AIFunctionFactory.Create(m, instance))];
}
