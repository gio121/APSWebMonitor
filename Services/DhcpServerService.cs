using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ApsMonitor.Services;

public class DhcpLeaseInfo
{
    public string InterfaceName { get; set; } = string.Empty;
    public int InterfaceIndex { get; set; } = 0;
    public string MacAddress { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string HostName { get; set; } = string.Empty;
    public DateTime LeaseStart { get; set; } = DateTime.Now;
    public DateTime LeaseEnd { get; set; } = DateTime.Now.AddHours(24);
    public bool IsActive => DateTime.Now < LeaseEnd;
}

public class DhcpServerConfig
{
    public string InterfaceName { get; set; } = string.Empty;
    public int InterfaceIndex { get; set; } = 0;
    public string InterfaceMac { get; set; } = string.Empty;
    public string ServerIp { get; set; } = "192.168.1.10";
    public string SubnetMask { get; set; } = "255.255.255.0";
    public string StartIp { get; set; } = "192.168.1.50";
    public string EndIp { get; set; } = "192.168.1.100";
    public string GatewayIp { get; set; } = "192.168.1.1";
    public string DnsIp { get; set; } = "8.8.8.8";
    public int LeaseTimeHours { get; set; } = 24;
}

public class DhcpServerService : IDisposable
{
    private Socket? _socket;
    private Socket? _interfaceSocket;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private readonly ConcurrentDictionary<string, DhcpLeaseInfo> _leases = new();

    public bool IsRunning { get; private set; }
    public DhcpServerConfig Config { get; set; } = new();
    public event Action? OnLeasesChanged;

    public List<DhcpLeaseInfo> GetActiveLeases(string? interfaceName = null, string? subnetIp = null, string? subnetMask = null)
    {
        var list = _leases.Values.Where(l => l.IsActive);

        if (!string.IsNullOrEmpty(interfaceName))
        {
            list = list.Where(l => string.IsNullOrEmpty(l.InterfaceName) ||
                                   string.Equals(l.InterfaceName, interfaceName, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(subnetIp) && !string.IsNullOrEmpty(subnetMask) &&
            IPAddress.TryParse(subnetIp, out var sIp) && IPAddress.TryParse(subnetMask, out var sMask))
        {
            list = list.Where(l =>
            {
                if (!IPAddress.TryParse(l.IpAddress, out var lIp)) return false;
                return IsInSameSubnet(lIp, sIp, sMask);
            });
        }

        return list.OrderBy(l => l.IpAddress).ToList();
    }

    public void ClearLeases(string? interfaceName = null)
    {
        if (string.IsNullOrEmpty(interfaceName))
        {
            _leases.Clear();
        }
        else
        {
            var keysToRemove = _leases
                .Where(kvp => string.Equals(kvp.Value.InterfaceName, interfaceName, StringComparison.OrdinalIgnoreCase))
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var k in keysToRemove)
            {
                _leases.TryRemove(k, out _);
            }
        }
        OnLeasesChanged?.Invoke();
    }

    public (bool Success, string Message) StartServer(DhcpServerConfig config)
    {
        if (IsRunning)
            return (false, "El servidor DHCP ya está en ejecución.");

        Config = config;

        // Resolver InterfaceIndex si no viene indicado
        if (Config.InterfaceIndex <= 0 && (!string.IsNullOrEmpty(Config.InterfaceName) || !string.IsNullOrEmpty(Config.ServerIp)))
        {
            try
            {
                var nics = NetworkInterface.GetAllNetworkInterfaces();
                var match = nics.FirstOrDefault(n =>
                    (!string.IsNullOrEmpty(Config.InterfaceName) && string.Equals(n.Name, Config.InterfaceName, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(Config.ServerIp) && n.GetIPProperties().UnicastAddresses.Any(u => u.Address.ToString() == Config.ServerIp)));

                if (match != null)
                {
                    try
                    {
                        var ipv4Props = match.GetIPProperties().GetIPv4Properties();
                        if (ipv4Props != null)
                        {
                            Config.InterfaceIndex = ipv4Props.Index;
                        }
                    }
                    catch { }
                    if (string.IsNullOrEmpty(Config.InterfaceName))
                        Config.InterfaceName = match.Name;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DHCP] Error resolviendo interfaz: {ex.Message}");
            }
        }

        // Limpiar de la memoria concesiones que pertenezcan a otras subredes o que no coincidan con la interfaz seleccionada
        if (IPAddress.TryParse(Config.ServerIp, out var srvIpCheck) && IPAddress.TryParse(Config.SubnetMask, out var maskCheck))
        {
            var staleKeys = _leases
                .Where(kvp =>
                {
                    if (!kvp.Value.IsActive) return true;
                    if (!string.IsNullOrEmpty(kvp.Value.InterfaceName) &&
                        !string.IsNullOrEmpty(Config.InterfaceName) &&
                        !string.Equals(kvp.Value.InterfaceName, Config.InterfaceName, StringComparison.OrdinalIgnoreCase))
                        return true;
                    if (IPAddress.TryParse(kvp.Value.IpAddress, out var lIp) && !IsInSameSubnet(lIp, srvIpCheck, maskCheck))
                        return true;
                    return false;
                })
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in staleKeys)
            {
                _leases.TryRemove(key, out _);
            }
        }

        try
        {
            // Socket principal de escucha en 0.0.0.0:67
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            try
            {
                _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DHCP] Aviso configurando PacketInformation: {ex.Message}");
            }
            _socket.Bind(new IPEndPoint(IPAddress.Any, 67));

            // Socket emisor dedicado ligado a la IP de la interfaz para forzar la salida por esa tarjeta de red
            if (IPAddress.TryParse(Config.ServerIp, out var srvIp) && !IPAddress.Any.Equals(srvIp))
            {
                try
                {
                    _interfaceSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    _interfaceSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    _interfaceSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
                    _interfaceSocket.Bind(new IPEndPoint(srvIp, 67));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DHCP] Aviso ligando socket de interfaz a {srvIp}: {ex.Message}");
                }
            }

            _cts = new CancellationTokenSource();
            _listenTask = Task.Run(() => ListenLoop(_cts.Token));
            IsRunning = true;

            string ifLabel = !string.IsNullOrEmpty(Config.InterfaceName) ? $" en '{Config.InterfaceName}'" : "";
            return (true, $"Servidor DHCP iniciado{ifLabel}. Rango: {Config.StartIp} - {Config.EndIp}");
        }
        catch (SocketException ex)
        {
            return (false, $"Error al abrir puerto 67 (posible conflicto con otro servicio DHCP): {ex.Message}");
        }
        catch (Exception ex)
        {
            return (false, $"Error al iniciar DHCP: {ex.Message}");
        }
    }

