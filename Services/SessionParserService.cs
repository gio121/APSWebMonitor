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
    /// Decodifica todas las tramas del stream .dat que viene en formato texto con timestamps.
    /// </summary>
    public List<SessionFrame> Parse(byte[] fileData, List<Signal> signals)
    {
        var frames = new List<SessionFrame>();
        if (signals == null || signals.Count == 0 || fileData == null || fileData.Length == 0)
            return frames;

        string fileContent;
        try
        {
            fileContent = System.Text.Encoding.UTF8.GetString(fileData);
        }
        catch
        {
            return frames;
        }

        var lines = fileContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        // Calcular el tamaño mapeado de cada nodo para diferenciar tramas
        var nodeSizes = new Dictionary<int, int>();
        foreach (var signal in signals)
        {
            int end = signal.BytePosicion + GetByteSize(signal.TipoVariable);
            if (!nodeSizes.ContainsKey(signal.NodoNumero) || end > nodeSizes[signal.NodoNumero])
            {
                nodeSizes[signal.NodoNumero] = end;
            }
        }

        var currentValues = new Dictionary<int, double>();
        DateTime? startTime = null;
        int frameIndex = 0;

        foreach (var line in lines)
        {
            var parts = line.Split(';');
            if (parts.Length < 2) continue;

            if (!DateTime.TryParse(parts[0], out var timestamp))
                continue;

            if (startTime == null) startTime = timestamp;

            string hexString = parts[1].Trim();
            var hexParts = hexString.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var frameData = new byte[hexParts.Length];
            for (int i = 0; i < hexParts.Length; i++)
            {
                if (byte.TryParse(hexParts[i], System.Globalization.NumberStyles.HexNumber, null, out byte b))
                    frameData[i] = b;
            }

            if (frameData.Length < 6) continue;

            // Identificar si es petición o respuesta. 
            // Las peticiones (1D para control, 1A para comms) son tramas cortas.
            if (frameData.Length < 30)
                continue;

            // Es una trama de respuesta (Control o Comms).
            // Separamos solo los datos reales: omitimos los primeros 6 bytes (Cabecera) y el último byte (Checksum).
            int payloadLength = frameData.Length - 7;
            if (payloadLength <= 0) continue;

            byte[] payload = new byte[payloadLength];
            Array.Copy(frameData, 6, payload, 0, payloadLength);

            // Buscar qué nodo coincide mejor con el tamaño de este payload
            int? matchingNode = null;
            int minDifference = int.MaxValue;
            foreach (var kvp in nodeSizes)
            {
                // El tamaño del payload puede ser exacto o ligeramente mayor (padding)
                if (payload.Length >= kvp.Value)
                {
                    int diff = payload.Length - kvp.Value;
                    if (diff < minDifference)
                    {
                        minDifference = diff;
                        matchingNode = kvp.Key;
                    }
                }
            }

            if (matchingNode == null) continue;

            // Extraemos solo las señales del nodo que corresponde a esta trama
            bool anyChange = false;
            foreach (var signal in signals)
            {
                if (signal.NodoNumero != matchingNode.Value) continue;

                int end = signal.BytePosicion + GetByteSize(signal.TipoVariable);
                if (end <= payload.Length)
                {
                    double rawValue = ReadValue(payload, signal.BytePosicion, signal.TipoVariable);
                    double physicalValue = ApsCalculationUtils.CalculatePhysical(rawValue, signal.Escala, signal.Offset);
                    currentValues[signal.Id] = physicalValue;
                    anyChange = true;
                }
            }

            if (anyChange)
            {
                var frame = new SessionFrame
                {
                    Index = frameIndex++,
                    Timestamp = timestamp - startTime.Value,
                    AbsoluteTimestamp = timestamp,
                    Values = new Dictionary<int, double>(currentValues)
                };
                frames.Add(frame);
            }
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