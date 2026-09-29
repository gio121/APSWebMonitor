using ApsMonitor.Models;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ApsMonitor.Services;

/// <summary>
/// Servicio cliente de streaming en tiempo real de tramas TRDP.
/// Comunicación primaria: UDP nativo (heartbeat a puerto 50003 de la placa y recepción de tramas en puerto 50002).
/// Comunicación secundaria/fallback: WebSocket (ws://<HOST>:8080/ws/trdp).
/// </summary>
public sealed class TrdpWebSocketService : IAsyncDisposable
{
    // WebSocket fallback state
    private ClientWebSocket? _webSocket;
    private Task? _wsReceiveTask;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    // UDP state
    private CancellationTokenSource? _sessionCts;
    public bool IsUdpStreaming { get; private set; } = true;

    // State
    public bool IsConnected => IsUdpStreaming ? (_sessionCts != null && !_sessionCts.IsCancellationRequested) : (_webSocket?.State == WebSocketState.Open);
    public string? ConnectedHost { get; private set; }
    public string? ActiveDataset { get; private set; }
    public long? ActiveComId { get; private set; }
    public TrdpFrameMessage? LatestFrame { get; private set; }
    public long TotalPacketsReceived { get; private set; }
    public double RefreshRateHz { get; private set; }
    public string? LastError { get; private set; }

    // Frequency calculation
    private readonly Queue<DateTime> _packetTimestamps = new();
    private readonly object _statsLock = new();

    // Callbacks
    public event Action<TrdpFrameMessage>? OnFrameReceived;
    public event Action<bool>? OnConnectionStateChanged;
    public event Action<string>? OnStatusMessage;

    /// <summary>
    /// Extrae únicamente la IP o nombre de host limpio.
    /// </summary>
    public static string ExtractHost(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "127.0.0.1";
        var s = input.Trim()
            .Replace("http://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("https://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("ws://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("wss://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("udp://", "", StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');
        var colonIdx = s.IndexOf(':');
        if (colonIdx > 0)
            s = s[..colonIdx];
        return s;
    }

    /// <summary>
    /// Normaliza host/IP a URI websocket (ej: "192.168.15.1:8080" -> "ws://192.168.15.1:8080/ws/trdp")
    /// </summary>
    public static Uri BuildWebSocketUri(string deviceHost)
    {
        var s = deviceHost.Trim();
        s = s.Replace("http://", "", StringComparison.OrdinalIgnoreCase)
             .Replace("https://", "", StringComparison.OrdinalIgnoreCase)
             .Replace("ws://", "", StringComparison.OrdinalIgnoreCase)
             .Replace("wss://", "", StringComparison.OrdinalIgnoreCase)
             .TrimEnd('/');
        if (!s.Contains(':'))
            s += ":8080";
        return new Uri($"ws://{s}/ws/trdp");
    }

    /// <summary>
    /// Conecta al streaming TRDP. Si la dirección comienza explícitamente con ws:// o wss://, usa WebSocket.
    /// De lo contrario, utiliza streaming UDP nativo estándar (puerto 50002/50003).
    /// </summary>
    public async Task ConnectAsync(string deviceHost, CancellationToken ct = default)
    {
        var targetDataset = ActiveDataset;
        var targetComId   = ActiveComId;

        await DisconnectAsync();

        ActiveDataset = targetDataset;
        ActiveComId   = targetComId;
        LastError     = null;

        var trimmed = (deviceHost ?? string.Empty).Trim();
        bool useWsFallback = trimmed.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) ||
                             trimmed.StartsWith("wss://", StringComparison.OrdinalIgnoreCase);

        if (useWsFallback)
        {
            await ConnectWebSocketAsync(deviceHost!, ct);
        }
        else
        {
            await ConnectUdpAsync(deviceHost!, ct);
        }
    }

    /// <summary>
    /// Conecta y se suscribe al dataset y com_id indicados.
    /// </summary>
    public async Task ConnectAndSubscribeAsync(string deviceHost, string datasetName, long comId, CancellationToken ct = default)
    {
        ActiveDataset = datasetName;
        ActiveComId   = comId;

        if (IsConnected)
        {
            await SubscribeAsync(datasetName, comId, ct);
            return;
        }

        await ConnectAsync(deviceHost, ct);
    }

    /// <summary>
    /// Se suscribe a las tramas entrantes de un dataset específico y com_id.
    /// </summary>
    public async Task SubscribeAsync(string datasetName, long comId, CancellationToken ct = default)
    {
        ActiveDataset = datasetName;
        ActiveComId   = comId;

        if (!IsConnected) return;

        if (!IsUdpStreaming && _webSocket != null && _webSocket.State == WebSocketState.Open)
        {
            await SendWsSubscribePayloadAsync(datasetName, comId, ct);
        }
        else
        {
            OnStatusMessage?.Invoke($"Filtro UDP activo: '{datasetName}' (ComID: {comId})");
        }
    }

