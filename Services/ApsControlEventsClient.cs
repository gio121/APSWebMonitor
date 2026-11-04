using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using ApsMonitor.Models;

namespace ApsMonitor.Services;

public static class ApsControlEventsClient
{
    public static byte[] Request(ControlEventConfig config, int address, ushort size)
    {
        var payload = new byte[8];
        payload[1] = MemoryType(config);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(2), address);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6), size);
        return ReprogrammingProtocol.Frame(config.Node, 0x5C, payload);
    }

    public static bool TryReadBlock(byte[] frame, ControlEventConfig config, int address, int size, out byte[] data)
    {
        data = [];
        // Response payload: signature[6], subcommand[2], address[4], size[2], bytes.
        if (frame.Length != 21 + size || frame[0] != 0xAA || BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1)) != frame.Length ||
            frame[3] != 1 || frame[4] != config.Node || frame[5] != 0x54 || frame[12] != 0 || frame[13] != MemoryType(config) ||
            BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(14)) != address || BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(18)) != size)
            return false;
        byte sum = 0;
        foreach (byte b in frame.AsSpan(0, frame.Length - 1)) sum = unchecked((byte)(sum + b));
        if (sum != frame[^1]) return false;
        data = frame[20..^1];
        return true;
    }

    public static async Task<byte[]> DownloadAsync(string host, int port, ControlEventConfig config,
        Action<double>? progress = null, CancellationToken cancellationToken = default, TlsSecurityService? tlsSecurity = null)
    {
        int length = config.DownloadEnd - config.DownloadStart;
        if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535 || config.DownloadStart != 0 || length is < 6 or > ControlEventsParser.MaxFileBytes || config.BlockSize is < 1 or > 32767)
            throw new ArgumentException(ApsMonitor.Resources.MonitorResources.Text("Invalid download configuration: an image starting at address 0, a valid UDP port, and a valid block size are required."));
        _ = MemoryType(config);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(8));
        var token = deadline.Token;

        // Modo TLS / mTLS: descarga de bloques de eventos mediante HTTPS /api/send
        if (tlsSecurity is { UseTls: true })
        {
            var tlsBytes = new byte[length];
            progress?.Invoke(0);
            for (int offset = 0; offset < length;)
            {
                int size = Math.Min(config.BlockSize, length - offset);
                byte[] request = Request(config, config.DownloadStart + offset, (ushort)size);
                byte[]? received = null;
                for (int attempt = 0; attempt < 5 && received is null; attempt++)
                {
                    using var blockTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    blockTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                    try
                    {
                        var res = await tlsSecurity.SendSepsaFrameOverTlsAsync(host, request, blockTimeout.Token);
                        if (res.IsSuccess && TryReadBlock(res.Payload, config, config.DownloadStart + offset, size, out var block))
                        {
                            received = block;
                        }
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                }
                if (received is null) throw new TimeoutException($"El equipo no respondió por TLS con un bloque válido en la dirección {offset} tras cinco intentos.");
                received.CopyTo(tlsBytes, offset);
                offset += received.Length;
                progress?.Invoke(100.0 * offset / length);
            }
            _ = ControlEventsParser.MatchConfig(tlsBytes, [config]);
            return tlsBytes;
        }

        // Modo Tradicional: descarga de bloques de eventos por UDP puerto 50001
        using var udp = new UdpClient();
        var ip = IPAddress.TryParse(host, out var parsed) ? parsed :
            (await Dns.GetHostAddressesAsync(host, token)).First(a => a.AddressFamily == AddressFamily.InterNetwork);
        token.ThrowIfCancellationRequested();
        udp.Connect(ip, port);
        var bytes = new byte[length];
        progress?.Invoke(0);
        for (int offset = 0; offset < length;)
        {
            int size = Math.Min(config.BlockSize, length - offset);
            byte[] request = Request(config, config.DownloadStart + offset, (ushort)size);
            byte[]? received = null;
            for (int attempt = 0; attempt < 5 && received is null; attempt++)
            {
                await udp.SendAsync(request, token);
                using var blockTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                blockTimeout.CancelAfter(TimeSpan.FromSeconds(1));
                try
                {
                    while (received is null)
                    {
                        var response = await udp.ReceiveAsync(blockTimeout.Token);
                        if (TryReadBlock(response.Buffer, config, config.DownloadStart + offset, size, out var block)) received = block;
                    }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            }
            if (received is null) throw new TimeoutException(ApsMonitor.Resources.MonitorResources.Format("The device did not respond with a valid block at address {0} after five attempts.", offset));
            received.CopyTo(bytes, offset);
            offset += received.Length;
            progress?.Invoke(100.0 * offset / length);
        }
        _ = ControlEventsParser.MatchConfig(bytes, [config]);
        return bytes;
    }

    private static byte MemoryType(ControlEventConfig config) => config.DownloadType.ToUpperInvariant() switch
    {
        "RAM" => 1, "FLASH" => 2,
        _ => throw new ArgumentException(ApsMonitor.Resources.MonitorResources.Text("The CMFX must specify a RAM or FLASH download."))
    };
}
