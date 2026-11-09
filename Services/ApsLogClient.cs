using System.Net.Sockets;

namespace ApsMonitor.Services;

// Port and receive-until-EOF protocol from MON/frmLog.cs.
// In TLS / mTLS mode, connects first to LogGateway HTTPS (port 16801 /api/v1/logs) or TLS stream on port 9882.
public static class ApsLogClient
{
    public const int Port = 9882;
    public const int MaxFileBytes = 50 * 1024 * 1024;

    public static async Task<byte[]> DownloadAsync(
        string ip,
        CancellationToken cancellationToken = default,
        TlsSecurityService? tlsSecurity = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ip);
        var host = TlsSecurityService.ExtractHost(ip);

        if (tlsSecurity is { UseTls: true })
        {
            if (tlsSecurity.LogGatewayTlsPort == Port)
            {
                using var tlsConnectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                tlsConnectTimeout.CancelAfter(TimeSpan.FromSeconds(8));
                await using var sslStream = await tlsSecurity.ConnectTlsStreamAsync(host, Port, tlsConnectTimeout.Token);
                return await ReadStreamWithLimitAsync(sslStream, cancellationToken);
            }

            try
            {
                using var httpClient = tlsSecurity.CreateHttpClient(TimeSpan.FromSeconds(30));
                var url = $"https://{host}:{tlsSecurity.LogGatewayTlsPort}/api/v1/logs";
                using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var respStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                return await ReadStreamWithLimitAsync(respStream, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                string certHint = !tlsSecurity.HasClientCertificate
                    ? " Asegúrate de haber cargado el certificado 'sepsa_aps_log_reader.pem' + '.key' (Perfil Log Reader mTLS)."
                    : !tlsSecurity.ClientCertSubject.Contains("APS Log Reader", StringComparison.OrdinalIgnoreCase)
                        ? $" El certificado activo ({tlsSecurity.ClientCertSubject}) no es 'CN=APS Log Reader'; LogGateway exige 'sepsa_aps_log_reader.pem'."
                        : "";
                throw new InvalidOperationException(
                    $"Error al descargar logs por mTLS desde https://{host}:{tlsSecurity.LogGatewayTlsPort}/api/v1/logs: {ex.Message}.{certHint}",
                    ex);
            }
        }

        // Modo Tradicional: TCP directo en claro al puerto 9882
        using var client = new TcpClient();
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(host, Port, connectTimeout.Token);
        using var stream = client.GetStream();
        return await ReadStreamWithLimitAsync(stream, cancellationToken);
    }

    private static async Task<byte[]> ReadStreamWithLimitAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            var count = await stream.ReadAsync(buffer, readTimeout.Token);
            if (count == 0) break;
            if (output.Length + count > MaxFileBytes)
                throw new InvalidDataException(ApsMonitor.Resources.MonitorResources.Text("The log exceeds the 50 MB limit."));
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
}
