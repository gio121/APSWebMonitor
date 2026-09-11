using ApsMonitor.Models;
using System.Text;
using System.Text.Json;

namespace ApsMonitor.Services;

/// <summary>
/// Representa una trama TRDP / paquete de red extraído de una captura de Wireshark.
/// </summary>
public class TrdpCapturedFrame
{
    public int Index { get; set; }
    public int WiresharkFrameNumber { get; set; }
    public DateTime Timestamp { get; set; }
    public TimeSpan RelativeTime { get; set; }
    public string SourceIp { get; set; } = string.Empty;
    public string DestinationIp { get; set; } = string.Empty;
    public ushort SourcePort { get; set; }
    public ushort DestinationPort { get; set; }
    public long ComId { get; set; }
    public bool HasTrdpHeader { get; set; }
    public byte[] Payload { get; set; } = Array.Empty<byte>();

    public string RawHex => Convert.ToHexString(Payload);

    /// <summary>
    /// Convierte esta trama capturada a un TrdpFrameMessage decodificado listo para la UI.
    /// </summary>
    public TrdpFrameMessage ToFrameMessage(string datasetName, long defaultComId, TrdpDataset? datasetDef = null)
    {
        long effectiveComId = ComId > 0 ? ComId : defaultComId;

        var message = new TrdpFrameMessage
        {
            Type      = "trdp_frame",
            Dataset   = datasetName,
            ComId     = effectiveComId,
            Timestamp = ((DateTimeOffset)Timestamp).ToUnixTimeMilliseconds(),
            Size      = Payload.Length,
            RawHex    = RawHex,
            RawBytes  = Payload.ToList(),
            Snapshot  = false
        };

        message.Parsed = DecodeParsedPayload(Payload, datasetDef);
        return message;
    }

    private static Dictionary<string, JsonElement> DecodeParsedPayload(byte[] payload, TrdpDataset? datasetDef)
    {
        var result = new Dictionary<string, JsonElement>();
        if (payload == null || payload.Length == 0)
            return result;

        if (datasetDef == null || datasetDef.Variables == null || datasetDef.Variables.Count == 0)
        {
            // Decodificación genérica por bytes si no hay definición de dataset
            for (int i = 0; i < payload.Length; i += 2)
            {
                if (i + 1 < payload.Length)
                {
                    ushort val = BitConverter.ToUInt16(payload, i);
                    result[$"word_{i}"] = JsonSerializer.SerializeToElement(val);
                }
                else
                {
                    result[$"byte_{i}"] = JsonSerializer.SerializeToElement((int)payload[i]);
                }
            }
            return result;
        }

        foreach (var v in datasetDef.Variables)
        {
            if (string.IsNullOrWhiteSpace(v.Id)) continue;
            if (v.Offset < 0 || v.Offset >= payload.Length) continue;

            double rawVal = SessionParserService.ReadValue(payload, v.Offset, v.Type);
            double physVal = rawVal * (v.Scale ?? 1.0) + (v.OffsetVal ?? 0.0);

            // Decodificación de tipo entero / flotante
            bool isFloat = v.Type.Equals("float", StringComparison.OrdinalIgnoreCase) || 
                           v.Type.Equals("float32", StringComparison.OrdinalIgnoreCase);

            if (isFloat)
            {
                result[v.Id] = JsonSerializer.SerializeToElement(Math.Round(physVal, 4));
            }
            else
            {
                result[v.Id] = JsonSerializer.SerializeToElement((long)Math.Round(physVal));
            }

            // Si es un tipo de 8 o 16 bits, generar también sus bitfields badges para el panel LED
            int bitCount = SessionParserService.GetByteSize(v.Type) * 8;
            if (bitCount is 8 or 16)
            {
                ulong intVal = (ulong)rawVal;
                var bitDict = new Dictionary<string, int>();
                for (int b = 0; b < Math.Min(bitCount, 16); b++)
                {
                    int bitState = (int)((intVal >> b) & 1);
                    bitDict[$"bit_{b}"] = bitState;
                }
                result[$"{v.Id}_bits"] = JsonSerializer.SerializeToElement(bitDict);
            }
        }

        return result;
    }
}

