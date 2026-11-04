using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ApsMonitor.Models;
using ApsMonitor.Services;

void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-ES");
var legacy = JsonSerializer.Deserialize<SinopticoElement>("""{"Type":"Dotline","Width":12,"Height":150}""")!;
Check(legacy.StrokeWidth == 1.5, "Old windows must have a usable default stroke.");
legacy.StrokeWidth = 2.5;
Check(JsonSerializer.Deserialize<SinopticoElement>(JsonSerializer.Serialize(legacy))!.StrokeWidth == 2.5, "Stroke must survive saving/loading.");
var dot = XElement.Parse(SinopticoIconProvider.RenderIcon(SinopticoIconProvider.GetIcon(legacy.Type), legacy, "#000000"));
Check((string?)dot.Attribute("stroke-width") == "2.5", "SVG numbers must use invariant culture.");
Check(dot.Elements("circle").Count() == 1, "Dotline must have a dot at only one end.");
Check((string?)dot.Element("path")?.Attribute("d") == "M6 5 V150", "Dotline must extend to the opposite edge without a dot.");
Check(dot.Elements("circle").All(c => (string?)c.Attribute("r") == "5"), "Dotline terminals must remain 10px across.");
legacy.Height = 300;
var tall = XElement.Parse(SinopticoIconProvider.RenderIcon(SinopticoIconProvider.GetIcon(legacy.Type), legacy, "#ffffff"));
Check((string?)tall.Attribute("stroke-width") == "2.5" && tall.Elements("circle").All(c => (string?)c.Attribute("r") == "5"), "Resizing must not thicken the line or terminals.");
Check(SinopticoIconProvider.GetIcon("Contactor") != SinopticoIconProvider.GetIcon("Contactor_Closed"), "Contactor states must stay distinguishable.");
Check(SinopticoIconProvider.GetIcon("Contactor V") != SinopticoIconProvider.GetIcon("Contactor V_Closed"), "Vertical contactor states must stay distinguishable.");

string[] types = ["Linea H", "Linea V", "Dotline", "Diodo", "Resistencia", "Bobina", "Descargador", "Transformador", "Fusible", "Interruptor", "Seccionador", "Contactor", "Contactor_Closed", "Contactor V", "Contactor V_Closed", "Borne", "Rectificador", "Inversor", "Convertidor", "Trifasico", "Ventilador", "Condensador", "ACpresence", "Box"];
var html = new StringBuilder("<meta charset='utf-8'><style>body{font:14px Arial;background:#eee;padding:24px}.grid{display:grid;grid-template-columns:repeat(6,150px);gap:12px}.card{height:170px;background:white;display:flex;align-items:center;justify-content:center;flex-direction:column;gap:20px}h1{font-size:20px}.dark .card{background:#17202c;color:white}</style><h1>Símbolos del editor · trazo 1,5 px</h1>");
foreach (var color in new[] { "#000000", "#ffffff" })
{
    html.Append(color == "#000000" ? "<div class='grid'>" : "<h1>Modo oscuro</h1><div class='grid dark'>");
    foreach (var type in types)
    {
        var (width, height) = SinopticoIconProvider.GetDefaultSize(type.Replace("_Closed", ""));
        var element = new SinopticoElement { Type = type, Width = width, Height = height };
        var svg = SinopticoIconProvider.RenderIcon(SinopticoIconProvider.GetIcon(type), element, color);
        var xml = XElement.Parse(svg);
        Check(xml.Descendants().All(n => (string?)n.Attribute("vector-effect") == "non-scaling-stroke"), $"{type}: all geometry must keep a constant stroke.");
        Check((string?)xml.Attribute("stroke") == color, $"{type}: theme color must be applied.");
        html.Append($"<div class='card'><div style='width:{width}px;height:{height}px'>{svg}</div><span>{type}</span></div>");
    }
    html.Append("</div>");
}
if (args.Length > 0) File.WriteAllText(args[0], html.ToString());
Console.WriteLine("PASS: legacy JSON, save/load, resizing, terminal geometry, contactor states and 24 symbols in both themes.");

