using System.Globalization;
using System.Text.Json;
using ApsMonitor.Models;

namespace ApsMonitor.Services;

public static class ConverterInfoEvaluator
{
    public static ConverterInfoConfig Load(string? json, IReadOnlyList<Signal> signals)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}") return Defaults(signals);
        try { return JsonSerializer.Deserialize<ConverterInfoConfig>(json) ?? Defaults(signals); }
        catch (JsonException) { return Defaults(signals); }
    }

    public static ConverterInfoConfig Defaults(IReadOnlyList<Signal> signals)
    {
        // Never pick an arbitrary node when several nodes expose the same tag.
        int? UniqueNode(string tag) => signals.Where(s => !s.IsDeleted && s.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.NodoNumero).Distinct().Select(n => (int?)n).SingleOrDefaultIfUnique();
        var control = UniqueNode("status1_i");
        var communications = signals.Where(s => !s.IsDeleted && s.NodoNumero != control &&
                s.Tag.Equals("rtim_seconds", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.NodoNumero).Distinct().Select(n => (int?)n).SingleOrDefaultIfUnique();
        return Defaults(signals, control, communications);
    }

    public static ConverterInfoConfig Defaults(IReadOnlyList<Signal> signals, int? control, int? communications)
    {
        List<int> Ids(int? node, params string[] tags)
        {
            if (!node.HasValue) return new();
            var ids = tags.Select(tag => signals.FirstOrDefault(s => !s.IsDeleted && s.NodoNumero == node &&
                s.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase))?.Id).ToList();
            return ids.All(id => id.HasValue) ? ids.Select(id => id!.Value).ToList() : new();
        }
        var communicationsNameTags = Enumerable.Range(1, 6).Select(i =>
            signals.Any(s => !s.IsDeleted && s.NodoNumero == communications && s.Tag.Equals($"SW_NAME_{i}", StringComparison.OrdinalIgnoreCase))
                ? $"SW_NAME_{i}" : $"SW_NAME_{i}_U_BOOT").ToArray();
        var config = new ConverterInfoConfig
        {
            ControlSoftware = new() { SignalIds = Ids(control, Enumerable.Range(1, 6).Select(i => $"SW_NAME_{i}").ToArray()) },
            ControlVersion = new() { SignalIds = Ids(control, "SW_VER_H", "SW_VER_L"), Separator = "." },
            FpgaName = new() { SignalIds = Ids(control, Enumerable.Range(1, 6).Reverse().Select(i => $"FPGA_VER{i}").ToArray()) },
            FpgaVersion = new() { SignalIds = Ids(control, "FPGA_OFF_VER"), Format = "DecimalVersion" },
            CommunicationsSoftware = new() { SignalIds = Ids(communications, communicationsNameTags) },
            CommunicationsVersion = new() { SignalIds = Ids(communications, "SW_VER_L_U_BOOT", "SW_VER_H_U_BOOT", "SW_VER_I_U_BOOT") },
            CommunicationsDate = new() { SignalIds = Ids(communications, "rtim_seconds", "rtim_minutes", "rtim_hours", "rtim_day", "rtim_month", "rtim_year"), Format = "DateTime" }
        };
        // Some CMFX files use the ordinary SW_VER names on the communications node.
        if (config.CommunicationsVersion.SignalIds.Count == 0)
        {
            config.CommunicationsVersion.SignalIds = Ids(communications, "SW_VER_H", "SW_VER_L");
            config.CommunicationsVersion.Separator = ".";
        }
        var masks = new (string Tag, ushort Mask)[]
        {
            ("status1_i", 0xFDFF), ("status_aux_2", 0xCA20), ("status_aux_3", 0xFFFF),
            ("status_aux_4", 0xFFFF), ("status_aux_5", 0xFFFF), ("status_aux_6", 0xFFFF),
            ("status_aux_7", 0xFFFF), ("status_aux_aps", 0xFF3F), ("fpga_din_mux_2", 0xFF20),
            ("comms_events", 0xFF08)
        };
        foreach (var (tag, mask) in masks)
            foreach (var id in Ids(control, tag)) config.DetailSignals.Add(new() { SignalId = id, Mask = mask });
        return config;
    }

    public static string Value(InfoValueBinding binding, IReadOnlyDictionary<int, double> values)
    {
        if (binding.SignalIds.Count == 0) return "--";
        var data = new List<double>();
        foreach (var id in binding.SignalIds)
        {
            if (!values.TryGetValue(id, out var value) || !double.IsFinite(value)) return "--";
            data.Add(value);
        }
        if (binding.Format == "DateTime")
        {
            if (data.Count != 6 || data.Any(v => v != Math.Truncate(v)) || data[5] < 0 || data[5] > 99) return "--";
            try
            {
                // Values have already been BCD-decoded by the telemetry parser. No time-zone shift.
                return new DateTime(checked(2000 + (int)data[5]), checked((int)data[4]), checked((int)data[3]),
                    checked((int)data[2]), checked((int)data[1]), checked((int)data[0]))
                    .ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
            }
            catch (ArgumentOutOfRangeException) { return "--"; }
            catch (OverflowException) { return "--"; }
        }
        if (binding.Format == "DecimalVersion")
            return data.Count == 1 ? (data[0] / 10).ToString("0.0", CultureInfo.InvariantCulture) : "--";
        if (binding.Format == "Number")
            return string.Join(binding.Separator, data.Select(v => v.ToString("0.##", CultureInfo.CurrentCulture)));
        if (data.Any(v => (v != 0 && v < 32) || v > 126 || v != Math.Truncate(v))) return "--";
        var result = string.Join(binding.Separator, data.Select(v => v == 0 ? "" : ((char)v).ToString())).Trim();
        return result.Length == 0 ? "--" : result;
    }

    public static List<ConverterInfoEntry> Entries(ConverterInfoConfig config, IReadOnlyList<Signal> signals,
        IReadOnlyDictionary<int, double> values, IReadOnlyList<ControlEventConfig>? definitions = null)
    {
        var entries = new List<ConverterInfoEntry>();
        foreach (var binding in config.DetailSignals)
        {
            var signal = signals.FirstOrDefault(s => s.Id == binding.SignalId && !s.IsDeleted);
            if (signal is null || !values.TryGetValue(signal.Id, out var value) || !double.IsFinite(value) || value < 0 || value > ushort.MaxValue) continue;
            var bits = (ushort)value & binding.Mask;
            for (int bit = 0; bit < Math.Min(16, signal.BitTextoActivo.Length); bit++)
                if ((bits & (1 << bit)) != 0 && !string.IsNullOrWhiteSpace(signal.BitTextoActivo[bit]))
                    entries.Add(new(signal.BindingLabel, bit, SynopticFailureEvaluator.Label(signal, bit, definitions ?? [], signal.BitTextoActivo[bit])!));
        }
        return entries;
    }

    private static int? SingleOrDefaultIfUnique(this IEnumerable<int?> source)
    {
        var nodes = source.Take(2).ToArray();
        return nodes.Length == 1 ? nodes[0] : null;
    }
}

public sealed record ConverterInfoEntry(string Tag, int Bit, string Description);
