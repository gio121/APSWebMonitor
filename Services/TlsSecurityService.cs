using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ApsMonitor.Services;

/// <summary>
/// Servicio Singleton que gestiona el modo de comunicación del Monitor Web:
/// - Modo Tradicional (UseTls = false): UDP 50001, HTTP 8080, TCP 9882, WS 8080.
/// - Modo Seguro TLS / mTLS (UseTls = true): HTTPS 8080 (/api/send, /api/session/*),
///   HTTPS 16801 (LogGateway), HTTPS 16800 (RecoveryAgent), WSS 8080 y SslStream TCP.
/// </summary>
public sealed class TlsSecurityService
{
    private readonly object _lock = new();
    private bool _useTls = false;
    private bool _allowUntrustedServerCertificate = true;
    private int _httpsApiPort = 8080;
    private int _logGatewayTlsPort = 16801;
    private int _recoveryAgentTlsPort = 16800;

    private X509Certificate2? _rootCaCertificate;
    private X509Certificate2? _clientCertificate;

    public event Action? OnSettingsChanged;
    public event Action? OnChanged;

    public bool UseTls
    {
        get { lock (_lock) return _useTls; }
        set
        {
            bool changed = false;
            lock (_lock)
            {
                if (_useTls != value)
                {
                    _useTls = value;
                    changed = true;
                }
            }
            if (changed) NotifyChanged();
        }
    }

    public bool AllowUntrustedServerCertificate
    {
        get { lock (_lock) return _allowUntrustedServerCertificate; }
        set
        {
            lock (_lock) _allowUntrustedServerCertificate = value;
            NotifyChanged();
        }
    }

    public int HttpsApiPort
    {
        get { lock (_lock) return _httpsApiPort; }
        set
        {
            if (value is >= 1 and <= 65535)
            {
                lock (_lock) _httpsApiPort = value;
                NotifyChanged();
            }
        }
    }

    public int LogGatewayTlsPort
    {
        get { lock (_lock) return _logGatewayTlsPort; }
        set
        {
            if (value is >= 1 and <= 65535)
            {
                lock (_lock) _logGatewayTlsPort = value;
                NotifyChanged();
            }
        }
    }

    public int RecoveryAgentTlsPort
    {
        get { lock (_lock) return _recoveryAgentTlsPort; }
        set
        {
            if (value is >= 1 and <= 65535)
            {
                lock (_lock) _recoveryAgentTlsPort = value;
                NotifyChanged();
            }
        }
    }

    public string? RootCaFileName { get; private set; }
    public string? ClientCertFileName { get; private set; }

    public X509Certificate2? RootCaCertificate
    {
        get { lock (_lock) return _rootCaCertificate; }
    }

    public X509Certificate2? ClientCertificate
    {
        get { lock (_lock) return _clientCertificate; }
    }

    public bool HasRootCa
    {
        get { lock (_lock) return _rootCaCertificate != null; }
    }

    public bool HasClientCertificate
    {
        get { lock (_lock) return _clientCertificate != null; }
    }

    public string RootCaSubject
    {
        get { lock (_lock) return _rootCaCertificate?.Subject ?? ""; }
    }

    public string RootCaThumbprint
    {
        get { lock (_lock) return _rootCaCertificate?.Thumbprint ?? ""; }
    }

    public string ClientCertSubject
    {
        get { lock (_lock) return _clientCertificate?.Subject ?? ""; }
    }

    public string ClientCertThumbprint
    {
        get { lock (_lock) return _clientCertificate?.Thumbprint ?? ""; }
    }

    public string ModeDisplayName
    {
        get
        {
            lock (_lock)
            {
                if (!_useTls) return "Tradicional (UDP/HTTP)";
                return _clientCertificate != null ? "Seguro (mTLS)" : "Seguro (TLS)";
            }
        }
    }

