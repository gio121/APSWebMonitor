using ApsMonitor.Models;
using Renci.SshNet;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApsMonitor.Services;

/// <summary>
/// Servicio de comunicación para configuración TRDP y comandos hacia la tarjeta de comunicaciones.
/// Implementa gestión de configuración mediante SFTP (puerto 22), reinicio de servicio por SSH / ControlManager,
/// y envío de comandos SEPSA por UDP (puerto 50001 habitual de comms), con fallback a HTTP si se requiere.
/// </summary>
public sealed class TrdpBackendService
{
    private readonly IHttpClientFactory _factory;
    private readonly TrdpConfigService  _configService;

    private record SftpCredential(string User, string Password);

    private static readonly List<SftpCredential> DefaultCredentials = new()
    {
        new("root", "changeme"),
        new("sepsa", "thisisveryunsafepleasedeactivatemeaftersetup"),
        new("update", "38I3jf/(Rihs959.fe9L0ra93nf")
    };

    public TrdpBackendService(IHttpClientFactory factory, TrdpConfigService configService)
    {
        _factory       = factory;
        _configService = configService;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    public static string ExtractHost(string urlOrHost)
    {
        var s = urlOrHost.Trim();
        s = s.Replace("http://", "", StringComparison.OrdinalIgnoreCase)
             .Replace("https://", "", StringComparison.OrdinalIgnoreCase)
             .Replace("ws://", "", StringComparison.OrdinalIgnoreCase)
             .Replace("wss://", "", StringComparison.OrdinalIgnoreCase)
             .TrimEnd('/');
        var colonIdx = s.IndexOf(':');
        if (colonIdx > 0)
            s = s.Substring(0, colonIdx);
        return s;
    }

    private HttpClient CreateClient(string deviceBaseUrl)
    {
        var client = _factory.CreateClient("TrdpBackend");
        client.BaseAddress = NormalizeBase(deviceBaseUrl);
        client.Timeout     = TimeSpan.FromSeconds(10);
        return client;
    }

    private static Uri NormalizeBase(string url)
    {
        var s = url.Trim();
        if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            s = "http://" + s;

        var authority = new Uri(s).Authority;
        if (!authority.Contains(':'))
            s = s.TrimEnd('/') + ":8080";

        return new Uri(s.TrimEnd('/') + "/", UriKind.Absolute);
    }

    // ── A. GET current config (SFTP primario con fallback a HTTP) ───────────

    /// <summary>
    /// Lee la configuración activa de /mnt/artifacts/ecnmanager.json o /etc/sepsa/ecnmanager.json mediante SFTP.
    /// </summary>
    public async Task<TrdpBackendResult<string>> GetConfigAsync(
        string deviceBaseUrl,
        CancellationToken ct = default)
    {
        var host = ExtractHost(deviceBaseUrl);

        // 1. Intento por SFTP
        try
        {
            foreach (var cred in DefaultCredentials)
            {
                try
                {
                    using var sftp = new SftpClient(host, 22, cred.User, cred.Password);
                    sftp.ConnectionInfo.Timeout = TimeSpan.FromSeconds(5);
                    sftp.OperationTimeout = TimeSpan.FromSeconds(10);
                    await sftp.ConnectAsync(ct);

                    string content = "";
                    if (sftp.Exists("/mnt/artifacts/ecnmanager.json"))
                    {
                        content = sftp.ReadAllText("/mnt/artifacts/ecnmanager.json");
                    }
                    else if (sftp.Exists("/etc/sepsa/ecnmanager.json"))
                    {
                        content = sftp.ReadAllText("/etc/sepsa/ecnmanager.json");
                    }

                    sftp.Disconnect();

                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        return TrdpBackendResult<string>.Ok(content);
                    }
                }
                catch
                {
                    // Probar siguiente credencial
                }
            }
        }
        catch { }

        // 2. Fallback HTTP si SFTP no responde o no está disponible
        try
        {
            using var client   = CreateClient(deviceBaseUrl);
            using var response = await client.GetAsync("api/config/trdp", ct);
            var body           = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return TrdpBackendResult<string>.Fail($"HTTP {(int)response.StatusCode}: {body}");

            return TrdpBackendResult<string>.Ok(body);
        }
        catch (Exception ex)
        {
            return TrdpBackendResult<string>.Fail($"Error obteniendo configuración (SFTP/HTTP): {ex.Message}");
        }
    }

