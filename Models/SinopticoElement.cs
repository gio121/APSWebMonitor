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
    public double StrokeWidth { get; set; } = 1.5; // Canvas pixels, independent of symbol size

    // Properties for specific types
    public string Text { get; set; } = "Text";
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
    public bool ShowValue { get; set; }
    public int? ValueBit { get; set; }
    public string? ValueFormat { get; set; }
    public string? ValueUnit { get; set; }
    public string ValuePrefix { get; set; } = "";
    public Dictionary<string, string> ValueTexts { get; set; } = new();
    public List<SynopticTextRule> TextRules { get; set; } = new();
    public List<SynopticCondition> VisibilityRules { get; set; } = new();
    public bool VisibilityMatchAll { get; set; }
    public bool InvertState { get; set; }
    public bool InvertCommand { get; set; }
    public bool ProgressCountDown { get; set; }
}

public class SynopticCondition
{
    public int? SignalId { get; set; }
    public int? SignalBit { get; set; }
    public double? ExpectedValue { get; set; }
    public bool Invert { get; set; }
    public int? CompareSignalId { get; set; }
    public int? CompareSignalBit { get; set; }
}

public class ColorRule : SynopticCondition
{
    public string Color { get; set; } = "#4caf50";
}

public class SynopticTextRule : SynopticCondition
{
    public string Text { get; set; } = "";
}