    public static string ExtractHost(string urlOrHost)
    {
        if (string.IsNullOrWhiteSpace(urlOrHost)) return "192.168.15.1";
        var s = urlOrHost.Trim()
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

    public string RootCaSummary
    {
        get
        {
            lock (_lock)
            {
                if (_rootCaCertificate == null) return "Sin CA raíz cargada";
                return $"{_rootCaCertificate.GetNameInfo(X509NameType.SimpleName, false)} ({_rootCaCertificate.Thumbprint[..Math.Min(12, _rootCaCertificate.Thumbprint.Length)]}…)";
            }
        }
    }

    public string ClientCertSummary
    {
        get
        {
            lock (_lock)
            {
                if (_clientCertificate == null) return "Sin certificado de cliente (TLS estándar)";
                return $"{_clientCertificate.GetNameInfo(X509NameType.SimpleName, false)} ({_clientCertificate.Thumbprint[..Math.Min(12, _clientCertificate.Thumbprint.Length)]}…)";
            }
        }
    }

    public void LoadRootCa(byte[] rawBytes, string fileName = "sepsa_aps_rootCA.pem")
    {
        ArgumentNullException.ThrowIfNull(rawBytes);
        X509Certificate2 cert;
        var text = Encoding.UTF8.GetString(rawBytes);
        if (text.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
        {
            cert = X509Certificate2.CreateFromPem(text);
        }
        else
        {
            cert = X509CertificateLoader.LoadCertificate(rawBytes);
        }

        lock (_lock)
        {
            _rootCaCertificate?.Dispose();
            _rootCaCertificate = cert;
            RootCaFileName = fileName;
        }
        NotifyChanged();
    }

    public void LoadClientCertificatePfx(byte[] pfxBytes, string? password, string fileName = "client.pfx")
    {
        ArgumentNullException.ThrowIfNull(pfxBytes);
        var cert = X509CertificateLoader.LoadPkcs12(
            pfxBytes,
            password,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);

        lock (_lock)
        {
            _clientCertificate?.Dispose();
            _clientCertificate = cert;
            ClientCertFileName = fileName;
        }
        NotifyChanged();
    }

    public void LoadClientCertificatePem(byte[] certPemBytes, byte[] keyPemBytes, string? password = null, string fileName = "client.pem")
    {
        ArgumentNullException.ThrowIfNull(certPemBytes);
        ArgumentNullException.ThrowIfNull(keyPemBytes);

        var certPem = Encoding.UTF8.GetString(certPemBytes);
        var keyPem = Encoding.UTF8.GetString(keyPemBytes);

        using var ephemeral = string.IsNullOrEmpty(password)
            ? X509Certificate2.CreateFromPem(certPem, keyPem)
            : X509Certificate2.CreateFromEncryptedPem(certPem, keyPem, password);

        // En Windows SslStream/HttpClient requiere que la clave privada esté en un contenedor PKCS#12 persistible/exportable
        byte[] pfxBytes = ephemeral.Export(X509ContentType.Pfx);
        var cert = X509CertificateLoader.LoadPkcs12(pfxBytes, (string?)null, X509KeyStorageFlags.Exportable);

        lock (_lock)
        {
            _clientCertificate?.Dispose();
            _clientCertificate = cert;
            ClientCertFileName = fileName;
        }
        NotifyChanged();
    }

    public void ClearRootCa()
    {
        lock (_lock)
        {
            _rootCaCertificate?.Dispose();
            _rootCaCertificate = null;
            RootCaFileName = null;
        }
        NotifyChanged();
    }

    public void ClearClientCertificate()
    {
        lock (_lock)
        {
            _clientCertificate?.Dispose();
            _clientCertificate = null;
            ClientCertFileName = null;
        }
        NotifyChanged();
    }

    public string BuildHttpBaseUrl(string hostOrUrl, int defaultPort = 8080)
    {
        var raw = (hostOrUrl ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(raw))
            raw = "192.168.15.1";

        bool explicitHttps = raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        bool explicitHttp = raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        bool useHttps = explicitHttps || (!explicitHttp && UseTls);

        if (!explicitHttp && !explicitHttps)
        {
            raw = (useHttps ? "https://" : "http://") + raw;
        }
        else if (UseTls && explicitHttp)
        {
            raw = "https://" + raw["http://".Length..];
            useHttps = true;
        }

        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            int port = uri.IsDefaultPort ? defaultPort : uri.Port;
            string scheme = useHttps ? "https" : uri.Scheme;
            return $"{scheme}://{uri.Host}:{port}";
        }

        return $"{(useHttps ? "https" : "http")}://{raw}:{defaultPort}";
    }

    public Uri BuildWebSocketUri(string hostOrUrl, int defaultPort = 8080, string path = "/ws/trdp")
    {
        var s = (hostOrUrl ?? string.Empty).Trim();
        bool explicitWss = s.StartsWith("wss://", StringComparison.OrdinalIgnoreCase) ||
                           s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        bool useWss = explicitWss || UseTls;

        s = s.Replace("http://", "", StringComparison.OrdinalIgnoreCase)
             .Replace("https://", "", StringComparison.OrdinalIgnoreCase)
             .Replace("ws://", "", StringComparison.OrdinalIgnoreCase)
             .Replace("wss://", "", StringComparison.OrdinalIgnoreCase)
             .TrimEnd('/');

        if (!s.Contains(':'))
            s += $":{defaultPort}";

        string normalizedPath = path.StartsWith('/') ? path : "/" + path;
        return new Uri($"{(useWss ? "wss" : "ws")}://{s}{normalizedPath}");
    }

    public HttpClient CreateHttpClient(TimeSpan? timeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };

        ConfigureSslOptions(handler.SslOptions);

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(10)
        };
    }

    public void ConfigureWebSocketOptions(ClientWebSocketOptions options)
    {
        X509Certificate2? clientCert;
        lock (_lock)
        {
            clientCert = _clientCertificate;
        }

        if (clientCert != null)
        {
            options.ClientCertificates.Add(clientCert);
        }

        options.RemoteCertificateValidationCallback = ValidateServerCertificate;
    }

    public void ConfigureSslOptions(SslClientAuthenticationOptions sslOptions, string? targetHost = null)
    {
        X509Certificate2? clientCert;
        lock (_lock)
        {
            clientCert = _clientCertificate;
        }

        sslOptions.EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
        if (!string.IsNullOrWhiteSpace(targetHost))
            sslOptions.TargetHost = targetHost;

        if (clientCert != null)
        {
            sslOptions.ClientCertificates = new X509CertificateCollection { clientCert };
            sslOptions.LocalCertificateSelectionCallback = (_, _, _, _, _) => clientCert;
        }

        sslOptions.RemoteCertificateValidationCallback = ValidateServerCertificate;
    }

    public async Task<SslStream> ConnectTlsStreamAsync(string host, int port, CancellationToken ct = default)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, ct);
            var sslStream = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, ValidateServerCertificate);
            var sslOptions = new SslClientAuthenticationOptions();
            ConfigureSslOptions(sslOptions, host);
            await sslStream.AuthenticateAsClientAsync(sslOptions, ct);
            return sslStream;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Envía una trama SEPSA cifrada mediante HTTPS POST a https://&lt;host&gt;:&lt;HttpsApiPort&gt;/api/send.
    /// </summary>
    public async Task<SepsaExchangeResult> SendSepsaFrameOverTlsAsync(
        string host,
        ReadOnlyMemory<byte> frame,
        CancellationToken ct = default)
    {
        var baseUrl = BuildHttpBaseUrl(host, HttpsApiPort);
        using var httpClient = CreateHttpClient(TimeSpan.FromSeconds(5));
        var sepsaClient = new SepsaProtocolClient(httpClient);
        return await sepsaClient.SendAsync(baseUrl, frame, ct);
    }

    public bool ValidateServerCertificate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        X509Certificate2? rootCa;
        bool allowUntrusted;
        lock (_lock)
        {
            rootCa = _rootCaCertificate;
            allowUntrusted = _allowUntrustedServerCertificate;
        }

        if (certificate == null)
            return false;

        // Si el usuario cargó la CA Raíz de SEPSA (sepsa_aps_rootCA.pem), validar la cadena contra ella
        if (rootCa != null)
        {
            using var cert2 = new X509Certificate2(certificate);
            using var customChain = new X509Chain();
            customChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            customChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            customChain.ChainPolicy.CustomTrustStore.Add(rootCa);
            customChain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;

            if (customChain.Build(cert2))
            {
                // Comprobar que la raíz de la cadena coincide con nuestra CA Raíz
                var chainRoot = customChain.ChainElements[^1].Certificate;
                if (string.Equals(chainRoot.Thumbprint, rootCa.Thumbprint, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            if (!allowUntrusted)
                return false;
        }

        if (sslPolicyErrors == SslPolicyErrors.None)
            return true;

        return allowUntrusted;
    }

    private void NotifyChanged()
    {
        OnSettingsChanged?.Invoke();
        OnChanged?.Invoke();
    }
}