    // ── B. POST config (SFTP primario con fallback a HTTP) ──────────────────

    /// <summary>
    /// Envía la configuración TRDP al dispositivo mediante SFTP en /mnt/artifacts/ecnmanager.json.
    /// Si <paramref name="restartService"/> es true, reinicia ecnmanager vía SSH y ControlManager.
    /// </summary>
    public async Task<TrdpBackendResult<TrdpSaveResponse>> SendConfigAsync(
        string deviceBaseUrl,
        TrdpSessionConfig config,
        bool restartService = false,
        CancellationToken ct = default)
    {
        var host = ExtractHost(deviceBaseUrl);

        var payload = new
        {
            restart_service     = restartService,
            use_dynamic_mapping = config.UseDynamicMapping,
            tcms_ok             = config.TcmsOk,
            control_frame       = config.ControlFrame,
            comms_datasets      = config.CommsDatasets,
            networks            = config.Networks
        };
        var finalJson = JsonSerializer.Serialize(payload, TrdpJsonOptions.Pretty);

        // 1. Intento por SFTP
        try
        {
            foreach (var cred in DefaultCredentials)
            {
                try
                {
                    using var sftp = new SftpClient(host, 22, cred.User, cred.Password);
                    sftp.ConnectionInfo.Timeout = TimeSpan.FromSeconds(5);
                    sftp.OperationTimeout = TimeSpan.FromSeconds(10);
                    await sftp.ConnectAsync(ct);

                    // Asegurar directorio /mnt/artifacts
                    if (!sftp.Exists("/mnt/artifacts"))
                    {
                        try { sftp.CreateDirectory("/mnt/artifacts"); } catch { }
                    }

                    sftp.WriteAllText("/mnt/artifacts/ecnmanager.json", finalJson);
                    sftp.Disconnect();

                    bool restarted = false;
                    if (restartService)
                    {
                        restarted = await RestartEcnManagerServiceAsync(host, cred, ct);
                    }

                    return TrdpBackendResult<TrdpSaveResponse>.Ok(
                        new TrdpSaveResponse("ok", "Configuración guardada por SFTP en /mnt/artifacts/ecnmanager.json", restarted));
                }
                catch
                {
                    // Probar siguiente credencial
                }
            }
        }
        catch { }

        // 2. Fallback HTTP
        try
        {
            using var content  = new StringContent(finalJson, System.Text.Encoding.UTF8, "application/json");
            if (content.Headers.ContentType != null)
                content.Headers.ContentType.CharSet = null;

            using var client   = CreateClient(deviceBaseUrl);
            using var response = await client.PostAsync("api/config/trdp", content, ct);
            var body           = await response.Content.ReadAsStringAsync(ct);

            var parsed = TryParseSaveResponse(body);

            if (!response.IsSuccessStatusCode)
            {
                var errMsg = parsed?.Message is { Length: > 0 } m ? m : $"HTTP {(int)response.StatusCode}";
                return TrdpBackendResult<TrdpSaveResponse>.Fail($"{errMsg}\n\n{body}".Trim());
            }

            return TrdpBackendResult<TrdpSaveResponse>.Ok(
                parsed ?? new TrdpSaveResponse("ok", "Configuración guardada (HTTP fallback)", false));
        }
        catch (Exception ex)
        {
            return TrdpBackendResult<TrdpSaveResponse>.Fail($"Error al guardar configuración: {ex.Message}");
        }
    }

    // ── C. POST mode switch ────────────────────────────────────────────────

