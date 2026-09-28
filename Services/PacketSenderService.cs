using ApsMonitor.Models;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ApsMonitor.Services;

/// <summary>
/// Servicio de alta precisión para reproducción de tramas de red Wireshark (PCAP / PCAPNG / JSON)
/// y generador de paquetes (Packet Sender) a través de interfaces de red físicas.
/// </summary>
public class PacketSenderService : IDisposable
{
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Task? _playbackTask;
    private Socket? _udpSocket;
    private string _boundAdapterIp = string.Empty;

    // Fichero y tramas cargadas
    private readonly List<ReplayPacket> _allPackets = new();
    private List<ReplayPacket> _playlist = new();
    private CaptureFileInfo? _fileInfo;
    private string? _tempFilePath;

    // Estado actual
    private PacketReplayConfig _config = new();
    private readonly PacketReplayStats _stats = new();
    private bool _isPaused = false;
    private int _currentIndex = 0;

    // Eventos públicos
    public event Action<PacketReplayStats>? OnPlaybackProgress;
    public event Action<ReplayStatus>? OnStatusChanged;
    public event Action<string>? OnLogMessage;

    // Historial de envíos manuales
    public List<ManualPacketResult> ManualHistory { get; } = new();

    public PacketReplayStats Stats
    {
        get
        {
            lock (_lock)
            {
                return new PacketReplayStats
                {
                    Status = _stats.Status,
                    CurrentIndex = _currentIndex,
                    TotalPackets = _playlist.Count,
                    PacketsSent = _stats.PacketsSent,
                    PacketsFailed = _stats.PacketsFailed,
                    TotalBytesSent = _stats.TotalBytesSent,
                    CurrentPps = _stats.CurrentPps,
                    CurrentBitrateKbps = _stats.CurrentBitrateKbps,
                    ElapsedRealTime = _stats.ElapsedRealTime,
                    CurrentCaptureRelativeTime = _stats.CurrentCaptureRelativeTime,
                    TotalCaptureDuration = _fileInfo?.TotalDuration ?? TimeSpan.Zero,
                    CurrentPacketDescription = _stats.CurrentPacketDescription
                };
            }
        }
    }

    public CaptureFileInfo? FileInfo => _fileInfo;
    public IReadOnlyList<ReplayPacket> Packets => _playlist;
    public int TotalLoadedPackets => _allPackets.Count;
    public bool IsPlaying => _stats.Status == ReplayStatus.Playing;
    public bool IsPaused => _isPaused;

    // ─────────────────────────────────────────────────────────────
    // CARGA Y PARSEO DE ARCHIVOS WIRESHARK
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Guarda el stream subido a disco temporal y parsea las tramas Wireshark.
    /// </summary>
    public async Task<CaptureFileInfo> LoadCaptureAsync(
        Stream stream, 
        string fileName, 
        long totalBytes, 
        Action<long, long>? onUploadProgress = null,
        CancellationToken ct = default)
    {
        StopPlayback();

        // Limpiar archivo temporal previo
        DeleteTempFile();

        _tempFilePath = Path.Combine(Path.GetTempPath(), $"pcap_replay_{Guid.NewGuid():N}.tmp");
        byte[] buffer = new byte[64 * 1024];
        long totalRead = 0;
        int read;

        using (var fs = new FileStream(_tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true))
        {
            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                await fs.WriteAsync(buffer, 0, read, ct);
                totalRead += read;
                onUploadProgress?.Invoke(totalRead, totalBytes);
            }
        }

