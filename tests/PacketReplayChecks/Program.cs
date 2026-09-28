using ApsMonitor.Models;
using ApsMonitor.Services;
using System.Net;
using System.Net.Sockets;
using System.Text;

static void Assert(bool cond, string msg)
{
    if (!cond) throw new Exception("ASSERTION FAILED: " + msg);
}

Console.WriteLine("=== Iniciando pruebas de PacketSenderService y Reproductor PCAP ===");

var service = new PacketSenderService();

// 1. Crear un stream PCAP clásico sintético en memoria con 3 tramas UDP (Ethernet + IPv4 + UDP)
using var ms = new MemoryStream();
using var bw = new BinaryWriter(ms);

// Global Header PCAP (24 bytes) - magic 0xa1b2c3d4, version 2.4, linktype 1 (Ethernet)
bw.Write(0xa1b2c3d4); // magic
bw.Write((ushort)2);  // v_major
bw.Write((ushort)4);  // v_minor
bw.Write(0);          // thiszone
bw.Write(0);          // sigfigs
bw.Write(65535);      // snaplen
bw.Write(1);          // linktype (Ethernet)

// Trama 1: UDP TRDP Port 17224, ComID 1001, payload 32 bytes
byte[] trdpPayload = new byte[32];
trdpPayload[6] = 0x50; // 'P'
trdpPayload[8] = 0x00; trdpPayload[9] = 0x00; trdpPayload[10] = 0x03; trdpPayload[11] = 0xE9; // ComID = 1001
byte[] ethIpv4Udp1 = BuildUdpPacket("192.168.1.10", 17224, "239.255.0.1", 17224, trdpPayload);

// Escribir cabecera de registro trama 1 (ts_sec = 1000, ts_usec = 0)
bw.Write(1000);
bw.Write(0);
bw.Write(ethIpv4Udp1.Length);
bw.Write(ethIpv4Udp1.Length);
bw.Write(ethIpv4Udp1);

// Trama 2: UDP Syslog Port 514 (ts_sec = 1000, ts_usec = 200000 -> +200ms)
byte[] syslogPayload = Encoding.ASCII.GetBytes("<134>Test Syslog Message");
byte[] ethIpv4Udp2 = BuildUdpPacket("192.168.1.10", 514, "192.168.1.50", 514, syslogPayload);
bw.Write(1000);
bw.Write(200000);
bw.Write(ethIpv4Udp2.Length);
bw.Write(ethIpv4Udp2.Length);
bw.Write(ethIpv4Udp2);

// Trama 3: UDP DHCP Port 67 -> 68 (ts_sec = 1001, ts_usec = 100000 -> +1.1s)
byte[] dhcpPayload = new byte[64];
dhcpPayload[0] = 0x01; // BootRequest
byte[] ethIpv4Udp3 = BuildUdpPacket("0.0.0.0", 68, "255.255.255.255", 67, dhcpPayload);
bw.Write(1001);
bw.Write(100000);
bw.Write(ethIpv4Udp3.Length);
bw.Write(ethIpv4Udp3.Length);
bw.Write(ethIpv4Udp3);

bw.Flush();
ms.Seek(0, SeekOrigin.Begin);

// 2. Probar carga de captura en el servicio
Console.WriteLine("-> Probando carga de captura PCAP sintética...");
var fileInfo = await service.LoadCaptureAsync(ms, "test_traffic.pcap", ms.Length);

Assert(fileInfo.TotalPackets == 3, $"Se esperaban 3 paquetes, obtenidos {fileInfo.TotalPackets}");
Assert(fileInfo.UdpPackets == 3, $"Se esperaban 3 paquetes UDP, obtenidos {fileInfo.UdpPackets}");
Assert(service.Packets.Count == 3, $"Se esperaban 3 tramas en playlist, obtenidos {service.Packets.Count}");
Console.WriteLine($"   Formato detectado: {fileInfo.Format}");
Console.WriteLine($"   Duración captura: {fileInfo.TotalDuration.TotalSeconds:F2}s");

