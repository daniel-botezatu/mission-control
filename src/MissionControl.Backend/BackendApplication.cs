using System.Text.Json;
using MissionControl.Core;

namespace MissionControl.Backend;

internal interface IBackendApplication : IAsyncDisposable
{
    event Action? Changed;
    Task InitializeAsync();
    Task<object?> DispatchAsync(string method, JsonElement args);
    void RequestStop();
}

internal sealed class BackendApplication : IBackendApplication
{
    private readonly AppController _controller;
    private readonly DesktopStartup _startup;
    public event Action? Changed;

    public BackendApplication(BackendOptions options)
    {
        _controller = new AppController(options.DataDirectory);
        _startup = new DesktopStartup(options.DataDirectory, options.ShellPath);
        _controller.SourceChanged += OnChanged;
    }

    public async Task InitializeAsync()
    {
        await _controller.InitializeAsync();
    }

    public async Task<object?> DispatchAsync(string method, JsonElement args)
    {
        if (method is "host.startup.get" or "startupEnabled") return new { enabled = _startup.Enabled };
        if (method == "host.startup.set")
        {
            var enabled = args.GetProperty("enabled").GetBoolean();
            _startup.SetEnabled(enabled);
            await _controller.DispatchAsync("saveSettings", JsonSerializer.SerializeToElement(new { startWithWindows = enabled }));
            return new { enabled = _startup.Enabled };
        }
        if (method == "saveSettings" && args.TryGetProperty("startWithWindows", out var startup))
            _startup.SetEnabled(startup.GetBoolean());
        var result = await _controller.DispatchAsync(method, args);
        if (method == "settings" && result is not null)
        {
            var settings = JsonSerializer.SerializeToElement(result, AppController.Json)
                .EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
            settings["startWithWindows"] = JsonSerializer.SerializeToElement(_startup.Enabled);
            settings["startupSupported"] = JsonSerializer.SerializeToElement(DesktopStartup.Supported);
            return settings;
        }
        return result;
    }

    public void RequestStop() => _controller.RequestStop();
    private void OnChanged() => Changed?.Invoke();
    public async ValueTask DisposeAsync()
    {
        _controller.SourceChanged -= OnChanged;
        await _controller.DisposeAsync();
    }
}
