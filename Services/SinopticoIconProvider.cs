using MudBlazor;

namespace ApsMonitor.Services;

public static class SinopticoIconProvider
{
    private static readonly Dictionary<string, string> _icons = new()
    {
        { "Etiqueta", Icons.Material.Filled.TextFormat },
        { "Señal", Icons.Material.Filled.Sensors },
        { "Comando", Icons.Material.Filled.ToggleOn },
        { "Linea H", Icons.Material.Filled.Remove },
        { "Linea V", Icons.Material.Filled.MoreVert },
        { "Diodo", Icons.Material.Filled.PlayArrow },
        { "Resistencia", Icons.Material.Filled.Timeline },
        { "Transformador", Icons.Material.Filled.SyncAlt },
        { "Fusible", Icons.Material.Filled.PowerInput },
        { "Interruptor", Icons.Material.Filled.ToggleOff },
        { "Borne", Icons.Material.Filled.RadioButtonUnchecked },
        { "Contactor", "<path d=\"M2,11h6v2h-6z M16,11h6v2h-6z M7.5,12l8.5,-6 1,1.5 -8.5,6z\"/>" },
        { "Contactor_Closed", "<path d=\"M2,11h6v2h-6z M16,11h6v2h-6z M7,11h10v2h-10z\"/>" },
        { "Contactor V", "<path d=\"M11,2h2v6h-2z M11,16h2v6h-2z M12,7.5l-6,8.5 1.5,1 6,-8.5z\"/>" },
        { "Contactor V_Closed", "<path d=\"M11,2h2v6h-2z M11,16h2v6h-2z M11,7h2v10h-2z\"/>" }
    };

    public static string GetIcon(string type)
    {
        if (type == null) return Icons.Material.Filled.Help;
        
        if (_icons.TryGetValue(type, out var icon))
            return icon;
            
        return Icons.Material.Filled.SettingsInputComponent;
    }
}
