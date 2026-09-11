using ApsMonitor.Models;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApsMonitor.Services;

/// <summary>
/// Cliente HTTP que se comunica con el WebMonitorBackend (ecnmanager HTTP server).
/// Expuesto en http://&lt;IP_DISPOSITIVO&gt;:8080.
/// </summary>
public sealed class TrdpBackendService
{
    private readonly IHttpClientFactory _factory;
    private readonly TrdpConfigService  _configService;

    public TrdpBackendService(IHttpClientFactory factory, TrdpConfigService configService)
    {
        _factory       = factory;
        _configService = configService;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

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

        // Ensure port 8080 if no port specified (check after the "//host" part)
        var authority = new Uri(s).Authority; // "host" or "host:port"
        if (!authority.Contains(':'))
            s = s.TrimEnd('/') + ":8080";

        return new Uri(s.TrimEnd('/') + "/", UriKind.Absolute);
    }

    // ── A. GET current config ──────────────────────────────────────────────

    /// <summary>
    /// Lee la configuración activa de /etc/sepsa/ecnmanager.json en el dispositivo.
    /// Devuelve el JSON crudo para que el llamador lo importe con TrdpConfigService.ImportFromJson.
    /// </summary>
    public async Task<TrdpBackendResult<string>> GetConfigAsync(
        string deviceBaseUrl,
        CancellationToken ct = default)
    {
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
            return TrdpBackendResult<string>.Fail($"Error de conexión: {ex.Message}");
        }
    }

    // ── B. POST config ─────────────────────────────────────────────────────

    /// <summary>
    /// Envía la configuración TRDP al dispositivo.
    /// Si <paramref name="restartService"/> es true, el backend reiniciará el servicio ecnmanager.
    /// </summary>
    public async Task<TrdpBackendResult<TrdpSaveResponse>> SendConfigAsync(
        string deviceBaseUrl,
        TrdpSessionConfig config,
        bool restartService = false,
        CancellationToken ct = default)
    {
        try
        {
            // Build the JSON payload with restart_service included
            var payload = new
            {
                restart_service     = restartService,
                use_dynamic_mapping = config.UseDynamicMapping,
                control_frame       = config.ControlFrame,
                comms_datasets      = config.CommsDatasets,
                networks            = config.Networks
            };
            var finalJson = JsonSerializer.Serialize(payload, TrdpJsonOptions.Pretty);

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
                parsed ?? new TrdpSaveResponse("ok", "Configuración guardada", false));
        }
        catch (Exception ex)
        {
            return TrdpBackendResult<TrdpSaveResponse>.Fail($"Error de conexión: {ex.Message}");
        }
    }

    // ── C. POST mode switch ────────────────────────────────────────────────

    /// <summary>
    /// Conmuta rápidamente entre modo dinámico y legacy sin reenviar la configuración completa.
    /// </summary>
    public async Task<TrdpBackendResult<TrdpSaveResponse>> SetModeAsync(
        string deviceBaseUrl,
        bool useDynamicMapping,
        CancellationToken ct = default)
    {
        try
        {
            var payload = JsonSerializer.Serialize(
                new { use_dynamic_mapping = useDynamicMapping },
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
                parsed ?? new TrdpSaveResponse("ok", "Modo actualizado", false));
        }
        catch (Exception ex)
        {
            return TrdpBackendResult<TrdpSaveResponse>.Fail($"Error de conexión: {ex.Message}");
        }
    }

    // ── D. GET RX Last Snapshot (REST Fallback) ───────────────────────────

    /// <summary>
    /// Consulta el último snapshot de trama TRDP recibida para un dataset (Fallback REST).
    /// Endpoint: GET /api/trdp/rx/last?dataset=<datasetName>
    /// </summary>
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

    // ── Private helpers ────────────────────────────────────────────────────

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