// 3. Validar decodificación de tramas individuales
var p1 = service.Packets[0];
Assert(p1.Protocol == "UDP", "Protocolo trama 1 debe ser UDP");
Assert(p1.SourceIp == "192.168.1.10" && p1.SourcePort == 17224, "Origen trama 1 incorrecto");
Assert(p1.DestinationIp == "239.255.0.1" && p1.DestinationPort == 17224, "Destino trama 1 incorrecto");
Assert(p1.Info.Contains("TRDP PD ComID:1001"), $"Info TRDP incorrecta: {p1.Info}");
Console.WriteLine($"   Trama 1 OK: {p1.Info} [{p1.SourceIp}:{p1.SourcePort} -> {p1.DestinationIp}:{p1.DestinationPort}]");

var p2 = service.Packets[1];
Assert(p2.Info.Contains("Syslog"), $"Info Syslog incorrecta: {p2.Info}");
Console.WriteLine($"   Trama 2 OK: {p2.Info} [Delta: {p2.DeltaTime.TotalMilliseconds}ms]");

var p3 = service.Packets[2];
Assert(p3.Info.Contains("DHCP"), $"Info DHCP incorrecta: {p3.Info}");
Console.WriteLine($"   Trama 3 OK: {p3.Info}");

// 4. Probar filtros
Console.WriteLine("-> Probando filtros de búsqueda...");
service.ApplyFilters(new PacketReplayConfig { IpFilter = "239.255.0.1" });
Assert(service.Packets.Count == 1, $"Filtro por IP Multicast falló: {service.Packets.Count}");
service.ApplyFilters(new PacketReplayConfig { ProtocolFilter = "TCP" });
Assert(service.Packets.Count == 0, $"Filtro por TCP debió dar 0: {service.Packets.Count}");
service.ApplyFilters(new PacketReplayConfig { ProtocolFilter = "ALL" });
Assert(service.Packets.Count == 3, $"Reset de filtros debió dar 3: {service.Packets.Count}");

// 5. Probar envío de paquete manual (Loopback UDP 127.0.0.1)
Console.WriteLine("-> Probando envío manual de paquete UDP (Packet Sender)...");
var manualResult = await service.SendManualPacketAsync(new ManualPacketRequest
{
    Protocol = "UDP",
    DestinationIp = "127.0.0.1",
    DestinationPort = 39876,
    DataFormat = "HEX",
    Data = "01 02 03 04 05",
    RepeatCount = 1
});
Assert(manualResult.Success, $"Envío manual falló: {manualResult.Message}");
Assert(manualResult.BytesSent == 5, $"Se debieron enviar 5 bytes, enviados: {manualResult.BytesSent}");
Console.WriteLine($"   Envío manual OK en {manualResult.RoundTripMs}ms: {manualResult.Message}");

// 6. Probar paso a paso de trama en red local
Console.WriteLine("-> Probando reproducción paso a paso (Step)...");
var cfg = new PacketReplayConfig
{
    OverrideDestination = true,
    TargetIp = "127.0.0.1",
    TargetPort = 39877,
    EnableBroadcast = false
};
service.ApplyFilters(cfg);
var (stepOk, stepMsg) = service.StepPacket();
Assert(stepOk, $"Step falló: {stepMsg}");
Assert(service.Stats.PacketsSent == 1, "Stats.PacketsSent debe ser 1");
Console.WriteLine($"   Paso a paso OK: {stepMsg}");

// 7. Pruebas de DhcpServerService (Aislamiento de interfaz, subred y filtros)
Console.WriteLine("-> Probando lógica de DhcpServerService (Aislamiento por interfaz y subred)...");
var dhcpSvc = new DhcpServerService();
dhcpSvc.Config = new DhcpServerConfig
{
    InterfaceName = "Ethernet 7",
    InterfaceIndex = 23,
    ServerIp = "10.0.1.1",
    SubnetMask = "255.255.255.0",
    StartIp = "10.0.1.50",
    EndIp = "10.0.1.150"
};

