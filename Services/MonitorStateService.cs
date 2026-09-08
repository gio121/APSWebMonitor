using System.Net.Http.Json;
using ApsMonitor.Models;

namespace ApsMonitor.Services;

/// <summary>
/// Servicio Singleton que mantiene el estado de la monitorización en tiempo real.
/// Al ser Singleton, la conexión y el bucle de polling persisten mientras la aplicación
/// está en ejecución, independientemente de la navegación del usuario.
/// Patrón idéntico a SessionStateService para que cualquier página pueda suscribirse
/// a los cambios de estado mediante el evento OnStateChanged.
/// </summary>
public class MonitorStateService : IDisposable
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SepsaProtocolClient _sepsaClient;

    private CancellationTokenSource? _monitoringCts;

    // ── Configuración ────────────────────────────────────────────────────────────
    public string IpAddress { get; set; } = "192.168.15.1";
    public string Port { get; set; } = "8080";
    public string PayloadHex { get; set; } = "00 00";

    // ── Estado de conexión ───────────────────────────────────────────────────────
    public bool IsConnected { get; private set; } = false;
    public bool IsConnecting { get; private set; } = false;
    public bool IsMonitoring { get; private set; } = false;

    // ── Logs de red ─────────────────────────────────────────────────────────────
    private readonly object _logsLock = new();
    private readonly List<MonitorLogEntry> _logs = new();
    public IReadOnlyList<MonitorLogEntry> Logs
    {
        get { lock (_logsLock) { return _logs.ToList(); } }
    }

    // ── Valores de señales en tiempo real ───────────────────────────────────────
    private readonly object _valuesLock = new();
    private Dictionary<int, double> _currentValues = new();
    private Dictionary<int, double> _previousValues = new();

    // Metadatos de señales cargadas (necesarios para decodificación)
    private List<Signal> _signals = new();
    private Dictionary<int, List<Signal>> _signalsByNode = new();
    private Dictionary<int, int> _nodeSizes = new();

    // ── Evento de cambio de estado ───────────────────────────────────────────────
    /// <summary>
    /// Se dispara cada vez que el estado del monitor cambia (nueva trama, conexión, etc.).
    /// Las páginas deben suscribirse para re-renderizar: OnStateChanged += () => InvokeAsync(StateHasChanged);
    /// </summary>
    public event Action? OnStateChanged;

    public MonitorStateService(IHttpClientFactory httpClientFactory, IServiceScopeFactory scopeFactory)
    {
        _httpClientFactory = httpClientFactory;
        _scopeFactory = scopeFactory;

        // Crear el cliente SEPSA usando el factory (compatible con Singleton)
        var httpClient = _httpClientFactory.CreateClient("SepsaMonitor");
        _sepsaClient = new SepsaProtocolClient(httpClient);
    }

    // ── API pública ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Conecta al dispositivo y carga las señales desde la base de datos.
    /// </summary>
    public async Task ConnectAsync()
    {
        if (string.IsNullOrWhiteSpace(IpAddress)) return;

        IsConnecting = true;
        NotifyStateChanged();

        // Simular latencia de conexión
        await Task.Delay(500);

        // Cargar señales desde DB (necesitamos un scope porque ApsDataService es Scoped)
        await LoadSignalsAsync();

        IsConnected = true;
        IsConnecting = false;

        AddLog("Sistema", $"Conectado a http://{IpAddress}:{Port}. Listo para enviar/recibir.", false, false);
        NotifyStateChanged();
    }

    /// <summary>
    /// Desconecta y detiene cualquier monitorización activa.
    /// </summary>
    public void Disconnect()
    {
        StopMonitoring();
        IsConnected = false;
        AddLog("Sistema", "Desconectado. Monitorización detenida.", false, false);
        NotifyStateChanged();
    }

    /// <summary>
    /// Inicia el bucle de monitorización continua (alterna tramas 0x1A y 0x1D).
    /// </summary>
    public void StartMonitoring()
    {
        if (!IsConnected || IsMonitoring) return;

        IsMonitoring = true;
        _monitoringCts = new CancellationTokenSource();

        AddLog("Sistema", "Monitorización continua iniciada: enviando tramas 1A y 1D alternadas cada 100ms.", false, false);
        NotifyStateChanged();

        _ = RunMonitoringLoopAsync(_monitoringCts.Token);
    }

    /// <summary>
    /// Detiene el bucle de monitorización.
    /// </summary>
    public void StopMonitoring()
    {
        if (!IsMonitoring) return;

        IsMonitoring = false;
        try
        {
            _monitoringCts?.Cancel();
            _monitoringCts?.Dispose();
        }
        catch { }
        _monitoringCts = null;
    }

    /// <summary>
    /// Envía una única trama de prueba y registra TX/RX en el log.
    /// </summary>
    public async Task SendTestFrameAsync()
    {
        if (!IsConnected) return;

        try
        {
            var baseUrl = $"{IpAddress}:{Port}";
            var payload = SepsaProtocolClient.ParsePayload(PayloadHex);
            var frame = _sepsaClient.BuildFrame(payload);

            AddLog("Trama", $"TX {SepsaProtocolClient.ToHex(frame)}", true, false);
            NotifyStateChanged();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var response = await _sepsaClient.SendAsync(baseUrl, frame, cts.Token);

            if (response.IsSuccess)
            {
                AddLog("Respuesta", $"RX {SepsaProtocolClient.ToHex(response.Payload)}", false, false);
                ProcessIncomingFrame(response.Payload);
            }
            else
            {
                AddLog("Error", $"Fallo en {response.Endpoint}. Código: {response.StatusCode}. {response.Error}", false, true);
            }
        }
        catch (Exception ex)
        {
            AddLog("Excepción", ex.Message, false, true);
        }

        NotifyStateChanged();
    }

    /// <summary>
    /// Limpia el log de red.
    /// </summary>
    public void ClearLogs()
    {
        lock (_logsLock) { _logs.Clear(); }
        NotifyStateChanged();
    }

    /// <summary>
    /// Devuelve un snapshot de los valores actuales de todas las señales (signalId → valor físico).
    /// </summary>
    public Dictionary<int, double> GetCurrentValues()
    {
        lock (_valuesLock) { return new Dictionary<int, double>(_currentValues); }
    }

    /// <summary>
    /// Devuelve las señales cargadas desde la base de datos.
    /// </summary>
    public IReadOnlyList<Signal> Signals => _signals;

    // ── Bucle de monitorización ──────────────────────────────────────────────────

    private async Task RunMonitoringLoopAsync(CancellationToken cancellationToken)
    {
        byte currentFrameType = 0x1A;

        try
        {
            while (!cancellationToken.IsCancellationRequested && IsConnected)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();

                try
                {
                    var baseUrl = $"{IpAddress}:{Port}";
                    var payload = SepsaProtocolClient.ParsePayload(PayloadHex);
                    var frame = _sepsaClient.BuildFrame(payload, messageType: currentFrameType);

                    AddLog("Trama", $"TX [{currentFrameType:X2}] {SepsaProtocolClient.ToHex(frame)}", true, false);

                    using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    reqCts.CancelAfter(500);

                    var response = await _sepsaClient.SendAsync(baseUrl, frame, reqCts.Token);
                    if (response.IsSuccess)
                    {
                        AddLog("Respuesta", $"RX [{currentFrameType:X2}] {SepsaProtocolClient.ToHex(response.Payload)}", false, false);
                        ProcessIncomingFrame(response.Payload);
                    }
                    else
                    {
                        AddLog("Error", $"Fallo en {response.Endpoint}. Código: {response.StatusCode}. {response.Error}", false, true);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException)
                {
                    AddLog("Timeout", $"Sin respuesta en trama [{currentFrameType:X2}] tras 500ms", false, true);
                }
                catch (Exception ex)
                {
                    AddLog("Excepción", $"Error enviando trama [{currentFrameType:X2}]: {ex.Message}", false, true);
                }

                // Alternar tipo de trama: 1A → 1D → 1A …
                currentFrameType = (currentFrameType == 0x1A) ? (byte)0x1D : (byte)0x1A;

                NotifyStateChanged();

                sw.Stop();
                var remainingMs = (int)(100 - sw.ElapsedMilliseconds);
                if (remainingMs > 0)
                    await Task.Delay(remainingMs, cancellationToken);
                else
                    await Task.Delay(1, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelación esperada
        }
        catch (Exception ex)
        {
            AddLog("Excepción", $"Error en bucle de monitorización: {ex.Message}", false, true);
        }
        finally
        {
            IsMonitoring = false;
            NotifyStateChanged();
        }
    }

    // ── Procesamiento de tramas ──────────────────────────────────────────────────

    /// <summary>
    /// Decodifica una trama de respuesta según el protocolo SEPSA.
    /// Lógica idéntica a SessionParserService.Parse() y Monitor.razor.ProcessIncomingFrame().
    /// </summary>
    private void ProcessIncomingFrame(byte[] frameData)
    {
        if (frameData == null || frameData.Length < 7 || _signals.Count == 0) return;

        int payloadLength = frameData.Length - 7;
        if (payloadLength <= 0) return;

        // Identificar nodo por tamaño de payload
        int? matchingNode = null;
        int minDiff = int.MaxValue;
        foreach (var kvp in _nodeSizes)
        {
            int diff = Math.Abs(payloadLength - kvp.Value);
            if (diff < minDiff)
            {
                minDiff = diff;
                matchingNode = kvp.Key;
            }
        }

        if (!matchingNode.HasValue || !_signalsByNode.TryGetValue(matchingNode.Value, out var nodeSignals))
            return;

        lock (_valuesLock)
        {
            foreach (var sig in nodeSignals)
            {
                int offset = sig.BytePosicion + 6;
                if (offset + SessionParserService.GetByteSize(sig.TipoVariable) <= frameData.Length)
                {
                    double rawValue = SessionParserService.ReadValue(frameData, offset, sig.TipoVariable);
                    double physValue = ApsCalculationUtils.CalculatePhysical(rawValue, sig.Escala, sig.Offset);

                    _previousValues[sig.Id] = _currentValues.TryGetValue(sig.Id, out var prev) ? prev : physValue;
                    _currentValues[sig.Id] = physValue;
                }
            }
        }
    }

    // ── Carga de señales ─────────────────────────────────────────────────────────

    private async Task LoadSignalsAsync()
    {
        try
        {
            // ApsDataService es Scoped → necesitamos crear un scope temporal
            using var scope = _scopeFactory.CreateScope();
            var dataService = scope.ServiceProvider.GetRequiredService<ApsDataService>();

            _signals = await dataService.GetSignalsAsync();

            _signalsByNode = _signals
                .GroupBy(s => s.NodoNumero)
                .ToDictionary(g => g.Key, g => g.ToList());

            _nodeSizes = _signalsByNode.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.Count > 0
                    ? kvp.Value.Max(s => s.BytePosicion + SessionParserService.GetByteSize(s.TipoVariable))
                    : 0);
        }
        catch (Exception ex)
        {
            AddLog("Error", $"No se pudieron cargar las señales: {ex.Message}", false, true);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private void AddLog(string prefix, string message, bool isSent, bool isError)
    {
        lock (_logsLock)
        {
            _logs.Add(new MonitorLogEntry
            {
                Timestamp = DateTime.Now,
                Message = $"[{prefix}] {message}",
                IsSent = isSent,
                IsError = isError
            });

            // Conservar máximo 100 entradas
            if (_logs.Count > 100)
                _logs.RemoveAt(0);
        }
    }

    private void NotifyStateChanged()
    {
        // Invocar cada suscriptor de forma independiente:
        // si un componente está disposed o desconectado no interrumpe al resto.
        var handlers = OnStateChanged?.GetInvocationList();
        if (handlers == null) return;
        foreach (var handler in handlers)
        {
            try
            {
                ((Action)handler)();
            }
            catch { /* Componente desconectado o disposed, ignorar */ }
        }
    }

    public void Dispose()
    {
        StopMonitoring();
    }
}

/// <summary>Entrada del log de red de monitorización.</summary>
public class MonitorLogEntry
{
    public DateTime Timestamp { get; set; }
    public string Message { get; set; } = "";
    public bool IsSent { get; set; }
    public bool IsError { get; set; }
}
