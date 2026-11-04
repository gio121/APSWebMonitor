using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using Org.BouncyCastle.X509;

namespace ApsMonitor.Services;

/// <summary>
/// Servicio Singleton que gestiona el modo de comunicación del Monitor Web:
/// - Modo Tradicional (UseTls = false): UDP 50001, HTTP 8080, TCP 9882, WS 8080.
/// - Modo Seguro TLS / mTLS (UseTls = true): HTTPS 8080 (/api/send, /api/session/*),
///   HTTPS 16801 (LogGateway), HTTPS 16800 (RecoveryAgent), WSS 8080 y TLS Stream TCP.
/// Soporta certificados Ed25519 (OID 1.3.101.112), ECDSA y RSA mediante BouncyCastle TLS 1.3/1.2,
/// superando la limitación de Windows Schannel/CNG con claves Ed25519.
/// </summary>
public sealed class TlsSecurityService
{
    private readonly object _lock = new();
    private bool _useTls = false;
    private bool _allowUntrustedServerCertificate = true;
    private int _httpsApiPort = 8080;
    private int _logGatewayTlsPort = 16801;
    private int _recoveryAgentTlsPort = 16800;

    private Org.BouncyCastle.X509.X509Certificate? _bcRootCa;
    private List<Org.BouncyCastle.X509.X509Certificate> _bcClientChain = new();
    private AsymmetricKeyParameter? _bcClientPrivateKey;

    private X509Certificate2? _rootCaCertificate;
    private X509Certificate2? _clientCertificate;

