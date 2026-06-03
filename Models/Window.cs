namespace ApsMonitor.Models;

public class Window
{
    public int Id { get; set; }
    public string Nombre { get; set; } = string.Empty;
    public string Descripcion { get; set; } = string.Empty;
    public string Categoria { get; set; } = string.Empty;
    
    // UI badges
    public string Tipo { get; set; } = "Normal"; // e.g., Normal, Sinóptico
    public bool IsActive { get; set; } = true;

    // Synoptic Content (JSON serialized elements)
    public string ContentJson { get; set; } = "[]";

    // Access control
    public bool PermitirMantenimiento { get; set; } = false;

    // Failure configuration (JSON serialized List<FailureCategoryConfig>)
    public string FailuresConfigJson { get; set; } = "[]";
}

public class FailureCategoryConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public int? StatusSignalId { get; set; }
    public int? CountSignalId { get; set; }
    public List<int> SignalIds { get; set; } = new();
}

