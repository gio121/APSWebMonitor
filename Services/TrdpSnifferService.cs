using ApsMonitor.Models;
using SharpPcap;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;

namespace ApsMonitor.Services;

/// <summary>
/// Información representativa de un adaptador de red para el sniffer en vivo.
/// </summary>
public class TrdpSnifferDevice
{
    public string DeviceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string MacAddress { get; set; } = string.Empty;
    public bool IsLoopback { get; set; }

    public string DisplayText
    {
        get
        {
            var ip = string.IsNullOrWhiteSpace(IpAddress) ? "Sin IP asignada" : IpAddress;
            return $"{Name} — {ip} ({Description})";
        }
    }
}

/// <summary>
/// Servicio de captura directa en vivo desde interfaz de red física (Sniffer estilo Wireshark/Npcap)
/// para tramas TRDP (Process Data / UDP) con decodificación en tiempo real.
/// </summary>
public sealed class TrdpSnifferService : IDisposable
{
    private readonly NetworkAdapterService _adapterService;
    private readonly object _lock = new();

    private ILiveDevice? _activeDevice;
    private DateTime _firstPacketTime = DateTime.Now;
    private long _packetsCaptured = 0;
    private long _matchingFramesCount = 0;

    // Sliding window para medición de frecuencia (Hz)
    private readonly Queue<DateTime> _arrivalTimestamps = new();
    private const int HZ_WINDOW_SIZE = 40;

    public bool IsCapturing { get; private set; }
    public string? ActiveDeviceId { get; private set; }
    public string? ActiveDeviceName { get; private set; }
    public string? LastError { get; private set; }

    public long PacketsCaptured => Interlocked.Read(ref _packetsCaptured);
    public long MatchingFramesCount => Interlocked.Read(ref _matchingFramesCount);
    public double RefreshRateHz { get; private set; }

    public long TargetComId { get; set; }
    public string TargetIp { get; set; } = string.Empty;
    public bool FilterByComId { get; set; } = true;
    public string DatasetName { get; set; } = string.Empty;
    public TrdpDataset? DatasetDef { get; set; }
    public bool EnableEndianSwap { get; set; }

    public event Action<TrdpFrameMessage>? OnFrameCaptured;
    public event Action<bool>? OnCaptureStateChanged;
    public event Action<string>? OnStatusMessage;

    public TrdpSnifferService(NetworkAdapterService adapterService)
    {
        _adapterService = adapterService;
    }

