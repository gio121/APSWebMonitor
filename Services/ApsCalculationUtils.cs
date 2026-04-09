using System.Globalization;

namespace ApsMonitor.Services;

/// <summary>
/// Utilidades centralizadas para cálculos de SCADA y parseo seguro de datos.
/// </summary>
public static class ApsCalculationUtils
{
    /// <summary>
    /// Convierte una cadena a double de forma segura, manejando cultura e invariante.
    /// </summary>
    public static double SafeParseDouble(string? value, double defaultValue = 0)
    {
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;
        
        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out double result))
            return result;
            
        if (double.TryParse(value, NumberStyles.Any, CultureInfo.CurrentCulture, out result))
            return result;
            
        return defaultValue;
    }

    /// <summary>
    /// Convierte una cadena a int de forma segura.
    /// </summary>
    public static int SafeParseInt(string? value, int defaultValue = 0)
    {
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;
        if (int.TryParse(value, out int result)) return result;
        return defaultValue;
    }

    /// <summary>
    /// Calcula el valor físico: (rawValue * scale) + offset
    /// </summary>
    public static double CalculatePhysical(double raw, double scale, double offset)
    {
        if (double.IsNaN(raw) || double.IsInfinity(raw)) return 0;
        return (raw * scale) + offset;
    }

    /// <summary>
    /// Calcula el valor absoluto del valor físico.
    /// </summary>
    public static double CalculateAbsolute(double physical)
    {
        return Math.Abs(physical);
    }
}
