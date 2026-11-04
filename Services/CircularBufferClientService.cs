using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApsMonitor.Services;

public sealed class CircularBufferClientService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TlsSecurityService? _tlsSecurity;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public CircularBufferClientService(IHttpClientFactory httpClientFactory, TlsSecurityService? tlsSecurity = null)
    {
        _httpClientFactory = httpClientFactory;
        _tlsSecurity = tlsSecurity;
    }

    private HttpClient CreateClient(TimeSpan timeout)
    {
        if (_tlsSecurity is { UseTls: true })
        {
            return _tlsSecurity.CreateHttpClient(timeout);
        }

        var client = _httpClientFactory.CreateClient("CircularBuffer");
        client.Timeout = timeout;
        return client;
    }

    private string ResolveBaseUrl(string hostOrUrl, int defaultPort = 8080)
    {
        if (_tlsSecurity is { UseTls: true })
        {
            return _tlsSecurity.BuildHttpBaseUrl(hostOrUrl, defaultPort);
        }

        return BuildBaseUrl(hostOrUrl, defaultPort);
    }

    public static string BuildBaseUrl(string hostOrUrl, int defaultPort = 8080)
    {
        var raw = (hostOrUrl ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(raw))
            raw = "192.168.15.1";

        if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            raw = "http://" + raw;
        }

        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            int port = uri.IsDefaultPort ? defaultPort : uri.Port;
            return $"{uri.Scheme}://{uri.Host}:{port}";
        }

        return $"http://{raw}:{defaultPort}";
    }

    public async Task<CircularSessionInfoDto?> GetSessionInfoAsync(
        string hostOrUrl,
        int port = 8080,
        int buckets = 60,
        CancellationToken ct = default)
    {
        var baseUrl = ResolveBaseUrl(hostOrUrl, port);
        using var client = CreateClient(TimeSpan.FromSeconds(8));

        var url = $"{baseUrl}/api/session/info?buckets={Math.Clamp(buckets, 10, 240)}";
        using var response = await client.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var wrapper = await response.Content.ReadFromJsonAsync<CircularSessionInfoResponseDto>(JsonOptions, ct);
        return wrapper?.SessionInfo;
    }

    public async Task<CircularRangeSummaryDto?> GetRangeSummaryAsync(
        string hostOrUrl,
        int port,
        ulong fromTimestampUs,
        ulong toTimestampUs,
        string? typeFilter = null,
        uint? comId = null,
        CancellationToken ct = default)
    {
        var baseUrl = ResolveBaseUrl(hostOrUrl, port);
        using var client = CreateClient(TimeSpan.FromSeconds(8));

        var query = BuildRangeQueryString(fromTimestampUs, toTimestampUs, typeFilter, comId);
        var url = $"{baseUrl}/api/session/range-summary{query}";
        using var response = await client.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var wrapper = await response.Content.ReadFromJsonAsync<CircularRangeSummaryResponseDto>(JsonOptions, ct);
        return wrapper?.RangeSummary;
    }

    public async Task<(byte[] Data, string FileName, string ContentType)> DownloadSessionBytesAsync(
        string hostOrUrl,
        int port,
        string format,
        ulong fromTimestampUs,
        ulong toTimestampUs,
        string? typeFilter = null,
        uint? comId = null,
        int limit = 0,
        CancellationToken ct = default)
    {
        var baseUrl = ResolveBaseUrl(hostOrUrl, port);
        using var client = CreateClient(TimeSpan.FromSeconds(60));

        var query = BuildRangeQueryString(fromTimestampUs, toTimestampUs, typeFilter, comId, format, limit);
        var url = $"{baseUrl}/api/session/download{query}";

        using var response = await client.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        var contentType = response.Content.Headers.ContentType?.MediaType
            ?? (string.Equals(format, "pcap", StringComparison.OrdinalIgnoreCase)
                ? "application/vnd.tcpdump.pcap"
                : "application/json");

        var ext = string.Equals(format, "pcap", StringComparison.OrdinalIgnoreCase) ? "pcap" : "json";
        var fileName = response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
            ?? $"apscomms_session_{DateTime.Now:yyyyMMdd_HHmmss}.{ext}";

        return (bytes, fileName, contentType);
    }

    public async Task<CircularDownloadedSessionDto?> DownloadSessionJsonAsync(
        string hostOrUrl,
        int port,
        ulong fromTimestampUs,
        ulong toTimestampUs,
        string? typeFilter = null,
        uint? comId = null,
        int limit = 0,
        CancellationToken ct = default)
    {
        var (bytes, _, _) = await DownloadSessionBytesAsync(
            hostOrUrl, port, "json", fromTimestampUs, toTimestampUs, typeFilter, comId, limit, ct);

        return JsonSerializer.Deserialize<CircularDownloadedSessionDto>(bytes, JsonOptions);
    }

    public async Task<CircularReplayStartResponseDto?> StartReplayAsync(
        string hostOrUrl,
        int port,
        ulong fromTimestampUs,
        ulong toTimestampUs,
        string? typeFilter = null,
        float speed = 1.0f,
        string targetIp = "127.0.0.1",
        ushort targetPort = 50002,
        bool loop = false,
        CancellationToken ct = default)
    {
        var baseUrl = ResolveBaseUrl(hostOrUrl, port);
        using var client = CreateClient(TimeSpan.FromSeconds(10));

        var payload = new Dictionary<string, object>
        {
            ["from"] = fromTimestampUs,
            ["to"] = toTimestampUs,
            ["speed"] = speed,
            ["target_ip"] = string.IsNullOrWhiteSpace(targetIp) ? "127.0.0.1" : targetIp.Trim(),
            ["target_port"] = targetPort,
            ["loop"] = loop
        };

        if (!string.IsNullOrWhiteSpace(typeFilter) && !string.Equals(typeFilter, "all", StringComparison.OrdinalIgnoreCase))
        {
            payload["type"] = typeFilter;
        }

        using var response = await client.PostAsJsonAsync($"{baseUrl}/api/session/replay/start", payload, ct);
        var body = await response.Content.ReadFromJsonAsync<CircularReplayStartResponseDto>(JsonOptions, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(body?.Message ?? $"Error HTTP {(int)response.StatusCode} al iniciar replay.");
        }
        return body;
    }

    public async Task PauseReplayAsync(string hostOrUrl, int port = 8080, CancellationToken ct = default)
    {
        var baseUrl = ResolveBaseUrl(hostOrUrl, port);
        using var client = CreateClient(TimeSpan.FromSeconds(5));
        using var response = await client.PostAsync($"{baseUrl}/api/session/replay/pause", null, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task ResumeReplayAsync(string hostOrUrl, int port = 8080, CancellationToken ct = default)
    {
        var baseUrl = ResolveBaseUrl(hostOrUrl, port);
        using var client = CreateClient(TimeSpan.FromSeconds(5));
        using var response = await client.PostAsync($"{baseUrl}/api/session/replay/resume", null, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task StopReplayAsync(string hostOrUrl, int port = 8080, CancellationToken ct = default)
    {
        var baseUrl = ResolveBaseUrl(hostOrUrl, port);
        using var client = CreateClient(TimeSpan.FromSeconds(5));
        using var response = await client.PostAsync($"{baseUrl}/api/session/replay/stop", null, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<CircularReplayStatusDto?> GetReplayStatusAsync(string hostOrUrl, int port = 8080, CancellationToken ct = default)
    {
        var baseUrl = ResolveBaseUrl(hostOrUrl, port);
        using var client = CreateClient(TimeSpan.FromSeconds(5));
        using var response = await client.GetAsync($"{baseUrl}/api/session/replay/status", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CircularReplayStatusDto>(JsonOptions, ct);
    }

    public async Task ClearBufferAsync(string hostOrUrl, int port = 8080, CancellationToken ct = default)
    {
        var baseUrl = ResolveBaseUrl(hostOrUrl, port);
        using var client = CreateClient(TimeSpan.FromSeconds(8));
        using var response = await client.PostAsync($"{baseUrl}/api/frames/clear", null, ct);
        response.EnsureSuccessStatusCode();
    }

    private static string BuildRangeQueryString(
        ulong fromTimestampUs,
        ulong toTimestampUs,
        string? typeFilter = null,
        uint? comId = null,
        string? format = null,
        int limit = 0)
    {
        var parts = new List<string>();
        if (fromTimestampUs > 0)
            parts.Add($"from={fromTimestampUs.ToString(CultureInfo.InvariantCulture)}");
        if (toTimestampUs > 0)
            parts.Add($"to={toTimestampUs.ToString(CultureInfo.InvariantCulture)}");
        if (!string.IsNullOrWhiteSpace(typeFilter) && !string.Equals(typeFilter, "all", StringComparison.OrdinalIgnoreCase))
            parts.Add($"type={Uri.EscapeDataString(typeFilter)}");
        if (comId.HasValue && comId.Value > 0)
            parts.Add($"com_id={comId.Value.ToString(CultureInfo.InvariantCulture)}");
        if (!string.IsNullOrWhiteSpace(format))
            parts.Add($"format={Uri.EscapeDataString(format)}");
        if (limit > 0)
            parts.Add($"limit={limit.ToString(CultureInfo.InvariantCulture)}");

        return parts.Count > 0 ? "?" + string.Join("&", parts) : string.Empty;
    }

    public static DateTime TimestampUsToLocalDateTime(ulong timestampUs)
    {
        if (timestampUs == 0)
            return DateTime.MinValue;

        try
        {
            long ticks = (long)(timestampUs * 10UL); // 1 us = 10 ticks
            return DateTime.UnixEpoch.AddTicks(ticks).ToLocalTime();
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    public static ulong LocalDateTimeToTimestampUs(DateTime localDateTime)
    {
        if (localDateTime <= DateTime.UnixEpoch)
            return 0;

        var utc = localDateTime.Kind == DateTimeKind.Utc
            ? localDateTime
            : DateTime.SpecifyKind(localDateTime, DateTimeKind.Local).ToUniversalTime();

        long diffTicks = utc.Ticks - DateTime.UnixEpoch.Ticks;
        return diffTicks > 0 ? (ulong)(diffTicks / 10L) : 0UL;
    }

    public static string FormatBytes(ulong bytes)
    {
        if (bytes >= 1024UL * 1024UL)
            return $"{(bytes / (1024.0 * 1024.0)):F2} MB";
        if (bytes >= 1024UL)
            return $"{(bytes / 1024.0):F1} KB";
        return $"{bytes} B";
    }
}

public sealed class CircularSessionInfoResponseDto
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("session_info")]
    public CircularSessionInfoDto? SessionInfo { get; set; }
}

public sealed class CircularSessionInfoDto
{
    [JsonPropertyName("start_timestamp_us")]
    public ulong StartTimestampUs { get; set; }

    [JsonPropertyName("end_timestamp_us")]
    public ulong EndTimestampUs { get; set; }

    [JsonPropertyName("start_iso")]
    public string StartIso { get; set; } = "";

    [JsonPropertyName("end_iso")]
    public string EndIso { get; set; } = "";

    [JsonPropertyName("duration_seconds")]
    public ulong DurationSeconds { get; set; }

    [JsonPropertyName("duration_ms")]
    public ulong DurationMs { get; set; }

    [JsonPropertyName("duration_formatted")]
    public string DurationFormatted { get; set; } = "0s";

    [JsonPropertyName("retention_hours")]
    public double RetentionHours { get; set; }

    [JsonPropertyName("total_frames")]
    public ulong TotalFrames { get; set; }

    [JsonPropertyName("total_blocks")]
    public ulong TotalBlocks { get; set; }

    [JsonPropertyName("total_capacity_bytes")]
    public ulong TotalCapacityBytes { get; set; }

    [JsonPropertyName("used_bytes")]
    public ulong UsedBytes { get; set; }

    [JsonPropertyName("compression_ratio")]
    public double CompressionRatio { get; set; } = 1.0;

    [JsonPropertyName("counts_by_source")]
    public CircularSourceCountsDto CountsBySource { get; set; } = new();

    [JsonPropertyName("timeline_buckets")]
    public List<CircularTimelineBucketDto> TimelineBuckets { get; set; } = new();
}

public sealed class CircularSourceCountsDto
{
    [JsonPropertyName("control_rx")]
    public ulong ControlRx { get; set; }

    [JsonPropertyName("control_tx")]
    public ulong ControlTx { get; set; }

    [JsonPropertyName("trdp_rx")]
    public ulong TrdpRx { get; set; }

    [JsonPropertyName("trdp_tx")]
    public ulong TrdpTx { get; set; }

    [JsonPropertyName("pte_rx")]
    public ulong PteRx { get; set; }

    [JsonPropertyName("pte_tx")]
    public ulong PteTx { get; set; }
}

public sealed class CircularTimelineBucketDto
{
    [JsonPropertyName("start_timestamp_us")]
    public ulong StartTimestampUs { get; set; }

    [JsonPropertyName("end_timestamp_us")]
    public ulong EndTimestampUs { get; set; }

    [JsonPropertyName("total_frames")]
    public ulong TotalFrames { get; set; }

    [JsonPropertyName("counts_by_source")]
    public CircularSourceCountsDto CountsBySource { get; set; } = new();
}

public sealed class CircularRangeSummaryResponseDto
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("range_summary")]
    public CircularRangeSummaryDto? RangeSummary { get; set; }
}

public sealed class CircularRangeSummaryDto
{
    [JsonPropertyName("from_timestamp_us")]
    public ulong FromTimestampUs { get; set; }

    [JsonPropertyName("to_timestamp_us")]
    public ulong ToTimestampUs { get; set; }

    [JsonPropertyName("actual_start_timestamp_us")]
    public ulong ActualStartTimestampUs { get; set; }

    [JsonPropertyName("actual_end_timestamp_us")]
    public ulong ActualEndTimestampUs { get; set; }

    [JsonPropertyName("start_iso")]
    public string StartIso { get; set; } = "";

    [JsonPropertyName("end_iso")]
    public string EndIso { get; set; } = "";

    [JsonPropertyName("duration_seconds")]
    public ulong DurationSeconds { get; set; }

    [JsonPropertyName("duration_formatted")]
    public string DurationFormatted { get; set; } = "0s";

    [JsonPropertyName("total_frames")]
    public ulong TotalFrames { get; set; }

    [JsonPropertyName("estimated_raw_bytes")]
    public ulong EstimatedRawBytes { get; set; }

    [JsonPropertyName("counts_by_source")]
    public CircularSourceCountsDto CountsBySource { get; set; } = new();
}

public sealed class CircularDownloadedSessionDto
{
    [JsonPropertyName("format_version")]
    public int FormatVersion { get; set; } = 1;

    [JsonPropertyName("frames")]
    public List<CircularFrameDto> Frames { get; set; } = new();
}

public sealed class CircularFrameDto
{
    [JsonPropertyName("timestamp_us")]
    public ulong TimestampUs { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("identifier")]
    public uint Identifier { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("size")]
    public int Size { get; set; }

    [JsonPropertyName("payload_hex")]
    public string PayloadHex { get; set; } = "";
}

public sealed class CircularReplayStartResponseDto
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("replay_info")]
    public CircularReplayInfoDto? ReplayInfo { get; set; }
}

public sealed class CircularReplayInfoDto
{
    [JsonPropertyName("total_frames")]
    public ulong TotalFrames { get; set; }

    [JsonPropertyName("speed")]
    public float Speed { get; set; }

    [JsonPropertyName("target_ip")]
    public string TargetIp { get; set; } = "";

    [JsonPropertyName("target_port")]
    public ushort TargetPort { get; set; }

    [JsonPropertyName("loop")]
    public bool Loop { get; set; }
}

public sealed class CircularReplayStatusDto
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("is_replaying")]
    public bool IsReplaying { get; set; }

    [JsonPropertyName("is_paused")]
    public bool IsPaused { get; set; }

    [JsonPropertyName("current_frame_index")]
    public ulong CurrentFrameIndex { get; set; }

    [JsonPropertyName("total_frames")]
    public ulong TotalFrames { get; set; }

    [JsonPropertyName("progress_percent")]
    public double ProgressPercent { get; set; }

    [JsonPropertyName("current_timestamp_us")]
    public ulong CurrentTimestampUs { get; set; }

    [JsonPropertyName("speed")]
    public float Speed { get; set; }

    [JsonPropertyName("target")]
    public string Target { get; set; } = "";
}
