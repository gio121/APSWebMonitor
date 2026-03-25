namespace ApsMonitor.Models;

public class EventMessage
{
    public int Id { get; set; }
    public string Mensaje { get; set; } = string.Empty;
    public DateTime Fecha { get; set; } = DateTime.Now;
    public string Estado { get; set; } = "OK"; // e.g., OK, Error
}