    /// <summary>
    /// Conmuta entre modo dinámico y legacy actualizando ecnmanager.json por SFTP.
    /// </summary>
    public async Task<TrdpBackendResult<TrdpSaveResponse>> SetModeAsync(
        string deviceBaseUrl,
        bool useDynamicMapping,
        bool restartService = true,
        CancellationToken ct = default)
    {
        try
        {
            var cfgRes = await GetConfigAsync(deviceBaseUrl, ct);
            if (!cfgRes.IsSuccess || string.IsNullOrWhiteSpace(cfgRes.Data))
                return TrdpBackendResult<TrdpSaveResponse>.Fail($"No se pudo leer la configuración actual: {cfgRes.Error}");

            using var doc = JsonDocument.Parse(cfgRes.Data);
            var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(cfgRes.Data, TrdpJsonOptions.Default)
                       ?? new Dictionary<string, object>();

            dict["use_dynamic_mapping"] = useDynamicMapping;
            var updatedJson = JsonSerializer.Serialize(dict, TrdpJsonOptions.Pretty);

            var host = ExtractHost(deviceBaseUrl);

            // Guardar por SFTP
            bool saved = false;
            foreach (var cred in DefaultCredentials)
            {
                try
                {
                    using var sftp = new SftpClient(host, 22, cred.User, cred.Password);
                    sftp.ConnectionInfo.Timeout = TimeSpan.FromSeconds(5);
                    await sftp.ConnectAsync(ct);
                    sftp.WriteAllText("/mnt/artifacts/ecnmanager.json", updatedJson);
                    sftp.Disconnect();
                    saved = true;

                    if (restartService)
                    {
                        await RestartEcnManagerServiceAsync(host, cred, ct);
                    }
                    break;
                }
                catch { }
            }

            if (saved)
            {
                return TrdpBackendResult<TrdpSaveResponse>.Ok(
                    new TrdpSaveResponse("ok", $"Modo cambiado a {(useDynamicMapping ? "Dinámico" : "Estático/Legacy")}", restartService));
            }

            // Fallback HTTP
            var payload = JsonSerializer.Serialize(
                new { use_dynamic_mapping = useDynamicMapping, restart_service = restartService },
                TrdpJsonOptions.Default);

            using var content  = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            if (content.Headers.ContentType != null)
                content.Headers.ContentType.CharSet = null;
            using var client   = CreateClient(deviceBaseUrl);
            using var response = await client.PostAsync("api/config/trdp/mode", content, ct);
            var body           = await response.Content.ReadAsStringAsync(ct);

            var parsed = TryParseSaveResponse(body);
            if (!response.IsSuccessStatusCode)
                return TrdpBackendResult<TrdpSaveResponse>.Fail(
                    parsed?.Message ?? $"HTTP {(int)response.StatusCode}: {body}");

            return TrdpBackendResult<TrdpSaveResponse>.Ok(
                parsed ?? new TrdpSaveResponse("ok", "Modo actualizado", restartService));
        }
        catch (Exception ex)
        {
            return TrdpBackendResult<TrdpSaveResponse>.Fail($"Error de conexión: {ex.Message}");
        }
    }

    // ── D. GET RX Last Snapshot (REST Fallback) ───────────────────────────

    public async Task<TrdpBackendResult<TrdpFrameMessage>> GetRxLastFrameAsync(
        string deviceBaseUrl,
        string datasetName,
        CancellationToken ct = default)
    {
        try
        {
            using var client   = CreateClient(deviceBaseUrl);
            var uri            = $"api/trdp/rx/last?dataset={Uri.EscapeDataString(datasetName)}";
            using var response = await client.GetAsync(uri, ct);
            var body           = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return TrdpBackendResult<TrdpFrameMessage>.Fail($"HTTP {(int)response.StatusCode}: {body}");

            var frame = JsonSerializer.Deserialize<TrdpFrameMessage>(body, TrdpJsonOptions.Default);
            if (frame is null)
                return TrdpBackendResult<TrdpFrameMessage>.Fail("No se pudo deserializar la respuesta del snapshot.");

            return TrdpBackendResult<TrdpFrameMessage>.Ok(frame);
        }
        catch (Exception ex)
        {
            return TrdpBackendResult<TrdpFrameMessage>.Fail($"Error al consultar snapshot: {ex.Message}");
        }
    }

    // ── E. Send Command (UDP a ControlManager puerto 50001) ─────────────────

