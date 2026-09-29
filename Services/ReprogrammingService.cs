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
    SepsaProtocolClient? sepsaClient = null)
{
    private ReprogrammingProgress _state = new("Listo.", 0, false);
    public ReprogrammingProgress State => _state;
    public event Action? Changed;

    private void Report(string message, double percent, bool running = true, bool success = false)
    {
        _state = new(message, percent, running, success);
        Changed?.Invoke();
    }

    public async Task UploadControlAsync(ReprogrammingTarget target, bool fpga, string signature, byte[] image, int blockSize = 128)
    {
        if (image == null || image.Length == 0) throw new ArgumentException("El archivo de firmware estÃ¡ vacÃ­o.");
        if (blockSize is < 1 or > 4096) throw new ArgumentException("TamaÃ±o de bloque no vÃ¡lido.");
        int blocks = (image.Length + blockSize - 1) / blockSize;
        byte[] pending = ReprogrammingProtocol.Start(2, fpga, blocks);
        _ = ReprogrammingProtocol.Block(2, fpga, signature, image, blockSize, 0);

        await RunAsync(target, fpga ? TimeSpan.FromMinutes(12) : TimeSpan.FromMinutes(35), async (udp, token) =>
        {
            Report("Iniciando transferencia SEPSA a la tarjeta de control en puerto 50001â€¦", 0);
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
                    // - En bloques (1): re-enviar el Ãºltimo bloque pendiente tras 10 segundos.
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

                // Ignorar tramas periÃ³dicas de telemetrÃ­a de monitorizaciÃ³n recibidas mientras se cambia de estado
                if (ReprogrammingProtocol.IsMonitoringOrTelemetryResponse(response))
                {
                    continue;
                }

                if (ReprogrammingProtocol.IsErrorResponse(response, 2))
                    throw new IOException("El equipo de control rechazÃ³ el comando SEPSA de reprogramaciÃ³n.");

                if (!ReprogrammingProtocol.TryReadStatus(response, 2, out ushort index, out byte status))
                    continue;

                switch (status)
                {
                    case 0 or 4:
                        // PeticiÃ³n de bloque index.
                        // Si index >= blocks, Block(...) rellenarÃ¡ con 0xFF (sendFFBlock heredado de SepsaCtlNet)
                        pending = ReprogrammingProtocol.Block(2, fpga, signature, image, blockSize, index);
                        await udp.SendAsync(pending, token);
                        lastSent = DateTime.UtcNow;
                        processState = 1;
                        if (index < blocks) sentBlocks.Add(index);
                        double percent = Math.Min(99.0, 100.0 * Math.Min((int)index, blocks) / blocks);
                        Report($"Transfiriendo bloque {Math.Min(index + 1, blocks)} de {blocks} ({percent:F0}%)", percent);
                        break;

                    case 1 or 2:
                        // GrabaciÃ³n o borrado de flash en progreso. En SepsaCtlNet processState = 2 (espera pasiva).
                        processState = 2;
                        if (sentBlocks.Count >= blocks)
                        {
                            Report("Fichero transferido. Verificando y grabando memoria flashâ€¦", 99);
                        }
                        else
                        {
                            double currentPercent = Math.Min(99.0, 100.0 * sentBlocks.Count / blocks);
                            Report($"Equipo procesando memoria flash ({currentPercent:F0}%)â€¦", currentPercent);
                        }
                        break;

                    case 0x80:
                        if (sentBlocks.Count >= blocks)
                        {
                            Report("ReprogramaciÃ³n de control confirmada con Ã©xito por el equipo.", 100, false, true);
                            return;
                        }
                        throw new IOException("El equipo de control indicÃ³ finalizaciÃ³n prematura antes de completar la transferencia.");

                    case 0x08: throw new IOException("El equipo de control indicÃ³ un error de transferencia de datos.");
                    case 0x10 or 0x90: throw new IOException("El equipo de control indicÃ³ un error de verificaciÃ³n.");
                    case 0x20 or 0xA0: throw new IOException("El equipo de control indicÃ³ un error de borrado de memoria flash.");
                    case 0x40 or 0xC0: throw new IOException("El equipo de control indicÃ³ un error de grabaciÃ³n en memoria flash.");
                    default: throw new IOException($"Estado SEPSA inesperado recibido de control: 0x{status:X2}.");
                }
            }
        });
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
            if (!monitor.TryBeginReprogramming()) throw new InvalidOperationException("Ya existe una reprogramación en curso.");
            try
            {
                Report("Deteniendo monitorización…", 0);
                await monitor.WaitForMonitoringStoppedAsync();
                Report($"Conectando por SFTP a {target.Address}:{sftpPort} como '{sftpUser}'…", 5);
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                await sftpUploader.UploadAsync(target.Address, sftpPort, sftpUser, sftpPassword,
                    hostKeySha256, files, (message, percent) => Report(message, percent), cts.Token, targetDir);

                if (trigger8ACommand)
                {
                    Report($"Archivos transferidos a '{targetDir}'. Enviando comando 8A de reprogramación…", 95);

                    var (ok, msg) = await SendReprogramming8ACommandInternalAsync(target, cts.Token);
                    if (ok)
                    {
                        Report($"Actualización confirmada. {msg}", 100, false, true);
                    }
                    else
                    {
                        Report($"Actualización denegada por el equipo: {msg}", State.Percent, false, false);
                        throw new IOException($"El equipo denegó la reprogramación: {msg}");
                    }
                }
                else
                {
                    Report($"Archivos transferidos a '{targetDir}'. Reprogramación completada con éxito.", 100, false, true);
                }
            }
            catch (OperationCanceledException)
            {
                Report("Tiempo de espera agotado en la subida SFTP.", State.Percent, false);
                throw;
            }
            catch (Exception ex)
            {
                Report($"Fallo en reprogramación SFTP: {ex.Message}", State.Percent, false);
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
            Report("Iniciando transferencia de comunicaciones…", 0);
            byte[] login = await FileCommandAsync(udp, 1, token);
            if (login.Length < 5 || login[^1] != 2) throw new IOException("El equipo no anunció acceso SFTP.");
            var fields = Encoding.Latin1.GetString(login, 2, login.Length - 3)
                .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2) throw new IOException("Respuesta de acceso SFTP incompleta.");
            static string Decode(string value) => new(value.Select(c => (char)(c ^ 1)).ToArray());
            string user = Decode(fields[1]);
            string password = Decode(fields[0]);

            Report("Subiendo archivos por SFTP…", 10);
            await sftpUploader.UploadAsync(target.Address, sftpPort, user, password, hostKeySha256, files,
                (message, percent) => Report(message, 10 + percent * 0.8), token, targetDir);

            Report("Finalizando transferencia…", 90);
            byte[] completion = await FileCommandAsync(udp, 4, token);
            if (completion.Length < 4 || completion[2] != 100)
                throw new IOException("El equipo no confirmó la finalización de la transferencia.");

            Report("Ficheros transferidos. Equipo reiniciando comunicaciones…", 100);
        });
    }

    /// <summary>
    /// Envía el comando SEPSA 0x8A (FileCommandByIndex) para disparar la reprogramación de comunicaciones
    /// comprobando si existen archivos en /mnt/artifacts/update.
    /// Utiliza primero la API HTTP si está disponible, o un socket UDP directo al puerto 50001 como respaldo.
    /// </summary>
    private async Task<(bool Success, string Message)> SendReprogramming8ACommandInternalAsync(
        ReprogrammingTarget target, CancellationToken token)
    {
        byte[] requestFrame = ReprogrammingProtocol.FileCommand8ARequest();

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
            return (false, "Tiempo de espera agotado esperando confirmación del comando 8A (5s).");
        }
        catch (Exception ex)
        {
            return (false, $"Error al enviar comando 8A por UDP: {ex.Message}");
        }

        return (false, "No se recibió respuesta válida del comando 8A.");
    }

    public Task<(bool Success, string Message)> SendReprogramming8ACommandAsync(
        string address, int udpPort = 50001, CancellationToken token = default) =>
        SendReprogramming8ACommandAsync(new ReprogrammingTarget(address, udpPort), token);

    public async Task<(bool Success, string Message)> SendReprogramming8ACommandAsync(
        ReprogrammingTarget target, CancellationToken token = default)
    {
        if (!monitor.TryBeginReprogramming()) throw new InvalidOperationException("Ya existe una reprogramación en curso.");
        try
        {
            Report("Deteniendo monitorización…", 0);
            await monitor.WaitForMonitoringStoppedAsync();
            Report("Enviando comando 8A de reprogramación al equipo…", 50);

            var (ok, msg) = await SendReprogramming8ACommandInternalAsync(target, token);
            if (ok)
            {
                Report($"Comando 8A aceptado por el equipo: {msg}", 100, false, true);
            }
            else
            {
                Report($"Comando 8A rechazado: {msg}", 0, false, false);
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
            throw new InvalidDataException("Seleccione hasta 32 archivos, máximo 512 MiB en total.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
            if (file.Content.Length == 0 || file.Name.Length > 200 || file.Name is "." or ".." ||
                file.Name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')) || !names.Add(file.Name))
                throw new InvalidDataException("Nombre duplicado o no válido, o archivo vacío.");

        bool images = files.Any(f => f.Name.EndsWith(".img", StringComparison.OrdinalIgnoreCase));
        if (images)
        {
            foreach (var file in files.Where(f => f.Name.EndsWith(".img", StringComparison.OrdinalIgnoreCase)))
            {
                var md5 = files.SingleOrDefault(f => f.Name.Equals(file.Name + ".md5", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"Falta {file.Name}.md5.");
                string expected = Encoding.ASCII.GetString(md5.Content).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                if (!Convert.ToHexString(MD5.HashData(file.Content)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"MD5 incorrecto para {file.Name}.");
            }
            if (files.Any(f => !f.Name.EndsWith(".img", StringComparison.OrdinalIgnoreCase) &&
                !(f.Name.EndsWith(".img.md5", StringComparison.OrdinalIgnoreCase) && names.Contains(f.Name[..^4]))))
                throw new InvalidDataException("Seleccione únicamente las imágenes y sus archivos .img.md5.");
        }
        else if (files.Any(f => f.Name.EndsWith(".gz.md5", StringComparison.OrdinalIgnoreCase)) || (files.Count == 2 && files.Any(f => f.Name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) && files.Any(f => f.Name.EndsWith(".gz.md5", StringComparison.OrdinalIgnoreCase))))
        {
            foreach (var file in files.Where(f => f.Name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) && !f.Name.EndsWith(".md5", StringComparison.OrdinalIgnoreCase)))
            {
                var md5 = files.SingleOrDefault(f => f.Name.Equals(file.Name + ".md5", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"Falta {file.Name}.md5.");
                string expected = Encoding.ASCII.GetString(md5.Content).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                if (!Convert.ToHexString(MD5.HashData(file.Content)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"MD5 incorrecto para {file.Name}.");
            }
        }
        else if (files.Any(f => f.Name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)))
        {
            // Ficheros .gz directos permitidos
        }
        else if (files.Count != 2 || files.Count(f => f.Name.StartsWith("edf", StringComparison.OrdinalIgnoreCase) && (f.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".bin.gz", StringComparison.OrdinalIgnoreCase))) != 1 ||
                 files.Count(f => f.Name.StartsWith("sdf", StringComparison.OrdinalIgnoreCase) && (f.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".bin.gz", StringComparison.OrdinalIgnoreCase))) != 1)
            throw new InvalidDataException("Seleccione imágenes IMG con sus MD5, o una pareja edf / sdf.");
    }

    private async Task RunAsync(ReprogrammingTarget target, TimeSpan timeout, Func<UdpClient, CancellationToken, Task> operation)
    {
        int udpPort = target.UdpPort is >= 1 and <= 65535 ? target.UdpPort : 50001;
        if (udpPort == 50000) udpPort = 50001; // Auto-fallback a puerto 50001 de ControlManager
        int localPort = target.LocalPort is >= 0 and <= 65535 ? target.LocalPort : 0;
        if (!IPAddress.TryParse(target.Address, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("Indique una dirección IPv4 válida para el equipo destino.");
        if (!monitor.TryBeginReprogramming()) throw new InvalidOperationException("Ya existe una reprogramación en curso.");
        try
        {
            Report("Deteniendo monitorización de fondo…", 0);
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
            string message = State.Message.Contains("reiniciando", StringComparison.Ordinal) ? State.Message : "Reprogramación confirmada por el equipo.";
            Report(message, 100, false, true);
        }
        catch (OperationCanceledException)
        {
            Report("Tiempo de espera agotado. No se ha confirmado el resultado de la reprogramación.", State.Percent, false);
        }
        catch (Exception ex)
        {
            Report($"Reprogramación no completada: {ex.Message}", State.Percent, false);
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
            if (ReprogrammingProtocol.IsErrorResponse(response, 3)) throw new IOException("Comunicaciones rechazó el comando de actualización.");
            if (!ReprogrammingProtocol.TryReadFileResponse(response, out var payload)) continue;
            if (payload[0] == 5) throw new IOException("El equipo canceló la actualización de comunicaciones.");
            if (payload[0] == command) return payload;
        }
    }
}