    /// <summary>
    /// Enumera todas las interfaces de red disponibles para captura en el sistema mediante Npcap/WinPcap,
    /// correlacionándolas con las interfaces de red de Windows.
    /// </summary>
    public List<TrdpSnifferDevice> GetAvailableDevices()
    {
        var result = new List<TrdpSnifferDevice>();
        try
        {
            var pcapDevices = CaptureDeviceList.Instance;
            var systemAdapters = _adapterService.GetAdapters();

            foreach (var dev in pcapDevices)
            {
                var devName = dev.Name;
                var devDesc = dev.Description ?? string.Empty;
                bool isLoop = devName.Contains("Loopback", StringComparison.OrdinalIgnoreCase);

                // Buscar correspondencia en adaptadores del sistema (por GUID o Mac o IP)
                var matched = systemAdapters.FirstOrDefault(a =>
                    (!string.IsNullOrEmpty(a.Id) && devName.Contains(a.Id, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(a.MacAddress) && dev.MacAddress != null &&
                     string.Equals(a.MacAddress.Replace("-", ":"), dev.MacAddress.ToString(), StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(a.Description) && string.Equals(a.Description, devDesc, StringComparison.OrdinalIgnoreCase))
                );

                string friendlyName = matched?.Name ?? (isLoop ? "Loopback" : devDesc);
                string ip = matched?.IpAddress ?? "";
                string mac = matched?.MacAddress ?? (dev.MacAddress?.ToString() ?? "");

                // Si no se obtuvo IP del adaptador del sistema, intentar extraer de las propiedades Npcap
                // (En SharpPcap 6.x ILiveDevice no expone Addresses; se depende del match con NetworkInterface)

                result.Add(new TrdpSnifferDevice
                {
                    DeviceId    = devName,
                    Name        = friendlyName,
                    Description = devDesc,
                    IpAddress   = ip,
                    MacAddress  = mac,
                    IsLoopback  = isLoop
                });
            }
        }
        catch (Exception ex)
        {
            LastError = $"Error al enumerar interfaces Npcap: {ex.Message}";
            OnStatusMessage?.Invoke(LastError);
        }

        // Ordenar: primero adaptadores físicos con IP, luego resto, loopback al final
        return result
            .OrderBy(d => d.IsLoopback)
            .ThenByDescending(d => !string.IsNullOrEmpty(d.IpAddress) && !d.IpAddress.StartsWith("169.254"))
            .ThenBy(d => d.Name)
            .ToList();
    }

    /// <summary>
    /// Inicia la captura en modo promiscuo de la interfaz seleccionada con filtrado kernel UDP.
    /// </summary>
    public (bool Success, string Message) StartCapture(
        string deviceId,
        long targetComId = 0,
        string targetIp = "",
        string datasetName = "",
        TrdpDataset? datasetDef = null,
        bool enableEndianSwap = false)
    {
        lock (_lock)
        {
            StopCaptureInternal();

            TargetComId      = targetComId;
            FilterByComId    = targetComId > 0;
            TargetIp         = targetIp?.Trim() ?? string.Empty;
            DatasetName      = datasetName;
            DatasetDef       = datasetDef;
            EnableEndianSwap = enableEndianSwap;
            LastError        = null;
            _packetsCaptured = 0;
            _matchingFramesCount = 0;
            _firstPacketTime = DateTime.Now;

            lock (_arrivalTimestamps)
            {
                _arrivalTimestamps.Clear();
            }

            try
            {
                var devices = CaptureDeviceList.Instance;
                var dev = devices.FirstOrDefault(d => string.Equals(d.Name, deviceId, StringComparison.OrdinalIgnoreCase))
                          ?? devices.FirstOrDefault(d => !d.Name.Contains("Loopback", StringComparison.OrdinalIgnoreCase))
                          ?? devices.FirstOrDefault();

                if (dev == null)
                {
                    LastError = "No se encontró ningún dispositivo de captura Npcap disponible.";
                    OnStatusMessage?.Invoke(LastError);
                    return (false, LastError);
                }

                _activeDevice = dev;
                ActiveDeviceId = dev.Name;
                ActiveDeviceName = dev.Description ?? dev.Name;

                var config = new DeviceConfiguration
                {
                    Mode = DeviceModes.Promiscuous,
                    ReadTimeout = 25,
                    Snaplen = 65535
                };

                _activeDevice.Open(config);

                // Filtro BPF a nivel de kernel para tráfico UDP
                try
                {
                    _activeDevice.Filter = "udp";
                }
                catch
                {
                    // Algunos adaptadores virtuales pueden no soportar BPF complejo
                }

                _activeDevice.OnPacketArrival += HandlePacketArrival;
                _activeDevice.StartCapture();

                IsCapturing = true;
                OnCaptureStateChanged?.Invoke(true);

                var filterMsg = TargetComId > 0 ? $" (Filtro ComID: {TargetComId})" : " (Todas las tramas)";
                var status = $"Sniffer activo en '{ActiveDeviceName}'{filterMsg}";
                OnStatusMessage?.Invoke(status);

                return (true, status);
            }
            catch (Exception ex)
            {
                LastError = $"Error al iniciar captura en red: {ex.Message}";
                StopCaptureInternal();
                OnStatusMessage?.Invoke(LastError);
                return (false, LastError);
            }
        }
    }

    /// <summary>
    /// Actualiza los parámetros de filtrado en caliente sin reiniciar la captura.
    /// </summary>
    public void UpdateFilter(
        long targetComId,
        string targetIp,
        string datasetName,
        TrdpDataset? datasetDef,
        bool enableEndianSwap)
    {
        TargetComId      = targetComId;
        FilterByComId    = targetComId > 0;
        TargetIp         = targetIp?.Trim() ?? string.Empty;
        DatasetName      = datasetName;
        DatasetDef       = datasetDef;
        EnableEndianSwap = enableEndianSwap;
    }

    /// <summary>
    /// Detiene la captura en vivo y libera el adaptador de red.
    /// </summary>
    public void StopCapture()
    {
        lock (_lock)
        {
            StopCaptureInternal();
            OnCaptureStateChanged?.Invoke(false);
            OnStatusMessage?.Invoke("Sniffer de red detenido.");
        }
    }

    private void StopCaptureInternal()
    {
        if (_activeDevice != null)
        {
            try { _activeDevice.StopCapture(); } catch { }
            try { _activeDevice.Close(); } catch { }
            _activeDevice.OnPacketArrival -= HandlePacketArrival;
            _activeDevice = null;
        }

        IsCapturing = false;
        RefreshRateHz = 0;
        ActiveDeviceId = null;
        ActiveDeviceName = null;
    }

    private void HandlePacketArrival(object sender, PacketCapture e)
    {
        if (!IsCapturing) return;

        try
        {
            var rawData = e.Data.ToArray();
            var ts = e.Header.Timeval.Date;

            // Extraer y validar trama IPv4/UDP/TRDP
            long filterComId = FilterByComId ? TargetComId : 0;
            string filterIp = FilterByComId ? TargetIp : string.Empty;

            var capturedFrame = TrdpPcapParserService.ExtractPayloadFromPktBytes(
                rawData,
                (int)Interlocked.Increment(ref _packetsCaptured),
                (int)_packetsCaptured,
                ts,
                _firstPacketTime,
                filterComId,
                filterIp);

            if (capturedFrame == null || capturedFrame.Payload == null || capturedFrame.Payload.Length == 0)
                return;

            Interlocked.Increment(ref _matchingFramesCount);

            // Medición de frecuencia (Hz)
            UpdateHzRate(ts);

            // Decodificar payload según dataset seleccionado
            var dsDef = DatasetDef;
            var parsed = TrdpPcapParserService.DecodeParsedPayload(capturedFrame.Payload, dsDef, EnableEndianSwap);

            var frameMsg = new TrdpFrameMessage
            {
                Type      = "trdp_frame",
                Dataset   = DatasetName,
                ComId     = capturedFrame.ComId,
                Size      = capturedFrame.Payload.Length,
                RawBytes  = capturedFrame.Payload.ToList(),
                RawHex    = string.Join(" ", capturedFrame.Payload.Select(b => b.ToString("X2"))),
                Timestamp = new DateTimeOffset(ts.Kind == DateTimeKind.Utc ? ts : ts.ToUniversalTime()).ToUnixTimeMilliseconds(),
                Parsed    = parsed
            };

            OnFrameCaptured?.Invoke(frameMsg);
        }
        catch { }
    }

    private void UpdateHzRate(DateTime now)
    {
        lock (_arrivalTimestamps)
        {
            _arrivalTimestamps.Enqueue(now);
            while (_arrivalTimestamps.Count > HZ_WINDOW_SIZE)
            {
                _arrivalTimestamps.Dequeue();
            }

            if (_arrivalTimestamps.Count >= 2)
            {
                var oldest = _arrivalTimestamps.Peek();
                var span = (now - oldest).TotalSeconds;
                if (span > 0.001)
                {
                    RefreshRateHz = Math.Round((_arrivalTimestamps.Count - 1) / span, 1);
                }
            }
        }
    }

    public void Dispose()
    {
        StopCapture();
    }
}
