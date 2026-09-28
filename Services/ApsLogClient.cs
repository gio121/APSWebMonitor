using System.Net.Sockets;

namespace ApsMonitor.Services;

// Port and receive-until-EOF protocol from MON/frmLog.cs.
public static class ApsLogClient
{
    public const int Port = 9882;
    public const int MaxFileBytes = 50 * 1024 * 1024;

    public static async Task<byte[]> DownloadAsync(string ip, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ip);
        using var client = new TcpClient();
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(ip, Port, connectTimeout.Token);
        using var stream = client.GetStream();
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            var count = await stream.ReadAsync(buffer, readTimeout.Token);
            if (count == 0) break;
            if (output.Length + count > MaxFileBytes)
                throw new InvalidDataException("El log supera el límite de 50 MB.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
}
