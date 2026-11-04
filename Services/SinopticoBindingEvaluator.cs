using System.Globalization;
using ApsMonitor.Models;

namespace ApsMonitor.Services;

public static class SinopticoBindingEvaluator
{
    public static double? Read(IReadOnlyDictionary<int, double> values, int? id, int? bit = null)
    {
        if (id is null || !values.TryGetValue(id.Value, out var value) || !double.IsFinite(value)) return null;
        if (bit is null) return value;
        if (bit is < 0 or > 63) return null;
        return (((long)value >> bit.Value) & 1) != 0 ? 1 : 0;
    }

    public static bool Matches(SynopticCondition rule, IReadOnlyDictionary<int, double> values)
    {
        var value = Read(values, rule.SignalId, rule.SignalBit);
        if (value is null) return false; // Missing telemetry never satisfies an inverted alarm.
        var expected = rule.CompareSignalId.HasValue ? Read(values, rule.CompareSignalId, rule.CompareSignalBit) : rule.ExpectedValue;
        if (rule.CompareSignalId.HasValue && expected is null) return false;
        var match = expected.HasValue ? value.Value == expected.Value : value.Value != 0;
        return rule.Invert ? !match : match;
    }

    public static bool Visible(SinopticoElement element, IReadOnlyDictionary<int, double> values) =>
        element.VisibilityRules.Count == 0 || (element.VisibilityMatchAll
            ? element.VisibilityRules.All(r => Matches(r, values)) : element.VisibilityRules.Any(r => Matches(r, values)));

    public static string Color(SinopticoElement element, IReadOnlyDictionary<int, double> values, string? fallback = null) =>
        element.BoxColorRules.FirstOrDefault(r => Matches(r, values))?.Color ?? fallback ?? element.Color;

    public static string Text(SinopticoElement element, IReadOnlyDictionary<int, double> values, Func<int?, string> fallback)
    {
        var rule = element.TextRules.FirstOrDefault(r => Matches(r, values));
        if (rule is not null) return rule.Text;
        if (!element.ShowValue) return element.Text;
        var value = Read(values, element.SignalId, element.ValueBit);
        if (value is null) return element.ValuePrefix + "--";
        if (element.ValueTexts.TryGetValue(value.Value.ToString("G", CultureInfo.InvariantCulture), out var label)) return element.ValuePrefix + label;
        if (element.ValueFormat is null && element.ValueBit is null) return element.ValuePrefix + fallback(element.SignalId);
        try { return element.ValuePrefix + value.Value.ToString(element.ValueFormat ?? "0", CultureInfo.CurrentCulture) +
            (string.IsNullOrEmpty(element.ValueUnit) ? "" : " " + element.ValueUnit); }
        catch (FormatException) { return "--"; }
    }

    public static double Progress(SinopticoElement element, IReadOnlyDictionary<int, double> values)
    {
        var value = Read(values, element.SignalId);
        var max = element.StateSignalId.HasValue ? Read(values, element.StateSignalId) : element.MaxValue;
        if (value is null || max is null || max <= element.MinValue) return 0;
        var fraction = (value.Value - element.MinValue) / (max.Value - element.MinValue);
        return Math.Clamp((element.ProgressCountDown ? 1 - fraction : fraction) * 100, 0, 100);
    }

    public static (string Icon, string Color) Contactor(SinopticoElement element, IReadOnlyDictionary<int, double> values)
    {
        var state = Read(values, element.StateSignalId, element.StateSignalBit);
        var command = Read(values, element.CommandSignalId, element.CommandSignalBit);
        bool? stateActive = state is null ? null : (state != 0) ^ element.InvertState;
        bool? commandActive = command is null ? null : (command != 0) ^ element.InvertCommand;
        var closed = stateActive ?? commandActive ?? false;
        var discrepancy = stateActive.HasValue && commandActive.HasValue && stateActive != commandActive;
        return (SinopticoIconProvider.GetIcon(element.Type + (closed ? "_Closed" : "")),
            Color(element, values, discrepancy ? element.ErrorColor : element.Color));
    }
}