    /// <summary>
    /// Envía una trama de comando directamente hacia el ControlManager a través de UDP en el puerto 50001 habitual.
    /// </summary>
    public async Task<TrdpBackendResult<string>> SendCommandAsync(
        string deviceBaseUrl,
        byte[] payload,
        CancellationToken ct = default)
    {
        var host = ExtractHost(deviceBaseUrl);

        try
        {
            using var udp = new UdpClient();
            if (OperatingSystem.IsWindows())
            {
                const int SIO_UDP_CONNRESET = -1744830452;
                udp.Client.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
            }

            IPAddress ip;
            if (!IPAddress.TryParse(host, out ip!))
            {
                var addresses = await Dns.GetHostAddressesAsync(host, ct);
                ip = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0];
            }

            var ep = new IPEndPoint(ip, 50001);
            await udp.SendAsync(payload, payload.Length, ep);

            return TrdpBackendResult<string>.Ok("Comando enviado exitosamente por UDP a ControlManager (puerto 50001)");
        }
        catch (Exception udpEx)
        {
            // Fallback a HTTP /api/send si está disponible
            try
            {
                var bodyObj = new { payload = payload.Select(b => (int)b).ToArray() };
                using var client = CreateClient(deviceBaseUrl);
                using var response = await client.PostAsJsonAsync("api/send", bodyObj, ct);
                var body = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                    return TrdpBackendResult<string>.Fail($"UDP fallo ({udpEx.Message}) y HTTP {(int)response.StatusCode}: {body}");

                return TrdpBackendResult<string>.Ok("Comando enviado a Control (vía HTTP)");
            }
            catch (Exception ex)
            {
                return TrdpBackendResult<string>.Fail($"Error al enviar comando: {udpEx.Message} / {ex.Message}");
            }
        }
    }

    // ── Private helpers ────────────────────────────────────────────────────

    private static async Task<bool> RestartEcnManagerServiceAsync(string host, SftpCredential cred, CancellationToken ct)
    {
        bool restarted = false;

        // 1. Vía SSH Command
        try
        {
            using var ssh = new SshClient(host, 22, cred.User, cred.Password);
            ssh.ConnectionInfo.Timeout = TimeSpan.FromSeconds(5);
            await ssh.ConnectAsync(ct);
            using var cmd = ssh.CreateCommand("systemctl restart ecnmanager");
            await Task.Run(() => cmd.Execute(), ct);
            ssh.Disconnect();
            restarted = true;
        }
        catch { }

        // 2. Vía comando SEPSA FileCommandByName (0x8B) a ControlManager por UDP puerto 50001
        try
        {
            using var udp = new UdpClient();
            if (OperatingSystem.IsWindows())
            {
                const int SIO_UDP_CONNRESET = -1744830452;
                udp.Client.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
            }
            IPAddress ip;
            if (!IPAddress.TryParse(host, out ip!))
            {
                var addresses = await Dns.GetHostAddressesAsync(host, ct);
                ip = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0];
            }

            // Trama SEPSA 0x8B con comando 'R' (0x52)
            byte[] frame = new byte[8];
            frame[0] = 0xAA;
            frame[1] = 0x08;
            frame[2] = 0x03; // Dest: Comms
            frame[3] = 0x01; // Source: PIU
            frame[4] = 0x8B; // FileCommandByName
            frame[5] = 0x52; // 'R' = Reload / Restart
            frame[6] = 0x00;
            byte chk = 0;
            for (int i = 0; i < 7; i++) chk += frame[i];
            frame[7] = chk;

            await udp.SendAsync(frame, frame.Length, new IPEndPoint(ip, 50001));
            restarted = true;
        }
        catch { }

        return restarted;
    }

    private static TrdpSaveResponse? TryParseSaveResponse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<TrdpSaveResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { return null; }
    }
}

// ── Result types ───────────────────────────────────────────────────────────

public sealed class TrdpBackendResult<T>
{
    public bool   IsSuccess { get; private init; }
    public T?     Data      { get; private init; }
    public string Error     { get; private init; } = string.Empty;

    public static TrdpBackendResult<T> Ok(T data)
        => new() { IsSuccess = true, Data = data };

    public static TrdpBackendResult<T> Fail(string error)
        => new() { IsSuccess = false, Error = error };
}

public sealed class TrdpSaveResponse
{
    public TrdpSaveResponse() { }
    public TrdpSaveResponse(string status, string message, bool serviceRestarted)
    {
        Status = status; Message = message; ServiceRestarted = serviceRestarted;
    }

    [JsonPropertyName("status")]           public string Status           { get; set; } = string.Empty;
    [JsonPropertyName("message")]          public string Message          { get; set; } = string.Empty;
    [JsonPropertyName("service_restarted")]public bool   ServiceRestarted { get; set; }
}