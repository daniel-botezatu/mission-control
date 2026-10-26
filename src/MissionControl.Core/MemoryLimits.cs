namespace MissionControl.Core;

/// <summary>Bounded retained human text; paging limits are independent of storage.</summary>
internal static class MemoryLimits
{
    public const int MessageCharacters = 4 * 1024 * 1024;
}
