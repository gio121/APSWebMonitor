using ApsMonitor.Models;
using System.Buffers;
using System.Text;

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
    /// Decodifica todas las tramas del stream .dat que viene en formato texto con timestamps.
    /// </summary>
    /// <summary>
    /// Decodifica todas las tramas del stream .dat de forma altamente eficiente.
    /// </summary>
    public SessionDataset? Parse(byte[] fileData, List<Signal> signals)
    {
        if (signals == null || signals.Count == 0 || fileData == null || fileData.Length == 0)
            return null;

        // 1. Pre-agrupar señales por nodo para optimizar la búsqueda
        var signalsByNode = signals
            .GroupBy(s => s.NodoNumero)
            .ToDictionary(g => g.Key, g => g.ToList());

        // 2. Calcular tamaños mapeados por cada nodo (para identificar tramas por tamaño)
        var nodeSizes = signalsByNode.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.Max(s => s.BytePosicion + GetByteSize(s.TipoVariable))
        );

        // 3. Estimar número de tramas (contando saltos de línea) para pre-alegación
        int frameCount = 0;
        foreach (byte b in fileData) if (b == '\n') frameCount++;
        // Si no termina en \n, sumamos uno más
        if (fileData[^1] != '\n') frameCount++;

        var dataset = new SessionDataset(frameCount, signals.Select(s => s.Id).ToList());
        
        ReadOnlySpan<byte> span = fileData;
        int currentFramePos = 0;
        DateTime? startTime = null;

        // Mantener los últimos valores conocidos para propagar el snapshot
        double[] currentValues = new double[signals.Count];
        double[] currentRawValues = new double[signals.Count];

        int processedFrames = 0;

        int lineStart = 0;
        for (int i = 0; i <= span.Length; i++)
        {
            // Detectar fin de línea
            if (i == span.Length || span[i] == '\n' || span[i] == '\r')
            {
                if (i > lineStart)
                {
                    var line = span.Slice(lineStart, i - lineStart);
                    
                    // Procesar la línea: Timestamp;HEX HEX HEX
                    int semiColonIndex = line.IndexOf((byte)';');
                    if (semiColonIndex > 0)
                    {
                        var timestampPart = line.Slice(0, semiColonIndex);
                        var hexPart = line.Slice(semiColonIndex + 1);

                        // 4. Parsear Timestamp rápido (ej: 2023-10-27 10:00:00.123)
                        string tsStr = Encoding.UTF8.GetString(timestampPart);
                        if (DateTime.TryParse(tsStr, out var timestamp))
                        {
                            if (startTime == null) startTime = timestamp;

                            // 5. Convertir Hex a Bytes rápidamente sin Split
                            byte[] frameData = ParseHex(hexPart);

                            if (frameData.Length >= 30) // Trama de respuesta (Control/Comms)
                            {
                                int payloadLength = frameData.Length - 7;
                                if (payloadLength > 0)
                                {
                                    // Identificar Nodo por tamaño de payload
                                    int? matchingNode = null;
                                    int minDiff = int.MaxValue;
                                    foreach (var kvp in nodeSizes)
                                    {
                                        if (payloadLength >= kvp.Value)
                                        { 
                                            int diff = payloadLength - kvp.Value;
                                            if (diff < minDiff) { minDiff = diff; matchingNode = kvp.Key; }
                                        }
                                    }

                                    if (matchingNode.HasValue && signalsByNode.TryGetValue(matchingNode.Value, out var nodeSignals))
                                    {
                                        // Extraer datos usando el buffer de 6 bytes de cabecera omitidos
                                        foreach (var sig in nodeSignals)
                                        {
                                            int offset = sig.BytePosicion + 6;
                                            if (offset + GetByteSize(sig.TipoVariable) <= frameData.Length)
                                            {
                                                double rawValue = ReadValue(frameData, offset, sig.TipoVariable);
                                                double physValue = ApsCalculationUtils.CalculatePhysical(rawValue, sig.Escala, sig.Offset);
                                                
                                                int sigIdx = dataset.SignalIdToIndex[sig.Id];
                                                currentValues[sigIdx] = physValue;
                                                currentRawValues[sigIdx] = rawValue;
                                            }
                                        }

                                        // Guardar snapshot actual en el dataset (todas las tramas)
                                        dataset.Timestamps[processedFrames] = timestamp - startTime.Value;
                                        dataset.AbsoluteTimestamps[processedFrames] = timestamp;
                                        
                                        for (int s = 0; s < dataset.SignalCount; s++)
                                        {
                                            dataset.Values[processedFrames, s] = currentValues[s];
                                            dataset.RawValues[processedFrames, s] = currentRawValues[s];
                                        }
                                        processedFrames++;
                                    }
                                }
                            }
                        }
                    }
                }
                
                // Saltar caracteres de nueva línea múltiples (\r\n)
                if (i < span.Length && span[i] == '\r' && (i + 1 < span.Length) && span[i + 1] == '\n') i++;
                lineStart = i + 1;
            }
        }

        // Ordenar y ajustar el dataset para que sea cronológico y no tenga huecos
        return SortAndTrimDataset(dataset, processedFrames);
    }

    private SessionDataset SortAndTrimDataset(SessionDataset old, int count)
    {
        if (count == 0) return new SessionDataset(0, old.SignalIds);

        // 1. Crear un array de índices y ordenarlos por AbsoluteTimestamp
        int[] indices = new int[count];
        for (int i = 0; i < count; i++) indices[i] = i;

        Array.Sort(indices, (a, b) => old.AbsoluteTimestamps[a].CompareTo(old.AbsoluteTimestamps[b]));

        // 2. Crear el nuevo dataset
        var @new = new SessionDataset(count, old.SignalIds);
        DateTime minTime = old.AbsoluteTimestamps[indices[0]];

        for (int i = 0; i < count; i++)
        {
            int oldIdx = indices[i];

            // Tiempos
            @new.AbsoluteTimestamps[i] = old.AbsoluteTimestamps[oldIdx];
            @new.Timestamps[i] = @new.AbsoluteTimestamps[i] - minTime;

            // Valores de todas las señales
            for (int s = 0; s < old.SignalCount; s++)
            {
                @new.Values[i, s] = old.Values[oldIdx, s];
                @new.RawValues[i, s] = old.RawValues[oldIdx, s];
            }
        }

        return @new;
    }

    private byte[] ParseHex(ReadOnlySpan<byte> hexSpan)
    {
        // El formato es "XX XX XX" o "XXXXXX"
        // Estimamos tamaño: cada byte son 2 chars + posible espacio
        int count = 0;
        for (int i = 0; i < hexSpan.Length; i++)
        {
            if (IsHexChar(hexSpan[i]))
            {
                count++;
                i++; // Saltar el segundo char del byte
                while (i + 1 < hexSpan.Length && hexSpan[i + 1] == ' ') i++; // Saltar espacios
            }
        }

        byte[] result = new byte[count];
        int resIdx = 0;
        for (int i = 0; i < hexSpan.Length; i++)
        {
            if (IsHexChar(hexSpan[i]))
            {
                result[resIdx++] = (byte)((HexVal(hexSpan[i]) << 4) | HexVal(hexSpan[i+1]));
                i += 2;
                while (i < hexSpan.Length && hexSpan[i] == ' ') i++;
                i--; // El bucle for hará el i++
            }
        }
        return result;
    }

    private static bool IsHexChar(byte b) => (b >= '0' && b <= '9') || (b >= 'a' && b <= 'f') || (b >= 'A' && b <= 'F');
    private static int HexVal(byte b) => b switch {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => 0
    };


    private static double ReadValue(byte[] data, int offset, string tipoVariable)
    {
        try
        {
            if (offset >= data.Length) return 0;

            return tipoVariable.ToUpper() switch
            {
                "UINT8" or "BYTE" => data[offset],
                "INT8" or "SBYTE" => (sbyte)data[offset],
                "BCD_BYTE" => (data[offset] >> 4) * 10 + (data[offset] & 0x0F),
                "UINT16" or "UINT" => offset + 1 < data.Length ? BitConverter.ToUInt16(data, offset) : 0,
                "INT16" or "INT" => offset + 1 < data.Length ? BitConverter.ToInt16(data, offset) : 0,
                "UINT32" or "ULONG" => offset + 3 < data.Length ? BitConverter.ToUInt32(data, offset) : 0,
                "INT32" => offset + 3 < data.Length ? BitConverter.ToInt32(data, offset) : 0,
                "FLOAT" or "FLOAT32" => offset + 3 < data.Length ? BitConverter.ToSingle(data, offset) : 0,
                _ => data[offset]
            };
        }
        catch { return 0; }
    }

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
