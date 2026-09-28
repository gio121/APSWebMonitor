using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace ApsMonitor.Services;

public class NetworkAdapterInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string MacAddress { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string SubnetMask { get; set; } = string.Empty;
    public string Gateway { get; set; } = string.Empty;
    public OperationalStatus Status { get; set; }
    public NetworkInterfaceType InterfaceType { get; set; }
    public bool IsDhcpEnabled { get; set; }
}

public class NetworkAdapterService
{
    public List<NetworkAdapterInfo> GetAdapters()
    {
        var list = new List<NetworkAdapterInfo>();
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var ni in interfaces)
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                var ipProps = ni.GetIPProperties();
                var ipv4 = ipProps.UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);

                var gw = ipProps.GatewayAddresses
                    .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);

                var mac = string.Join(":", ni.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));

                list.Add(new NetworkAdapterInfo
                {
                    Id = ni.Id,
                    Name = ni.Name,
                    Description = ni.Description,
                    MacAddress = mac,
                    IpAddress = ipv4?.Address.ToString() ?? "",
                    SubnetMask = ipv4?.IPv4Mask.ToString() ?? "255.255.255.0",
                    Gateway = gw?.Address.ToString() ?? "",
                    Status = ni.OperationalStatus,
                    InterfaceType = ni.NetworkInterfaceType,
                    IsDhcpEnabled = ipProps.GetIPv4Properties()?.IsDhcpEnabled ?? false
                });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error enumerating network adapters: {ex.Message}");
        }
        return list;
    }

    public (bool Success, string Message) SetStaticIp(string adapterName, string ipAddress, string subnetMask, string gateway = "")
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return (false, "La configuración de red automática está implementada para Windows.");
        }

        try
        {
            if (!IPAddress.TryParse(ipAddress, out _))
                return (false, "Dirección IP inválida.");
            if (!IPAddress.TryParse(subnetMask, out _))
                return (false, "Máscara de subred inválida.");

            string gwArg = string.IsNullOrWhiteSpace(gateway) ? "" : $" gateway={gateway} gwmetric=1";
            string args = $"interface ipv4 set address name=\"{adapterName}\" static {ipAddress} {subnetMask}{gwArg}";

            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = args,
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var process = Process.Start(psi);
            process?.WaitForExit(5000);

            if (process != null && process.ExitCode == 0)
            {
                return (true, $"IP {ipAddress} y máscara {subnetMask} configuradas en '{adapterName}'.");
            }

            return (false, $"netsh finalizó con código {process?.ExitCode}. Verifique permisos de administrador.");
        }
        catch (Exception ex)
        {
            return (false, $"Error al configurar IP: {ex.Message}");
        }
    }

    public (bool Success, string Message) SetDhcpClient(string adapterName)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return (false, "Implementado para Windows.");
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"interface ipv4 set address name=\"{adapterName}\" dhcp",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var process = Process.Start(psi);
            process?.WaitForExit(5000);

            if (process != null && process.ExitCode == 0)
            {
                return (true, $"Interfaz '{adapterName}' configurada en cliente DHCP.");
            }

            return (false, $"Error netsh código {process?.ExitCode}.");
        }
        catch (Exception ex)
        {
            return (false, $"Error al configurar DHCP: {ex.Message}");
        }
    }
}
