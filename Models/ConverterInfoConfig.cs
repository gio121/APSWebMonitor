namespace ApsMonitor.Models;

public sealed class ConverterInfoConfig
{
    public bool Enabled { get; set; } = true;
    public InfoValueBinding ControlSoftware { get; set; } = new();
    public InfoValueBinding ControlVersion { get; set; } = new() { Separator = "." };
    public InfoValueBinding FpgaName { get; set; } = new();
    public InfoValueBinding FpgaVersion { get; set; } = new() { Format = "DecimalVersion" };
    public InfoValueBinding CommunicationsSoftware { get; set; } = new();
    public InfoValueBinding CommunicationsVersion { get; set; } = new();
    // RTC order: seconds, minutes, hours, day, month, year (decoded BCD).
    public InfoValueBinding CommunicationsDate { get; set; } = new() { Format = "DateTime" };
    public List<InfoFlagBinding> DetailSignals { get; set; } = new();
}

public sealed class InfoValueBinding
{
    public List<int> SignalIds { get; set; } = new();
    public string Format { get; set; } = "Ascii";
    public string Separator { get; set; } = "";
}

public sealed class InfoFlagBinding
{
    public int SignalId { get; set; }
    public ushort Mask { get; set; } = ushort.MaxValue;
}
