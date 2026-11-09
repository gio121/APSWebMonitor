using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using ApsMonitor.Components.Shared;
using ApsMonitor.Data;
using ApsMonitor.Models;
using ApsMonitor.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

int checks = 0;
FailureCardChecks.Run();
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
var signals = new List<Signal>();
var values = new Dictionary<int, double>();
int Add(int node, string tag, double value)
{
    int id = signals.Count + 1;
    signals.Add(new() { Id = id, NodoNumero = node, Tag = tag });
    values[id] = value;
    return id;
}
for (int i = 1; i <= 6; i++)
{
    Add(2, $"SW_NAME_{i}", "C135KF"[i - 1]);
    Add(2, $"FPGA_VER{i}", "F44022"[i - 1]);
    Add(3, i == 1 ? "SW_NAME_1" : $"SW_NAME_{i}_U_BOOT", "COMMS1"[i - 1]);
}
Add(2, "SW_VER_H", '2'); Add(2, "SW_VER_L", 'A'); Add(2, "FPGA_OFF_VER", 12);
Add(3, "SW_VER_L_U_BOOT", '1'); Add(3, "SW_VER_H_U_BOOT", '.'); Add(3, "SW_VER_I_U_BOOT", '3');
foreach (var (tag, value) in new[] { ("seconds", 59), ("minutes", 8), ("hours", 15), ("day", 5), ("month", 11), ("year", 26) })
    Add(3, "rtim_" + tag, value);
var flags = Add(2, "status1_i", (1 << 15) | (1 << 9) | 1);
signals.Single(s => s.Id == flags).BitTextoActivo[15] = "High flag";
signals.Single(s => s.Id == flags).BitTextoActivo[9] = "Hidden flag";
signals.Single(s => s.Id == flags).BitTextoActivo[0] = "Converter ready";
var config = ConverterInfoEvaluator.Defaults(signals);
Check(ConverterInfoEvaluator.Value(config.ControlSoftware, values) == "C135KF", "Control software name joins six bytes.");
Check(ConverterInfoEvaluator.Value(config.ControlVersion, values) == "2.A", "Control version uses H.L ASCII order.");
Check(ConverterInfoEvaluator.Value(config.FpgaName, values) == "22044F", "FPGA name reverses the six firmware bytes.");
CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-ES");
Check(ConverterInfoEvaluator.Value(config.FpgaVersion, values) == "1.2", "Firmware version keeps its decimal point in Spanish.");
Check(ConverterInfoEvaluator.Value(config.CommunicationsSoftware, values) == "COMMS1", "Duplicate names are resolved within the communications node.");
Check(ConverterInfoEvaluator.Value(config.CommunicationsVersion, values) == "1.3", "Communications version joins three ASCII bytes as the monitor does.");
Check(ConverterInfoEvaluator.Value(config.CommunicationsDate, values) == "05/11/2026 15:08:59", "RTC preserves device time without a zone offset or double BCD decoding.");
var entries = ConverterInfoEvaluator.Entries(config, signals, values);
Check(entries.Select(e => e.Description).SequenceEqual(new[] { "Converter ready", "High flag" }), "Only described active bits allowed by the legacy mask appear.");
Check(values[flags] == (1 << 15) + (1 << 9) + 1, "Informational masks do not mutate telemetry used by fault cards.");
var partial = new Dictionary<int, double>(values);
partial.Remove(config.ControlSoftware.SignalIds[2]);
Check(ConverterInfoEvaluator.Value(config.ControlSoftware, partial) == "--", "Missing bytes do not produce a misleading partial name.");
partial = new(values) { [config.CommunicationsDate.SignalIds[4]] = 13 };
Check(ConverterInfoEvaluator.Value(config.CommunicationsDate, partial) == "--", "Invalid RTC is unavailable.");
partial[config.CommunicationsDate.SignalIds[4]] = double.NaN;
Check(ConverterInfoEvaluator.Value(config.CommunicationsDate, partial) == "--", "Non-finite session samples are unavailable.");
Check(ConverterInfoEvaluator.Entries(config, signals, new Dictionary<int, double>()).Count == 0, "Missing telemetry does not activate information.");
Add(4, "status1_i", 0);
Check(ConverterInfoEvaluator.Defaults(signals).ControlSoftware.SignalIds.Count == 0, "Ambiguous control nodes require an explicit choice.");
signals.RemoveAt(signals.Count - 1);
config.Enabled = false;
var json = JsonSerializer.Serialize(config);
Check(!ConverterInfoEvaluator.Load(json, signals).Enabled, "Disabled cards remain disabled after reload.");
Check(ConverterInfoEvaluator.Load(json, signals).DetailSignals.Single().Mask == 0xFDFF, "Detail masks survive saving.");
config.Enabled = true;
json = JsonSerializer.Serialize(config);

