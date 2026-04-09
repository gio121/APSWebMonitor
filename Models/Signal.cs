namespace ApsMonitor.Models;

public class Signal
{
    public int Id { get; set; }
    
    // General Tab
    public string Nombre { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
    public string? DescripcionEs { get; set; }
    public string? DescripcionEn { get; set; }
    public string? Unidad { get; set; }
    public string Formato { get; set; } = "0.1";
    public double ValorInicial { get; set; } = 0;
    public int NodoNumero { get; set; } = 0;
    public string NodoDescripcion { get; set; } = string.Empty;

    // Protocol Tab
    public string TipoVariable { get; set; } = "UINT16"; // e.g. UINT16 (2 bytes), UINT8
    public int BytePosicion { get; set; } = 0;
    public double Escala { get; set; } = 1;
    public double Offset { get; set; } = 0;

    // Bits Tab – active/inactive text per bit (0..15)
    public string?[] BitTextoActivo   { get; set; } = new string?[16];
    public string?[] BitTextoInactivo { get; set; } = new string?[16];

    // Current State (Mocked)
    public double ValorActual { get; set; } = 0;

    // Helper to format value
    public string GetFormattedValue()
    {
        return ValorActual.ToString(Formato) + (string.IsNullOrWhiteSpace(Unidad) ? "" : $" {Unidad}");
    }

    // Number of configurable bits based on type
    public int BitCount => TipoVariable switch
    {
        "UINT8"  => 8,
        "INT16"  => 16,
        _        => 16   // UINT16 / FLOAT32 defaults to 16
    };
}
