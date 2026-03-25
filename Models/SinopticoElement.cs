namespace ApsMonitor.Models;

public class SinopticoElement
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Type { get; set; } = "Etiqueta"; // Etiqueta, Señal, Comando, LineaH, LineaV, Diodo, Resistencia, Transformador, Fusible, Interruptor, Borne
    
    // Canvas dimensions
    public double X { get; set; } = 100;
    public double Y { get; set; } = 100;
    public double Width { get; set; } = 100;
    public double Height { get; set; } = 60;
    public double Rotation { get; set; } = 0;

    // Properties for specific types
    public string Text { get; set; } = "Texto";
    public int? SignalId { get; set; } // Nullable, bound to Signal if it's a "Señal"
}
