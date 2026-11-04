using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace ApsMonitor.Services;

public sealed record ReprogrammingTarget(string Address, int UdpPort = 50001, int LocalPort = 0)
{
    public ReprogrammingTarget(string address) : this(address, 50001, 0) { }
    public int UdpPort { get; init; } = UdpPort > 0 ? UdpPort : 50001;
}

public sealed record ReprogrammingProgress(string Message, double Percent, bool Running, bool Success = false);
public sealed record CommunicationsFile(string Name, byte[] Content);

public sealed class ReprogrammingService(
    MonitorStateService monitor, 
    ISftpFirmwareUploader sftpUploader,
    SepsaProtocolClient? sepsaClient = null,
    TlsSecurityService? tlsSecurity = null)
{
    private ReprogrammingProgress _state = new(ApsMonitor.Resources.MonitorResources.Text("Ready."), 0, false);
    public ReprogrammingProgress State => _state;
    public event Action? Changed;

    private void Report(string message, double percent, bool running = true, bool success = false)
    {
        _state = new(message, percent, running, success);
        Changed?.Invoke();
    }

    public async Task UploadControlAsync(ReprogrammingTarget target, bool fpga, string signature, byte[] image, int blockSize = 128)
    {
        if (image == null || image.Length == 0) throw new ArgumentException(ApsMonitor.Resources.MonitorResources.Text("The firmware file is empty."));
        if (blockSize is < 1 or > 4096) throw new ArgumentException(ApsMonitor.Resources.MonitorResources.Text("Invalid block size."));
        int blocks = (image.Length + blockSize - 1) / blockSize;
        byte[] pending = ReprogrammingProtocol.Start(2, fpga, blocks);
        _ = ReprogrammingProtocol.Block(2, fpga, signature, image, blockSize, 0);

        if (tlsSecurity is { UseTls: true })
        {
            await RunTlsControlUploadAsync(target, fpga, signature, image, blockSize, blocks, pending);
            return;
        }

        await RunAsync(target, fpga ? TimeSpan.FromMinutes(12) : TimeSpan.FromMinutes(35), async (udp, token) =>
        {
            Report(ApsMonitor.Resources.MonitorResources.Text("Starting SEPSA transfer to the control board on port 50001…"), 0);
            await udp.SendAsync(pending, token);

            int processState = 0; // 0 = inicio / borrado flash, 1 = enviando bloques, 2 = flash ocupada
            var sentBlocks = new HashSet<ushort>();
            DateTime lastSent = DateTime.UtcNow;

            while (!token.IsCancellationRequested)
            {
                // Durante el borrado de flash o espera inicial, timeout de 20s (ERASURE_TX_TIME_S = 20 en MonitoringOptions).
                // Durante la transferencia de bloques, timeout de 10s (UPLOAD_TX_TIME = 10000ms en MonitoringOptions).
                TimeSpan rxTimeout = processState == 0 ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(10);
                byte[]? response = await ReceiveAsync(udp, rxTimeout, token);
                if (response is null)
                {
                    // Si expira el timeout y no estamos en espera de escritura/borrado (processState != 2):
                    // - En inicio (0): re-enviar Start solo tras 15-20 segundos de espera.
                    // - En bloques (1): re-enviar el último bloque pendiente tras 10 segundos.
                    if (processState == 0 && (DateTime.UtcNow - lastSent).TotalSeconds >= 15)
                    {
                        await udp.SendAsync(pending, token);
                        lastSent = DateTime.UtcNow;
                    }
                    else if (processState == 1 && (DateTime.UtcNow - lastSent).TotalSeconds >= 10)
                    {
                        await udp.SendAsync(pending, token);
                        lastSent = DateTime.UtcNow;
                    }
                    continue;
                }

                // Ignorar tramas periódicas de telemetría de monitorización recibidas mientras se cambia de estado
                if (ReprogrammingProtocol.IsMonitoringOrTelemetryResponse(response))
                {
                    continue;
                }

                if (ReprogrammingProtocol.IsErrorResponse(response, 2))
                    throw new IOException(ApsMonitor.Resources.MonitorResources.Text("The control device rejected the SEPSA firmware update command."));

                if (!ReprogrammingProtocol.TryReadStatus(response, 2, out ushort index, out byte status))
                    continue;

                switch (status)
                {
                    case 0 or 4:
                        // Petición de bloque index.
                        // Si index >= blocks, Block(...) rellenará con 0xFF (sendFFBlock heredado de SepsaCtlNet)
                        pending = ReprogrammingProtocol.Block(2, fpga, signature, image, blockSize, index);
                        await udp.SendAsync(pending, token);
                        lastSent = DateTime.UtcNow;
                        processState = 1;
                        if (index < blocks) sentBlocks.Add(index);
                        double percent = Math.Min(99.0, 100.0 * Math.Min((int)index, blocks) / blocks);
                        Report(ApsMonitor.Resources.MonitorResources.Format("Transferring block {0} of {1} ({2:F0}%)", Math.Min(index + 1, blocks), blocks, percent), percent);
                        break;

                    case 1 or 2:
                        // Grabación o borrado de flash en progreso. En SepsaCtlNet processState = 2 (espera pasiva).
                        processState = 2;
                        if (sentBlocks.Count >= blocks)
                        {
                            Report(ApsMonitor.Resources.MonitorResources.Text("File transferred. Verifying and writing flash memory…"), 99);
                        }
                        else
                        {
                            double currentPercent = Math.Min(99.0, 100.0 * sentBlocks.Count / blocks);
                            Report(ApsMonitor.Resources.MonitorResources.Format("Device processing flash memory ({0:F0}%)…", currentPercent), currentPercent);
                        }
                        break;

                    case 0x80:
                        if (sentBlocks.Count >= blocks)
                        {
                            Report(ApsMonitor.Resources.MonitorResources.Text("Control firmware update confirmed successfully by the device."), 100, false, true);
                            return;
                        }
                        throw new IOException(ApsMonitor.Resources.MonitorResources.Text("The control device reported completion before the transfer finished."));

                    case 0x08: throw new IOException(ApsMonitor.Resources.MonitorResources.Text("The control device reported a data transfer error."));
                    case 0x10 or 0x90: throw new IOException(ApsMonitor.Resources.MonitorResources.Text("The control device reported a verification error."));
                    case 0x20 or 0xA0: throw new IOException(ApsMonitor.Resources.MonitorResources.Text("The control device reported a flash erase error."));
                    case 0x40 or 0xC0: throw new IOException(ApsMonitor.Resources.MonitorResources.Text("The control device reported a flash write error."));
                    default: throw new IOException(ApsMonitor.Resources.MonitorResources.Format("Unexpected SEPSA status received from Control: 0x{0:X2}.", status));
                }
            }
        });
    }

    private async Task RunTlsControlUploadAsync(
        ReprogrammingTarget target,
        bool fpga,
        string signature,
        byte[] image,
        int blockSize,
        int blocks,
        byte[] pending)
    {
        if (tlsSecurity is null)
            throw new InvalidOperationException("Servicio TLS no disponible.");
        if (!monitor.TryBeginReprogramming())
            throw new InvalidOperationException("Ya existe una reprogramación en curso.");

        try
        {
            Report("Deteniendo monitorización de fondo…", 0);
            await monitor.WaitForMonitoringStoppedAsync();
            Report($"Iniciando transferencia SEPSA por TLS/mTLS hacia https://{target.Address}:{tlsSecurity.HttpsApiPort}/api/send…", 0);

            using var cts = new CancellationTokenSource(fpga ? TimeSpan.FromMinutes(12) : TimeSpan.FromMinutes(35));
            var token = cts.Token;
            var sentBlocks = new HashSet<ushort>();

            while (!token.IsCancellationRequested)
            {
                var res = await tlsSecurity.SendSepsaFrameOverTlsAsync(target.Address, pending, token);
                if (!res.IsSuccess || res.Payload.Length == 0)
                {
                    await Task.Delay(500, token);
                    continue;
                }

                var response = res.Payload;
                if (ReprogrammingProtocol.IsMonitoringOrTelemetryResponse(response))
                    continue;

                if (ReprogrammingProtocol.IsErrorResponse(response, 2))
                    throw new IOException("El equipo de control rechazó el comando SEPSA de reprogramación.");

                if (!ReprogrammingProtocol.TryReadStatus(response, 2, out ushort index, out byte status))
                    continue;

                switch (status)
                {
                    case 0 or 4:
                        pending = ReprogrammingProtocol.Block(2, fpga, signature, image, blockSize, index);
                        if (index < blocks) sentBlocks.Add(index);
                        double percent = Math.Min(99.0, 100.0 * Math.Min((int)index, blocks) / blocks);
                        Report($"Transfiriendo bloque [TLS] {Math.Min(index + 1, blocks)} de {blocks} ({percent:F0}%)", percent);
                        break;

                    case 1 or 2:
                        if (sentBlocks.Count >= blocks)
                            Report("Fichero transferido [TLS]. Verificando y grabando memoria flash…", 99);
                        else
                        {
                            double currentPercent = Math.Min(99.0, 100.0 * sentBlocks.Count / blocks);
                            Report($"Equipo procesando memoria flash [TLS] ({currentPercent:F0}%)…", currentPercent);
                        }
                        await Task.Delay(500, token);
                        break;

                    case 0x80:
                        if (sentBlocks.Count >= blocks)
                        {
                            Report("Reprogramación de control por TLS confirmada con éxito por el equipo.", 100, false, true);
                            return;
                        }
                        throw new IOException("El equipo de control indicó finalización prematura antes de completar la transferencia.");

                    case 0x08: throw new IOException("El equipo de control indicó un error de transferencia de datos.");
                    case 0x10 or 0x90: throw new IOException("El equipo de control indicó un error de verificación.");
                    case 0x20 or 0xA0: throw new IOException("El equipo de control indicó un error de borrado de memoria flash.");
                    case 0x40 or 0xC0: throw new IOException("El equipo de control indicó un error de grabación en memoria flash.");
                    default: throw new IOException($"Estado SEPSA inesperado recibido de control: 0x{status:X2}.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            Report("Tiempo de espera agotado. No se ha confirmado el resultado de la reprogramación.", State.Percent, false);
        }
        catch (Exception ex)
        {
            Report($"Reprogramación no completada: {ex.Message}", State.Percent, false);
        }
        finally
        {
            monitor.EndReprogramming();
        }
    }

    public async Task UploadCommunicationsAsync(ReprogrammingTarget target, int sftpPort = 22,
        string hostKeySha256 = "", IReadOnlyList<CommunicationsFile> files = null!,
        string sftpUser = "", string sftpPassword = "", string remotePath = "/mnt/artifacts/update",
        bool trigger8ACommand = false)
    {
        ValidateCommunicationsFiles(files);
        if (sftpPort is < 1 or > 65535)
            sftpPort = 22; // Puerto por defecto del protocolo SFTP

        string targetDir = string.IsNullOrWhiteSpace(remotePath) ? "/mnt/artifacts/update" : remotePath.Trim();

        // Si se especifica usuario SFTP (ej. "sepsa" o "root"), se realiza la subida directa
        // por SFTP y opcionalmente se envía el comando 8A para iniciar la reprogramación.
        if (!string.IsNullOrWhiteSpace(sftpUser))
        {
            if (!monitor.TryBeginReprogramming()) throw new InvalidOperationException(ApsMonitor.Resources.MonitorResources.Text("A firmware update is already in progress."));
            try
            {
                Report(ApsMonitor.Resources.MonitorResources.Text("Stopping monitoring…"), 0);
                await monitor.WaitForMonitoringStoppedAsync();
                Report(ApsMonitor.Resources.MonitorResources.Format("Connecting through SFTP to {0}:{1} as '{2}'…", target.Address, sftpPort, sftpUser), 5);
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                await sftpUploader.UploadAsync(target.Address, sftpPort, sftpUser, sftpPassword,
                    hostKeySha256, files, (message, percent) => Report(message, percent), cts.Token, targetDir);

                if (trigger8ACommand)
                {
                    Report(ApsMonitor.Resources.MonitorResources.Format("Files transferred to '{0}'. Sending firmware update command 8A…", targetDir), 95);

                    var (ok, msg) = await SendReprogramming8ACommandInternalAsync(target, cts.Token);
                    if (ok)
                    {
                        Report(ApsMonitor.Resources.MonitorResources.Format("Update confirmed. {0}", msg), 100, false, true);
                    }
                    else
                    {
                        Report(ApsMonitor.Resources.MonitorResources.Format("Update denied by the device: {0}", msg), State.Percent, false, false);
                        throw new IOException(ApsMonitor.Resources.MonitorResources.Format("The device denied the firmware update: {0}", msg));
                    }
                }
                else
                {
                    Report(ApsMonitor.Resources.MonitorResources.Format("Files transferred to '{0}'. Firmware update completed successfully.", targetDir), 100, false, true);
                }
            }
            catch (OperationCanceledException)
            {
                Report(ApsMonitor.Resources.MonitorResources.Text("SFTP upload timed out."), State.Percent, false);
                throw;
            }
            catch (Exception ex)
            {
                Report(ApsMonitor.Resources.MonitorResources.Format("SFTP firmware update failed: {0}", ex.Message), State.Percent, false);
                throw;
            }
            finally
            {
                monitor.EndReprogramming();
            }
            return;
        }

        // Modo heredado MetroMadrid: negociación de credenciales por UDP puerto 50001/50000
        await RunAsync(target, TimeSpan.FromMinutes(30), async (udp, token) =>
        {
            Report(ApsMonitor.Resources.MonitorResources.Text("Starting communications transfer…"), 0);
            byte[] login = await FileCommandAsync(udp, 1, token);
            if (login.Length < 5 || login[^1] != 2) throw new IOException(ApsMonitor.Resources.MonitorResources.Text("The device did not announce SFTP access."));
            var fields = Encoding.Latin1.GetString(login, 2, login.Length - 3)
                .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2) throw new IOException(ApsMonitor.Resources.MonitorResources.Text("Incomplete SFTP access response."));
            static string Decode(string value) => new(value.Select(c => (char)(c ^ 1)).ToArray());
            string user = Decode(fields[1]);
            string password = Decode(fields[0]);

            Report(ApsMonitor.Resources.MonitorResources.Text("Uploading files through SFTP…"), 10);
            await sftpUploader.UploadAsync(target.Address, sftpPort, user, password, hostKeySha256, files,
                (message, percent) => Report(message, 10 + percent * 0.8), token, targetDir);

            Report("Finalizando transferencia…", 90);
            byte[] completion = await FileCommandAsync(udp, 4, token);
            if (completion.Length < 4 || completion[2] != 100)
                throw new IOException(ApsMonitor.Resources.MonitorResources.Text("The device did not confirm transfer completion."));

            Report(ApsMonitor.Resources.MonitorResources.Text("Files transferred. Device restarting communications…"), 100);
        });
    }

    /// <summary>
    /// Envía el comando SEPSA 0x8A (FileCommandByIndex) para disparar la reprogramación de comunicaciones
    /// comprobando si existen archivos en /mnt/artifacts/update.
    /// Utiliza primero TLS/mTLS si está activo, luego la API HTTP si está disponible, o un socket UDP directo al puerto 50001 como respaldo.
    /// </summary>
    private async Task<(bool Success, string Message)> SendReprogramming8ACommandInternalAsync(
        ReprogrammingTarget target, CancellationToken token)
    {
        byte[] requestFrame = ReprogrammingProtocol.FileCommand8ARequest();

        // 0. Si el modo TLS / mTLS está activo, enviar por HTTPS mTLS a /api/send
        if (tlsSecurity is { UseTls: true })
        {
            try
            {
                var tlsRes = await tlsSecurity.SendSepsaFrameOverTlsAsync(target.Address, requestFrame, token);
                if (tlsRes.IsSuccess && tlsRes.Payload.Length > 0 &&
                    ReprogrammingProtocol.TryRead8AResponse(tlsRes.Payload, out bool startingTls, out string messageTls))
                {
                    return (startingTls, messageTls);
                }
            }
            catch
            {
                // Continuar al fallback si falla
            }
        }

        // 1. Intentar a través del cliente HTTP de Sepsa (WebMonitorBackend API /api/send en puerto 8080)
        if (sepsaClient != null)
        {
            try
            {
                string baseUrl = $"{target.Address}:8080";
                var httpRes = await sepsaClient.SendAsync(baseUrl, requestFrame, token);
                if (httpRes.IsSuccess && httpRes.Payload.Length > 0)
                {
                    if (ReprogrammingProtocol.TryRead8AResponse(httpRes.Payload, out bool starting, out string message))
                    {
                        return (starting, message);
                    }
                }
            }
            catch
            {
                // Fallback a UDP directo
            }
        }

        // 2. Fallback: Envío directo mediante socket UDP al puerto 50001 (o target.UdpPort)
        try
        {
            int port = target.UdpPort is > 0 and <= 65535 ? target.UdpPort : 50001;
            if (port == 50000) port = 50001;
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            if (OperatingSystem.IsWindows())
            {
                const int SIO_UDP_CONNRESET = -1744830452;
                try { udp.Client.IOControl((IOControlCode)SIO_UDP_CONNRESET, [0, 0, 0, 0], null); } catch { }
            }
            udp.Connect(target.Address, port);
            using var udpCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            udpCts.CancelAfter(TimeSpan.FromSeconds(5));

            await udp.SendAsync(requestFrame, udpCts.Token);

            while (!udpCts.IsCancellationRequested)
            {
                var udpRes = await udp.ReceiveAsync(udpCts.Token);
                if (ReprogrammingProtocol.TryRead8AResponse(udpRes.Buffer, out bool starting, out string message))
                {
                    return (starting, message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return (false, ApsMonitor.Resources.MonitorResources.Text("Timed out waiting for command 8A acknowledgement (5 s)."));
        }
        catch (Exception ex)
        {
            return (false, ApsMonitor.Resources.MonitorResources.Format("Unable to send command 8A over UDP: {0}", ex.Message));
        }

        return (false, ApsMonitor.Resources.MonitorResources.Text("No valid response received for command 8A."));
    }

    public Task<(bool Success, string Message)> SendReprogramming8ACommandAsync(
        string address, int udpPort = 50001, CancellationToken token = default) =>
        SendReprogramming8ACommandAsync(new ReprogrammingTarget(address, udpPort), token);

    public async Task<(bool Success, string Message)> SendReprogramming8ACommandAsync(
        ReprogrammingTarget target, CancellationToken token = default)
    {
        if (!monitor.TryBeginReprogramming()) throw new InvalidOperationException(ApsMonitor.Resources.MonitorResources.Text("A firmware update is already in progress."));
        try
        {
            Report(ApsMonitor.Resources.MonitorResources.Text("Stopping monitoring…"), 0);
            await monitor.WaitForMonitoringStoppedAsync();
            Report(ApsMonitor.Resources.MonitorResources.Text("Sending firmware update command 8A to the device…"), 50);

            var (ok, msg) = await SendReprogramming8ACommandInternalAsync(target, token);
            if (ok)
            {
                Report(ApsMonitor.Resources.MonitorResources.Format("Command 8A accepted by the device: {0}", msg), 100, false, true);
            }
            else
            {
                Report(ApsMonitor.Resources.MonitorResources.Format("Command 8A rejected: {0}", msg), 0, false, false);
            }
            return (ok, msg);
        }
        finally
        {
            monitor.EndReprogramming();
        }
    }


    public static void ValidateCommunicationsFiles(IReadOnlyList<CommunicationsFile> files)
    {
        if (files.Count is 0 or > 32 || files.Sum(f => (long)f.Content.Length) > 512L * 1024 * 1024)
            throw new InvalidDataException(ApsMonitor.Resources.MonitorResources.Text("Select up to 32 files, maximum 512 MiB in total."));
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
            if (file.Content.Length == 0 || file.Name.Length > 200 || file.Name is "." or ".." ||
                file.Name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')) || !names.Add(file.Name))
                throw new InvalidDataException(ApsMonitor.Resources.MonitorResources.Text("Duplicate or invalid name, or empty file."));

        bool images = files.Any(f => f.Name.EndsWith(".img", StringComparison.OrdinalIgnoreCase));
        if (images)
        {
            foreach (var file in files.Where(f => f.Name.EndsWith(".img", StringComparison.OrdinalIgnoreCase)))
            {
                var md5 = files.SingleOrDefault(f => f.Name.Equals(file.Name + ".md5", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException(ApsMonitor.Resources.MonitorResources.Format("Missing {0}.md5.", file.Name));
                string expected = Encoding.ASCII.GetString(md5.Content).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                if (!Convert.ToHexString(MD5.HashData(file.Content)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(ApsMonitor.Resources.MonitorResources.Format("Incorrect MD5 for {0}.", file.Name));
            }
            if (files.Any(f => !f.Name.EndsWith(".img", StringComparison.OrdinalIgnoreCase) &&
                !(f.Name.EndsWith(".img.md5", StringComparison.OrdinalIgnoreCase) && names.Contains(f.Name[..^4]))))
                throw new InvalidDataException(ApsMonitor.Resources.MonitorResources.Text("Select only images and their .img.md5 files."));
        }
        else if (files.Any(f => f.Name.EndsWith(".gz.md5", StringComparison.OrdinalIgnoreCase)) || (files.Count == 2 && files.Any(f => f.Name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) && files.Any(f => f.Name.EndsWith(".gz.md5", StringComparison.OrdinalIgnoreCase))))
        {
            foreach (var file in files.Where(f => f.Name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) && !f.Name.EndsWith(".md5", StringComparison.OrdinalIgnoreCase)))
            {
                var md5 = files.SingleOrDefault(f => f.Name.Equals(file.Name + ".md5", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException(ApsMonitor.Resources.MonitorResources.Format("Missing {0}.md5.", file.Name));
                string expected = Encoding.ASCII.GetString(md5.Content).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                if (!Convert.ToHexString(MD5.HashData(file.Content)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(ApsMonitor.Resources.MonitorResources.Format("Incorrect MD5 for {0}.", file.Name));
            }
        }
        else if (files.Any(f => f.Name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)))
        {
            // Ficheros .gz directos permitidos
        }
        else if (files.Count != 2 || files.Count(f => f.Name.StartsWith("edf", StringComparison.OrdinalIgnoreCase) && (f.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".bin.gz", StringComparison.OrdinalIgnoreCase))) != 1 ||
                 files.Count(f => f.Name.StartsWith("sdf", StringComparison.OrdinalIgnoreCase) && (f.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".bin.gz", StringComparison.OrdinalIgnoreCase))) != 1)
            throw new InvalidDataException(ApsMonitor.Resources.MonitorResources.Text("Select IMG images with their MD5 files, or an edf / sdf pair."));
    }

    private async Task RunAsync(ReprogrammingTarget target, TimeSpan timeout, Func<UdpClient, CancellationToken, Task> operation)
    {
        int udpPort = target.UdpPort is >= 1 and <= 65535 ? target.UdpPort : 50001;
        if (udpPort == 50000) udpPort = 50001; // Auto-fallback a puerto 50001 de ControlManager
        int localPort = target.LocalPort is >= 0 and <= 65535 ? target.LocalPort : 0;
        if (!IPAddress.TryParse(target.Address, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException(ApsMonitor.Resources.MonitorResources.Text("Enter a valid IPv4 address for the target device."));
        if (!monitor.TryBeginReprogramming()) throw new InvalidOperationException(ApsMonitor.Resources.MonitorResources.Text("A firmware update is already in progress."));
        try
        {
            Report(ApsMonitor.Resources.MonitorResources.Text("Stopping background monitoring…"), 0);
            await monitor.WaitForMonitoringStoppedAsync();
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, localPort));
            if (OperatingSystem.IsWindows())
            {
                const int SIO_UDP_CONNRESET = -1744830452; // 0x9800000C
                try { udp.Client.IOControl((IOControlCode)SIO_UDP_CONNRESET, [0, 0, 0, 0], null); } catch { }
            }
            udp.Connect(address, udpPort);
            using var cts = new CancellationTokenSource(timeout);
            await operation(udp, cts.Token);
            string message = State.Message.Contains("restarting", StringComparison.Ordinal) || State.Message.Contains("reiniciando", StringComparison.Ordinal)
                ? State.Message : ApsMonitor.Resources.MonitorResources.Text("Firmware update confirmed by the device.");
            Report(message, 100, false, true);
        }
        catch (OperationCanceledException)
        {
            Report(ApsMonitor.Resources.MonitorResources.Text("Timed out. The firmware update result has not been confirmed."), State.Percent, false);
        }
        catch (Exception ex)
        {
            Report(ApsMonitor.Resources.MonitorResources.Format("Firmware update incomplete: {0}", ex.Message), State.Percent, false);
        }
        finally { monitor.EndReprogramming(); }
    }

    private static async Task<byte[]?> ReceiveAsync(UdpClient udp, TimeSpan timeout, CancellationToken token)
    {
        using var receive = CancellationTokenSource.CreateLinkedTokenSource(token);
        receive.CancelAfter(timeout);
        try { return (await udp.ReceiveAsync(receive.Token)).Buffer; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused)
        {
            return null; // Ignorar WSAECONNRESET (10054) causado por ICMP Port Unreachable
        }
    }

    private static Task<byte[]?> ReceiveAsync(UdpClient udp, CancellationToken token) =>
        ReceiveAsync(udp, TimeSpan.FromSeconds(5), token);

    private static async Task<byte[]> FileCommandAsync(UdpClient udp, byte command, CancellationToken token)
    {
        byte[] request = ReprogrammingProtocol.Frame(3, 0x8B, [command, 0]);
        await udp.SendAsync(request, token);
        while (true)
        {
            byte[]? response = await ReceiveAsync(udp, token);
            if (response is null) { await udp.SendAsync(request, token); continue; }
            if (ReprogrammingProtocol.IsErrorResponse(response, 3)) throw new IOException(ApsMonitor.Resources.MonitorResources.Text("Communications rejected the update command."));
            if (!ReprogrammingProtocol.TryReadFileResponse(response, out var payload)) continue;
            if (payload[0] == 5) throw new IOException(ApsMonitor.Resources.MonitorResources.Text("The device cancelled the communications update."));
            if (payload[0] == command) return payload;
        }
    }
}
