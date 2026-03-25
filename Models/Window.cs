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
}