// The initializer must upgrade an existing project and preserve all window content.
var options = new DbContextOptionsBuilder<ApsDbContext>().UseSqlite("Data Source=:memory:").Options;
using (var db = new ApsDbContext(options))
{
    db.Database.OpenConnection();
    ApsDatabaseInitializer.Initialize(db);
    db.Windows.Add(new() { Nombre = "Legacy", ContentJson = "[]", FailuresConfigJson = "[]" });
    db.SaveChanges();
    db.Database.ExecuteSqlRaw("ALTER TABLE Windows DROP COLUMN ConverterInfoConfigJson");
    ApsDatabaseInitializer.Initialize(db);
    db.ChangeTracker.Clear();
    var window = db.Windows.Single();
    Check(window.ConverterInfoConfigJson == "{}" && window.Nombre == "Legacy", "Old project windows gain a default configuration without losing content.");
    window.ConverterInfoConfigJson = json;
    db.SaveChanges(); db.ChangeTracker.Clear();
    Check(db.Windows.Single().ConverterInfoConfigJson == json, "Card configuration persists with the window.");
}

var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
async Task<string> Render(string configuration, bool tags = false, bool versions = true, bool details = true) => await renderer.Dispatcher.InvokeAsync(async () =>
{
    var output = await renderer.RenderComponentAsync<ConverterInfoCard>(ParameterView.FromDictionary(new Dictionary<string, object?>
    {
        [nameof(ConverterInfoCard.ConfigJson)] = configuration,
        [nameof(ConverterInfoCard.Signals)] = signals,
        [nameof(ConverterInfoCard.Values)] = values,
        [nameof(ConverterInfoCard.ShowTags)] = tags,
        [nameof(ConverterInfoCard.ShowVersions)] = versions,
        [nameof(ConverterInfoCard.ShowDetails)] = details
    }));
    return output.ToHtmlString();
});
var html = await Render(json);
Check(html.Contains("C135KF") && html.Contains("COMMS1") && html.Contains("05/11/2026 15:08:59"), "Rendered card contains firmware and RTC.");
Check(html.Contains("High flag") && !html.Contains("Hidden flag") && !html.Contains("converter-tag"), "Rendered card hides masked bits and diagnostic tags.");
Check((await Render(json, true)).Contains("converter-tag"), "Admin mode can show source tags.");
var detailsOnly = await Render(json, versions: false);
Check(detailsOnly.Contains("High flag") && !detailsOnly.Contains("C135KF") && !detailsOnly.Contains("converter-versions"), "Left details card omits firmware information.");
var versionsOnly = await Render(json, details: false);
Check(versionsOnly.Contains("C135KF") && versionsOnly.Contains("COMMS1") && !versionsOnly.Contains("High flag") && !versionsOnly.Contains("converter-info-entries"), "Right versions card retains firmware information without duplicating details.");
config.Enabled = false;
Check(string.IsNullOrWhiteSpace(await Render(JsonSerializer.Serialize(config))), "Disabled card renders nothing.");
if (args.Length > 0)
{
    var css = File.ReadAllText("Components/Shared/ConverterInfoCard.razor.css");
    File.WriteAllText(args[0], "<meta charset='utf-8'><style>body{background:#0d1117;font:14px Arial;padding:32px}*{box-sizing:border-box}" + css + "</style>" + html);
}
Console.WriteLine($"PASS: {checks} converter information checks.");