var values = new Dictionary<int, double> { [1] = 32768, [2] = 0, [3] = 25, [4] = 100, [5] = 644 };
Check(SinopticoBindingEvaluator.Read(values, 1, 15) == 1, "High-byte flags must address bit 15.");
Check(!SinopticoBindingEvaluator.Matches(new() { SignalId = 99, Invert = true }, values), "Missing telemetry must not activate inverted conditions.");
Check(SinopticoBindingEvaluator.Matches(new() { SignalId = 2, SignalBit = 5, Invert = true }, values), "FLP DC return is active low.");
Check(SinopticoBindingEvaluator.Matches(new() { SignalId = 5, ExpectedValue = 644 }, values), "Failure codes must use equality.");
Check(!SinopticoBindingEvaluator.Matches(new() { SignalId = 5, ExpectedValue = 645 }, values), "Another nonzero failure must not match.");
Check(SinopticoBindingEvaluator.Matches(new() { SignalId = 1, SignalBit = 15, CompareSignalId = 2, CompareSignalBit = 0, Invert = true }, values), "Return/mando mismatch must be detected.");
var timer = new SinopticoElement { SignalId = 3, StateSignalId = 4, ProgressCountDown = true };
Check(SinopticoBindingEvaluator.Progress(timer, values) == 75, "Count-down timers use max minus count.");
timer.ProgressCountDown = false;
Check(SinopticoBindingEvaluator.Progress(timer, values) == 25, "Direct timers retain their direction.");
timer.MinValue = 20;
Check(SinopticoBindingEvaluator.Progress(timer, values) == 6.25, "Progress must respect its configured minimum.");
values[4] = 0;
Check(SinopticoBindingEvaluator.Progress(timer, values) == 0, "Zero/invalid timer maxima must be safe.");
timer.StateSignalId = 99;
Check(SinopticoBindingEvaluator.Progress(timer, values) == 0, "Unknown maximum must not fabricate progress.");
var contactor = new SinopticoElement { Type = "Contactor", StateSignalId = 2, StateSignalBit = 5, InvertState = true, CommandSignalId = 1, CommandSignalBit = 15 };
Check(SinopticoBindingEvaluator.Contactor(contactor, values).Icon == SinopticoIconProvider.GetIcon("Contactor_Closed"), "Inverted feedback must close contactor.");
contactor.InvertState = false;
Check(SinopticoBindingEvaluator.Contactor(contactor, values).Color == contactor.ErrorColor, "Command/feedback mismatch must show error.");
var label = new SinopticoElement { Text = "Legacy label", SignalId = 3 };
Check(SinopticoBindingEvaluator.Text(label, values, _ => "fallback") == "Legacy label", "Existing labels remain static by default.");
label.ShowValue = true; label.ValueFormat = "0.0"; label.ValueUnit = "V";
Check(SinopticoBindingEvaluator.Text(label, values, _ => "fallback") == "25,0 V", "Dynamic labels respect culture and units.");
label.ValueTexts["25"] = "ON";
Check(SinopticoBindingEvaluator.Text(label, values, _ => "fallback") == "ON", "Enumerated text must override numeric formatting.");
label.TextRules.Add(new() { SignalId = 5, ExpectedValue = 644, Text = "ERROR" });
Check(SinopticoBindingEvaluator.Text(label, values, _ => "fallback") == "ERROR", "Alarm text must override enum values.");
label.VisibilityRules.Add(new() { SignalId = 99 }); label.VisibilityRules.Add(new() { SignalId = 5 });
Check(SinopticoBindingEvaluator.Visible(label, values), "Default visibility is OR.");
label.VisibilityMatchAll = true;
Check(!SinopticoBindingEvaluator.Visible(label, values), "All-condition visibility is AND.");
label.BoxColorRules.Add(new() { SignalId = 5, ExpectedValue = 644, Color = "#ef4444" });
label.BoxColorRules.Add(new() { SignalId = 5, Color = "#f59e0b" });
Check(SinopticoBindingEvaluator.Color(label, values) == "#ef4444", "First color rule has priority.");
var restored = JsonSerializer.Deserialize<SinopticoElement>(JsonSerializer.Serialize(label))!;
Check(restored.ValueTexts["25"] == "ON" && restored.TextRules[0].ExpectedValue == 644 && restored.VisibilityMatchAll && restored.BoxColorRules[0].ExpectedValue == 644, "New binding rules must survive save and duplication.");
Console.WriteLine("PASS: FLP flags, inverted returns, failure codes, timer directions, missing telemetry, dynamic text, visibility, priority and binding persistence.");
