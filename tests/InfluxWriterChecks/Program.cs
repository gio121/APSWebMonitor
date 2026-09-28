using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using ApsMonitor.Services;
using Microsoft.Extensions.Options;

CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-ES");
static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static async Task Until(Func<bool> predicate)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    while (!predicate()) await Task.Delay(20, timeout.Token);
}
static InfluxOptions Config() => new()
{
    Enabled = true, Organization = "org pruebas", Bucket = "bucket/pruebas", Token = "test-token",
    QueueCapacity = 2, BatchSize = 2
};

var handler = new CaptureHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.NoContent);
using (var writer = new InfluxWriterService(Options.Create(Config()), new ClientFactory(handler)))
{
    writer.Enqueue("equipo 1", "sesion", 1, 1, "temp,=a\\b", 45.2, 1770000000000000000);
    writer.Enqueue("equipo 1", "sesion", 1, 2, "estado", 0, 1770000000000000000);
    writer.Enqueue("equipo 1", "sesion", 1, 3, "overflow", 1, 1770000000000000000);
    writer.Enqueue("equipo 1", "sesion", 1, 4, "invalid", double.NaN, 1770000000000000000);
    Check(writer.Status.Pending == 2 && writer.Status.Dropped == 2, "Bounded queue/invalid value failure");
    await writer.StartAsync(default);
    await Until(() => writer.Status.Written == 2);
    var requests = handler.Requests.ToArray();
    Check(requests.Length == 2 && requests[0].Body == requests[1].Body, "Retry must preserve original timestamps/body");
    Check(requests[0].Body.Contains("valor=45.2 ") && requests[0].Body.Split('\n').Length == 2, "Invariant decimal/batching failure");
    Check(requests[0].Body.Contains(@"equipo=equipo\ 1") && requests[0].Body.Contains(@"senal=temp\,\=a\\b"), "Tag escaping failure");
    Check(requests[0].Url.Contains("org=org%20pruebas") && requests[0].Url.Contains("bucket=bucket%2Fpruebas") && requests[0].Url.Contains("precision=ns"), "Endpoint encoding failure");
    Check(requests[0].Auth == "Token test-token", "Token header failure");
    Check(writer.Status.Pending == 0 && writer.Status.Error == null && writer.Status.LastWrite != null, "Success status failure");
    await writer.StopAsync(default);
}

var rejected = new CaptureHandler(HttpStatusCode.Unauthorized);
using (var writer = new InfluxWriterService(Options.Create(Config()), new ClientFactory(rejected)))
{
    writer.Enqueue("d", "s", 1, 1, "v", 1, 1);
    await writer.StartAsync(default);
    await Until(() => writer.Status.Dropped == 1);
    Check(writer.Status.Error?.Contains("401") == true && writer.Status.Pending == 0, "Permanent error status failure");
    await writer.StopAsync(default);
}

foreach (var options in new[] { new InfluxOptions(), new InfluxOptions { Enabled = true } })
{
    var unused = new CaptureHandler(HttpStatusCode.NoContent);
    using var writer = new InfluxWriterService(Options.Create(options), new ClientFactory(unused));
    writer.Enqueue("d", "s", 1, 1, "v", 1, 1);
    await writer.StartAsync(default);
    Check(writer.Status.Pending == 0 && unused.Requests.IsEmpty, "Disabled/invalid configuration sent data");
    Check(!options.Enabled || writer.Status.Error != null, "Configuration error missing");
    await writer.StopAsync(default);
}
Console.WriteLine("PASS: batching, timestamps, culture, escaping, token, retry, queue limits, errors, disabled/configuration.");

sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}
sealed class CaptureHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
{
    public ConcurrentQueue<(string Url, string Body, string? Auth)> Requests { get; } = new();
    private int _index;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue((request.RequestUri!.AbsoluteUri, await request.Content!.ReadAsStringAsync(cancellationToken), request.Headers.Authorization?.ToString()));
        return new HttpResponseMessage(statuses[Math.Min(Interlocked.Increment(ref _index) - 1, statuses.Length - 1)]);
    }
}
