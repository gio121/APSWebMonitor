using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace ApsMonitor.Services;

public sealed class SepsaProtocolClient
{
    private const byte StartByte = 0xAA;
    private const int HeaderSize = 6;
    private const int ChecksumSize = 1;
    private readonly HttpClient _httpClient;

    public SepsaProtocolClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public byte[] BuildFrame(
        ReadOnlySpan<byte> payload,
        byte source = 0x01,
        byte destination = 0x03,
        byte messageType = 0x1D)
    {
        var frame = new byte[HeaderSize + payload.Length + ChecksumSize];

        frame[0] = StartByte;

        // El backend C++ espera el APSMessageHeader dentro del buffer. En las tramas
        // existentes el campo longitud equivale al tamano de trama mas un byte.
        ushort protocolLength = checked((ushort)(frame.Length));
        frame[1] = (byte)(protocolLength & 0xFF);
        frame[2] = (byte)(protocolLength >> 8);
        frame[3] = destination;
        frame[4] = source;
        frame[5] = messageType;

        payload.CopyTo(frame.AsSpan(HeaderSize));
        frame[^1] = CalculateChecksum(frame.AsSpan(0, frame.Length - 1));

        return frame;
    }

    public async Task<SepsaExchangeResult> SendAsync(
        string baseUrl,
        ReadOnlyMemory<byte> frame,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("La URL base no puede estar vacia.", nameof(baseUrl));

        var requestUri = BuildEndpoint(baseUrl);
        var request = new SepsaPayloadDto(frame.ToArray().Select(b => (int)b).ToArray());

        using var response = await _httpClient.PostAsJsonAsync(requestUri, request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            return SepsaExchangeResult.Failed(requestUri, response.StatusCode, responseText);

        var payload = await response.Content.ReadFromJsonAsync<SepsaPayloadDto>(cancellationToken);
        return SepsaExchangeResult.Success(requestUri, response.StatusCode, payload?.PayloadBytes ?? Array.Empty<int>());
    }

    public static byte[] ParsePayload(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Array.Empty<byte>();

        var tokens = value
            .Replace(",", " ")
            .Replace(";", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var bytes = new byte[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            bytes[i] = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToByte(token[2..], 16)
                : Convert.ToByte(token, 16);
        }

        return bytes;
    }

    public static string ToHex(IEnumerable<byte> bytes)
        => string.Join(" ", bytes.Select(b => b.ToString("X2")));

    private static Uri BuildEndpoint(string baseUrl)
    {
        var trimmed = baseUrl.Trim();
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "http://" + trimmed;
        }

        if (!trimmed.EndsWith("/api/send", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed.TrimEnd('/') + "/api/send";

        return new Uri(trimmed, UriKind.Absolute);
    }

    private static byte CalculateChecksum(ReadOnlySpan<byte> data)
    {
        int sum = 0;
        foreach (var b in data)
            sum = (sum + b) & 0xFF;

        // La trama de referencia enviada por el monitor actual termina en 0x83.
        return (byte)((sum + 2) & 0xFF);
    }

    private sealed record SepsaPayloadDto(
        [property: JsonPropertyName("payload")] int[] PayloadBytes);
}

public sealed record SepsaExchangeResult(
    Uri Endpoint,
    System.Net.HttpStatusCode StatusCode,
    byte[] Payload,
    string? Error)
{
    public bool IsSuccess => Error is null;

    public static SepsaExchangeResult Success(Uri endpoint, System.Net.HttpStatusCode statusCode, int[] payload)
        => new(endpoint, statusCode, payload.Select(Convert.ToByte).ToArray(), null);

    public static SepsaExchangeResult Failed(Uri endpoint, System.Net.HttpStatusCode statusCode, string error)
        => new(endpoint, statusCode, Array.Empty<byte>(), error);
}