public class PcapChunkResult
{
    public List<TrdpCapturedFrame> Frames { get; set; } = new();
    public int TotalFrames { get; set; }
    public int TotalChunks { get; set; }
    public int CurrentChunkIndex { get; set; }
    public int ChunkSize { get; set; } = 200;
    public string TempFilePath { get; set; } = string.Empty;
    public long FilterComId { get; set; }
    public string FilterIp { get; set; } = string.Empty;
}

/// <summary>
/// Servicio parser paginado por bloques (chunks) para capturas Wireshark con filtrado específico de ComID e IP Multicast TRDP.
/// </summary>
public class TrdpPcapParserService
{
    public const int DEFAULT_CHUNK_SIZE = 200;

    /// <summary>
    /// Guarda el stream entrante en un archivo temporal en disco de forma secuencial reportando el progreso de transferencia.
    /// </summary>
    public async Task<string> SaveToTempFileAsync(
        Stream stream, 
        long totalBytes, 
        Action<long, long>? onProgress = null, 
        CancellationToken ct = default)
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"trdp_pcap_{Guid.NewGuid():N}.tmp");
        byte[] buffer = new byte[64 * 1024];
        long totalRead = 0;
        int read;

        using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true))
        {
            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                await fs.WriteAsync(buffer, 0, read, ct);
                totalRead += read;
                onProgress?.Invoke(totalRead, totalBytes);
            }
        }
        return tempPath;
    }

    /// <summary>
    /// Elimina el archivo temporal del sistema de archivos.
    /// </summary>
    public void DeleteTempFile(string? tempPath)
    {
        if (string.IsNullOrWhiteSpace(tempPath)) return;
        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch { }
    }

    /// <summary>
    /// Lee un bloque específico de tramas (chunk) filtrando únicamente por ComID e IP de red.
    /// </summary>
    public async Task<PcapChunkResult> ReadChunkAsync(
        string tempFilePath, 
        int chunkIndex, 
        long targetComId = 0, 
        string targetIp = "", 
        int chunkSize = DEFAULT_CHUNK_SIZE)
    {
        return await Task.Run(() =>
        {
            var result = new PcapChunkResult
            {
                TempFilePath = tempFilePath,
                CurrentChunkIndex = Math.Max(0, chunkIndex),
                ChunkSize = chunkSize,
                FilterComId = targetComId,
                FilterIp = targetIp
            };

            if (!File.Exists(tempFilePath))
                return result;

            using var fs = new FileStream(tempFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
            byte[] headerBuffer = new byte[24];

            if (fs.Read(headerBuffer, 0, 24) < 24)
                return result;

            uint magic = BitConverter.ToUInt32(headerBuffer, 0);

            if (magic is 0xa1b2c3d4 or 0xd4c3b2a1 or 0xa1b23c4d or 0x4d3cb2a1)
            {
                ReadClassicPcapChunk(fs, magic, targetComId, targetIp, result);
            }
            else if (magic == 0x0A0D0D0A)
            {
                ReadPcapNgChunk(fs, targetComId, targetIp, result);
            }
            else
            {
                // Fallback para JSON/texto
                fs.Seek(0, SeekOrigin.Begin);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                string content = sr.ReadToEnd();
                List<TrdpCapturedFrame> allFrames = content.TrimStart().StartsWith("[") 
                    ? ParseWiresharkJsonString(content, targetComId, targetIp) 
                    : ParseTextHexDumpString(content);

                result.TotalFrames = allFrames.Count;
                result.TotalChunks = (int)Math.Ceiling((double)allFrames.Count / chunkSize);
                if (result.TotalChunks == 0) result.TotalChunks = 1;

                int skip = result.CurrentChunkIndex * chunkSize;
                result.Frames = allFrames.Skip(skip).Take(chunkSize).ToList();
            }

            return result;
        });
    }

    private static void ReadClassicPcapChunk(FileStream fs, uint magic, long targetComId, string targetIp, PcapChunkResult result)
    {
        bool littleEndian = magic is 0xa1b2c3d4 or 0xa1b23c4d;
        bool isNano = magic is 0xa1b23c4d or 0x4d3cb2a1;

        int startFrameIdx = result.CurrentChunkIndex * result.ChunkSize + 1;
        int endFrameIdx = (result.CurrentChunkIndex + 1) * result.ChunkSize;

        byte[] hdrBuf = new byte[16];
        int rawPktIndex = 1;
        int matchingIdx = 1;
        DateTime? firstTs = null;

        while (fs.Read(hdrBuf, 0, 16) == 16)
        {
            uint tsSec   = ReadUInt32(hdrBuf, 0, littleEndian);
            uint tsUsec  = ReadUInt32(hdrBuf, 4, littleEndian);
            uint inclLen = ReadUInt32(hdrBuf, 8, littleEndian);

            long dtTicks = DateTime.UnixEpoch.Ticks + (tsSec * TimeSpan.TicksPerSecond);
            if (isNano)
                dtTicks += tsUsec * 10;
            else
                dtTicks += tsUsec * (TimeSpan.TicksPerSecond / 1000000);

            DateTime pktTime = new DateTime(dtTicks, DateTimeKind.Utc).ToLocalTime();
            if (!firstTs.HasValue) firstTs = pktTime;

            byte[] pktBytes = new byte[inclLen];
            int readBytes = fs.Read(pktBytes, 0, (int)inclLen);

            if (readBytes == (int)inclLen)
            {
                var frame = ExtractPayloadFromPktBytes(pktBytes, matchingIdx, rawPktIndex, pktTime, firstTs.Value, targetComId, targetIp);
                if (frame != null)
                {
                    if (matchingIdx >= startFrameIdx && matchingIdx <= endFrameIdx)
                    {
                        result.Frames.Add(frame);
                    }
                    matchingIdx++;
                }
            }
            rawPktIndex++;
        }

        int totalMatchingFrames = matchingIdx - 1;
        result.TotalFrames = totalMatchingFrames;
        result.TotalChunks = (int)Math.Ceiling((double)totalMatchingFrames / result.ChunkSize);
        if (result.TotalChunks == 0) result.TotalChunks = 1;
    }

    private static void ReadPcapNgChunk(FileStream fs, long targetComId, string targetIp, PcapChunkResult result)
    {
        fs.Seek(0, SeekOrigin.Begin);
        bool littleEndian = true;
        int rawPktIndex = 1;
        int matchingIdx = 1;
        DateTime? firstTs = null;

        int startFrameIdx = result.CurrentChunkIndex * result.ChunkSize + 1;
        int endFrameIdx = (result.CurrentChunkIndex + 1) * result.ChunkSize;

        byte[] blkHdr = new byte[8];

        while (fs.Read(blkHdr, 0, 8) == 8)
        {
            uint blockType = ReadUInt32(blkHdr, 0, littleEndian);
            uint blockLen  = ReadUInt32(blkHdr, 4, littleEndian);

            if (blockLen < 12) break;
            int bodyLen = (int)blockLen - 8;

            if (blockType == 0x0A0D0D0A) // Section Header Block
            {
                byte[] shbMagic = new byte[4];
                if (fs.Read(shbMagic, 0, 4) == 4)
                {
                    uint magic = ReadUInt32(shbMagic, 0, true);
                    littleEndian = (magic == 0x1A2B3C4D);
                    fs.Seek(bodyLen - 4, SeekOrigin.Current);
                }
                else break;
            }
            else if (blockType == 0x00000006) // Enhanced Packet Block
            {
                byte[] epbHeader = new byte[20];
                if (fs.Read(epbHeader, 0, 20) == 20)
                {
                    uint tsHigh = ReadUInt32(epbHeader, 4, littleEndian);
                    uint tsLow  = ReadUInt32(epbHeader, 8, littleEndian);
                    uint capLen = ReadUInt32(epbHeader, 12, littleEndian);

                    ulong rawTs = ((ulong)tsHigh << 32) | tsLow;
                    DateTime pktTime = DateTime.UnixEpoch.AddMicroseconds(rawTs).ToLocalTime();
                    if (!firstTs.HasValue) firstTs = pktTime;

                    int packetDataLen = (int)capLen;
                    int padLen = (4 - (packetDataLen % 4)) % 4;
                    int remainingBytesInBlock = bodyLen - 20 - packetDataLen - padLen;

                    byte[] pktBytes = new byte[packetDataLen];
                    fs.Read(pktBytes, 0, packetDataLen);
                    if (padLen + remainingBytesInBlock > 0)
                        fs.Seek(padLen + remainingBytesInBlock, SeekOrigin.Current);

                    var frame = ExtractPayloadFromPktBytes(pktBytes, matchingIdx, rawPktIndex, pktTime, firstTs.Value, targetComId, targetIp);
                    if (frame != null)
                    {
                        if (matchingIdx >= startFrameIdx && matchingIdx <= endFrameIdx)
                        {
                            result.Frames.Add(frame);
                        }
                        matchingIdx++;
                    }
                    rawPktIndex++;
                }
                else break;
            }
            else
            {
                fs.Seek(bodyLen, SeekOrigin.Current);
            }
        }

        int totalMatchingFrames = matchingIdx - 1;
        result.TotalFrames = totalMatchingFrames;
        result.TotalChunks = (int)Math.Ceiling((double)totalMatchingFrames / result.ChunkSize);
        if (result.TotalChunks == 0) result.TotalChunks = 1;
    }

    private static List<TrdpCapturedFrame> ParseWiresharkJsonString(string jsonText, long targetComId, string targetIp)
    {
        var frames = new List<TrdpCapturedFrame>();
        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return frames;

            int rawPktIndex = 1;
            int matchingIdx = 1;
            DateTime? firstTs = null;

            foreach (var packet in doc.RootElement.EnumerateArray())
            {
                if (!packet.TryGetProperty("_source", out var source)) { rawPktIndex++; continue; }
                if (!source.TryGetProperty("layers", out var layers)) { rawPktIndex++; continue; }

                DateTime pktTime = DateTime.Now;
                if (layers.TryGetProperty("frame", out var frameLayer) && frameLayer.TryGetProperty("frame.time_epoch", out var epochEl))
                {
                    if (double.TryParse(epochEl.GetString(), System.Globalization.CultureInfo.InvariantCulture, out double epoch))
                        pktTime = DateTime.UnixEpoch.AddSeconds(epoch).ToLocalTime();
                }

                if (!firstTs.HasValue) firstTs = pktTime;

                string srcIp = "", dstIp = "";
                if (layers.TryGetProperty("ip", out var ipLayer))
                {
                    if (ipLayer.TryGetProperty("ip.src", out var sEl)) srcIp = sEl.GetString() ?? "";
                    if (ipLayer.TryGetProperty("ip.dst", out var dEl)) dstIp = dEl.GetString() ?? "";
                }

                byte[] rawUdpPayload = Array.Empty<byte>();
                if (layers.TryGetProperty("data", out var dataLayer) && dataLayer.TryGetProperty("data.data", out var hexEl))
                {
                    rawUdpPayload = ParseHex(hexEl.GetString());
                }
                else if (layers.TryGetProperty("udp", out var udpDataLayer) && udpDataLayer.TryGetProperty("udp.payload", out var payloadEl))
                {
                    rawUdpPayload = ParseHex(payloadEl.GetString());
                }

                var (pktComId, datasetPayload, hasHeader) = ParseTrdpHeader(rawUdpPayload);

                int currentRawFrameNum = rawPktIndex++;

                // Filtrar por ComID / IP
                if (targetComId > 0 && pktComId.HasValue && pktComId.Value != targetComId)
                    continue;

                if (!string.IsNullOrWhiteSpace(targetIp) && !dstIp.Equals(targetIp, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (datasetPayload.Length > 0)
                {
                    frames.Add(new TrdpCapturedFrame
                    {
                        Index                = matchingIdx++,
                        WiresharkFrameNumber = currentRawFrameNum,
                        Timestamp            = pktTime,
                        RelativeTime         = pktTime - firstTs.Value,
                        SourceIp             = srcIp,
                        DestinationIp        = dstIp,
                        ComId                = pktComId ?? targetComId,
                        HasTrdpHeader        = hasHeader,
                        Payload              = datasetPayload
                    });
                }
            }
        }
        catch { }
        return frames;
    }

    private static List<TrdpCapturedFrame> ParseTextHexDumpString(string text)
    {
        var frames = new List<TrdpCapturedFrame>();
        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        int frameIdx = 1;
        DateTime startTs = DateTime.Now;

        var currentHex = new StringBuilder();
        foreach (var line in lines)
        {
            var clean = line.Trim();
            if (clean.Contains(";"))
            {
                if (currentHex.Length > 0)
                {
                    byte[] payload = ParseHex(currentHex.ToString());
                    if (payload.Length > 0)
                    {
                        frames.Add(new TrdpCapturedFrame
                        {
                            Index                = frameIdx,
                            WiresharkFrameNumber = frameIdx,
                            Timestamp            = startTs.AddMilliseconds((frameIdx - 1) * 100),
                            RelativeTime         = TimeSpan.FromMilliseconds((frameIdx - 1) * 100),
                            Payload              = payload
                        });
                        frameIdx++;
                    }
                    currentHex.Clear();
                }
            }
            currentHex.Append(clean).Append(" ");
        }

        if (currentHex.Length > 0)
        {
            byte[] payload = ParseHex(currentHex.ToString());
            if (payload.Length > 0)
            {
                frames.Add(new TrdpCapturedFrame
                {
                    Index                = frameIdx,
                    WiresharkFrameNumber = frameIdx,
                    Timestamp            = startTs.AddMilliseconds((frameIdx - 1) * 100),
                    RelativeTime         = TimeSpan.FromMilliseconds((frameIdx - 1) * 100),
                    Payload              = payload
                });
            }
        }

        return frames;
    }

    private static TrdpCapturedFrame? ExtractPayloadFromPktBytes(
        byte[] pktBytes, 
        int matchingIndex, 
        int rawPktIndex,
        DateTime timestamp, 
        DateTime firstTs, 
        long targetComId, 
        string targetIp)
    {
        if (pktBytes.Length < 14) return null;

        int etherType = (pktBytes[12] << 8) | pktBytes[13];
        int ipOffset = 14;

        if (etherType == 0x8100) // VLAN 802.1Q
        {
            ipOffset = 18;
            etherType = (pktBytes[16] << 8) | pktBytes[17];
        }

        if (etherType != 0x0800 || pktBytes.Length < ipOffset + 20) return null; // IPv4

        int ipHeaderLen = (pktBytes[ipOffset] & 0x0F) * 4;
        byte protocol = pktBytes[ipOffset + 9];

        string srcIp = $"{pktBytes[ipOffset + 12]}.{pktBytes[ipOffset + 13]}.{pktBytes[ipOffset + 14]}.{pktBytes[ipOffset + 15]}";
        string dstIp = $"{pktBytes[ipOffset + 16]}.{pktBytes[ipOffset + 17]}.{pktBytes[ipOffset + 18]}.{pktBytes[ipOffset + 19]}";

        int udpOffset = ipOffset + ipHeaderLen;
        if (protocol != 17 || pktBytes.Length < udpOffset + 8) return null; // UDP protocol = 17

        ushort srcPort = (ushort)((pktBytes[udpOffset] << 8) | pktBytes[udpOffset + 1]);
        ushort dstPort = (ushort)((pktBytes[udpOffset + 2] << 8) | pktBytes[udpOffset + 3]);
        ushort udpLen  = (ushort)((pktBytes[udpOffset + 4] << 8) | pktBytes[udpOffset + 5]);

        int payloadOffset = udpOffset + 8;
        int payloadLen = Math.Min(udpLen - 8, pktBytes.Length - payloadOffset);
        if (payloadLen <= 0) return null;

        byte[] rawUdpPayload = new byte[payloadLen];
        Buffer.BlockCopy(pktBytes, payloadOffset, rawUdpPayload, 0, payloadLen);

        // Extraer y validar cabecera TRDP
        var (pktComId, datasetPayload, hasHeader) = ParseTrdpHeader(rawUdpPayload);

        // Filtrar por ComID si se ha configurado
        if (targetComId > 0 && pktComId.HasValue && pktComId.Value != targetComId)
            return null;

        // Filtrar por IP Multicast si se especifica
        if (!string.IsNullOrWhiteSpace(targetIp) && !dstIp.Equals(targetIp, StringComparison.OrdinalIgnoreCase))
            return null;

        return new TrdpCapturedFrame
        {
            Index                = matchingIndex,
            WiresharkFrameNumber = rawPktIndex,
            Timestamp            = timestamp,
            RelativeTime         = timestamp - firstTs,
            SourceIp             = srcIp,
            DestinationIp        = dstIp,
            SourcePort           = srcPort,
            DestinationPort      = dstPort,
            ComId                = pktComId ?? targetComId,
            HasTrdpHeader        = hasHeader,
            Payload              = datasetPayload
        };
    }

    /// <summary>
    /// Inspecciona la cabecera del PDU TRDP para obtener el ComID y desplazar dinámicamente la lectura del payload.
    /// Calcula de forma exacta headerLen = totalPayloadLen - datasetLength.
    /// </summary>
    private static (long? comId, byte[] datasetPayload, bool hasHeader) ParseTrdpHeader(byte[] pktPayload)
    {
        if (pktPayload == null || pktPayload.Length < 12)
            return (null, pktPayload ?? Array.Empty<byte>(), false);

        // Verificar si el byte 6 contiene la firma 'P' (0x50) de TRDP
        bool isTrdpHeader = pktPayload.Length >= 32 && pktPayload[6] == (byte)'P';

        if (isTrdpHeader)
        {
            long comId = (long)((pktPayload[8] << 24) | (pktPayload[9] << 16) | (pktPayload[10] << 8) | pktPayload[11]);
            
            int headerLen = 40; // Tamaño estándar de cabecera TRDP PD (40 bytes)
            if (pktPayload.Length >= 24)
            {
                uint datasetLen = (uint)((pktPayload[20] << 24) | (pktPayload[21] << 16) | (pktPayload[22] << 8) | pktPayload[23]);
                if (datasetLen > 0 && datasetLen < pktPayload.Length)
                {
                    int calculatedHeaderLen = pktPayload.Length - (int)datasetLen;
                    if (calculatedHeaderLen is >= 24 and <= 64)
                    {
                        headerLen = calculatedHeaderLen;
                    }
                }
            }

            byte[] datasetPayload = pktPayload.Length > headerLen 
                ? pktPayload.AsSpan(headerLen).ToArray() 
                : Array.Empty<byte>();

            return (comId, datasetPayload, true);
        }
        else if (pktPayload.Length >= 12)
        {
            long comId = (long)((pktPayload[8] << 24) | (pktPayload[9] << 16) | (pktPayload[10] << 8) | pktPayload[11]);
            return (comId, pktPayload, false);
        }

        return (null, pktPayload, false);
    }

    private static uint ReadUInt32(byte[] b, int offset, bool littleEndian)
    {
        if (littleEndian)
            return (uint)(b[offset] | (b[offset + 1] << 8) | (b[offset + 2] << 16) | (b[offset + 3] << 24));
        return (uint)((b[offset] << 24) | (b[offset + 1] << 16) | (b[offset + 2] << 8) | b[offset + 3]);
    }

    private static byte[] ParseHex(string? hexStr)
    {
        if (string.IsNullOrWhiteSpace(hexStr)) return Array.Empty<byte>();
        var clean = hexStr.Replace(":", "").Replace("-", "").Replace(" ", "").Trim();
        if (clean.Length % 2 != 0) return Array.Empty<byte>();

        try
        {
            byte[] bytes = new byte[clean.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(clean.Substring(i * 2, 2), 16);
            }
            return bytes;
        }
        catch { return Array.Empty<byte>(); }
    }
}
