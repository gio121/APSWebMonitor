using Microsoft.JSInterop;

namespace ApsMonitor.Services;

public sealed class MonitorThemeService(IJSRuntime js)
{
    public bool IsDark { get; private set; } = true;
    private bool _loaded;
    public event Action? Changed;
    public async Task LoadAsync()
    {
        if (_loaded) return;
        try
        {
            var saved = await js.InvokeAsync<string?>("localStorage.getItem", "aps-monitor-theme");
            IsDark = saved != "light";
            _loaded = true;
            Changed?.Invoke();
        }
        catch (JSException) { }
    }
    public async Task ToggleAsync()
    {
        IsDark = !IsDark;
        _loaded = true;
        Changed?.Invoke();
        try { await js.InvokeVoidAsync("localStorage.setItem", "aps-monitor-theme", IsDark ? "dark" : "light"); }
        catch (JSException) { }
    }
}
