using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ApsMonitor.Models;

public enum PlaybackSpeedMode
{
    RealTime,       // Sincronizado según timestamp con multiplicador de velocidad
    Burst,          // Ráfaga sin retardo (velocidad máxima)
    FixedInterval   // Retardo fijo en milisegundos entre tramas
}

public enum ReplayStatus
{
    Idle,
    Playing,
    Paused,
    Completed
}

/// <summary>
/// Representa un paquete individual extraído de una captura de Wireshark (.pcap, .pcapng, .json)
/// </summary>
public class ReplayPacket
{
    public int Index { get; set; }
    public int OriginalFrameNumber { get; set; }
    public DateTime Timestamp { get; set; }
    public TimeSpan RelativeTime { get; set; }
    public TimeSpan DeltaTime { get; set; }
    public string Protocol { get; set; } = "UDP";
    public string SourceIp { get; set; } = string.Empty;
    public ushort SourcePort { get; set; }
    public string DestinationIp { get; set; } = string.Empty;
    public ushort DestinationPort { get; set; }
    public int PacketLength { get; set; }
    public byte[] Payload { get; set; } = Array.Empty<byte>();
    public byte[] RawBytes { get; set; } = Array.Empty<byte>();
    public string Info { get; set; } = string.Empty;
    public bool IsSelected { get; set; } = true;
    public bool Sent { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>Indica si la trama es un datagrama TRDP detectado.</summary>
    public bool IsTrdp { get; set; }
    /// <summary>FCS / CRC32 original de cabecera TRDP presente en la captura.</summary>
    public uint? TrdpHeaderCrc { get; set; }
    /// <summary>FCS / CRC32 esperado calculado con IEEE 802.3.</summary>
    public uint? TrdpExpectedCrc { get; set; }
    /// <summary>Indica si el CRC32 original de cabecera TRDP coincidía con el calculado.</summary>
    public bool HasValidTrdpCrc { get; set; } = true;

    public string PayloadHex => Convert.ToHexString(Payload);

    public string PayloadPreview => Payload.Length > 24
        ? Convert.ToHexString(Payload.AsSpan(0, 24)) + $"... ({Payload.Length} B)"
        : Convert.ToHexString(Payload);

    public string PayloadAscii
    {
        get
        {
            if (Payload.Length == 0) return string.Empty;
            int take = Math.Min(Payload.Length, 48);
            var sb = new StringBuilder(take);
            for (int i = 0; i < take; i++)
            {
                byte b = Payload[i];
                sb.Append(b >= 32 && b <= 126 ? (char)b : '.');
            }
            if (Payload.Length > 48) sb.Append("...");
            return sb.ToString();
        }
    }
}

/// <summary>
/// Configuración de reproducción del generador / replay de paquetes
/// </summary>
public class PacketReplayConfig
{
    public string BindAdapterIp { get; set; } = string.Empty;
    public bool OverrideDestination { get; set; } = false;
    public string TargetIp { get; set; } = "255.255.255.255";
    public int? TargetPort { get; set; } = null; // null = mantener puerto original del paquete
    public int? BindSourcePort { get; set; } = null; // null o 0 = puerto efímero automático
    public bool PreserveSourcePort { get; set; } = false; // si true, intenta enlazar al puerto origen de la trama (ej. 17224)
    public bool AutoFixTrdpCrc { get; set; } = true; // Recalcular automáticamente CRC32 TRDP (headerFcs y dataFcs)
    public bool EnableBroadcast { get; set; } = true;
    public int MulticastTtl { get; set; } = 32;
    public PlaybackSpeedMode SpeedMode { get; set; } = PlaybackSpeedMode.RealTime;
    public double SpeedMultiplier { get; set; } = 1.0; // 0.1x, 0.25x, 0.5x, 1x, 2x, 5x, 10x, 50x
    public int FixedIntervalMs { get; set; } = 50;
    public bool LoopPlayback { get; set; } = false;
    public string ProtocolFilter { get; set; } = "ALL"; // ALL, UDP, TCP
    public string IpFilter { get; set; } = string.Empty;
    public int? PortFilter { get; set; } = null;
}

/// <summary>
/// Estadísticas y métricas de rendimiento en tiempo real de la reproducción
/// </summary>
public class PacketReplayStats
{
    public ReplayStatus Status { get; set; } = ReplayStatus.Idle;
    public int CurrentIndex { get; set; } = 0;
    public int TotalPackets { get; set; } = 0;
    public int PacketsSent { get; set; } = 0;
    public int PacketsFailed { get; set; } = 0;
    public long TotalBytesSent { get; set; } = 0;
    public double CurrentPps { get; set; } = 0;
    public double CurrentBitrateKbps { get; set; } = 0;
    public TimeSpan ElapsedRealTime { get; set; } = TimeSpan.Zero;
    public TimeSpan CurrentCaptureRelativeTime { get; set; } = TimeSpan.Zero;
    public TimeSpan TotalCaptureDuration { get; set; } = TimeSpan.Zero;
    public double ProgressPercent => TotalPackets > 0 ? Math.Min(100.0, (double)CurrentIndex / TotalPackets * 100.0) : 0;
    public string CurrentPacketDescription { get; set; } = string.Empty;
}

/// <summary>
/// Resumen del fichero de captura Wireshark cargado
/// </summary>
public class CaptureFileInfo
{
    public string FileName { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string Format { get; set; } = string.Empty;
    public int TotalPackets { get; set; }
    public int UdpPackets { get; set; }
    public int TcpPackets { get; set; }
    public int OtherPackets { get; set; }
    public DateTime FirstPacketTime { get; set; }
    public DateTime LastPacketTime { get; set; }
    public TimeSpan TotalDuration => LastPacketTime >= FirstPacketTime ? LastPacketTime - FirstPacketTime : TimeSpan.Zero;
    public List<string> UniqueDestinationIps { get; set; } = new();
    public List<ushort> UniqueDestinationPorts { get; set; } = new();
}

/// <summary>
/// Petición para el envío de paquete individual manual (estilo Packet Sender)
/// </summary>
public class ManualPacketRequest
{
    public string Protocol { get; set; } = "UDP"; // UDP o TCP
    public string DestinationIp { get; set; } = "192.168.1.50";
    public int DestinationPort { get; set; } = 17224;
    public string DataFormat { get; set; } = "HEX"; // HEX o ASCII
    public string Data { get; set; } = "54 52 44 50 00 00 00 01";
    public int RepeatCount { get; set; } = 1;
    public int RepeatDelayMs { get; set; } = 100;
}

/// <summary>
/// Resultado del envío de paquete manual
/// </summary>
public class ManualPacketResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int BytesSent { get; set; }
    public long RoundTripMs { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Protocol { get; set; } = "UDP";
    public string Target { get; set; } = string.Empty;
}
