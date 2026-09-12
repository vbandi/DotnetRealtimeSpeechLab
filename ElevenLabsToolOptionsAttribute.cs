namespace DotnetRealtimeSpeechLab;

[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class ElevenLabsToolOptionsAttribute : Attribute
{
    public bool ExpectsResponse { get; set; } = true;
    public bool ForcePreToolSpeech { get; set; } = false;
}
