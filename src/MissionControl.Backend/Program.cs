namespace MissionControl.Backend;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            var options = BackendOptions.Parse(args);
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
                throw new PlatformNotSupportedException("The desktop backend currently supports Windows and macOS.");
            var application = new BackendApplication(options);
            var protocol = new BackendProtocol(application, Console.OpenStandardInput(), Console.OpenStandardOutput());
            return await protocol.RunAsync();
        }
        catch (Exception error)
        {
            // Never put arguments, exception messages, source paths, or credentials on diagnostic output.
            await Console.Error.WriteLineAsync($"Mission Control backend could not start ({error.GetType().Name}).");
            return 1;
        }
    }
}