    public void StopServer()
    {
        if (!IsRunning) return;

        IsRunning = false;
        try
        {
            _cts?.Cancel();
            _socket?.Close();
            _socket?.Dispose();
            _interfaceSocket?.Close();
            _interfaceSocket?.Dispose();
        }
        catch { }
        finally
        {
            _socket = null;
            _interfaceSocket = null;
            _cts = null;
        }
        OnLeasesChanged?.Invoke();
    }

    private void ListenLoop(CancellationToken ct)
    {
        byte[] buffer = new byte[1500];
        EndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);

        while (!ct.IsCancellationRequested && IsRunning)
        {
            try
            {
                if (_socket == null) break;

                SocketFlags flags = SocketFlags.None;
                int received = 0;
                int incomingInterfaceIndex = 0;

                try
                {
                    received = _socket.ReceiveMessageFrom(buffer, 0, buffer.Length, ref flags, ref remoteEp, out IPPacketInformation packetInfo);
                    incomingInterfaceIndex = packetInfo.Interface;
                }
                catch (SocketException) when (ct.IsCancellationRequested || !IsRunning)
                {
                    break;
                }
                catch (Exception)
                {
                    received = _socket.ReceiveFrom(buffer, ref remoteEp);
                }

                if (received < 240) continue; // Longitud mínima de paquete DHCP

                // FILTRADO ESTRICTO POR INTERFAZ:
                // Si conocemos el índice de la interfaz configurada, descartar paquetes recibidos en cualquier otra interfaz (Wi-Fi, corporativa, etc.)
                if (Config.InterfaceIndex > 0 && incomingInterfaceIndex > 0 && incomingInterfaceIndex != Config.InterfaceIndex)
                {
                    continue;
                }

                ProcessPacket(buffer, received, incomingInterfaceIndex);
            }
            catch (SocketException) when (ct.IsCancellationRequested || !IsRunning)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DHCP] Error en recepción: {ex.Message}");
            }
        }
    }

    private void ProcessPacket(byte[] buffer, int len, int incomingIfIndex)
    {
        if (buffer[0] != 1) return; // BootRequest únicamente

        // Verificar Magic Cookie: 99, 130, 83, 99 (0x63, 0x82, 0x53, 0x63)
        if (buffer[236] != 99 || buffer[237] != 130 || buffer[238] != 83 || buffer[239] != 99)
            return;

        // Preservar exactamente los 4 bytes de XID en orden de red
        byte[] xidBytes = new byte[4];
        Array.Copy(buffer, 4, xidBytes, 0, 4);

        // Hardware address (MAC)
        byte hlen = buffer[2];
        if (hlen > 16) hlen = 6;
        string mac = string.Join(":", buffer.Skip(28).Take(hlen).Select(b => b.ToString("X2")));

        // Parsear opciones DHCP
        byte msgType = 0;
        string hostName = "";
        IPAddress? requestedIp = null;
        IPAddress? serverId = null;

        int i = 240;
        while (i < len)
        {
            byte optCode = buffer[i++];
            if (optCode == 255) break; // End
            if (optCode == 0) continue; // Pad
            if (i >= len) break;
            byte optLen = buffer[i++];
            if (i + optLen > len) break;

            if (optCode == 53 && optLen >= 1) // DHCP Message Type
            {
                msgType = buffer[i];
            }
            else if (optCode == 50 && optLen == 4) // Requested IP
            {
                requestedIp = new IPAddress(buffer.Skip(i).Take(4).ToArray());
            }
            else if (optCode == 54 && optLen == 4) // Server Identifier
            {
                serverId = new IPAddress(buffer.Skip(i).Take(4).ToArray());
            }
            else if (optCode == 12) // Host Name
            {
                hostName = Encoding.ASCII.GetString(buffer, i, optLen);
            }

            i += optLen;
        }

        // Si el paquete incluye Server Identifier (Opción 54) y no coincide con nuestra ServerIp,
        // la petición va dirigida a OTRO servidor DHCP de la red (ej. router corporativo). Ignorar por completo.
        if (serverId != null && !string.IsNullOrEmpty(Config.ServerIp))
        {
            if (IPAddress.TryParse(Config.ServerIp, out var mySrvIp) && !serverId.Equals(mySrvIp))
            {
                return;
            }
        }

        byte[] chaddr16 = buffer.Skip(28).Take(16).ToArray();

        if (msgType == 1) // DHCPDISCOVER
        {
            Console.WriteLine($"[DHCP] DHCPDISCOVER recibido de MAC {mac}, host '{hostName}' en interfaz '{Config.InterfaceName}'");
            var offeredIp = GetOrAllocateIp(mac, requestedIp);
            if (offeredIp != null)
            {
                SendReply(2, xidBytes, mac, chaddr16, offeredIp, hostName); // DHCPOFFER
            }
        }
        else if (msgType == 3) // DHCPREQUEST
        {
            Console.WriteLine($"[DHCP] DHCPREQUEST recibido de MAC {mac}, requestedIp={requestedIp} en interfaz '{Config.InterfaceName}'");

            IPAddress? chosenIp = null;

            if (requestedIp != null)
            {
                // Comprobar estrictamente si la IP solicitada está dentro del rango o si es una concesión existente de esta MAC
                if (IsIpInRange(requestedIp))
                {
                    chosenIp = requestedIp;
                }
                else if (_leases.TryGetValue(mac, out var existing) && existing.IsActive && existing.IpAddress == requestedIp.ToString())
                {
                    chosenIp = requestedIp;
                }
                else
                {
                    // La IP solicitada es ajena al rango configurado (ej. IP de otra red corporativa/Wi-Fi).
                    // No conceder ni registrar concesión.
                    Console.WriteLine($"[DHCP] Ignorando DHCPREQUEST fuera de rango/subred ({requestedIp}) para MAC {mac}");
                    return;
                }
            }
            else
            {
                chosenIp = GetOrAllocateIp(mac, null);
            }

            if (chosenIp != null)
            {
                var lease = new DhcpLeaseInfo
                {
                    InterfaceName = Config.InterfaceName,
                    InterfaceIndex = Config.InterfaceIndex,
                    MacAddress = mac,
                    IpAddress = chosenIp.ToString(),
                    HostName = hostName,
                    LeaseStart = DateTime.Now,
                    LeaseEnd = DateTime.Now.AddHours(Config.LeaseTimeHours)
                };
                _leases[mac] = lease;
                OnLeasesChanged?.Invoke();

                SendReply(5, xidBytes, mac, chaddr16, chosenIp, hostName); // DHCPACK
            }
        }
        else if (msgType == 7) // DHCPRELEASE
        {
            Console.WriteLine($"[DHCP] DHCPRELEASE recibido de MAC {mac}");
            _leases.TryRemove(mac, out _);
            OnLeasesChanged?.Invoke();
        }
    }

    private IPAddress? GetOrAllocateIp(string mac, IPAddress? preferred)
    {
        if (_leases.TryGetValue(mac, out var existing) && existing.IsActive)
        {
            if (IPAddress.TryParse(existing.IpAddress, out var ip) && IsIpInRange(ip))
                return ip;
        }

        if (preferred != null && IsIpInRange(preferred) && !_leases.Values.Any(l => l.IpAddress == preferred.ToString() && l.MacAddress != mac && l.IsActive))
        {
            return preferred;
        }

        // Buscar IP libre dentro del rango
        if (!IPAddress.TryParse(Config.StartIp, out var start) || !IPAddress.TryParse(Config.EndIp, out var end))
            return null;

        uint startNum = IpToUint(start);
        uint endNum = IpToUint(end);

        for (uint u = startNum; u <= endNum; u++)
        {
            var cand = UintToIp(u);
            string candStr = cand.ToString();
            if (candStr == Config.ServerIp) continue; // No auto-asignar la IP del propio servidor
            if (!_leases.Values.Any(l => l.IpAddress == candStr && l.IsActive))
            {
                return cand;
            }
        }

        return null;
    }

    public bool IsIpInRange(IPAddress ip)
    {
        if (!IPAddress.TryParse(Config.StartIp, out var start) || !IPAddress.TryParse(Config.EndIp, out var end))
            return false;
        uint num = IpToUint(ip);
        return num >= IpToUint(start) && num <= IpToUint(end);
    }

    public static bool IsInSameSubnet(IPAddress ip1, IPAddress ip2, IPAddress mask)
    {
        byte[] b1 = ip1.GetAddressBytes();
        byte[] b2 = ip2.GetAddressBytes();
        byte[] m = mask.GetAddressBytes();
        if (b1.Length != 4 || b2.Length != 4 || m.Length != 4) return false;
        for (int i = 0; i < 4; i++)
        {
            if ((b1[i] & m[i]) != (b2[i] & m[i])) return false;
        }
        return true;
    }

    private static uint IpToUint(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static IPAddress UintToIp(uint val)
    {
        return new IPAddress(new byte[] {
            (byte)(val >> 24),
            (byte)(val >> 16),
            (byte)(val >> 8),
            (byte)val
        });
    }

    private static IPAddress GetSubnetBroadcast(IPAddress ip, IPAddress mask)
    {
        byte[] ipBytes = ip.GetAddressBytes();
        byte[] maskBytes = mask.GetAddressBytes();
        byte[] bcBytes = new byte[ipBytes.Length];
        for (int i = 0; i < ipBytes.Length; i++)
        {
            bcBytes[i] = (byte)(ipBytes[i] | (~maskBytes[i]));
        }
        return new IPAddress(bcBytes);
    }

    private void SendReply(byte msgType, byte[] xidBytes, string mac, byte[] chaddr16, IPAddress yiaddr, string hostName)
    {
        if (_socket == null) return;

        // Longitud mínima de paquete BOOTP/DHCP es de 300 octetos (RFC 1542 / RFC 2131)
        byte[] packet = new byte[576];
        packet[0] = 2; // BootReply
        packet[1] = 1; // Ethernet (10Mb/100Mb/1Gb)
        packet[2] = 6; // Hardware addr len (6 for MAC)
        packet[3] = 0; // Hops

        // XID original en orden de red
        Array.Copy(xidBytes, 0, packet, 4, 4);

        // Flags: 0x80, 0x00 (Broadcast)
        packet[10] = 0x80;
        packet[11] = 0x00;

        // Your IP address (yiaddr)
        Array.Copy(yiaddr.GetAddressBytes(), 0, packet, 16, 4);

        // Server IP address (siaddr)
        IPAddress? srvIp = null;
        if (IPAddress.TryParse(Config.ServerIp, out var parsedSrvIp))
        {
            srvIp = parsedSrvIp;
            Array.Copy(srvIp.GetAddressBytes(), 0, packet, 20, 4);
        }

        // Client Hardware Address (chaddr)
        Array.Copy(chaddr16, 0, packet, 28, 16);

        // Magic Cookie: 99, 130, 83, 99 (0x63, 0x82, 0x53, 0x63)
        packet[236] = 99;
        packet[237] = 130;
        packet[238] = 83;
        packet[239] = 99;

        // Opciones DHCP
        var opts = new List<byte>
        {
            53, 1, msgType // Option 53: DHCP Message Type (2=Offer, 5=Ack)
        };

        // Option 54: Server Identifier
        if (srvIp != null)
        {
            opts.Add(54); opts.Add(4);
            opts.AddRange(srvIp.GetAddressBytes());
        }

        // Option 1: Subnet Mask
        IPAddress? mask = null;
        if (IPAddress.TryParse(Config.SubnetMask, out var parsedMask))
        {
            mask = parsedMask;
            opts.Add(1); opts.Add(4);
            opts.AddRange(mask.GetAddressBytes());
        }

        // Option 3: Router
        if (IPAddress.TryParse(Config.GatewayIp, out var gw))
        {
            opts.Add(3); opts.Add(4);
            opts.AddRange(gw.GetAddressBytes());
        }

        // Option 6: DNS
        if (IPAddress.TryParse(Config.DnsIp, out var dns))
        {
            opts.Add(6); opts.Add(4);
            opts.AddRange(dns.GetAddressBytes());
        }

        // Option 51: Lease Time (segundos en Big-Endian)
        uint leaseSecs = (uint)(Config.LeaseTimeHours * 3600);
        opts.Add(51); opts.Add(4);
        opts.Add((byte)(leaseSecs >> 24));
        opts.Add((byte)(leaseSecs >> 16));
        opts.Add((byte)(leaseSecs >> 8));
        opts.Add((byte)(leaseSecs & 0xFF));

        // Option 255: End
        opts.Add(255);

        // Copiar opciones al paquete a partir del offset 240
        Array.Copy(opts.ToArray(), 0, packet, 240, opts.Count);

        // Asegurar longitud mínima de 300 octetos con relleno de ceros (RFC 1542 / RFC 2131)
        int finalLen = Math.Max(300, 240 + opts.Count);
        byte[] sendData = new byte[finalLen];
        Array.Copy(packet, 0, sendData, 0, finalLen);

        // Calcular dirección de broadcast dirigida a la subred de la interfaz
        IPAddress? subnetBroadcast = null;
        if (srvIp != null && mask != null)
        {
            subnetBroadcast = GetSubnetBroadcast(srvIp, mask);
        }

        string typeName = msgType == 2 ? "DHCPOFFER" : "DHCPACK";

        // 1. Enviar broadcast dirigido a la subred (fuerza la salida por la interfaz del adaptador)
        if (subnetBroadcast != null)
        {
            try
            {
                _socket.SendTo(sendData, new IPEndPoint(subnetBroadcast, 68));
                Console.WriteLine($"[DHCP] {typeName} enviado a subred broadcast {subnetBroadcast}:68");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DHCP] Error enviando a {subnetBroadcast}: {ex.Message}");
            }
        }

        // 2. Enviar por socket ligado a la interfaz hacia 255.255.255.255 y a la IP asignada
        if (_interfaceSocket != null)
        {
            try
            {
                _interfaceSocket.SendTo(sendData, new IPEndPoint(IPAddress.Broadcast, 68));
                Console.WriteLine($"[DHCP] {typeName} enviado via socket de interfaz a 255.255.255.255:68");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DHCP] Error enviando via interfaceSocket broadcast: {ex.Message}");
            }

            try
            {
                _interfaceSocket.SendTo(sendData, new IPEndPoint(yiaddr, 68));
            }
            catch { }
        }
        else
        {
            try
            {
                _socket.SendTo(sendData, new IPEndPoint(IPAddress.Broadcast, 68));
            }
            catch { }

            try
            {
                _socket.SendTo(sendData, new IPEndPoint(yiaddr, 68));
            }
            catch { }
        }

        Console.WriteLine($"[DHCP] {typeName} completado para {mac} -> {yiaddr}");
    }

    public void Dispose()
    {
        StopServer();
    }
}
