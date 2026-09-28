using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace ApsMonitor.Services;

public sealed record ReprogrammingTarget(string Address, int UdpPort, int LocalPort = 0);
public sealed record ReprogrammingProgress(string Message, double Percent, bool Running, bool Success = false);
public sealed record CommunicationsFile(string Name, byte[] Content);

public sealed class ReprogrammingService(MonitorStateService monitor, ISftpFirmwareUploader sftpUploader)
{
    public ReprogrammingProgress State { get; private set; } = new("Sin iniciar", 0, false);
    public event Action? Changed;

    private void Report(string message, double percent, bool running = true, bool success = false)
    {
        State = new(message, Math.Clamp(percent, 0, 100), running, success);
        foreach (Action subscriber in Changed?.GetInvocationList() ?? [])
            try { subscriber(); } catch { /* A disconnected UI must not interrupt flash programming. */ }
    }

    public async Task UploadControlAsync(ReprogrammingTarget target, bool fpga, string signature,
        byte[] image, int blockSize)
    {
        if (image.Length is 0 or > FirmwareImage.MaximumBytes) throw new ArgumentException("Imagen no válida.");
        if (blockSize is < 1 or > 4096) throw new ArgumentException("Tamaño de bloque no válido.");
        int blocks = (image.Length + blockSize - 1) / blockSize;
        byte[] pending = ReprogrammingProtocol.Start(2, fpga, blocks);
        _ = ReprogrammingProtocol.Block(2, fpga, signature, image, blockSize, 0);
        await RunAsync(target, fpga ? TimeSpan.FromMinutes(12) : TimeSpan.FromMinutes(35), async (udp, token) =>
        {
            Report("Iniciando transferencia SEPSA…", 0);
            await udp.SendAsync(pending, token);
            bool programming = false;
            var sentBlocks = new HashSet<ushort>();
            while (true)
            {
                byte[]? response = await ReceiveAsync(udp, token);
                if (response is null)
                {
                    // Once flash programming starts, only listen: never restart a write.
                    if (!programming) await udp.SendAsync(pending, token);
                    continue;
                }
                if (ReprogrammingProtocol.IsErrorResponse(response, 2)) throw new IOException("El equipo rechazó el comando SEPSA de reprogramación.");
                if (!ReprogrammingProtocol.TryReadStatus(response, 2, out ushort index, out byte status)) continue;
                switch (status)
                {
                    case 0 or 4 when !programming:
                        if (index > blocks) throw new InvalidDataException("El equipo solicitó un bloque fuera de rango.");
                        pending = ReprogrammingProtocol.Block(2, fpga, signature, image, blockSize, index);
                        await udp.SendAsync(pending, token);
                        if (index < blocks) sentBlocks.Add(index);
                        Report($"Transfiriendo bloque {Math.Min(index + 1, blocks)} de {blocks}", Math.Min(99, 100.0 * index / blocks));
                        break;
                    case 1 or 2:
                        programming = true;
                        Report("Fichero transferido. Verificando y grabando…", 99);
                        break;
                    case 0x80:
                        if (sentBlocks.Count != blocks) throw new IOException("El equipo confirmó el fin sin solicitar todos los bloques.");
                        return;
                    case 0x08: throw new IOException("El equipo indicó un error de transferencia.");
                    case 0x10 or 0x90: throw new IOException("El equipo indicó un error de verificación.");
                    case 0x20 or 0xA0: throw new IOException("El equipo indicó un error de borrado de flash.");
                    case 0x40 or 0xC0: throw new IOException("El equipo indicó un error de grabación.");
                    default: throw new IOException($"Estado SEPSA inesperado: 0x{status:X2}.");
                }
            }
        });
    }

    public async Task UploadCommunicationsAsync(ReprogrammingTarget target, int sftpPort,
        string hostKeySha256, IReadOnlyList<CommunicationsFile> files)
    {
        ValidateCommunicationsFiles(files);
        if (sftpPort is < 1 or > 65535 || string.IsNullOrWhiteSpace(hostKeySha256))
            throw new ArgumentException("Indique puerto SFTP y huella SHA256 del equipo.");
        await RunAsync(target, TimeSpan.FromMinutes(30), async (udp, token) =>
        {
            Report("Solicitando acceso SFTP al equipo…", 0);
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
            handshake.CancelAfter(TimeSpan.FromSeconds(30));
            byte[] login = await FileCommandAsync(udp, 1, handshake.Token);
            if (login.Length < 5 || login[^1] != 2) throw new IOException("El equipo no anunció acceso SFTP.");
            var fields = Encoding.Latin1.GetString(login, 2, login.Length - 3)
                .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2) throw new IOException("Respuesta de acceso SFTP incompleta.");
            static string Decode(string value) => new(value.Select(c => (char)(c ^ 1)).ToArray());
            await sftpUploader.UploadAsync(target.Address, sftpPort, Decode(fields[1]), Decode(fields[0]),
                hostKeySha256, files, (message, percent) => Report(message, percent), token);
            // FILE_TRANSFER_END also polls the result. Never send it after a failed upload.
            using var verify = CancellationTokenSource.CreateLinkedTokenSource(token);
            verify.CancelAfter(TimeSpan.FromMinutes(5));
            while (true)
            {
                byte[] progress = await FileCommandAsync(udp, 4, verify.Token);
                if (progress.Length < 4 || progress[2] > 100) throw new IOException("Progreso de comunicaciones inválido.");
                if (progress[2] == 100)
                {
                    if (progress[3] != 0) Report("Actualización confirmada. Comunicaciones se está reiniciando.", 100);
                    return;
                }
                Report($"Verificando y grabando comunicaciones: {progress[2]} %", 90 + progress[2] / 10.0);
                await Task.Delay(1000, verify.Token);
            }
        });
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
        else if (files.Count != 2 || files.Count(f => f.Name.StartsWith("edf", StringComparison.OrdinalIgnoreCase) && f.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) != 1 ||
                 files.Count(f => f.Name.StartsWith("sdf", StringComparison.OrdinalIgnoreCase) && f.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) != 1)
            throw new InvalidDataException("Seleccione imágenes IMG con sus MD5, o una pareja edf*.bin y sdf*.bin.");
    }

    private async Task RunAsync(ReprogrammingTarget target, TimeSpan timeout, Func<UdpClient, CancellationToken, Task> operation)
    {
        if (!IPAddress.TryParse(target.Address, out var address) || address.AddressFamily != AddressFamily.InterNetwork ||
            target.UdpPort is < 1 or > 65535 || target.LocalPort is < 0 or > 65535)
            throw new ArgumentException("Indique la IPv4 y los puertos SEPSA del equipo (puerto local 0: automático).");
        if (!monitor.TryBeginReprogramming()) throw new InvalidOperationException("Ya existe una reprogramación en curso.");
        try
        {
            Report("Deteniendo monitorización…", 0);
            await monitor.WaitForMonitoringStoppedAsync();
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, target.LocalPort));
            udp.Connect(address, target.UdpPort);
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

    private static async Task<byte[]?> ReceiveAsync(UdpClient udp, CancellationToken token)
    {
        using var receive = CancellationTokenSource.CreateLinkedTokenSource(token);
        receive.CancelAfter(TimeSpan.FromSeconds(10));
        try { return (await udp.ReceiveAsync(receive.Token)).Buffer; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
    }

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
