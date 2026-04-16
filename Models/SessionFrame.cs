namespace ApsMonitor.Models;

/// <summary>
/// Representa una trama decodificada de una sesión.
/// Cada trama es un snapshot de todos los valores de señales en un instante.
/// </summary>
public class SessionFrame
{
    public int Index { get; set; }
    public TimeSpan Timestamp { get; set; }
    public DateTime AbsoluteTimestamp { get; set; }
    public Dictionary<int, double> Values { get; set; } = new();
    public Dictionary<int, double> RawValues { get; set; } = new();
}
