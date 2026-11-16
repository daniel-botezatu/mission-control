namespace MissionControl.Core;

/// <summary>Managed scope synchronization is permitted only in a dedicated bridge store.</summary>
public static class AlexaStoreOwnership
{
    public const string Marker = ".alexa-bridge-owned";
    public const string Version = "alexa-bridge-store-v1";

    public static void Require(string dataDirectory)
    {
        var root = Path.GetFullPath(dataDirectory);
        for (string? path = Path.Combine(root, "live", "workspace.db"); path is not null; path = Path.GetDirectoryName(path))
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("BRIDGE_STORE_REPARSE_PATH");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        var marker = Path.Combine(root, Marker);
        if (File.Exists(marker))
        {
            if ((File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0 || new FileInfo(marker).Length > 100
                || File.ReadAllText(marker).Trim() != Version)
                throw new ArgumentException("INVALID_BRIDGE_STORE_MARKER");
            return;
        }
        if (File.Exists(Path.Combine(root, "live", "workspace.db")))
            throw new ArgumentException("DEDICATED_BRIDGE_STORE_REQUIRED");
        Directory.CreateDirectory(root);
        using var file = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(file);
        writer.Write(Version);
    }
}
