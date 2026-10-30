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
    private readonly BackendOptions _options;
    private readonly DesktopStartup _startup;
    private volatile bool _stopping;
    public event Action? Changed;

    public BackendApplication(BackendOptions options)
    {
        _options = options;
        _controller = new AppController(options.DataDirectory);
        _startup = new DesktopStartup(options.DataDirectory, options.ShellPath);
        _controller.SourceChanged += OnChanged;
    }

    public async Task InitializeAsync()
    {
        await _controller.InitializeAsync();
        if (!_stopping && _options.Mode is { } mode)
            await _controller.DispatchAsync("mode", JsonSerializer.SerializeToElement(new { mode }));
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
            return settings;
        }
        return result;
    }

    public void RequestStop() { _stopping = true; _controller.RequestStop(); }
    private void OnChanged() => Changed?.Invoke();
    public async ValueTask DisposeAsync()
    {
        _controller.SourceChanged -= OnChanged;
        await _controller.DisposeAsync();
    }
}