// Validar IsInSameSubnet
Assert(DhcpServerService.IsInSameSubnet(IPAddress.Parse("10.0.1.50"), IPAddress.Parse("10.0.1.1"), IPAddress.Parse("255.255.255.0")), "10.0.1.50 debe estar en la misma subred");
Assert(!DhcpServerService.IsInSameSubnet(IPAddress.Parse("172.19.181.12"), IPAddress.Parse("10.0.1.1"), IPAddress.Parse("255.255.255.0")), "172.19.181.12 NO debe estar en subred 10.0.1.0/24");
Assert(!DhcpServerService.IsInSameSubnet(IPAddress.Parse("192.168.15.192"), IPAddress.Parse("10.0.1.1"), IPAddress.Parse("255.255.255.0")), "192.168.15.192 NO debe estar en subred 10.0.1.0/24");

// Validar IsIpInRange
Assert(dhcpSvc.IsIpInRange(IPAddress.Parse("10.0.1.50")), "10.0.1.50 dentro de rango");
Assert(dhcpSvc.IsIpInRange(IPAddress.Parse("10.0.1.100")), "10.0.1.100 dentro de rango");
Assert(!dhcpSvc.IsIpInRange(IPAddress.Parse("10.0.1.49")), "10.0.1.49 fuera de rango");
Assert(!dhcpSvc.IsIpInRange(IPAddress.Parse("172.19.181.12")), "172.19.181.12 fuera de rango");

Console.WriteLine("   Filtros DHCP validados con éxito.");

Console.WriteLine("\n TODOS LOS TESTS PASARON CON ÉXITO.");

// Helper para construir paquete Ethernet + IPv4 + UDP sintético
static byte[] BuildUdpPacket(string srcIp, ushort srcPort, string dstIp, ushort dstPort, byte[] payload)
{
    int totalLen = 14 + 20 + 8 + payload.Length;
    byte[] pkt = new byte[totalLen];

    // Ethernet (14B): Dst MAC, Src MAC, EtherType 0x0800
    pkt[12] = 0x08; pkt[13] = 0x00;

    // IPv4 Header (20B)
    pkt[14] = 0x45; // Version 4, IHL 5
    pkt[15] = 0x00; // TOS
    int ipTotalLen = 20 + 8 + payload.Length;
    pkt[16] = (byte)(ipTotalLen >> 8); pkt[17] = (byte)(ipTotalLen & 0xFF);
    pkt[18] = 0x12; pkt[19] = 0x34; // ID
    pkt[20] = 0x00; pkt[21] = 0x00; // Flags & Fragment offset
    pkt[22] = 64;   // TTL
    pkt[23] = 17;   // Protocol = UDP
    // Checksum (simplificado para prueba)
    pkt[24] = 0x00; pkt[25] = 0x00;

    // Src IP
    var sIp = IPAddress.Parse(srcIp).GetAddressBytes();
    Buffer.BlockCopy(sIp, 0, pkt, 26, 4);

    // Dst IP
    var dIp = IPAddress.Parse(dstIp).GetAddressBytes();
    Buffer.BlockCopy(dIp, 0, pkt, 30, 4);

    // UDP Header (8B)
    int udpOffset = 14 + 20;
    pkt[udpOffset] = (byte)(srcPort >> 8); pkt[udpOffset + 1] = (byte)(srcPort & 0xFF);
    pkt[udpOffset + 2] = (byte)(dstPort >> 8); pkt[udpOffset + 3] = (byte)(dstPort & 0xFF);
    int udpLen = 8 + payload.Length;
    pkt[udpOffset + 4] = (byte)(udpLen >> 8); pkt[udpOffset + 5] = (byte)(udpLen & 0xFF);
    pkt[udpOffset + 6] = 0x00; pkt[udpOffset + 7] = 0x00; // Checksum

    // Payload
    Buffer.BlockCopy(payload, 0, pkt, udpOffset + 8, payload.Length);

    return pkt;
}
