using System.Text.Json;

namespace MissionControl.Core;

/// <summary>Budget the single text representation, JSON escaping and framing.</summary>
public static class HistoryResponseSize
{
    public const int Default = 16000;
    public const int Maximum = 48000;
    public static int Characters(string json) => checked(JsonSerializer.Serialize(json).Length + 256);
}