    /// <summary>
    /// Desuscribe o limpia el filtro de la sesión actual.
    /// </summary>
    public async Task UnsubscribeAsync(CancellationToken ct = default)
    {
        ActiveDataset = null;
        ActiveComId   = null;

        if (!IsConnected) return;

        if (!IsUdpStreaming && _webSocket != null && _webSocket.State == WebSocketState.Open)
        {
            var payload = new { action = "unsubscribe" };
            var json = JsonSerializer.Serialize(payload, TrdpJsonOptions.Default);
            await SendWsTextAsync(json, ct);
            OnStatusMessage?.Invoke("Desuscrito de dataset TRDP.");
        }
        else
        {
            OnStatusMessage?.Invoke("Filtro UDP TRDP restablecido (mostrando todas las tramas).");
        }
    }

    /// <summary>
    /// Desconecta el streaming TRDP y libera recursos.
    /// </summary>
    public async Task DisconnectAsync()
    {
        if (_sessionCts != null)
        {
            _sessionCts.Cancel();
            _sessionCts.Dispose();
            _sessionCts = null;
        }

        if (IsUdpStreaming)
        {
            TrdpUdpHub.Unregister(this);
        }

        if (_webSocket != null)
        {
            if (_webSocket.State == WebSocketState.Open || _webSocket.State == WebSocketState.CloseReceived)
            {
                try
                {
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Desconectado por usuario", CancellationToken.None);
                }
                catch { }
            }
            _webSocket.Dispose();
            _webSocket = null;
        }

        ActiveDataset = null;
        ActiveComId   = null;
        ConnectedHost = null;
        RefreshRateHz = 0;

        OnConnectionStateChanged?.Invoke(false);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Modo Nativo UDP (Puerto 50002 / 50003)
    // ─────────────────────────────────────────────────────────────────────────

    private async Task ConnectUdpAsync(string deviceHost, CancellationToken ct)
    {
        try
        {
            IsUdpStreaming = true;
            var host = ExtractHost(deviceHost);
            ConnectedHost = $"udp://{host}:50002";
            _sessionCts = new CancellationTokenSource();

            // Registrar este servicio en el Hub UDP compartido para gestionar el puerto 50002 y heartbeat 50003
            await TrdpUdpHub.RegisterAsync(this, host);

            OnConnectionStateChanged?.Invoke(true);
            OnStatusMessage?.Invoke($"Conectado a Streaming TRDP por UDP ({host}:50002)");

            if (!string.IsNullOrEmpty(ActiveDataset) && ActiveComId.HasValue)
            {
                OnStatusMessage?.Invoke($"Filtro UDP activo: '{ActiveDataset}' (ComID: {ActiveComId})");
            }
        }
        catch (Exception ex)
        {
            LastError = $"Error al iniciar streaming UDP TRDP: {ex.Message}";
            IsUdpStreaming = false;
            OnConnectionStateChanged?.Invoke(false);
            OnStatusMessage?.Invoke(LastError);
        }
    }

    internal void DispatchUdpFrame(uint magic, uint comId, string datasetName, byte[] payload)
    {
        if (_sessionCts == null || _sessionCts.IsCancellationRequested) return;

        // Filtrado por ComID / Dataset si está configurado
        if (ActiveComId.HasValue && ActiveComId.Value > 0)
        {
            if (comId != (uint)ActiveComId.Value &&
                !string.Equals(datasetName, ActiveDataset, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
        else if (!string.IsNullOrEmpty(ActiveDataset))
        {
            if (!string.Equals(datasetName, ActiveDataset, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        var frame = new TrdpFrameMessage
        {
            Type      = "trdp_frame",
            Dataset   = datasetName,
            ComId     = comId,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Size      = payload.Length,
            RawHex    = Convert.ToHexString(payload),
            RawBytes  = payload.ToList(),
            Direction = (magic == 0x54584450) ? "tx" : "rx",
            Snapshot  = false
        };

        LatestFrame = frame;
        TotalPacketsReceived++;
        UpdateStatistics();
        OnFrameReceived?.Invoke(frame);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Modo Fallback WebSocket (ws://<HOST>:8080/ws/trdp)
    // ─────────────────────────────────────────────────────────────────────────

    private async Task ConnectWebSocketAsync(string deviceHost, CancellationToken ct)
    {
        try
        {
            IsUdpStreaming = false;
            var uri = BuildWebSocketUri(deviceHost);
            ConnectedHost = uri.ToString();

            _webSocket = new ClientWebSocket();
            _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            _sessionCts = new CancellationTokenSource();

            await _webSocket.ConnectAsync(uri, ct);

            OnConnectionStateChanged?.Invoke(true);
            OnStatusMessage?.Invoke($"Conectado a WebSocket TRDP ({uri.Host})");

            _wsReceiveTask = Task.Run(() => ReceiveWsLoopAsync(_sessionCts.Token), _sessionCts.Token);

            if (!string.IsNullOrEmpty(ActiveDataset) && ActiveComId.HasValue)
            {
                await SendWsSubscribePayloadAsync(ActiveDataset, ActiveComId.Value, ct);
            }
        }
        catch (Exception ex)
        {
            LastError = $"Error al conectar WebSocket: {ex.Message}";
            OnConnectionStateChanged?.Invoke(false);
            OnStatusMessage?.Invoke(LastError);
        }
    }

    private async Task SendWsSubscribePayloadAsync(string datasetName, long comId, CancellationToken ct)
    {
        var payload = new
        {
            action  = "subscribe",
            dataset = datasetName,
            com_id  = comId
        };

        var json = JsonSerializer.Serialize(payload, TrdpJsonOptions.Default);
        await SendWsTextAsync(json, ct);
        OnStatusMessage?.Invoke($"Enviada suscripción WS a dataset '{datasetName}' (ComID: {comId})");
    }

    private async Task SendWsTextAsync(string message, CancellationToken ct)
    {
        if (_webSocket == null || _webSocket.State != WebSocketState.Open)
            return;

        await _sendLock.WaitAsync(ct);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveWsLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var ms = new MemoryStream();

        while (!ct.IsCancellationRequested && _webSocket != null && _webSocket.State == WebSocketState.Open)
        {
            try
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await DisconnectAsync();
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    ms.Position = 0;
                    var jsonStr = Encoding.UTF8.GetString(ms.ToArray());
                    ProcessIncomingWsMessage(jsonStr);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                LastError = ex.Message;
                OnStatusMessage?.Invoke($"Error lect. WS: {ex.Message}");
                await Task.Delay(1000, ct);
            }
        }

        OnConnectionStateChanged?.Invoke(false);
    }

    private void ProcessIncomingWsMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("status", out var st))
            {
                var statusStr = st.GetString();
                OnStatusMessage?.Invoke($"Respuesta backend WS: {statusStr}");
            }

            var type = root.TryGetProperty("type", out var tEl) ? tEl.GetString() : null;
            var isSnapshot = root.TryGetProperty("snapshot", out var snEl) && snEl.GetBoolean();
            bool hasDataset = root.TryGetProperty("dataset", out _);
            bool hasParsed = root.TryGetProperty("parsed", out _);
            bool hasRaw = root.TryGetProperty("raw_bytes", out _) || root.TryGetProperty("raw_hex", out _);

            if (type == "trdp_frame" || isSnapshot || (hasDataset && (hasParsed || hasRaw)))
            {
                var frame = JsonSerializer.Deserialize<TrdpFrameMessage>(json, TrdpJsonOptions.Default);
                if (frame != null)
                {
                    LatestFrame = frame;
                    TotalPacketsReceived++;
                    UpdateStatistics();
                    OnFrameReceived?.Invoke(frame);
                }
            }
        }
        catch (Exception ex)
        {
            OnStatusMessage?.Invoke($"Error deserializando trama WS: {ex.Message}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Estadísticas y Dispose
    // ─────────────────────────────────────────────────────────────────────────

    private void UpdateStatistics()
    {
        lock (_statsLock)
        {
            var now = DateTime.UtcNow;
            _packetTimestamps.Enqueue(now);

            while (_packetTimestamps.Count > 0 && (now - _packetTimestamps.Peek()).TotalSeconds > 2.0)
            {
                _packetTimestamps.Dequeue();
            }

            if (_packetTimestamps.Count > 1)
            {
                var timeSpanSec = (now - _packetTimestamps.Peek()).TotalSeconds;
                RefreshRateHz = timeSpanSec > 0 ? (_packetTimestamps.Count - 1) / timeSpanSec : 0;
            }
            else
            {
                RefreshRateHz = 0;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _sendLock.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Hub UDP Singleton para evitar colisiones de bind en puerto 50002
    // ─────────────────────────────────────────────────────────────────────────

    private static class TrdpUdpHub
    {
        private static readonly object _hubLock = new();
        private static UdpClient? _udpClient;
        private static CancellationTokenSource? _hubCts;
        private static Task? _rxTask;
        private static Task? _heartbeatTask;

        private static readonly HashSet<TrdpWebSocketService> _clients = new();
        private static readonly Dictionary<string, int> _targets = new(StringComparer.OrdinalIgnoreCase);

        public static async Task RegisterAsync(TrdpWebSocketService service, string targetHost)
        {
            IPAddress targetIp;
            if (!IPAddress.TryParse(targetHost, out targetIp!))
            {
                var addrs = await Dns.GetHostAddressesAsync(targetHost);
                targetIp = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                           ?? addrs.First();
            }

            lock (_hubLock)
            {
                _clients.Add(service);
                if (_targets.TryGetValue(targetIp.ToString(), out int count))
                    _targets[targetIp.ToString()] = count + 1;
                else
                    _targets[targetIp.ToString()] = 1;

                if (_udpClient == null)
                {
                    _hubCts = new CancellationTokenSource();
                    _udpClient = new UdpClient();
                    _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    _udpClient.Client.ExclusiveAddressUse = false;

                    if (OperatingSystem.IsWindows())
                    {
                        const int SIO_UDP_CONNRESET = -1744830452;
                        try { _udpClient.Client.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null); } catch { }
                    }

                    _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, 50002));

                    var token = _hubCts.Token;
                    _rxTask = Task.Run(() => ReceiveLoopAsync(token), token);
                    _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(token), token);
                }
            }
        }

        public static void Unregister(TrdpWebSocketService service)
        {
            lock (_hubLock)
            {
                _clients.Remove(service);

                if (service.ConnectedHost != null && service.ConnectedHost.StartsWith("udp://", StringComparison.OrdinalIgnoreCase))
                {
                    var host = ExtractHost(service.ConnectedHost);
                    if (_targets.TryGetValue(host, out int count))
                    {
                        if (count <= 1) _targets.Remove(host);
                        else _targets[host] = count - 1;
                    }
                }

                if (_clients.Count == 0)
                {
                    StopHub_NoLock();
                }
            }
        }

        private static void StopHub_NoLock()
        {
            if (_hubCts != null)
            {
                _hubCts.Cancel();
                _hubCts.Dispose();
                _hubCts = null;
            }

            if (_udpClient != null)
            {
                try { _udpClient.Close(); } catch { }
                _udpClient.Dispose();
                _udpClient = null;
            }

            _targets.Clear();
        }

        private static async Task HeartbeatLoopAsync(CancellationToken ct)
        {
            // 0x54585354 = "TXST" en ASCII
            byte[] heartbeat = new byte[] { 0x54, 0x58, 0x53, 0x54 };

            while (!ct.IsCancellationRequested)
            {
                List<string> activeTargets;
                lock (_hubLock)
                {
                    activeTargets = _targets.Keys.ToList();
                }

                foreach (var tHost in activeTargets)
                {
                    try
                    {
                        if (_udpClient != null && IPAddress.TryParse(tHost, out var ip))
                        {
                            await _udpClient.SendAsync(heartbeat, heartbeat.Length, new IPEndPoint(ip, 50003));
                        }
                    }
                    catch { }
                }

                try
                {
                    await Task.Delay(1500, ct);
                }
                catch (OperationCanceledException) { break; }
            }
        }

        private static async Task ReceiveLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _udpClient != null)
            {
                try
                {
                    var result = await _udpClient.ReceiveAsync(ct);
                    var buf = result.Buffer;
                    if (buf.Length < 44) continue;

                    uint magic = BitConverter.ToUInt32(buf, 0);
                    // 0x54524450 = "TRDP" (Rx), 0x54584450 = "TXDP" (Tx)
                    if (magic != 0x54524450 && magic != 0x54584450)
                    {
                        continue;
                    }

                    uint comId = BitConverter.ToUInt32(buf, 4);
                    uint payloadSize = BitConverter.ToUInt32(buf, 8);
                    string datasetName = Encoding.ASCII.GetString(buf, 12, 32).TrimEnd('\0').Trim();

                    int actualPayloadLen = (int)Math.Min(payloadSize, (uint)Math.Max(0, buf.Length - 44));
                    byte[] payload = new byte[actualPayloadLen];
                    if (actualPayloadLen > 0)
                    {
                        Array.Copy(buf, 44, payload, 0, actualPayloadLen);
                    }

                    List<TrdpWebSocketService> listeners;
                    lock (_hubLock)
                    {
                        listeners = _clients.ToList();
                    }

                    foreach (var client in listeners)
                    {
                        client.DispatchUdpFrame(magic, comId, datasetName, payload);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch
                {
                    if (ct.IsCancellationRequested) break;
                    await Task.Delay(200, ct);
                }
            }
        }
    }
}
