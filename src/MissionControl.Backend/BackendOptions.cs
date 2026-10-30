namespace MissionControl.Backend;

internal sealed record BackendOptions(string DataDirectory, string? ShellPath)
{
    public static BackendOptions Parse(string[] args)
    {
        var data = MissionControl.Core.WorkspacePaths.DefaultDataDirectory();
        string? shell = null;
        for (var index = 0; index < args.Length; index++)
        {
            string Value() => ++index < args.Length ? args[index] : throw new ArgumentException("Missing backend option value.");
            switch (args[index])
            {
                case "--data-dir": data = AbsolutePath(Value()); break;
                case "--shell-path": shell = AbsolutePath(Value()); break;
                default: throw new ArgumentException("Unknown backend option.");
            }
        }
        return new BackendOptions(Path.TrimEndingDirectorySeparator(Path.GetFullPath(data)), shell);
    }

    private static string AbsolutePath(string value)
    {
        if (!Path.IsPathFullyQualified(value) || value.Any(char.IsControl))
            throw new ArgumentException("Backend paths must be absolute and contain no control characters.");
        return Path.GetFullPath(value);
    }
}
