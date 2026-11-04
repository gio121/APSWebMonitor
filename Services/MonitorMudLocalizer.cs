using System.Globalization;
using ApsMonitor.Resources;
using Microsoft.Extensions.Localization;
using MudBlazor;

namespace ApsMonitor.Services;

public sealed class MonitorMudLocalizer : MudLocalizer
{
    public override LocalizedString this[string key] =>
        MonitorResources.Manager.GetString(key, CultureInfo.CurrentUICulture) is { } value
            ? new LocalizedString(key, value)
            : base[key];
}
