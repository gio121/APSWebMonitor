using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using ApsMonitor.Models;

namespace ApsMonitor.Services;

/// <summary>
/// Servicio Singleton que mantiene el estado de la monitorización en tiempo real.
/// Se comunica habitualmente por UDP (puerto 50001) directamente con ControlManager de la tarjeta de comunicaciones.
/// </summary>
public class MonitorStateService : IDisposable
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SepsaProtocolClient _sepsaClient;
    private readonly InfluxWriterService _influx;
    public string? SessionId { get; private set; }
    private long _lastTimestampNs;

    private CancellationTokenSource? _monitoringCts;
    private UdpClient? _udpClient;

    // ── Configuración ────────────────────────────────────────────────────────────
    public string IpAddress { get; set; } = "192.168.15.1";
    public string Port { get; set; } = "50001";
    public string PayloadHex { get; set; } = "00 00";

    // ── Estado de conexión ───────────────────────────────────────────────────────
    public bool IsConnected { get; private set; } = false;
    public bool IsConnecting { get; private set; } = false;
    public bool IsMonitoring { get; private set; } = false;
    private int _reprogramming;
    private readonly object _operationLock = new();
    private Task _monitoringTask = Task.CompletedTask;
    private readonly SemaphoreSlim _protocolGate = new(1, 1);
    public bool IsReprogramming => Volatile.Read(ref _reprogramming) != 0;

    public bool TryBeginReprogramming()
    {
        lock (_operationLock)
        {
            if (Interlocked.CompareExchange(ref _reprogramming, 1, 0) != 0) return false;
            StopMonitoring();
            NotifyStateChanged();
            return true;
        }
    }

    public async Task WaitForMonitoringStoppedAsync()
    {
        await _monitoringTask;
        await _protocolGate.WaitAsync();
        _protocolGate.Release();
    }

    private async Task<SepsaExchangeResult> SendFrameAsync(string targetAddress, int targetPort, byte[] frame, CancellationToken token)
    {
        await _protocolGate.WaitAsync(token);
        try
        {
            if (IsReprogramming) throw new OperationCanceledException("Reprogramación en curso.");

            // 1. Envío directo por UDP a ControlManager (puerto 50001 habitual en proyectos de comms)
            if (_udpClient != null)
            {
                try
                {
                    IPAddress ip;
                    if (!IPAddress.TryParse(targetAddress, out ip!))
                    {
                        var addresses = await Dns.GetHostAddressesAsync(targetAddress, token);
                        ip = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0];
                    }

                    var endpoint = new IPEndPoint(ip, targetPort > 0 ? targetPort : 50001);
                    await _udpClient.SendAsync(frame, frame.Length, endpoint);

                    using var rxCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    rxCts.CancelAfter(TimeSpan.FromMilliseconds(800));

                    var rxResult = await _udpClient.ReceiveAsync(rxCts.Token);
                    return SepsaExchangeResult.Success(new Uri($"udp://{targetAddress}:{targetPort}"), System.Net.HttpStatusCode.OK, rxResult.Buffer.Select(b => (int)b).ToArray());
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    return SepsaExchangeResult.Failed(new Uri($"udp://{targetAddress}:{targetPort}"), System.Net.HttpStatusCode.RequestTimeout, "Timeout UDP ControlManager");
                }
                catch (SocketException ex)
                {
                    return SepsaExchangeResult.Failed(new Uri($"udp://{targetAddress}:{targetPort}"), System.Net.HttpStatusCode.BadGateway, $"Error socket UDP: {ex.Message}");
                }
            }

            // 2. Fallback a cliente HTTP si no hay socket UDP inicializado
            var baseUrl = $"{targetAddress}:{targetPort}";
            return await _sepsaClient.SendAsync(baseUrl, frame, token);
        }
        finally { _protocolGate.Release(); }
    }

    public void EndReprogramming()
    {
        Interlocked.Exchange(ref _reprogramming, 0);
        NotifyStateChanged();
    }

    // ── Logs de red ─────────────────────────────────────────────────────────────
    private readonly object _logsLock = new();
    private readonly List<MonitorLogEntry> _logs = new();
    public IReadOnlyList<MonitorLogEntry> Logs
    {
        get { lock (_logsLock) { return _logs.ToList(); } }
    }

    public int TotalTxCount { get; private set; }
    public int TotalRxCount { get; private set; }
    public int TotalErCount { get; private set; }

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

    public MonitorStateService(IHttpClientFactory httpClientFactory, IServiceScopeFactory scopeFactory, InfluxWriterService influx)
    {
        _httpClientFactory = httpClientFactory;
        _scopeFactory = scopeFactory;
        _influx = influx;

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

        try
        {
            _udpClient?.Dispose();
            _udpClient = new UdpClient();
            if (OperatingSystem.IsWindows())
            {
                const int SIO_UDP_CONNRESET = -1744830452;
                _udpClient.Client.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
            }

            await Task.Delay(300);

            // Cargar señales desde DB (necesitamos un scope porque ApsDataService es Scoped)
            await LoadSignalsAsync();

            IsConnected = true;
            IsConnecting = false;

            int targetPort = int.TryParse(Port, out var p) ? p : 50001;
            AddLog("Sistema", $"Conectado a ControlManager UDP en {IpAddress}:{targetPort}. Listo para enviar/recibir.", false, false);
            NotifyStateChanged();
        }
        catch (Exception ex)
        {
            IsConnecting = false;
            IsConnected = false;
            AddLog("Error", $"Error al conectar socket UDP: {ex.Message}", false, true);
            NotifyStateChanged();
        }
    }

    /// <summary>
    /// Desconecta y detiene cualquier monitorización activa.
    /// </summary>
    public void Disconnect()
    {
        StopMonitoring();
        IsConnected = false;
        try { _udpClient?.Dispose(); } catch { }
        _udpClient = null;
        AddLog("Sistema", "Desconectado. Monitorización detenida.", false, false);
        NotifyStateChanged();
    }

    /// <summary>
    /// Inicia el bucle de monitorización continua (alterna tramas 0x1A y 0x1D).
    /// </summary>
    public void StartMonitoring()
    {
        lock (_operationLock)
        {
            if (!IsConnected || IsMonitoring || IsReprogramming) return;

            IsMonitoring = true;
            SessionId = $"{DateTime.UtcNow:yyyyMMddTHHmmssZ}_{Guid.NewGuid():N}";
            _monitoringCts = new CancellationTokenSource();

            AddLog("Sistema", "Monitorización continua iniciada: enviando tramas 1A y 1D alternadas cada 100ms.", false, false);
            NotifyStateChanged();

            _monitoringTask = RunMonitoringLoopAsync(_monitoringCts.Token, SessionId);
        }
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
        if (!IsConnected || IsReprogramming) return;

        try
        {
            int targetPort = int.TryParse(Port, out var p) ? p : 50001;
            var payload = SepsaProtocolClient.ParsePayload(PayloadHex);
            var frame = _sepsaClient.BuildFrame(payload);

            AddLog("Trama", $"TX {SepsaProtocolClient.ToHex(frame)}", true, false);
            NotifyStateChanged();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var response = await SendFrameAsync(IpAddress, targetPort, frame, cts.Token);

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
    /// Envía un comando SEPSA al Control Board (start/stop/reset).
    /// Construye un apsCommandFrame con frameType y subCmd y lo envía por UDP.
    /// Los comandos son "fire-and-forget": el ControlManager los ejecuta sin responder.
    /// NO pasa por SendFrameAsync/_protocolGate para no interferir con el bucle de monitorización.
    /// SubCmds conocidos: Stop AC=0x0005, Start AC=0x0006, Stop DC=0x0003, Start DC=0x0004,
    ///                     Stop APS=0x00FE, Start APS=0x00FF.
    /// Reset: frameType=0x2A, subCmd: Chopper=0x0000, LVPS=0x0001, Inversor=0x0004.
    /// </summary>
    public async Task<bool> SendControlCommandAsync(byte frameType, ushort subCmd, CancellationToken token = default)
    {
        if (!IsConnected || _udpClient == null)
        {
            AddLog("Error", "No se puede enviar comando: no hay conexión UDP activa.", false, true);
            NotifyStateChanged();
            return false;
        }

        try
        {
            // Trama de comando SEPSA: payload estándar de 8 bytes
            byte[] payload = new byte[8];
            payload[0] = (byte)(subCmd & 0xFF);
            payload[1] = (byte)((subCmd >> 8) & 0xFF);

            // Destino 0x02 (Control Board / BOARD1), Origen 0x01 (Monitor PC)
            var frame = _sepsaClient.BuildFrame(payload, source: 0x01, destination: 0x02, messageType: frameType);

            int targetPort = int.TryParse(Port, out var p) ? p : 50001;

            IPAddress ip;
            if (!IPAddress.TryParse(IpAddress, out ip!))
            {
                var addresses = await Dns.GetHostAddressesAsync(IpAddress, token);
                ip = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0];
            }

            var endpoint = new IPEndPoint(ip, targetPort > 0 ? targetPort : 50001);

            AddLog("Comando", $"TX CMD [FT:0x{frameType:X2} SC:0x{subCmd:X4}] {SepsaProtocolClient.ToHex(frame)}", true, false);

            // Envío directo fire-and-forget: enviar 2 veces para evitar pérdidas en red UDP
            await _udpClient.SendAsync(frame, frame.Length, endpoint);
            await Task.Delay(20, token);
            await _udpClient.SendAsync(frame, frame.Length, endpoint);

            AddLog("Comando", $"CMD [FT:0x{frameType:X2} SC:0x{subCmd:X4}] enviado OK", false, false);
            NotifyStateChanged();
            return true;
        }
        catch (Exception ex)
        {
            AddLog("Error", $"Excepción CMD [{subCmd:X4}]: {ex.Message}", false, true);
            NotifyStateChanged();
            return false;
        }
    }

    /// <summary>
    /// Limpia el log de red.
    /// </summary>
    public void ClearLogs()
    {
        lock (_logsLock)
        {
            _logs.Clear();
            TotalTxCount = 0;
            TotalRxCount = 0;
            TotalErCount = 0;
        }
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

    private async Task RunMonitoringLoopAsync(CancellationToken cancellationToken, string sessionId)
    {
        byte currentFrameType = 0x1A;

        try
        {
            while (!cancellationToken.IsCancellationRequested && IsConnected)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();

                try
                {
                    int targetPort = int.TryParse(Port, out var p) ? p : 50001;
                    var payload = SepsaProtocolClient.ParsePayload(PayloadHex);
                    var frame = _sepsaClient.BuildFrame(payload, messageType: currentFrameType);

                    AddLog("Trama", $"TX [{currentFrameType:X2}] {SepsaProtocolClient.ToHex(frame)}", true, false);

                    using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    reqCts.CancelAfter(500);

                    var response = await SendFrameAsync(IpAddress, targetPort, frame, reqCts.Token);
                    if (response.IsSuccess)
                    {
                        AddLog("Respuesta", $"RX [{currentFrameType:X2}] {SepsaProtocolClient.ToHex(response.Payload)}", false, false);
                        if (!cancellationToken.IsCancellationRequested)
                            ProcessIncomingFrame(response.Payload, sessionId);
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
            if (SessionId == sessionId) IsMonitoring = false;
            NotifyStateChanged();
        }
    }

    // ── Procesamiento de tramas ──────────────────────────────────────────────────

    /// <summary>
    /// Decodifica una trama de respuesta según el protocolo SEPSA.
    /// Lógica idéntica a SessionParserService.Parse() y Monitor.razor.ProcessIncomingFrame().
    /// </summary>
    private void ProcessIncomingFrame(byte[] frameData, string? sessionId = null)
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
            var timestampNs = Math.Max((DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) * 100, _lastTimestampNs + 1);
            _lastTimestampNs = timestampNs;
            foreach (var sig in nodeSignals)
            {
                int offset = sig.BytePosicion + 6;
                if (offset + SessionParserService.GetByteSize(sig.TipoVariable) <= frameData.Length)
                {
                    double rawValue = SessionParserService.ReadValue(frameData, offset, sig.TipoVariable);
                    double physValue = ApsCalculationUtils.CalculatePhysical(rawValue, sig.Escala, sig.Offset);

                    _previousValues[sig.Id] = _currentValues.TryGetValue(sig.Id, out var prev) ? prev : physValue;
                    _currentValues[sig.Id] = physValue;
                    if (sessionId != null)
                        _influx.Enqueue($"{IpAddress}:{Port}", sessionId, sig.NodoNumero, sig.Id, sig.Tag, physValue, timestampNs);
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
            if (isSent)
                TotalTxCount++;
            else if (isError)
                TotalErCount++;
            else if (prefix != "Sistema")
                TotalRxCount++;

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
        _monitoringCts?.Dispose();
        try { _udpClient?.Dispose(); } catch { }
        _protocolGate.Dispose();
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
