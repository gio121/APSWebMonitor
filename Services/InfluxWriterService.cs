using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace ApsMonitor.Services;

public sealed record InfluxStatus(bool Enabled, string? Error, long Pending, long Written,
    long Dropped, DateTimeOffset? LastWrite);

/// <summary>Basic, bounded in-memory queue. No network IO on the acquisition path.</summary>
public sealed class InfluxWriterService : BackgroundService
{
    private readonly InfluxOptions _options;
    private readonly IHttpClientFactory _clients;
    private readonly Channel<string> _queue;
    private readonly object _gate = new();
    private readonly string? _configurationError;
    private string? _error;
    private long _pending, _written, _dropped;
    private DateTimeOffset? _lastWrite;

    public event Action? Changed;

    public InfluxWriterService(IOptions<InfluxOptions> options, IHttpClientFactory clients)
    {
        _options = options.Value;
        _clients = clients;
        _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(Math.Clamp(_options.QueueCapacity, 1, 1000000))
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        if (_options.Enabled)
        {
            if (!Uri.TryCreate(_options.Url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https") || !string.IsNullOrEmpty(uri.UserInfo) ||
                !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                _configurationError = "La URL de InfluxDB no es válida.";
            else if (string.IsNullOrWhiteSpace(_options.Organization) || string.IsNullOrWhiteSpace(_options.Bucket) ||
                     string.IsNullOrWhiteSpace(_options.Token) || _options.Token.Any(char.IsControl))
                _configurationError = "Falta organización, bucket o un token válido de InfluxDB.";
            else if (_options.FlushIntervalSeconds is < 1 or > 60 || _options.BatchSize is < 1 or > 50000 ||
                     _options.QueueCapacity is < 1 or > 1000000)
                _configurationError = "Los límites de envío de InfluxDB no son válidos.";
        }
        _error = _configurationError;
    }

    public InfluxStatus Status
    {
        get { lock (_gate) return new(_options.Enabled, _error, _pending, _written, _dropped, _lastWrite); }
    }

    public void Enqueue(string device, string session, int node, int signalId, string signal, double value, long timestampNs)
    {
        if (!_options.Enabled || _configurationError != null) return;
        if (!double.IsFinite(value) || new[] { device, session, signal }.Any(x => x.Any(char.IsControl)))
        {
            lock (_gate) _dropped++;
            return;
        }
        var line = $"aps_monitor,equipo={Tag(device)},Sesion={Tag(session)},nodo={node.ToString(CultureInfo.InvariantCulture)},signal_id={signalId.ToString(CultureInfo.InvariantCulture)},senal={Tag(string.IsNullOrEmpty(signal) ? signalId.ToString(CultureInfo.InvariantCulture) : signal)} valor={value.ToString("R", CultureInfo.InvariantCulture)} {timestampNs.ToString(CultureInfo.InvariantCulture)}";
        lock (_gate)
        {
            if (_queue.Writer.TryWrite(line)) _pending++;
            else _dropped++;
        }
    }

    private static string Tag(string value) => value.Replace("\\", "\\\\").Replace(" ", "\\ ").Replace(",", "\\,").Replace("=", "\\=");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || _configurationError != null) return;
        var batch = new List<string>();
        using var client = _clients.CreateClient("InfluxDB");
        var endpoint = $"{_options.Url.TrimEnd('/')}/api/v2/write?org={Uri.EscapeDataString(_options.Organization)}&bucket={Uri.EscapeDataString(_options.Bucket)}&precision=ns";
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.FlushIntervalSeconds), stoppingToken);
                while (batch.Count < _options.BatchSize && _queue.Reader.TryRead(out var line)) batch.Add(line);
                if (batch.Count == 0) { Notify(); continue; }
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Token", _options.Token);
                    request.Content = new StringContent(string.Join('\n', batch), Encoding.UTF8, "text/plain");
                    using var response = await client.SendAsync(request, stoppingToken);
                    lock (_gate)
                    {
                        if (response.IsSuccessStatusCode)
                        {
                            _pending -= batch.Count;
                            _written += batch.Count;
                            _lastWrite = DateTimeOffset.UtcNow;
                            _error = null;
                            batch.Clear();
                        }
                        else
                        {
                            _error = $"InfluxDB devuelve HTTP {(int)response.StatusCode}.";
                            // Retry transient failures; do not block the queue forever on invalid points/credentials.
                            if ((int)response.StatusCode < 500 && (int)response.StatusCode != 429 && (int)response.StatusCode != 408)
                            {
                                _pending -= batch.Count;
                                _dropped += batch.Count;
                                batch.Clear();
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
                {
                    lock (_gate) _error = "No se pudo escribir en InfluxDB. Se reintentará el lote pendiente.";
                }
                Notify();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private void Notify()
    {
        foreach (var handler in Changed?.GetInvocationList() ?? [])
            try { ((Action)handler)(); } catch { /* A disposed UI must not stop writes. */ }
    }
}