    private string _rootCaSubject = "";
    private string _rootCaThumbprint = "";
    private string _clientCertSubject = "";
    private string _clientCertThumbprint = "";
    private string _clientKeyAlgorithm = "";

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
        get { lock (_lock) return _bcRootCa != null; }
    }

    public bool HasClientCertificate
    {
        get { lock (_lock) return _bcClientChain.Count > 0 && _bcClientPrivateKey != null; }
    }

    public string RootCaSubject
    {
        get { lock (_lock) return _rootCaSubject; }
    }

    public string RootCaThumbprint
    {
        get { lock (_lock) return _rootCaThumbprint; }
    }

    public string ClientCertSubject
    {
        get { lock (_lock) return _clientCertSubject; }
    }

    public string ClientCertThumbprint
    {
        get { lock (_lock) return _clientCertThumbprint; }
    }

    public string ClientKeyAlgorithm
    {
        get { lock (_lock) return _clientKeyAlgorithm; }
    }

    public string ModeDisplayName
    {
        get
        {
            lock (_lock)
            {
                if (!_useTls) return "Tradicional (UDP/HTTP)";
                return (_bcClientChain.Count > 0 && _bcClientPrivateKey != null) ? "Seguro (mTLS)" : "Seguro (TLS)";
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
                if (_bcRootCa == null) return "Sin CA raíz cargada";
                return $"{_rootCaSubject} ({_rootCaThumbprint[..Math.Min(12, _rootCaThumbprint.Length)]}…)";
            }
        }
    }

    public string ClientCertSummary
    {
        get
        {
            lock (_lock)
            {
                if (_bcClientChain.Count == 0 || _bcClientPrivateKey == null) return "Sin certificado de cliente (TLS estándar)";
                return $"{_clientCertSubject} [{_clientKeyAlgorithm}] ({_clientCertThumbprint[..Math.Min(12, _clientCertThumbprint.Length)]}…)";
            }
        }
    }

    public void LoadRootCa(byte[] rawBytes, string fileName = "sepsa_aps_rootCA.pem")
    {
        ArgumentNullException.ThrowIfNull(rawBytes);
        var parser = new X509CertificateParser();
        var bcCert = parser.ReadCertificate(rawBytes)
            ?? throw new InvalidDataException("No se pudo leer ningún certificado X.509 válido del archivo Root CA.");

        var encoded = bcCert.GetEncoded();
        var thumbprint = Convert.ToHexString(SHA1.HashData(encoded));
        var subject = bcCert.SubjectDN.ToString();

        X509Certificate2? winCert = null;
        try
        {
            winCert = X509CertificateLoader.LoadCertificate(encoded);
        }
        catch
        {
            // Ignorar si Windows CNG no soporta el OID de clave pública
        }

        lock (_lock)
        {
            _rootCaCertificate?.Dispose();
            _rootCaCertificate = winCert;
            _bcRootCa = bcCert;
            _rootCaSubject = subject;
            _rootCaThumbprint = thumbprint;
            RootCaFileName = fileName;
        }
        NotifyChanged();
    }

    public void LoadClientCertificatePfx(byte[] pfxBytes, string? password, string fileName = "client.pfx")
    {
        ArgumentNullException.ThrowIfNull(pfxBytes);
        using var ms = new MemoryStream(pfxBytes);
        var store = new Pkcs12StoreBuilder().Build();
        store.Load(ms, string.IsNullOrEmpty(password) ? [] : password.ToCharArray());

        string? keyAlias = store.Aliases.FirstOrDefault(store.IsKeyEntry)
            ?? throw new InvalidDataException("El archivo PKCS#12 (.pfx/.p12) no contiene ninguna clave privada.");

        var keyEntry = store.GetKey(keyAlias);
        var chainEntries = store.GetCertificateChain(keyAlias);
        if (chainEntries == null || chainEntries.Length == 0)
        {
            var singleCert = store.GetCertificate(keyAlias);
            if (singleCert != null)
                chainEntries = [singleCert];
        }
        if (chainEntries == null || chainEntries.Length == 0)
            throw new InvalidDataException("El archivo PKCS#12 no contiene ningún certificado asociado a la clave privada.");

        var bcChain = chainEntries.Select(c => c.Certificate).ToList();
        var leaf = bcChain[0];
        var thumbprint = Convert.ToHexString(SHA1.HashData(leaf.GetEncoded()));
        var subject = leaf.SubjectDN.ToString();
        var keyAlg = DescribePrivateKeyAlgorithm(keyEntry.Key);

        X509Certificate2? winCert = null;
        try
        {
            winCert = X509CertificateLoader.LoadPkcs12(
                pfxBytes,
                password,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
        }
        catch
        {
            // Claves Ed25519 (OID 1.3.101.112) no son soportadas por Windows CNG, se usan vía BouncyCastle
        }

        lock (_lock)
        {
            _clientCertificate?.Dispose();
            _clientCertificate = winCert;
            _bcClientChain = bcChain;
            _bcClientPrivateKey = keyEntry.Key;
            _clientCertSubject = subject;
            _clientCertThumbprint = thumbprint;
            _clientKeyAlgorithm = keyAlg;
            ClientCertFileName = fileName;
        }
        NotifyChanged();
    }

    public void LoadClientCertificatePem(byte[] certPemBytes, byte[] keyPemBytes, string? password = null, string fileName = "client.pem")
    {
        ArgumentNullException.ThrowIfNull(certPemBytes);
        ArgumentNullException.ThrowIfNull(keyPemBytes);

        // 1. Leer cadena de certificados X.509 con BouncyCastle (compatible con Ed25519 OID 1.3.101.112, ECDSA, RSA)
        var certParser = new X509CertificateParser();
        var certs = certParser.ReadCertificates(certPemBytes);
        if (certs == null || certs.Count == 0)
            throw new InvalidDataException("No se encontró ningún certificado X.509 válido en el archivo PEM.");

        var bcChain = certs.ToList();
        var leaf = bcChain[0];

        // 2. Leer clave privada PEM (cifrada PKCS#8 o en claro) con BouncyCastle
        var keyPemText = Encoding.UTF8.GetString(keyPemBytes);
        AsymmetricKeyParameter privateKey = ParsePemPrivateKey(keyPemText, password);

        // 3. Verificar correspondencia entre la clave pública del certificado y la clave privada
        VerifyKeyPairMatch(leaf.GetPublicKey(), privateKey);

        var thumbprint = Convert.ToHexString(SHA1.HashData(leaf.GetEncoded()));
        var subject = leaf.SubjectDN.ToString();
        var keyAlg = DescribePrivateKeyAlgorithm(privateKey);

        // 4. Intentar crear X509Certificate2 nativo solo si el algoritmo es soportado por Windows CNG (RSA/ECDSA)
        X509Certificate2? winCert = null;
        if (privateKey is not Ed25519PrivateKeyParameters and not Ed448PrivateKeyParameters)
        {
            try
            {
                var certPem = Encoding.UTF8.GetString(certPemBytes);
                using var ephemeral = string.IsNullOrEmpty(password)
                    ? X509Certificate2.CreateFromPem(certPem, keyPemText)
                    : X509Certificate2.CreateFromEncryptedPem(certPem, keyPemText, password);
                byte[] pfxBytes = ephemeral.Export(X509ContentType.Pfx);
                winCert = X509CertificateLoader.LoadPkcs12(pfxBytes, (string?)null, X509KeyStorageFlags.Exportable);
            }
            catch
            {
                // Ignorar si Windows CNG no soporta el tipo de clave
            }
        }

        lock (_lock)
        {
            _clientCertificate?.Dispose();
            _clientCertificate = winCert;
            _bcClientChain = bcChain;
            _bcClientPrivateKey = privateKey;
            _clientCertSubject = subject;
            _clientCertThumbprint = thumbprint;
            _clientKeyAlgorithm = keyAlg;
            ClientCertFileName = fileName;
        }
        NotifyChanged();
    }

    private static AsymmetricKeyParameter ParsePemPrivateKey(string keyPemText, string? password)
    {
        using var reader = new StringReader(keyPemText);
        var pemReader = string.IsNullOrEmpty(password)
            ? new PemReader(reader)
            : new PemReader(reader, new StaticPasswordFinder(password));

        try
        {
            object? obj = pemReader.ReadObject();
            if (obj is AsymmetricCipherKeyPair keyPair)
                return keyPair.Private;
            if (obj is AsymmetricKeyParameter keyParam && keyParam.IsPrivate)
                return keyParam;
        }
        catch (Exception) when (!string.IsNullOrEmpty(password))
        {
            // Fallback directo para bloques "-----BEGIN ENCRYPTED PRIVATE KEY-----" (PKCS#8)
        }

        using var rawReader = new StringReader(keyPemText);
        var pemObj = new Org.BouncyCastle.Utilities.IO.Pem.PemReader(rawReader).ReadPemObject()
            ?? throw new InvalidDataException("No se encontró un bloque PEM válido en el archivo de clave privada.");

        if (pemObj.Type.Contains("ENCRYPTED PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(password))
                throw new InvalidDataException("La clave privada está cifrada. Introduce la contraseña de la clave privada (ej: sepsaEDP).");

            return PrivateKeyFactory.DecryptKey(password.ToCharArray(), pemObj.Content);
        }

        if (pemObj.Type.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
        {
            return PrivateKeyFactory.CreateKey(pemObj.Content);
        }

        throw new InvalidDataException($"Tipo de bloque PEM no soportado: '{pemObj.Type}'.");
    }

    private static void VerifyKeyPairMatch(AsymmetricKeyParameter publicKey, AsymmetricKeyParameter privateKey)
    {
        if (privateKey is Ed25519PrivateKeyParameters edPriv && publicKey is Ed25519PublicKeyParameters edPub)
        {
            var derivedPub = edPriv.GeneratePublicKey();
            if (!derivedPub.GetEncoded().AsSpan().SequenceEqual(edPub.GetEncoded()))
                throw new InvalidDataException("La clave privada Ed25519 no corresponde al certificado seleccionado.");
        }
        else if (privateKey is Ed448PrivateKeyParameters ed448Priv && publicKey is Ed448PublicKeyParameters ed448Pub)
        {
            var derivedPub = ed448Priv.GeneratePublicKey();
            if (!derivedPub.GetEncoded().AsSpan().SequenceEqual(ed448Pub.GetEncoded()))
                throw new InvalidDataException("La clave privada Ed448 no corresponde al certificado seleccionado.");
        }
        else if (privateKey is RsaKeyParameters rsaPriv && publicKey is RsaKeyParameters rsaPub)
        {
            if (!rsaPriv.Modulus.Equals(rsaPub.Modulus))
                throw new InvalidDataException("La clave privada RSA no corresponde al certificado seleccionado.");
        }
    }

    private static string DescribePrivateKeyAlgorithm(AsymmetricKeyParameter key) => key switch
    {
        Ed25519PrivateKeyParameters => "Ed25519",
        Ed448PrivateKeyParameters => "Ed448",
        ECPrivateKeyParameters => "ECDSA",
        RsaKeyParameters => "RSA",
        _ => key.GetType().Name
    };

    private sealed class StaticPasswordFinder(string password) : IPasswordFinder
    {
        public char[] GetPassword() => password.ToCharArray();
    }

    public void ClearRootCa()
    {
        lock (_lock)
        {
            _rootCaCertificate?.Dispose();
            _rootCaCertificate = null;
            _bcRootCa = null;
            _rootCaSubject = "";
            _rootCaThumbprint = "";
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
            _bcClientChain.Clear();
            _bcClientPrivateKey = null;
            _clientCertSubject = "";
            _clientCertThumbprint = "";
            _clientKeyAlgorithm = "";
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

    /// <summary>
    /// Crea un HttpClient capaz de negociar TLS 1.3 / 1.2 con certificados Ed25519 (mTLS de APSComms),
    /// ECDSA o RSA en Windows y Linux mediante el motor TLS de BouncyCastle.
    /// </summary>
    public HttpClient CreateHttpClient(TimeSpan? timeout = null)
    {
        var handler = new BcTlsHttpMessageHandler(this);
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

    /// <summary>
    /// Establece una conexión TCP + TLS 1.3/1.2 (incluyendo soporte Ed25519 mTLS) hacia el host y puerto indicados.
    /// </summary>
    [System.Diagnostics.DebuggerNonUserCode]
    public async Task<Stream> ConnectTlsStreamAsync(string host, int port, CancellationToken ct = default)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, ct);
            var netStream = tcp.GetStream();

            Org.BouncyCastle.X509.X509Certificate? rootCa;
            List<Org.BouncyCastle.X509.X509Certificate> clientChain;
            AsymmetricKeyParameter? clientKey;
            bool allowUntrusted;

            lock (_lock)
            {
                rootCa = _bcRootCa;
                clientChain = _bcClientChain.ToList();
                clientKey = _bcClientPrivateKey;
                allowUntrusted = _allowUntrustedServerCertificate;
            }

            var protocol = new TlsClientProtocol(netStream);
            var tlsClient = new SepsaBcTlsClient(host, rootCa, clientChain, clientKey, allowUntrusted);

            Exception? handshakeError = await Task.Run<Exception?>(() =>
            {
                try
                {
                    protocol.Connect(tlsClient);
                    return null;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            }, ct);

            if (handshakeError != null)
            {
                throw new IOException(
                    $"Fallo en handshake TLS con {host}:{port}: {handshakeError.Message}",
                    handshakeError);
            }

            return new BcTlsSocketStream(tcp, protocol);
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
        System.Security.Cryptography.X509Certificates.X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        Org.BouncyCastle.X509.X509Certificate? rootCa;
        bool allowUntrusted;
        lock (_lock)
        {
            rootCa = _bcRootCa;
            allowUntrusted = _allowUntrustedServerCertificate;
        }

        if (certificate == null)
            return false;

        if (rootCa != null)
        {
            try
            {
                var parser = new X509CertificateParser();
                var serverCert = parser.ReadCertificate(certificate.GetRawCertData());
                serverCert.Verify(rootCa.GetPublicKey());
                return true;
            }
            catch
            {
                if (!allowUntrusted)
                    return false;
            }
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

    // ── Motor TLS 1.3 / 1.2 BouncyCastle con soporte Ed25519 ─────────────────

    [System.Diagnostics.DebuggerNonUserCode]
    private sealed class SepsaBcTlsClient : DefaultTlsClient
    {
        private readonly string _targetHost;
        private readonly Org.BouncyCastle.X509.X509Certificate? _rootCa;
        private readonly List<Org.BouncyCastle.X509.X509Certificate> _clientChain;
        private readonly AsymmetricKeyParameter? _clientPrivateKey;
        private readonly bool _allowUntrusted;

        public SepsaBcTlsClient(
            string targetHost,
            Org.BouncyCastle.X509.X509Certificate? rootCa,
            List<Org.BouncyCastle.X509.X509Certificate> clientChain,
            AsymmetricKeyParameter? clientPrivateKey,
            bool allowUntrusted)
            : base(new BcTlsCrypto())
        {
            _targetHost = targetHost;
            _rootCa = rootCa;
            _clientChain = clientChain;
            _clientPrivateKey = clientPrivateKey;
            _allowUntrusted = allowUntrusted;
        }

        protected override ProtocolVersion[] GetSupportedVersions() =>
            ProtocolVersion.TLSv13.DownTo(ProtocolVersion.TLSv12);

        protected override IList<SignatureAndHashAlgorithm> GetSupportedSignatureAlgorithms()
        {
            var result = new List<SignatureAndHashAlgorithm>
            {
                SignatureAndHashAlgorithm.ed25519,
                SignatureAndHashAlgorithm.ed448
            };

            foreach (SignatureAndHashAlgorithm alg in TlsUtilities.GetDefaultSupportedSignatureAlgorithms(m_context))
            {
                if (!result.Contains(alg))
                    result.Add(alg);
            }

            return result;
        }

        protected override IList<ServerName>? GetSniServerNames()
        {
            if (string.IsNullOrWhiteSpace(_targetHost) || IPAddress.TryParse(_targetHost, out _))
                return null;
            return new List<ServerName> { new(NameType.host_name, Encoding.ASCII.GetBytes(_targetHost)) };
        }

        public override TlsAuthentication GetAuthentication() =>
            new SepsaBcTlsAuthentication(m_context, (BcTlsCrypto)Crypto, _rootCa, _clientChain, _clientPrivateKey, _allowUntrusted);
    }

    [System.Diagnostics.DebuggerNonUserCode]
    private sealed class SepsaBcTlsAuthentication : TlsAuthentication
    {
        private readonly TlsContext _context;
        private readonly BcTlsCrypto _crypto;
        private readonly Org.BouncyCastle.X509.X509Certificate? _rootCa;
        private readonly List<Org.BouncyCastle.X509.X509Certificate> _clientChain;
        private readonly AsymmetricKeyParameter? _clientPrivateKey;
        private readonly bool _allowUntrusted;

        public SepsaBcTlsAuthentication(
            TlsContext context,
            BcTlsCrypto crypto,
            Org.BouncyCastle.X509.X509Certificate? rootCa,
            List<Org.BouncyCastle.X509.X509Certificate> clientChain,
            AsymmetricKeyParameter? clientPrivateKey,
            bool allowUntrusted)
        {
            _context = context;
            _crypto = crypto;
            _rootCa = rootCa;
            _clientChain = clientChain;
            _clientPrivateKey = clientPrivateKey;
            _allowUntrusted = allowUntrusted;
        }

        public void NotifyServerCertificate(TlsServerCertificate serverCertificate)
        {
            var certs = serverCertificate?.Certificate?.GetCertificateList();
            if (certs == null || certs.Length == 0)
                throw new TlsFatalAlert(AlertDescription.bad_certificate);

            if (_rootCa != null)
            {
                try
                {
                    var parser = new X509CertificateParser();
                    var leaf = parser.ReadCertificate(certs[0].GetEncoded());

                    // Verificar directamente contra Root CA o contra el emisor intermedio (Device Issuing CA)
                    bool verified = false;
                    try
                    {
                        leaf.Verify(_rootCa.GetPublicKey());
                        verified = true;
                    }
                    catch
                    {
                        if (certs.Length > 1)
                        {
                            var issuer = parser.ReadCertificate(certs[1].GetEncoded());
                            leaf.Verify(issuer.GetPublicKey());
                            issuer.Verify(_rootCa.GetPublicKey());
                            verified = true;
                        }
                    }

                    if (!verified && !_allowUntrusted)
                        throw new TlsFatalAlert(AlertDescription.unknown_ca);
                }
                catch (TlsFatalAlert)
                {
                    throw;
                }
                catch
                {
                    if (!_allowUntrusted)
                        throw new TlsFatalAlert(AlertDescription.bad_certificate);
                }
            }
        }

        public TlsCredentials? GetClientCredentials(Org.BouncyCastle.Tls.CertificateRequest certificateRequest)
        {
            if (_clientChain.Count == 0 || _clientPrivateKey == null)
                return null;

            var tlsCerts = _clientChain
                .Select(c => _crypto.CreateCertificate(c.GetEncoded()))
                .ToArray();

            SignatureAndHashAlgorithm sigAlg = DetermineSignatureAlgorithm(_clientPrivateKey, certificateRequest);
            Certificate certChain;
            if (TlsUtilities.IsTlsV13(_context))
            {
                var entries = tlsCerts
                    .Select(c => new CertificateEntry(c, null))
                    .ToArray();
                byte[] reqCtx = certificateRequest.GetCertificateRequestContext() ?? TlsUtilities.EmptyBytes;
                certChain = new Certificate(reqCtx, entries);
            }
            else
            {
                certChain = new Certificate(tlsCerts);
            }

            return new BcDefaultTlsCredentialedSigner(
                new TlsCryptoParameters(_context),
                _crypto,
                _clientPrivateKey,
                certChain,
                sigAlg);
        }

        private static SignatureAndHashAlgorithm DetermineSignatureAlgorithm(
            AsymmetricKeyParameter privateKey,
            Org.BouncyCastle.Tls.CertificateRequest certificateRequest)
        {
            if (privateKey is Ed25519PrivateKeyParameters)
                return SignatureAndHashAlgorithm.ed25519;

            if (privateKey is Ed448PrivateKeyParameters)
                return SignatureAndHashAlgorithm.ed448;

            var supported = certificateRequest.SupportedSignatureAlgorithms;
            if (privateKey is ECPrivateKeyParameters)
            {
                if (supported != null)
                {
                    foreach (SignatureAndHashAlgorithm alg in supported)
                    {
                        if (alg.Signature == SignatureAlgorithm.ecdsa)
                            return alg;
                    }
                }
                return new SignatureAndHashAlgorithm(Org.BouncyCastle.Tls.HashAlgorithm.sha256, SignatureAlgorithm.ecdsa);
            }

            if (supported != null)
            {
                foreach (SignatureAndHashAlgorithm alg in supported)
                {
                    if (alg.Signature is SignatureAlgorithm.rsa_pss_rsae_sha256 or SignatureAlgorithm.rsa)
                        return alg;
                }
            }

            return SignatureAndHashAlgorithm.rsa_pss_rsae_sha256;
        }
    }

    [System.Diagnostics.DebuggerNonUserCode]
    private sealed class BcTlsSocketStream(TcpClient tcpClient, TlsClientProtocol protocol) : Stream
    {
        private readonly Stream _inner = protocol.Stream;
        private int _disposed;

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count)
        {
            try
            {
                return _inner.Read(buffer, offset, count);
            }
            catch (TlsNoCloseNotifyException)
            {
                return 0;
            }
            catch (EndOfStreamException)
            {
                return 0;
            }
        }

        public override int Read(Span<byte> buffer)
        {
            try
            {
                return _inner.Read(buffer);
            }
            catch (TlsNoCloseNotifyException)
            {
                return 0;
            }
            catch (EndOfStreamException)
            {
                return 0;
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var (bytesRead, error) = await Task.Run<(int, Exception?)>(() =>
            {
                try
                {
                    return (Read(buffer.Span), null);
                }
                catch (Exception ex)
                {
                    return (0, ex);
                }
            }, cancellationToken);

            if (error != null)
                throw new IOException($"Error de lectura en stream TLS: {error.Message}", error);

            return bytesRead;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var (bytesRead, error) = await Task.Run<(int, Exception?)>(() =>
            {
                try
                {
                    return (Read(buffer, offset, count), null);
                }
                catch (Exception ex)
                {
                    return (0, ex);
                }
            }, cancellationToken);

            if (error != null)
                throw new IOException($"Error de lectura en stream TLS: {error.Message}", error);

            return bytesRead;
        }

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Exception? error = await Task.Run<Exception?>(() =>
            {
                try
                {
                    _inner.Write(buffer.Span);
                    return null;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            }, cancellationToken);

            if (error != null)
                throw new IOException($"Error de escritura en stream TLS: {error.Message}", error);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Exception? error = await Task.Run<Exception?>(() =>
            {
                try
                {
                    _inner.Write(buffer, offset, count);
                    return null;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            }, cancellationToken);

            if (error != null)
                throw new IOException($"Error de escritura en stream TLS: {error.Message}", error);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0 && disposing)
            {
                try { protocol.Close(); } catch { }
                try { tcpClient.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// HttpMessageHandler que delega peticiones http:// al SocketsHttpHandler estándar y redirige
    /// peticiones https:// a través de un túnel TLS 1.3/1.2 de BouncyCastle (compatible con Ed25519 mTLS en Windows).
    /// </summary>
    [System.Diagnostics.DebuggerNonUserCode]
    private sealed class BcTlsHttpMessageHandler : HttpMessageHandler
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> PlainHttp8080Endpoints = new(StringComparer.OrdinalIgnoreCase);

        private readonly HttpMessageInvoker _plainInvoker;
        private readonly HttpMessageInvoker _bcTlsTunnelInvoker;

        public BcTlsHttpMessageHandler(TlsSecurityService tlsService)
        {
            var plainHandler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2)
            };

            var bcTlsTunnelHandler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(1),
                ConnectCallback = async (context, ct) =>
                {
                    return await tlsService.ConnectTlsStreamAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct);
                }
            };

            _plainInvoker = new HttpMessageInvoker(plainHandler, disposeHandler: true);
            _bcTlsTunnelInvoker = new HttpMessageInvoker(bcTlsTunnelHandler, disposeHandler: true);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var originalUri = request.RequestUri;
            if (originalUri != null && originalUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            {
                int port = originalUri.IsDefaultPort ? 443 : originalUri.Port;
                string endpointKey = $"{originalUri.Host}:{port}";
                var httpEquivalentUri = new UriBuilder(originalUri)
                {
                    Scheme = "http",
                    Port = port
                }.Uri;

                // En placas APSComms donde MonitorManager / WebMonitorBackend escucha HTTP en el puerto 8080
                // (mientras LogGateway 16801 y RecoveryAgent 16800 usan mTLS 1.3), evitamos reintentar TLS en 8080 si ya respondió HTTP plano.
                if (port == 8080 && PlainHttp8080Endpoints.ContainsKey(endpointKey) && request.Method == HttpMethod.Get)
                {
                    request.RequestUri = httpEquivalentUri;
                    try
                    {
                        return await _plainInvoker.SendAsync(request, cancellationToken);
                    }
                    finally
                    {
                        request.RequestUri = originalUri;
                    }
                }

                request.RequestUri = httpEquivalentUri;
                try
                {
                    return await _bcTlsTunnelInvoker.SendAsync(request, cancellationToken);
                }
                catch (HttpRequestException ex) when (port == 8080 && request.Method == HttpMethod.Get && IsPlainHttpHandshakeRejection(ex))
                {
                    PlainHttp8080Endpoints[endpointKey] = true;
                    using var fallbackReq = new HttpRequestMessage(HttpMethod.Get, httpEquivalentUri);
                    foreach (var header in request.Headers)
                        fallbackReq.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    return await _plainInvoker.SendAsync(fallbackReq, cancellationToken);
                }
                finally
                {
                    request.RequestUri = originalUri;
                }
            }

            return await _plainInvoker.SendAsync(request, cancellationToken);
        }

        private static bool IsPlainHttpHandshakeRejection(Exception ex)
        {
            for (Exception? cur = ex; cur != null; cur = cur.InnerException)
            {
                if (cur is TlsFatalAlert or TlsException)
                    return true;
            }
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _plainInvoker.Dispose();
                _bcTlsTunnelInvoker.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
