namespace ApsMonitor.Models;

public class ScadaCommand
{
    public int Id { get; set; }
    public string Nombre { get; set; } = string.Empty;
    public string CommandValue { get; set; } = string.Empty;
    public bool RequiereConfirmacion { get; set; } = true;
    public string Estilo { get; set; } = "default"; // success, destructive, warning, default
}
