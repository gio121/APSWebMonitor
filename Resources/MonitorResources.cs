namespace ApsMonitor.Resources;

/// <summary>English interface resources shared by monitor components.</summary>
public sealed class MonitorResources
{
    public static System.Resources.ResourceManager Manager { get; } = new(typeof(MonitorResources));
    public static string Text(string key) => Manager.GetString(key, System.Globalization.CultureInfo.CurrentUICulture) ?? key;
    public static string Format(string key, params object?[] arguments) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, Text(key), arguments);
}
