using System.Globalization;
using ApsMonitor.Models;

namespace ApsMonitor.Services;

public static class SynopticFailureEvaluator
{
    public static string? Label(Signal signal, int index, IReadOnlyList<ControlEventConfig> definitions, string? fallback)
    {
        var definition = definitions.FirstOrDefault(c => c.Node == signal.NodoNumero)?.Values.GetValueOrDefault(signal.Tag);
        if (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es"
            && definition?.SpanishLabels.TryGetValue(index, out var spanish) == true)
            return spanish;
        return fallback;
    }
    // frmSynoptic.UpdateControlData: stop causes, auxiliary status and regulation flags.
    public static IReadOnlyList<(string Tag, ushort Mask)> ReferenceDetails(string name)
    {
        var suffix = Suffix(name);
        return suffix switch
        {
            "ac" => [("stop_ac", 0xFFFF), ("status_aux_ac", 0xFFEF), ("regul_ac", 0x100F)],
            "dc" => [("stop_dc", 0xFFFF), ("status_aux_dc", 0xFFF7), ("regul_dc", 0x18FF)],
            "in" => [("stop_in", 0xFFFF), ("status_aux_in", 0xFFFF), ("regul_in", 0x0007)],
            _ => []
        };
    }

    public static int DetailBits(FailureCategoryConfig category, int signalId, double value)
        => (int)value & category.DetailSignalMasks.GetValueOrDefault(signalId, ushort.MaxValue);

    public static string? Suffix(string name) => name.Trim().ToLowerInvariant() switch
    {
        "inversor" or "inverter" => "ac", "lvps" => "dc", "chopper" => "in", _ => null
    };

    public static void ApplyDefaults(FailureCategoryConfig category, IReadOnlyList<Signal> signals)
    {
        var suffix = Suffix(category.Name);
        if (suffix is null) return;
        var node = signals.FirstOrDefault(s => s.Id == category.StatusSignalId)?.NodoNumero
            ?? signals.FirstOrDefault(s => s.Id == category.CountSignalId)?.NodoNumero;
        int? Find(string tag)
        {
            var matches = signals.Where(s => !s.IsDeleted && (!node.HasValue || s.NodoNumero == node)
                && s.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            return matches.Length == 1 ? matches[0].Id : null;
        }
        category.StatusSignalId ??= Find($"status_{suffix}");
        category.CountSignalId ??= Find($"nfails_{suffix}");
        category.LastStopSignalId ??= Find($"laststop_{suffix}");
        category.FailureSignalId ??= Find($"cfails_1_{suffix}");
        if (category.SignalIds.Count == 0)
            foreach (var (tag, mask) in ReferenceDetails(category.Name))
                if (Find(tag) is int id)
                {
                    category.SignalIds.Add(id);
                    category.DetailSignalMasks[id] = mask;
                }
    }

    public static string Decode(int? id, IReadOnlyList<Signal> signals,
        IReadOnlyDictionary<int, double> values, IReadOnlyList<ControlEventConfig> definitions, bool bitfield)
    {
        var signal = signals.FirstOrDefault(s => s.Id == id && !s.IsDeleted);
        if (signal is null || !values.TryGetValue(signal.Id, out var value) || !double.IsFinite(value)) return "—";
        if (value == 0) return "";
        var definition = definitions.FirstOrDefault(c => c.Node == signal.NodoNumero)?.Values.GetValueOrDefault(signal.Tag);
        if (bitfield)
        {
            var labels = Enumerable.Range(0, Math.Min(16, signal.BitTextoActivo.Length))
                .Where(bit => ((long)value & (1L << bit)) != 0)
                .Select(bit => Label(signal, bit, definitions, signal.BitTextoActivo[bit])).Where(text => !string.IsNullOrWhiteSpace(text));
            return string.Join(", ", labels);
        }
        // Calculated faults are enumerated codes (e.g. 430), never bit masks.
        return Label(signal, (int)value, definitions, definition?.Labels.GetValueOrDefault((int)value))
            ?? value.ToString("0", CultureInfo.InvariantCulture);
    }
}
