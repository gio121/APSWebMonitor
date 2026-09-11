using ApsMonitor.Models;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ApsMonitor.Services;

/// <summary>
/// Servicio cliente WebSocket para streaming en tiempo real de tramas TRDP.
/// Conecta al endpoint ws://<HOST>:8080/ws/trdp.
/// </summary>
public sealed class TrdpWebSocketService : IAsyncDisposable
{
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;

    // Concurrency lock
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    // State
    public bool IsConnected => _webSocket?.State == WebSocketState.Open;
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
    /// Normaliza host/IP a URI websocket (ej: "192.168.15.1:8080" -> "ws://192.168.15.1:8080/ws/trdp")
    /// </summary>
    public static Uri BuildWebSocketUri(string deviceHost)
    {
        var s = deviceHost.Trim();
        s = s.Replace("http://", "").Replace("https://", "").Replace("ws://", "").Replace("wss://", "").TrimEnd('/');
        if (!s.Contains(':'))
            s += ":8080";
        return new Uri($"ws://{s}/ws/trdp");
    }

    /// <summary>
    /// Conecta al WebSocket del dispositivo backend.
    /// </summary>
    public async Task ConnectAsync(string deviceHost, CancellationToken ct = default)
    {
        var targetDataset = ActiveDataset;
        var targetComId   = ActiveComId;

        await DisconnectAsync();

        ActiveDataset = targetDataset;
        ActiveComId   = targetComId;

        try
        {
            var uri = BuildWebSocketUri(deviceHost);
            ConnectedHost = uri.ToString();
            LastError = null;

            _webSocket = new ClientWebSocket();
            _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            _cts = new CancellationTokenSource();

            await _webSocket.ConnectAsync(uri, ct);

            OnConnectionStateChanged?.Invoke(true);
            OnStatusMessage?.Invoke($"Conectado a WebSocket TRDP ({uri.Host})");

            _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);

            // Auto-subscribe if ActiveDataset is set
            if (!string.IsNullOrEmpty(ActiveDataset) && ActiveComId.HasValue)
            {
                await SendSubscribePayloadAsync(ActiveDataset, ActiveComId.Value, ct);
            }
        }
        catch (Exception ex)
        {
            LastError = $"Error al conectar WebSocket: {ex.Message}";
            OnConnectionStateChanged?.Invoke(false);
            OnStatusMessage?.Invoke(LastError);
        }
    }

    /// <summary>
    /// Conecta al WebSocket y se suscribe al dataset y com_id indicados.
    /// </summary>
    public async Task ConnectAndSubscribeAsync(string deviceHost, string datasetName, long comId, CancellationToken ct = default)
    {
        ActiveDataset = datasetName;
        ActiveComId   = comId;

        var targetUri = BuildWebSocketUri(deviceHost).ToString();

        if (IsConnected && ConnectedHost == targetUri)
        {
            await SubscribeAsync(datasetName, comId, ct);
            return;
        }

        await ConnectAsync(deviceHost, ct);
    }

    /// <summary>
    /// Se suscribe a las tramas entrantes de un dataset específico y com_id.
    /// Payload: { "action": "subscribe", "dataset": "...", "com_id": 123 }
    /// </summary>
    public async Task SubscribeAsync(string datasetName, long comId, CancellationToken ct = default)
    {
        ActiveDataset = datasetName;
        ActiveComId   = comId;

        if (!IsConnected || _webSocket is null)
            return;

        await SendSubscribePayloadAsync(datasetName, comId, ct);
    }

    private async Task SendSubscribePayloadAsync(string datasetName, long comId, CancellationToken ct)
    {
        var payload = new
        {
            action  = "subscribe",
            dataset = datasetName,
            com_id  = comId
        };

        var json = JsonSerializer.Serialize(payload, TrdpJsonOptions.Default);
        await SendTextAsync(json, ct);
        OnStatusMessage?.Invoke($"Enviada suscripción WS a dataset '{datasetName}' (ComID: {comId})");
    }

    /// <summary>
    /// Desuscribe la sesión actual.
    /// Payload: { "action": "unsubscribe" }
    /// </summary>
    public async Task UnsubscribeAsync(CancellationToken ct = default)
    {
        ActiveDataset = null;
        ActiveComId   = null;

        if (!IsConnected || _webSocket is null)
            return;

        var payload = new { action = "unsubscribe" };
        var json = JsonSerializer.Serialize(payload, TrdpJsonOptions.Default);
        await SendTextAsync(json, ct);
        OnStatusMessage?.Invoke("Desuscrito de dataset TRDP.");
    }

    /// <summary>
    /// Desconecta el WebSocket y libera recursos.
    /// </summary>
    public async Task DisconnectAsync()
    {
        if (_cts != null)
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
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

    private async Task SendTextAsync(string message, CancellationToken ct)
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

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024]; // 64 KB buffer
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
                    ProcessIncomingMessage(jsonStr);
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

    private void ProcessIncomingMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Check status ack
            if (root.TryGetProperty("status", out var st))
            {
                var statusStr = st.GetString();
                OnStatusMessage?.Invoke($"Respuesta backend WS: {statusStr}");
            }

            // Check if payload contains frame data
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

    private void UpdateStatistics()
    {
        lock (_statsLock)
        {
            var now = DateTime.UtcNow;
            _packetTimestamps.Enqueue(now);

            // Remove timestamps older than 2 seconds
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
}
