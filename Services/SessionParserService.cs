using ApsMonitor.Models;

namespace ApsMonitor.Services;

/// <summary>
/// Servicio que decodifica archivos .dat de sesión binaria.
/// Cada trama es una secuencia de bytes consecutivos sin cabecera.
/// Intervalo entre tramas: 2ms. Byte order: Little Endian.
/// </summary>
public class SessionParserService
{
    private const double FRAME_INTERVAL_MS = 2.0;

    /// <summary>
    /// Decodifica todas las tramas del stream .dat usando la lista de señales configuradas.
    /// </summary>
    public List<SessionFrame> Parse(byte[] fileData, List<Signal> signals)
    {
        if (signals.Count == 0 || fileData.Length == 0)
            return new List<SessionFrame>();

        // Calcular tamaño de trama: máximo (BytePosicion + tamaño del tipo) de todas las señales
        int frameSize = 0;
        foreach (var signal in signals)
        {
            int end = signal.BytePosicion + GetByteSize(signal.TipoVariable);
            if (end > frameSize)
                frameSize = end;
        }

        if (frameSize == 0)
            return new List<SessionFrame>();

        int totalFrames = fileData.Length / frameSize;
        var frames = new List<SessionFrame>(totalFrames);

        for (int i = 0; i < totalFrames; i++)
        {
            int offset = i * frameSize;

            var frame = new SessionFrame
            {
                Index = i,
                Timestamp = TimeSpan.FromMilliseconds(i * FRAME_INTERVAL_MS),
                Values = new Dictionary<int, double>()
            };

            foreach (var signal in signals)
            {
                int pos = offset + signal.BytePosicion;

                double rawValue = ReadValue(fileData, pos, signal.TipoVariable);

                // Cálculo Físico Robusto usando utilidades centralizadas
                double physicalValue = ApsCalculationUtils.CalculatePhysical(rawValue, signal.Escala, signal.Offset);
                
                frame.Values[signal.Id] = physicalValue;
            }

            frames.Add(frame);
        }

        return frames;
    }

    /// <summary>
    /// Lee un valor del buffer según el tipo de variable (Little Endian).
    /// Adaptado para comportarse igual que el parser del MainForm.
    /// </summary>
    private static double ReadValue(byte[] data, int offset, string tipoVariable)
    {
        try
        {
            if (offset >= data.Length) return 0;

            return tipoVariable.ToUpper() switch
            {
                // 1 byte
                "UINT8" or "ASCII_BYTE" or "UNSIGNED_BYTE" or "BYTE" => data[offset],
                "INT8" or "SIGNED_BYTE" or "SBYTE" => (sbyte)data[offset],

                // BCD (como en el otro parser)
                "BCD_BYTE" => (data[offset] >> 4) * 10 + (data[offset] & 0x0F),

                // 2 bytes
                "UINT16" or "UNSIGNED_WORD" or "UINT" => offset + 1 < data.Length
                    ? BitConverter.ToUInt16(data, offset)
                    : 0,

                "INT16" or "SIGNED_WORD" or "INT" => offset + 1 < data.Length
                    ? BitConverter.ToInt16(data, offset)
                    : 0,

                // 4 bytes
                "UINT32" or "UNSIGNED_DWORD" or "ULONG" => offset + 3 < data.Length
                    ? BitConverter.ToUInt32(data, offset)
                    : 0,

                "INT32" or "SIGNED_DWORD" => offset + 3 < data.Length
                    ? BitConverter.ToInt32(data, offset)
                    : 0,

                "FLOAT" or "FLOAT32" => offset + 3 < data.Length
                    ? BitConverter.ToSingle(data, offset)
                    : 0,

                _ => data[offset]
            };
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Devuelve el tamaño en bytes de un tipo de variable.
    /// </summary>
    private static int GetByteSize(string tipoVariable)
    {
        return tipoVariable.ToUpper() switch
        {
            "UINT8" or "INT8" or "BYTE" or "SBYTE" or "BCD_BYTE" => 1,
            "UINT16" or "INT16" or "UINT" or "INT" => 2,
            "UINT32" or "INT32" or "FLOAT32" or "FLOAT" or "ULONG" => 4,
            _ => 1
        };
    }
}