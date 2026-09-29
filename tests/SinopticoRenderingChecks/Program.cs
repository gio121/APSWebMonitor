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
