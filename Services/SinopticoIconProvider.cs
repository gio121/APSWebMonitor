using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using ApsMonitor.Models;
using MudBlazor;

namespace ApsMonitor.Services;

public static class SinopticoIconProvider
{
    // Keep wire thickness independent of the size of the symbol.
    private static string Symbol(string geometry, string viewBox = "0 0 60 40") =>
        $"<svg viewBox='{viewBox}' fill='none' stroke='currentColor' stroke-width='1.5' stroke-linecap='round' stroke-linejoin='round'>" +
        Regex.Replace(geometry, @"<(path|circle|rect|ellipse|line|polyline|polygon)\b", "$0 vector-effect='non-scaling-stroke'") + "</svg>";

    private static readonly Dictionary<string, string> _icons = new()
    {
        ["Etiqueta"] = Icons.Material.Filled.TextFormat,
        ["Señal"] = Icons.Material.Filled.Sensors,
        ["Comando"] = Icons.Material.Filled.ToggleOn,
        ["Caja"] = Icons.Material.Filled.CheckBoxOutlineBlank,
        ["Barra"] = Icons.Material.Filled.LinearScale,
        ["Linea H"] = Symbol("<path d='M0 20 H60'/>"),
        ["Linea V"] = Symbol("<path d='M30 0 V40'/>"),
        ["Dotline"] = Symbol("<path d='M6 5 V100'/><circle cx='6' cy='5' r='4' fill='currentColor' stroke='none'/>", "0 0 12 100"),
        ["Diodo"] = Symbol("<path d='M0 20 H16 M16 6 L40 20 L16 34 Z M40 6 V34 M40 20 H60'/>"),
        ["Resistencia"] = Symbol("<path d='M0 20 H12 M48 20 H60'/><rect x='12' y='12' width='36' height='16'/>"),
        ["Bobina"] = Symbol("<path d='M0 26 H10 A5 12 0 0 1 20 26 A5 12 0 0 1 30 26 A5 12 0 0 1 40 26 A5 12 0 0 1 50 26 H60'/>"),
        ["Descargador"] = Symbol("<path d='M0 20 H18 L24 8 L36 32 L42 20 H60'/>"),
        ["Transformador"] = Symbol("<path d='M0 20 H10 M50 20 H60'/><circle cx='24' cy='20' r='14'/><circle cx='36' cy='20' r='14'/>"),
        ["Fusible"] = Symbol("<path d='M0 20 H60'/><rect x='18' y='12' width='24' height='16'/>"),
        ["Interruptor"] = Symbol("<path d='M0 20 H18 L40 6 M42 20 H60 M38 16 L46 24 M38 24 L46 16'/>"),
        ["Seccionador"] = Symbol("<path d='M0 20 H18 L40 6 M42 20 H60'/><circle cx='18' cy='20' r='2'/><circle cx='42' cy='20' r='2'/>"),
        ["Contactor"] = Symbol("<path d='M0 20 H18 L40 6 M42 20 H60'/>"),
        ["Contactor_Closed"] = Symbol("<path d='M0 20 H60'/>"),
        ["Contactor V"] = Symbol("<path d='M20 0 V18 L6 40 M20 42 V60'/>", "0 0 40 60"),
        ["Contactor V_Closed"] = Symbol("<path d='M20 0 V60'/>", "0 0 40 60"),
        ["Borne"] = Symbol("<circle cx='12' cy='12' r='5'/>", "0 0 24 24"),
        ["Rectificador"] = Converter("<path d='M10 15 Q14 7 18 15 T26 15 M36 42 H50 M36 46 H50'/>"),
        ["Inversor"] = Converter("<path d='M10 13 H24 M10 17 H24 M34 44 Q38 36 42 44 T50 44'/>"),
        ["Convertidor"] = Converter("<path d='M10 13 H24 M10 17 H24 M36 42 H50 M36 46 H50'/>"),
        ["Trifasico"] = Symbol("<rect x='2' y='2' width='56' height='56'/><path d='M30 10 L10 46 H50 Z M20 24 L26 28 M34 28 L40 24 M30 42 V50'/>", "0 0 60 60"),
        ["Ventilador"] = Symbol("<circle cx='30' cy='30' r='27'/><circle cx='30' cy='30' r='3'/><path d='M28 27 C8 20 17 5 28 9 C34 12 35 20 32 27 M33 30 C49 16 57 32 48 40 C41 43 36 38 32 33 M28 32 C33 52 14 54 12 42 C13 34 21 32 28 32'/>", "0 0 60 60"),
        ["Condensador"] = Symbol("<path d='M20 0 V25 M5 25 H35 M5 35 H35 M20 35 V60'/>", "0 0 40 60"),
        ["ACpresence"] = Symbol("<path d='M30 3 L58 56 H2 Z M33 16 L22 33 H33 L26 46 L41 27 H29 Z'/>", "0 0 60 60"),
        ["Box"] = Symbol("<rect x='1' y='1' width='58' height='38'/>")
    };

    private static string Converter(string markings) => Symbol(
        "<rect x='2' y='2' width='56' height='56'/><path d='M2 58 L58 2'/>" + markings, "0 0 60 60");

    public static string GetIcon(string type) =>
        type != null && _icons.TryGetValue(type, out var icon) ? icon : Icons.Material.Filled.Help;

    public static bool HasStroke(string type) => GetIcon(type).StartsWith("<svg");

    public static (double Width, double Height) GetDefaultSize(string type) => type switch
    {
        "Etiqueta" => (100, 20),
        "Señal" => (160, 60),
        "Comando" or "Caja" => (100, 30),
        "Linea H" => (100, 10),
        "Linea V" or "Dotline" => (12, 100),
        "Contactor V" or "Condensador" => (40, 60),
        "Borne" => (20, 20),
        "Rectificador" or "Inversor" or "Convertidor" or "Trifasico" or "Ventilador" or "ACpresence" => (60, 60),
        "Barra" => (200, 30),
        _ => (60, 40)
    };

    public static string RenderIcon(string icon, SinopticoElement element, string color)
    {
        string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        var stroke = double.IsFinite(element.StrokeWidth) ? Math.Clamp(element.StrokeWidth, 0.5, 8) : 1.5;
        if (element.Type == "Dotline")
        {
            // Canvas units keep the terminal dot round when resizing either axis.
            var width = double.IsFinite(element.Width) ? Math.Max(1, element.Width) : 12;
            var height = double.IsFinite(element.Height) ? Math.Max(1, element.Height) : 100;
            var radius = Math.Min(5, Math.Min(width, height) / 2);
            var x = F(width / 2);
            icon = Symbol($"<path d='M{x} {F(radius)} V{F(height)}'/><circle cx='{x}' cy='{F(radius)}' r='{F(radius)}' fill='currentColor' stroke='none'/>", $"0 0 {F(width)} {F(height)}");
        }
        color = HtmlEncoder.Default.Encode(color);
        if (icon.StartsWith("<svg"))
            return icon.Replace("<svg", "<svg width='100%' height='100%' preserveAspectRatio='none' style='display:block;overflow:visible' aria-hidden='true'")
                .Replace("stroke-width='1.5'", $"stroke-width='{F(stroke)}'").Replace("currentColor", color);
        var content = icon.StartsWith("<path") ? icon : $"<path d='{icon}'/>";
        return $"<svg viewBox='0 0 24 24' width='100%' height='100%' aria-hidden='true' fill='{color}'>{content}</svg>";
    }
}
