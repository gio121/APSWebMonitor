namespace ApsMonitor.Models;

public class SinopticoElement
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Type { get; set; } = "Etiqueta"; // Etiqueta, Señal, Comando, Caja, LineaH, LineaV, Diodo, Resistencia, Bobina, Descargador, Transformador, Fusible, Interruptor, Borne, Barra
    
    // Canvas dimensions
    public double X { get; set; } = 100;
    public double Y { get; set; } = 100;
    public double Width { get; set; } = 100;
    public double Height { get; set; } = 60;
    public double Rotation { get; set; } = 0;

    // Properties for specific types
    public string Text { get; set; } = "Texto";
    public int? SignalId { get; set; } // Nullable, bound to Signal if it's a "Señal"
    public string CommandValue { get; set; } = string.Empty; // The actual command to execute
    public string Color { get; set; } = "#000000"; // Default black color
    public double FontSize { get; set; } = 14; // Default font size
    
    // Progress Bar Specific
    public double MinValue { get; set; } = 0;
    public double MaxValue { get; set; } = 100;
    
    // Contactor Specific Properties
    public int? CommandSignalId { get; set; }
    public int? CommandSignalBit { get; set; }
    public int? StateSignalId { get; set; }
    public int? StateSignalBit { get; set; }
    public string ErrorColor { get; set; } = "#f44336"; // Default error/discrepancy color
    public string ActiveColor { get; set; } = "#4caf50"; // Default active color
    public List<ColorRule> BoxColorRules { get; set; } = new(); // Multiple color rules
    public bool ProportionalScale { get; set; } = true;
}

public class ColorRule
{
    public int? SignalId { get; set; }
    public int? SignalBit { get; set; }
    public string Color { get; set; } = "#4caf50";
}