        return await Task.Run(() =>
        {
            var parsed = ParseCaptureFile(_tempFilePath, fileName);
            lock (_lock)
            {
                _allPackets.Clear();
                _allPackets.AddRange(parsed.Packets);
                _fileInfo = parsed.Info;
                ApplyFilters(_config);
            }
            return _fileInfo;
        }, ct);
    }

    private (List<ReplayPacket> Packets, CaptureFileInfo Info) ParseCaptureFile(string filePath, string fileName)
    {
        var packets = new List<ReplayPacket>();
        var info = new CaptureFileInfo
        {
            FileName = fileName,
            FileSizeBytes = new FileInfo(filePath).Length
        };

        if (!File.Exists(filePath))
            return (packets, info);

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
        byte[] magicBuf = new byte[4];
        if (fs.Read(magicBuf, 0, 4) < 4)
            return (packets, info);

        uint magic = BitConverter.ToUInt32(magicBuf, 0);

        if (magic is 0xa1b2c3d4 or 0xd4c3b2a1 or 0xa1b23c4d or 0x4d3cb2a1)
        {
            info.Format = magic is 0xa1b23c4d or 0x4d3cb2a1 ? "PCAP Clásico (Nanosegundos)" : "PCAP Clásico (Microsegundos)";
            fs.Seek(0, SeekOrigin.Begin);
            ParseClassicPcap(fs, magic, packets);
        }
        else if (magic == 0x0A0D0D0A)
        {
            info.Format = "Wireshark PCAPNG";
            fs.Seek(0, SeekOrigin.Begin);
            ParsePcapNg(fs, packets);
        }
        else
        {
            // Intentar JSON exportado de Wireshark
            fs.Seek(0, SeekOrigin.Begin);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            string text = sr.ReadToEnd();
            if (text.TrimStart().StartsWith("["))
            {
                info.Format = "Wireshark JSON";
                ParseWiresharkJson(text, packets);
            }
            else
            {
                info.Format = "Texto / Hex Dump";
                ParseHexDump(text, packets);
            }
        }

        // Calcular estadísticas del fichero
        info.TotalPackets = packets.Count;
        info.UdpPackets = packets.Count(p => p.Protocol == "UDP");
        info.TcpPackets = packets.Count(p => p.Protocol == "TCP");
        info.OtherPackets = packets.Count - info.UdpPackets - info.TcpPackets;

        if (packets.Count > 0)
        {
            info.FirstPacketTime = packets[0].Timestamp;
            info.LastPacketTime = packets[^1].Timestamp;
            info.UniqueDestinationIps = packets
                .Where(p => !string.IsNullOrEmpty(p.DestinationIp))
                .Select(p => p.DestinationIp)
                .Distinct()
                .Take(20)
                .ToList();
            info.UniqueDestinationPorts = packets
                .Where(p => p.DestinationPort > 0)
                .Select(p => p.DestinationPort)
                .Distinct()
                .Take(20)
                .ToList();
        }

        return (packets, info);
    }

    private static void ParseClassicPcap(FileStream fs, uint magic, List<ReplayPacket> packets)
    {
        bool littleEndian = magic is 0xa1b2c3d4 or 0xa1b23c4d;
        bool isNano = magic is 0xa1b23c4d or 0x4d3cb2a1;

        byte[] globalHdr = new byte[24];
        if (fs.Read(globalHdr, 0, 24) < 24) return;

        uint linkType = ReadUInt32(globalHdr, 20, littleEndian);

        byte[] recordHdr = new byte[16];
        int frameNumber = 1;
        DateTime? firstTs = null;
        DateTime lastTs = DateTime.MinValue;

        while (fs.Read(recordHdr, 0, 16) == 16)
        {
            uint tsSec = ReadUInt32(recordHdr, 0, littleEndian);
            uint tsUsec = ReadUInt32(recordHdr, 4, littleEndian);
            uint inclLen = ReadUInt32(recordHdr, 8, littleEndian);

            if (inclLen > 65536) break; // salvaguarda

            long dtTicks = DateTime.UnixEpoch.Ticks + (tsSec * TimeSpan.TicksPerSecond);
            if (isNano)
                dtTicks += tsUsec * 10;
            else
                dtTicks += tsUsec * (TimeSpan.TicksPerSecond / 1000000);

            DateTime pktTime = new DateTime(dtTicks, DateTimeKind.Utc).ToLocalTime();
            if (!firstTs.HasValue) firstTs = pktTime;
            TimeSpan relTime = pktTime - firstTs.Value;
            TimeSpan deltaTime = lastTs == DateTime.MinValue ? TimeSpan.Zero : (pktTime > lastTs ? pktTime - lastTs : TimeSpan.Zero);
            lastTs = pktTime;

            byte[] pktBytes = new byte[inclLen];
            int read = fs.Read(pktBytes, 0, (int)inclLen);
            if (read == (int)inclLen)
            {
                var packet = DecodePacket(pktBytes, frameNumber, pktTime, relTime, deltaTime, linkType);
                if (packet != null)
                {
                    packets.Add(packet);
                    frameNumber++;
                }
            }
        }
    }

    private static void ParsePcapNg(FileStream fs, List<ReplayPacket> packets)
    {
        fs.Seek(0, SeekOrigin.Begin);
        bool littleEndian = true;
        uint defaultLinkType = 1; // Ethernet
        int frameNumber = 1;
        DateTime? firstTs = null;
        DateTime lastTs = DateTime.MinValue;

        byte[] blkHdr = new byte[8];

        while (fs.Read(blkHdr, 0, 8) == 8)
        {
            uint blockType = ReadUInt32(blkHdr, 0, littleEndian);
            uint blockLen = ReadUInt32(blkHdr, 4, littleEndian);

            if (blockLen < 12 || blockLen > 10 * 1024 * 1024) break;
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
            else if (blockType == 0x00000001) // Interface Description Block
            {
                byte[] idb = new byte[Math.Min(bodyLen, 16)];
                if (fs.Read(idb, 0, idb.Length) == idb.Length)
                {
                    defaultLinkType = ReadUInt16(idb, 0, littleEndian);
                    if (bodyLen > idb.Length)
                        fs.Seek(bodyLen - idb.Length, SeekOrigin.Current);
                }
                else break;
            }
            else if (blockType == 0x00000006) // Enhanced Packet Block
            {
                byte[] epbHeader = new byte[20];
                if (fs.Read(epbHeader, 0, 20) == 20)
                {
                    uint tsHigh = ReadUInt32(epbHeader, 4, littleEndian);
                    uint tsLow = ReadUInt32(epbHeader, 8, littleEndian);
                    uint capLen = ReadUInt32(epbHeader, 12, littleEndian);

                    ulong rawTs = ((ulong)tsHigh << 32) | tsLow;
                    DateTime pktTime = DateTime.UnixEpoch.AddMicroseconds(rawTs).ToLocalTime();
                    if (!firstTs.HasValue) firstTs = pktTime;
                    TimeSpan relTime = pktTime - firstTs.Value;
                    TimeSpan deltaTime = lastTs == DateTime.MinValue ? TimeSpan.Zero : (pktTime > lastTs ? pktTime - lastTs : TimeSpan.Zero);
                    lastTs = pktTime;

                    int packetDataLen = (int)Math.Min(capLen, (uint)(bodyLen - 20));
                    int padLen = (4 - (packetDataLen % 4)) % 4;
                    int remaining = bodyLen - 20 - packetDataLen - padLen;

                    byte[] pktBytes = new byte[packetDataLen];
                    fs.ReadExactly(pktBytes, 0, packetDataLen);
                    if (padLen + remaining > 0)
                        fs.Seek(padLen + remaining, SeekOrigin.Current);

                    var packet = DecodePacket(pktBytes, frameNumber, pktTime, relTime, deltaTime, defaultLinkType);
                    if (packet != null)
                    {
                        packets.Add(packet);
                        frameNumber++;
                    }
                }
                else break;
            }
            else if (blockType == 0x00000003) // Simple Packet Block
            {
                byte[] spbHdr = new byte[4];
                if (fs.Read(spbHdr, 0, 4) == 4)
                {
                    uint capLen = ReadUInt32(spbHdr, 0, littleEndian);
                    int packetDataLen = (int)Math.Min(capLen, (uint)(bodyLen - 4));
                    int padLen = (4 - (packetDataLen % 4)) % 4;
                    int remaining = bodyLen - 4 - packetDataLen - padLen;

                    byte[] pktBytes = new byte[packetDataLen];
                    fs.ReadExactly(pktBytes, 0, packetDataLen);
                    if (padLen + remaining > 0)
                        fs.Seek(padLen + remaining, SeekOrigin.Current);

                    DateTime pktTime = DateTime.Now;
                    if (!firstTs.HasValue) firstTs = pktTime;
                    TimeSpan relTime = pktTime - firstTs.Value;
                    TimeSpan deltaTime = lastTs == DateTime.MinValue ? TimeSpan.Zero : (pktTime > lastTs ? pktTime - lastTs : TimeSpan.Zero);
                    lastTs = pktTime;

                    var packet = DecodePacket(pktBytes, frameNumber, pktTime, relTime, deltaTime, defaultLinkType);
                    if (packet != null)
                    {
                        packets.Add(packet);
                        frameNumber++;
                    }
                }
                else break;
            }
            else
            {
                fs.Seek(bodyLen, SeekOrigin.Current);
            }
        }
    }

    private static void ParseWiresharkJson(string json, List<ReplayPacket> packets)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

            int frameNumber = 1;
            DateTime? firstTs = null;
            DateTime lastTs = DateTime.MinValue;

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (!el.TryGetProperty("_source", out var src)) continue;
                if (!src.TryGetProperty("layers", out var layers)) continue;

                DateTime pktTime = DateTime.Now;
                if (layers.TryGetProperty("frame", out var fl) && fl.TryGetProperty("frame.time_epoch", out var epEl))
                {
                    if (double.TryParse(epEl.GetString(), System.Globalization.CultureInfo.InvariantCulture, out double ep))
                        pktTime = DateTime.UnixEpoch.AddSeconds(ep).ToLocalTime();
                }

                if (!firstTs.HasValue) firstTs = pktTime;
                TimeSpan relTime = pktTime - firstTs.Value;
                TimeSpan deltaTime = lastTs == DateTime.MinValue ? TimeSpan.Zero : (pktTime > lastTs ? pktTime - lastTs : TimeSpan.Zero);
                lastTs = pktTime;

                string srcIp = "", dstIp = "";
                if (layers.TryGetProperty("ip", out var ipL))
                {
                    if (ipL.TryGetProperty("ip.src", out var sEl)) srcIp = sEl.GetString() ?? "";
                    if (ipL.TryGetProperty("ip.dst", out var dEl)) dstIp = dEl.GetString() ?? "";
                }

                string protocol = "UDP";
                ushort srcPort = 0, dstPort = 0;
                byte[] payload = Array.Empty<byte>();

                if (layers.TryGetProperty("udp", out var udpL))
                {
                    protocol = "UDP";
                    if (udpL.TryGetProperty("udp.srcport", out var spEl) && ushort.TryParse(spEl.GetString(), out var sp)) srcPort = sp;
                    if (udpL.TryGetProperty("udp.dstport", out var dpEl) && ushort.TryParse(dpEl.GetString(), out var dp)) dstPort = dp;
                    if (udpL.TryGetProperty("udp.payload", out var pEl)) payload = ParseHexString(pEl.GetString());
                }
                else if (layers.TryGetProperty("tcp", out var tcpL))
                {
                    protocol = "TCP";
                    if (tcpL.TryGetProperty("tcp.srcport", out var spEl) && ushort.TryParse(spEl.GetString(), out var sp)) srcPort = sp;
                    if (tcpL.TryGetProperty("tcp.dstport", out var dpEl) && ushort.TryParse(dpEl.GetString(), out var dp)) dstPort = dp;
                    if (tcpL.TryGetProperty("tcp.payload", out var pEl)) payload = ParseHexString(pEl.GetString());
                }
                else if (layers.TryGetProperty("data", out var dataL) && dataL.TryGetProperty("data.data", out var hexEl))
                {
                    payload = ParseHexString(hexEl.GetString());
                }

                packets.Add(new ReplayPacket
                {
                    Index = frameNumber,
                    OriginalFrameNumber = frameNumber,
                    Timestamp = pktTime,
                    RelativeTime = relTime,
                    DeltaTime = deltaTime,
                    Protocol = protocol,
                    SourceIp = srcIp,
                    SourcePort = srcPort,
                    DestinationIp = dstIp,
                    DestinationPort = dstPort,
                    PacketLength = payload.Length,
                    Payload = payload,
                    RawBytes = payload,
                    Info = BuildPacketDescription(protocol, srcPort, dstPort, payload)
                });

                frameNumber++;
            }
        }
        catch { }
    }

    private static void ParseHexDump(string text, List<ReplayPacket> packets)
    {
        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        int frameNumber = 1;
        DateTime start = DateTime.Now;
        var sb = new StringBuilder();

        foreach (var line in lines)
        {
            var clean = line.Trim();
            if (clean.Contains(';') || clean.StartsWith("FRAME", StringComparison.OrdinalIgnoreCase))
            {
                if (sb.Length > 0)
                {
                    byte[] data = ParseHexString(sb.ToString());
                    if (data.Length > 0)
                    {
                        packets.Add(new ReplayPacket
                        {
                            Index = frameNumber,
                            OriginalFrameNumber = frameNumber,
                            Timestamp = start.AddMilliseconds((frameNumber - 1) * 50),
                            RelativeTime = TimeSpan.FromMilliseconds((frameNumber - 1) * 50),
                            DeltaTime = TimeSpan.FromMilliseconds(50),
                            Protocol = "UDP",
                            PacketLength = data.Length,
                            Payload = data,
                            RawBytes = data,
                            Info = $"Hex Payload ({data.Length} B)"
                        });
                        frameNumber++;
                    }
                    sb.Clear();
                }
            }
            sb.Append(clean).Append(' ');
        }

        if (sb.Length > 0)
        {
            byte[] data = ParseHexString(sb.ToString());
            if (data.Length > 0)
            {
                packets.Add(new ReplayPacket
                {
                    Index = frameNumber,
                    OriginalFrameNumber = frameNumber,
                    Timestamp = start.AddMilliseconds((frameNumber - 1) * 50),
                    RelativeTime = TimeSpan.FromMilliseconds((frameNumber - 1) * 50),
                    DeltaTime = TimeSpan.FromMilliseconds(50),
                    Protocol = "UDP",
                    PacketLength = data.Length,
                    Payload = data,
                    RawBytes = data,
                    Info = $"Hex Payload ({data.Length} B)"
                });
            }
        }
    }

    // ─────────────────────────────────────────────────────────────
    // DECODIFICACIÓN DE PAQUETES DE RED
    // ─────────────────────────────────────────────────────────────

    private static ReplayPacket? DecodePacket(
        byte[] pktBytes, 
        int frameNumber, 
        DateTime timestamp, 
        TimeSpan relativeTime, 
        TimeSpan deltaTime, 
        uint linkType)
    {
        if (pktBytes.Length < 14) return null;

        int etherType;
        int ipOffset;

        if (linkType == 1) // Ethernet
        {
            etherType = (pktBytes[12] << 8) | pktBytes[13];
            ipOffset = 14;

            if (etherType == 0x8100 || etherType == 0x88A8) // VLAN Tag
            {
                if (pktBytes.Length < 18) return null;
                etherType = (pktBytes[16] << 8) | pktBytes[17];
                ipOffset = 18;
            }
        }
        else if (linkType == 113) // Linux SLL (Cooked capture)
        {
            if (pktBytes.Length < 16) return null;
            etherType = (pktBytes[14] << 8) | pktBytes[15];
            ipOffset = 16;
        }
        else if (linkType == 276) // Linux SLL2
        {
            if (pktBytes.Length < 20) return null;
            etherType = (pktBytes[0] << 8) | pktBytes[1];
            ipOffset = 20;
        }
        else // Raw IP fallback
        {
            etherType = 0x0800;
            ipOffset = 0;
        }

        string protocol = "OTHER";
        string srcIp = "";
        string dstIp = "";
        ushort srcPort = 0;
        ushort dstPort = 0;
        byte[] payload = Array.Empty<byte>();

        if (etherType == 0x0800 && pktBytes.Length >= ipOffset + 20) // IPv4
        {
            int ipHeaderLen = (pktBytes[ipOffset] & 0x0F) * 4;
            if (ipHeaderLen < 20 || pktBytes.Length < ipOffset + ipHeaderLen) return null;

            byte protoByte = pktBytes[ipOffset + 9];
            srcIp = $"{pktBytes[ipOffset + 12]}.{pktBytes[ipOffset + 13]}.{pktBytes[ipOffset + 14]}.{pktBytes[ipOffset + 15]}";
            dstIp = $"{pktBytes[ipOffset + 16]}.{pktBytes[ipOffset + 17]}.{pktBytes[ipOffset + 18]}.{pktBytes[ipOffset + 19]}";

            int transportOffset = ipOffset + ipHeaderLen;

            if (protoByte == 17 && pktBytes.Length >= transportOffset + 8) // UDP
            {
                protocol = "UDP";
                srcPort = (ushort)((pktBytes[transportOffset] << 8) | pktBytes[transportOffset + 1]);
                dstPort = (ushort)((pktBytes[transportOffset + 2] << 8) | pktBytes[transportOffset + 3]);
                ushort udpLen = (ushort)((pktBytes[transportOffset + 4] << 8) | pktBytes[transportOffset + 5]);

                int payloadOffset = transportOffset + 8;
                int payloadLen = Math.Min(udpLen - 8, pktBytes.Length - payloadOffset);
                if (payloadLen > 0)
                {
                    payload = new byte[payloadLen];
                    Buffer.BlockCopy(pktBytes, payloadOffset, payload, 0, payloadLen);
                }
            }
            else if (protoByte == 6 && pktBytes.Length >= transportOffset + 20) // TCP
            {
                protocol = "TCP";
                srcPort = (ushort)((pktBytes[transportOffset] << 8) | pktBytes[transportOffset + 1]);
                dstPort = (ushort)((pktBytes[transportOffset + 2] << 8) | pktBytes[transportOffset + 3]);
                int tcpHeaderLen = ((pktBytes[transportOffset + 12] >> 4) & 0x0F) * 4;

                int payloadOffset = transportOffset + tcpHeaderLen;
                int payloadLen = Math.Max(0, pktBytes.Length - payloadOffset);
                if (payloadLen > 0)
                {
                    payload = new byte[payloadLen];
                    Buffer.BlockCopy(pktBytes, payloadOffset, payload, 0, payloadLen);
                }
            }
            else if (protoByte == 1)
            {
                protocol = "ICMP";
                payload = pktBytes.AsSpan(transportOffset).ToArray();
            }
            else if (protoByte == 2)
            {
                protocol = "IGMP";
                payload = pktBytes.AsSpan(transportOffset).ToArray();
            }
        }
        else if (etherType == 0x0806) // ARP
        {
            protocol = "ARP";
            payload = pktBytes.AsSpan(ipOffset).ToArray();
        }
        else
        {
            protocol = $"ETH 0x{etherType:X4}";
            payload = pktBytes.AsSpan(ipOffset).ToArray();
        }

        return new ReplayPacket
        {
            Index = frameNumber,
            OriginalFrameNumber = frameNumber,
            Timestamp = timestamp,
            RelativeTime = relativeTime,
            DeltaTime = deltaTime,
            Protocol = protocol,
            SourceIp = srcIp,
            SourcePort = srcPort,
            DestinationIp = dstIp,
            DestinationPort = dstPort,
            PacketLength = pktBytes.Length,
            Payload = payload,
            RawBytes = pktBytes,
            Info = BuildPacketDescription(protocol, srcPort, dstPort, payload)
        };
    }

    private static string BuildPacketDescription(string protocol, ushort srcPort, ushort dstPort, byte[] payload)
    {
        if (protocol == "UDP")
        {
            // Detectar TRDP (Puerto 17224 o firma 'P' a offset 6)
            if (dstPort == 17224 || srcPort == 17224 || (payload.Length >= 32 && payload.Length > 6 && payload[6] == 0x50))
            {
                if (payload.Length >= 12)
                {
                    uint comId = (uint)((payload[8] << 24) | (payload[9] << 16) | (payload[10] << 8) | payload[11]);
                    return $"TRDP PD ComID:{comId} ({payload.Length} B)";
                }
                return $"TRDP Trama ({payload.Length} B)";
            }

            // Detectar DHCP
            if (dstPort == 67 || dstPort == 68 || srcPort == 67 || srcPort == 68)
            {
                return $"DHCP Bootp ({payload.Length} B)";
            }

            // Detectar Syslog
            if (dstPort == 514 || srcPort == 514)
            {
                return $"Syslog ({payload.Length} B)";
            }

            // Detectar DNS / NTP
            if (dstPort == 53 || srcPort == 53) return $"DNS ({payload.Length} B)";
            if (dstPort == 123 || srcPort == 123) return $"NTP ({payload.Length} B)";

            return $"UDP :{srcPort} -> :{dstPort} ({payload.Length} B)";
        }

        if (protocol == "TCP")
        {
            return $"TCP :{srcPort} -> :{dstPort} ({payload.Length} B)";
        }

        return $"{protocol} ({payload.Length} B)";
    }

    // ─────────────────────────────────────────────────────────────
    // FILTROS Y PLAYLIST
    // ─────────────────────────────────────────────────────────────

    public void ApplyFilters(PacketReplayConfig config)
    {
        lock (_lock)
        {
            _config = config;
            var query = _allPackets.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(config.ProtocolFilter) && config.ProtocolFilter != "ALL")
            {
                query = query.Where(p => p.Protocol.Equals(config.ProtocolFilter, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(config.IpFilter))
            {
                query = query.Where(p => p.SourceIp.Contains(config.IpFilter, StringComparison.OrdinalIgnoreCase) ||
                                         p.DestinationIp.Contains(config.IpFilter, StringComparison.OrdinalIgnoreCase));
            }

            if (config.PortFilter.HasValue && config.PortFilter.Value > 0)
            {
                query = query.Where(p => p.SourcePort == config.PortFilter.Value || p.DestinationPort == config.PortFilter.Value);
            }

            _playlist = query.ToList();
            for (int i = 0; i < _playlist.Count; i++)
            {
                _playlist[i].Index = i + 1;
            }

            _currentIndex = Math.Clamp(_currentIndex, 0, Math.Max(0, _playlist.Count - 1));
        }
    }

    // ─────────────────────────────────────────────────────────────
    // MOTOR DE TRANSMISIÓN / REPLAY EN RED
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Inicia la reproducción de las tramas cargadas a través de la red física.
    /// </summary>
    public (bool Success, string Message) StartPlayback(PacketReplayConfig config)
    {
        lock (_lock)
        {
            if (_stats.Status == ReplayStatus.Playing)
                return (false, "La reproducción ya se encuentra activa.");

            if (_playlist.Count == 0)
                return (false, "No hay tramas en la lista para reproducir.");

            _config = config;

            try
            {
                InitializeSocket(config.BindAdapterIp, config.MulticastTtl);
            }
            catch (Exception ex)
            {
                return (false, $"Error al inicializar socket de red: {ex.Message}");
            }

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _isPaused = false;
            _stats.Status = ReplayStatus.Playing;
            _stats.PacketsSent = 0;
            _stats.PacketsFailed = 0;
            _stats.TotalBytesSent = 0;
            _stats.ElapsedRealTime = TimeSpan.Zero;

            OnStatusChanged?.Invoke(ReplayStatus.Playing);
            OnLogMessage?.Invoke($"[PacketReplay] Reproducción iniciada con {_playlist.Count} tramas a velocidad {GetSpeedDescription(config)}.");

            _playbackTask = Task.Run(() => PlaybackLoopAsync(_cts.Token));
            return (true, "Reproducción iniciada correctamente.");
        }
    }

    public void PausePlayback()
    {
        lock (_lock)
        {
            if (_stats.Status == ReplayStatus.Playing && !_isPaused)
            {
                _isPaused = true;
                _stats.Status = ReplayStatus.Paused;
                OnStatusChanged?.Invoke(ReplayStatus.Paused);
                OnLogMessage?.Invoke($"[PacketReplay] Reproducción pausada en trama #{_currentIndex + 1}.");
            }
        }
    }

    public void ResumePlayback()
    {
        lock (_lock)
        {
            if (_isPaused)
            {
                _isPaused = false;
                _stats.Status = ReplayStatus.Playing;
                OnStatusChanged?.Invoke(ReplayStatus.Playing);
                OnLogMessage?.Invoke($"[PacketReplay] Reproducción reanudada.");
            }
        }
    }

    public void StopPlayback()
    {
        lock (_lock)
        {
            _cts?.Cancel();
            _isPaused = false;
            _stats.Status = ReplayStatus.Idle;
            _currentIndex = 0;
            _stats.CurrentPps = 0;
            _stats.CurrentBitrateKbps = 0;
            CloseSocket();
            OnStatusChanged?.Invoke(ReplayStatus.Idle);
            OnPlaybackProgress?.Invoke(Stats);
            OnLogMessage?.Invoke("[PacketReplay] Reproducción detenida y reiniciada.");
        }
    }

    public void SeekTo(int index)
    {
        lock (_lock)
        {
            if (_playlist.Count > 0)
            {
                _currentIndex = Math.Clamp(index, 0, _playlist.Count - 1);
                _stats.CurrentCaptureRelativeTime = _playlist[_currentIndex].RelativeTime;
                OnPlaybackProgress?.Invoke(Stats);
            }
        }
    }

    /// <summary>
    /// Envía un solo paquete individual (paso a paso) y avanza el cursor.
    /// </summary>
    public (bool Success, string Message) StepPacket()
    {
        lock (_lock)
        {
            if (_playlist.Count == 0) return (false, "No hay tramas.");
            if (_currentIndex >= _playlist.Count) _currentIndex = 0;

            try
            {
                InitializeSocket(_config.BindAdapterIp, _config.MulticastTtl);
            }
            catch (Exception ex)
            {
                return (false, $"Error de socket: {ex.Message}");
            }

            var pkt = _playlist[_currentIndex];
            bool ok = TransmitPacket(pkt, _config, out string err);

            if (ok)
            {
                _stats.PacketsSent++;
                _stats.TotalBytesSent += pkt.Payload.Length;
                _stats.CurrentCaptureRelativeTime = pkt.RelativeTime;
                _stats.CurrentPacketDescription = $"Trama #{pkt.Index}: {pkt.Info} -> {GetTargetEndPoint(pkt, _config)}";
                _currentIndex = (_currentIndex + 1) % _playlist.Count;
                OnPlaybackProgress?.Invoke(Stats);
                return (true, $"Trama #{pkt.Index} enviada.");
            }
            else
            {
                _stats.PacketsFailed++;
                OnPlaybackProgress?.Invoke(Stats);
                return (false, $"Error enviando trama #{pkt.Index}: {err}");
            }
        }
    }

    /// <summary>
    /// Envía una trama específica seleccionada de la tabla.
    /// </summary>
    public (bool Success, string Message) SendSinglePacket(ReplayPacket packet, PacketReplayConfig? overrideConfig = null)
    {
        var cfg = overrideConfig ?? _config;
        try
        {
            InitializeSocket(cfg.BindAdapterIp, cfg.MulticastTtl);
            bool ok = TransmitPacket(packet, cfg, out string err);
            if (ok)
            {
                return (true, $"Trama #{packet.Index} enviada con éxito ({packet.Payload.Length} bytes a {GetTargetEndPoint(packet, cfg)}).");
            }
            return (false, $"Error al enviar trama #{packet.Index}: {err}");
        }
        catch (Exception ex)
        {
            return (false, $"Excepción al enviar trama: {ex.Message}");
        }
    }

    // ─────────────────────────────────────────────────────────────
    // BUCLE PRINCIPAL DE REPRODUCCIÓN (HIGH PRECISION TIMER)
    // ─────────────────────────────────────────────────────────────

    private async Task PlaybackLoopAsync(CancellationToken ct)
    {
        var wallClock = Stopwatch.StartNew();
        var statsTimer = Stopwatch.StartNew();
        int intervalPacketsSent = 0;
        long intervalBytesSent = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Manejar pausa
                while (_isPaused && !ct.IsCancellationRequested)
                {
                    await Task.Delay(50, ct);
                }

                if (ct.IsCancellationRequested) break;

                ReplayPacket currentPacket;
                ReplayPacket? prevPacket;
                int idx;

                lock (_lock)
                {
                    if (_currentIndex >= _playlist.Count)
                    {
                        if (_config.LoopPlayback && _playlist.Count > 0)
                        {
                            _currentIndex = 0;
                            OnLogMessage?.Invoke("[PacketReplay] Reiniciando bucle de reproducción continuo.");
                        }
                        else
                        {
                            _stats.Status = ReplayStatus.Completed;
                            break;
                        }
                    }

                    idx = _currentIndex;
                    currentPacket = _playlist[idx];
                    prevPacket = idx > 0 ? _playlist[idx - 1] : null;
                }

                // Cálculo del retardo según el modo de velocidad
                if (_config.SpeedMode == PlaybackSpeedMode.RealTime)
                {
                    TimeSpan delta = prevPacket != null && currentPacket.Timestamp >= prevPacket.Timestamp
                        ? currentPacket.Timestamp - prevPacket.Timestamp
                        : TimeSpan.FromMilliseconds(10);

                    double multiplier = Math.Max(0.01, _config.SpeedMultiplier);
                    double targetWaitMs = delta.TotalMilliseconds / multiplier;

                    if (targetWaitMs > 0)
                    {
                        // Espera híbrida: Task.Delay para intervalos largos + Stopwatch para precisión sub-milisegundo
                        if (targetWaitMs > 15)
                        {
                            await Task.Delay((int)(targetWaitMs - 8), ct);
                        }

                        var sw = Stopwatch.StartNew();
                        double remaining = targetWaitMs > 15 ? 8 : targetWaitMs;
                        while (sw.Elapsed.TotalMilliseconds < remaining && !ct.IsCancellationRequested)
                        {
                            Thread.SpinWait(10);
                        }
                    }
                }
                else if (_config.SpeedMode == PlaybackSpeedMode.FixedInterval)
                {
                    if (_config.FixedIntervalMs > 0)
                    {
                        await Task.Delay(_config.FixedIntervalMs, ct);
                    }
                }
                else // Modo Ráfaga (Burst)
                {
                    if (idx % 30 == 0)
                    {
                        await Task.Yield();
                    }
                }

                if (ct.IsCancellationRequested) break;

                // Transmisión del paquete
                bool ok = TransmitPacket(currentPacket, _config, out string error);

                lock (_lock)
                {
                    if (ok)
                    {
                        _stats.PacketsSent++;
                        _stats.TotalBytesSent += currentPacket.Payload.Length;
                        intervalPacketsSent++;
                        intervalBytesSent += currentPacket.Payload.Length;
                    }
                    else
                    {
                        _stats.PacketsFailed++;
                    }

                    _stats.CurrentCaptureRelativeTime = currentPacket.RelativeTime;
                    _stats.ElapsedRealTime = wallClock.Elapsed;
                    _stats.CurrentPacketDescription = $"#{currentPacket.Index} {currentPacket.Protocol} ({currentPacket.Payload.Length} B)";

                    _currentIndex++;
                }

                // Actualizar métricas cada 100ms
                if (statsTimer.ElapsedMilliseconds >= 100)
                {
                    double elapsedSec = statsTimer.Elapsed.TotalSeconds;
                    lock (_lock)
                    {
                        _stats.CurrentPps = elapsedSec > 0 ? intervalPacketsSent / elapsedSec : 0;
                        _stats.CurrentBitrateKbps = elapsedSec > 0 ? (intervalBytesSent * 8.0 / 1000.0) / elapsedSec : 0;
                        _stats.ElapsedRealTime = wallClock.Elapsed;
                    }
                    intervalPacketsSent = 0;
                    intervalBytesSent = 0;
                    statsTimer.Restart();

                    OnPlaybackProgress?.Invoke(Stats);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            OnLogMessage?.Invoke($"[PacketReplay] Error en bucle: {ex.Message}");
        }
        finally
        {
            lock (_lock)
            {
                if (_stats.Status == ReplayStatus.Playing)
                {
                    _stats.Status = ReplayStatus.Completed;
                }
                _stats.CurrentPps = 0;
                _stats.CurrentBitrateKbps = 0;
                OnStatusChanged?.Invoke(_stats.Status);
                OnPlaybackProgress?.Invoke(Stats);
                OnLogMessage?.Invoke($"[PacketReplay] Reproducción finalizada. Total enviados: {_stats.PacketsSent} ({_stats.TotalBytesSent} bytes).");
            }
        }
    }

    private bool TransmitPacket(ReplayPacket packet, PacketReplayConfig config, out string error)
    {
        error = string.Empty;
        if (packet.Payload.Length == 0 && packet.RawBytes.Length == 0)
        {
            error = "Paquete vacío.";
            return false;
        }

        try
        {
            IPEndPoint targetEp = GetTargetEndPoint(packet, config);
            byte[] bytesToSend = packet.Payload.Length > 0 ? packet.Payload : packet.RawBytes;

            if (_udpSocket == null)
            {
                error = "Socket no inicializado.";
                return false;
            }

            _udpSocket.SendTo(bytesToSend, targetEp);
            packet.Sent = true;
            packet.ErrorMessage = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            packet.ErrorMessage = ex.Message;
            return false;
        }
    }

    private static IPEndPoint GetTargetEndPoint(ReplayPacket packet, PacketReplayConfig config)
    {
        IPAddress targetIp;
        int targetPort;

        if (config.OverrideDestination)
        {
            if (!IPAddress.TryParse(config.TargetIp, out targetIp!))
            {
                targetIp = IPAddress.Broadcast;
            }
            targetPort = config.TargetPort ?? (packet.DestinationPort > 0 ? packet.DestinationPort : 17224);
        }
        else
        {
            if (!IPAddress.TryParse(packet.DestinationIp, out targetIp!) || IPAddress.Any.Equals(targetIp))
            {
                targetIp = IPAddress.Broadcast;
            }
            targetPort = packet.DestinationPort > 0 ? packet.DestinationPort : (config.TargetPort ?? 17224);
        }

        return new IPEndPoint(targetIp, targetPort);
    }

    private void InitializeSocket(string bindAdapterIp, int multicastTtl)
    {
        if (_udpSocket != null && _boundAdapterIp == bindAdapterIp)
            return;

        CloseSocket();

        _udpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _udpSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udpSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);

        if (!string.IsNullOrWhiteSpace(bindAdapterIp) && IPAddress.TryParse(bindAdapterIp, out var localIp) && !IPAddress.Any.Equals(localIp))
        {
            try
            {
                _udpSocket.Bind(new IPEndPoint(localIp, 0));
                _boundAdapterIp = bindAdapterIp;
            }
            catch
            {
                _udpSocket.Bind(new IPEndPoint(IPAddress.Any, 0));
                _boundAdapterIp = string.Empty;
            }
        }
        else
        {
            _udpSocket.Bind(new IPEndPoint(IPAddress.Any, 0));
            _boundAdapterIp = string.Empty;
        }

        _udpSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, Math.Clamp(multicastTtl, 1, 255));
        _udpSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
    }

    private void CloseSocket()
    {
        try
        {
            _udpSocket?.Close();
            _udpSocket?.Dispose();
        }
        catch { }
        finally
        {
            _udpSocket = null;
            _boundAdapterIp = string.Empty;
        }
    }

    // ─────────────────────────────────────────────────────────────
    // ENVÍO DE PAQUETE MANUAL (PACKET SENDER)
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Envía un paquete UDP o TCP manual con payload en ASCII o HEX (estilo Packet Sender).
    /// </summary>
    public async Task<ManualPacketResult> SendManualPacketAsync(ManualPacketRequest req, string bindAdapterIp = "")
    {
        var result = new ManualPacketResult
        {
            Protocol = req.Protocol,
            Target = $"{req.DestinationIp}:{req.DestinationPort}"
        };

        if (!IPAddress.TryParse(req.DestinationIp, out var targetIp))
        {
            result.Success = false;
            result.Message = "Dirección IP destino inválida.";
            return result;
        }

        byte[] payload;
        try
        {
            if (req.DataFormat.Equals("ASCII", StringComparison.OrdinalIgnoreCase))
            {
                payload = Encoding.UTF8.GetBytes(req.Data);
            }
            else
            {
                payload = ParseHexString(req.Data);
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = $"Formato de datos no válido: {ex.Message}";
            return result;
        }

        if (payload.Length == 0)
        {
            result.Success = false;
            result.Message = "El payload está vacío.";
            return result;
        }

        var sw = Stopwatch.StartNew();

        try
        {
            int reps = Math.Max(1, Math.Min(req.RepeatCount, 100));
            int sentTotal = 0;

            if (req.Protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase))
            {
                using var tcpClient = new TcpClient();
                if (!string.IsNullOrWhiteSpace(bindAdapterIp) && IPAddress.TryParse(bindAdapterIp, out var localIp))
                {
                    try { tcpClient.Client.Bind(new IPEndPoint(localIp, 0)); } catch { }
                }

                await tcpClient.ConnectAsync(targetIp, req.DestinationPort);
                using var stream = tcpClient.GetStream();

                for (int i = 0; i < reps; i++)
                {
                    await stream.WriteAsync(payload, 0, payload.Length);
                    sentTotal += payload.Length;
                    if (reps > 1 && req.RepeatDelayMs > 0)
                        await Task.Delay(req.RepeatDelayMs);
                }
            }
            else // UDP
            {
                using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                udp.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
                if (!string.IsNullOrWhiteSpace(bindAdapterIp) && IPAddress.TryParse(bindAdapterIp, out var localIp))
                {
                    try { udp.Bind(new IPEndPoint(localIp, 0)); } catch { }
                }

                var ep = new IPEndPoint(targetIp, req.DestinationPort);
                for (int i = 0; i < reps; i++)
                {
                    udp.SendTo(payload, ep);
                    sentTotal += payload.Length;
                    if (reps > 1 && req.RepeatDelayMs > 0)
                        await Task.Delay(req.RepeatDelayMs);
                }
            }

            sw.Stop();
            result.Success = true;
            result.BytesSent = sentTotal;
            result.RoundTripMs = sw.ElapsedMilliseconds;
            result.Message = $"Enviado(s) {reps} paquete(s) con éxito ({sentTotal} bytes totales) en {sw.ElapsedMilliseconds} ms.";
        }
        catch (Exception ex)
        {
            sw.Stop();
            result.Success = false;
            result.RoundTripMs = sw.ElapsedMilliseconds;
            result.Message = $"Fallo al enviar: {ex.Message}";
        }

        lock (ManualHistory)
        {
            ManualHistory.Insert(0, result);
            if (ManualHistory.Count > 50) ManualHistory.RemoveAt(ManualHistory.Count - 1);
        }

        return result;
    }

    // ─────────────────────────────────────────────────────────────
    // UTILIDADES AUXILIARES
    // ─────────────────────────────────────────────────────────────

    private static string GetSpeedDescription(PacketReplayConfig cfg)
    {
        return cfg.SpeedMode switch
        {
            PlaybackSpeedMode.RealTime => $"Tiempo Real ({cfg.SpeedMultiplier:F2}x)",
            PlaybackSpeedMode.Burst => "Ráfaga Máxima",
            PlaybackSpeedMode.FixedInterval => $"Intervalo fijo ({cfg.FixedIntervalMs} ms)",
            _ => "Normal"
        };
    }

    private static uint ReadUInt32(byte[] b, int offset, bool littleEndian)
    {
        if (littleEndian)
            return (uint)(b[offset] | (b[offset + 1] << 8) | (b[offset + 2] << 16) | (b[offset + 3] << 24));
        return (uint)((b[offset] << 24) | (b[offset + 1] << 16) | (b[offset + 2] << 8) | b[offset + 3]);
    }

    private static ushort ReadUInt16(byte[] b, int offset, bool littleEndian)
    {
        if (littleEndian)
            return (ushort)(b[offset] | (b[offset + 1] << 8));
        return (ushort)((b[offset] << 8) | b[offset + 1]);
    }

    public static byte[] ParseHexString(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Array.Empty<byte>();
        var clean = hex.Replace(":", "").Replace("-", "").Replace(" ", "").Replace("\t", "").Replace("\r", "").Replace("\n", "").Trim();
        if (clean.Length % 2 != 0) clean = "0" + clean;

        byte[] bytes = new byte[clean.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(clean.Substring(i * 2, 2), 16);
        }
        return bytes;
    }

    public void DeleteTempFile()
    {
        if (!string.IsNullOrEmpty(_tempFilePath))
        {
            try
            {
                if (File.Exists(_tempFilePath))
                    File.Delete(_tempFilePath);
            }
            catch { }
            _tempFilePath = null;
        }
    }

    public void Dispose()
    {
        StopPlayback();
        DeleteTempFile();
    }
}
