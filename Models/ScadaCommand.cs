namespace ApsMonitor.Models;

public class ScadaCommand
{
    public int Id { get; set; }
    public string Nombre { get; set; } = string.Empty;
    public string CommandValue { get; set; } = string.Empty;
    public bool RequiereConfirmacion { get; set; } = true;
    public string Estilo { get; set; } = "default"; // success, destructive, warning, default
    public string Tipo { get; set; } = "Interna"; // Interna, Modificación de Variables
    public int? FrameType { get; set; }
    public int? Subcommand { get; set; }
    public int? SignalId { get; set; }
    public double? VariableValue { get; set; }
}
