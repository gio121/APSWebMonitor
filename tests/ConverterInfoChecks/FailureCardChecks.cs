using ApsMonitor.Models;
using ApsMonitor.Services;

internal static class FailureCardChecks
{
    public static void Run()
    {
        var originalCulture = System.Globalization.CultureInfo.CurrentUICulture;
        System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("en-GB");
        var signals = new List<Signal>
        {
            new() { Id = 1, Tag = "status_ac", NodoNumero = 2 },
            new() { Id = 2, Tag = "nfails_ac", NodoNumero = 2 },
            new() { Id = 3, Tag = "laststop_ac", NodoNumero = 2 },
            new() { Id = 4, Tag = "cfails_1_ac", NodoNumero = 2 }
        };
        signals[2].BitTextoActivo[0] = "Watch Dog";
        signals[2].BitTextoActivo[9] = "Failure";
        var category = new FailureCategoryConfig { Name = "Inverter" };
        SynopticFailureEvaluator.ApplyDefaults(category, signals);
        Assert(category.CountSignalId == 2 && category.LastStopSignalId == 3 && category.FailureSignalId == 4, "English inverter mapping");
        var definitions = new List<ControlEventConfig> { new("Control", "C135KF", 2, "", 0, 0, 0, 0, 0, 0,
            new Dictionary<string, ControlEventValue> { ["cfails_1_ac"] = new("cfails_1_ac", "Fault", "UINT", 1, 0, "", false, 0, 65535,
                new Dictionary<int, string> { [430] = "Inverter overcurrent" }) { SpanishLabels = new() { [430] = "Sobreintensidad del inversor" } },
                ["laststop_ac"] = new("laststop_ac", "Last stop", "UINT", 1, 0, "", true, 0, 65535, new Dictionary<int, string>())
                    { SpanishLabels = new() { [0] = "Watch Dog", [9] = "Fallo" } } }, [], []) };
        var values = new Dictionary<int, double> { [3] = 513, [4] = 430 };
        string Decode(int id, bool bits) => SynopticFailureEvaluator.Decode(id, signals, values, definitions, bits);
        Assert(Decode(3, true) == "Watch Dog, Failure", "All last-stop bits");
        Assert(Decode(4, false) == "Inverter overcurrent", "Fault code is an enumeration, not bits");
        var previousCulture = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("es-ES");
            Assert(Decode(3, true) == "Watch Dog, Fallo", "Spanish last stop");
            Assert(Decode(4, false) == "Sobreintensidad del inversor", "Spanish fault description");
            Assert(SynopticFailureEvaluator.Label(signals[2], 9, definitions, "Failure") == "Fallo", "Spanish card and dialog bit text");
            Assert(SynopticFailureEvaluator.Label(signals[2], 8, definitions, "Custom text") == "Custom text", "Missing translation preserves custom text");
            var restored = System.Text.Json.JsonSerializer.Deserialize<List<ControlEventConfig>>(System.Text.Json.JsonSerializer.Serialize(definitions))!;
            Assert(SynopticFailureEvaluator.Label(signals[2], 9, restored, "Failure") == "Fallo", "Spanish labels persist in JSON");
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = previousCulture; }
        values[3] = 0;
        Assert(Decode(3, true) == "", "Last stop follows telemetry back to zero");
        values[4] = 0;
        Assert(Decode(4, false) == "", "Cleared calculated fault");
        values.Remove(4);
        Assert(Decode(4, false) == "—", "Missing telemetry is not a fault");
        values[4] = double.NaN;
        Assert(Decode(4, false) == "—", "Invalid telemetry");
        var json = System.Text.Json.JsonSerializer.Serialize(category);
        Assert(System.Text.Json.JsonSerializer.Deserialize<FailureCategoryConfig>(json)!.FailureSignalId == 4, "Bindings round trip");
        foreach (var (name, auxiliaryHiddenBit, regulationAllowed, regulationHidden) in new[]
        {
            ("Inverter", 4, new[] { 0, 3, 12 }, new[] { 4, 8, 11, 13 }),
            ("LVPS", 3, new[] { 0, 7, 11, 12 }, new[] { 8, 10, 13, 15 }),
            ("Chopper", -1, new[] { 0, 1, 2 }, new[] { 3, 8, 15 })
        })
        {
            var details = SynopticFailureEvaluator.ReferenceDetails(name);
            var config = new FailureCategoryConfig { DetailSignalMasks = new() { [10] = details[1].Mask, [11] = details[2].Mask } };
            Assert(details[0].Tag == "stop_" + SynopticFailureEvaluator.Suffix(name), "Descriptions use current stop causes");
            Assert(details[0].Mask == ushort.MaxValue, "Keep all stop causes");
            if (auxiliaryHiddenBit >= 0)
                Assert(SynopticFailureEvaluator.DetailBits(config, 10, 1 << auxiliaryHiddenBit) == 0, "Hide contactor status text");
            foreach (var bit in regulationAllowed)
                Assert(SynopticFailureEvaluator.DetailBits(config, 11, 1 << bit) != 0, "Keep reference regulation bit " + bit);
            foreach (var bit in regulationHidden)
                Assert(SynopticFailureEvaluator.DetailBits(config, 11, 1 << bit) == 0, "Exclude reference regulation bit " + bit);
        }
        Console.WriteLine("Failure card bindings and reference bit filters passed.");
        System.Globalization.CultureInfo.CurrentUICulture = originalCulture;
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
