using System.Globalization;
using System.Text.Json;
using System.Text.Encodings.Web;
using ApsMonitor.Components.Shared;
using ApsMonitor.Models;
using ApsMonitor.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

var outputDirectory = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/synoptic-viewport-checks");
CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-GB");
CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-GB");
Directory.CreateDirectory(outputDirectory);
var elements = new List<SinopticoElement>
{
    new() { Id = "move", Type = "Caja", X = 100, Y = 100, Width = 120, Height = 60, ProportionalScale = false, Text = "Move / resize" },
    new() { Id = "rotated", Type = "Caja", X = -100, Y = -100, Width = 160, Height = 40, Rotation = 45 },
    new() { Id = "edge", Type = "Caja", X = 1100, Y = 550, Width = 100, Height = 50 }
};
if (args.Length > 1) elements = JsonSerializer.Deserialize<List<SinopticoElement>>(File.ReadAllText(args[1]))!;
using var services = new ServiceCollection().AddLogging().AddLocalization().AddSingleton<IJSRuntime, StaticJs>().BuildServiceProvider();
await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
var html = await renderer.Dispatcher.InvokeAsync(async () =>
{
    RenderFragment content = builder =>
    {
        foreach (var element in elements)
        {
            string Number(double value) => value.ToString(CultureInfo.InvariantCulture);
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "canvas-element");
            builder.AddAttribute(2, "data-synoptic-id", element.Id);
            builder.AddAttribute(3, "style", $"left:{Number(element.X)}px;top:{Number(element.Y)}px;width:{Number(element.Width)}px;height:{Number(element.Height)}px;transform:rotate({Number(element.Rotation)}deg);font-size:{Number(element.FontSize)}px;color:{element.Color}");
            if (element.Type is "Etiqueta" or "Señal" or "Caja" or "Box") builder.AddContent(4, element.Text);
            else builder.AddMarkupContent(5, SinopticoIconProvider.RenderIcon(SinopticoIconProvider.GetIcon(element.Type), element, "#172033"));
            if (args.Length <= 1) { builder.OpenElement(6, "div"); builder.AddAttribute(7, "class", "resize-handle"); builder.CloseElement(); }
            builder.CloseElement();
        }
    };
    var view = await renderer.RenderComponentAsync<SynopticViewport>(ParameterView.FromDictionary(new Dictionary<string, object?>
    { [nameof(SynopticViewport.Elements)] = elements, [nameof(SynopticViewport.Editable)] = true, [nameof(SynopticViewport.ChildContent)] = content }));
    return view.ToHtmlString();
});
if (!html.Contains("synoptic-scroll") || !html.Contains(">Center</button>") || !html.Contains("type=\"number\"") || html.Contains("data-view-action=\"expand\"")) throw new Exception("Viewport controls did not render");
var layoutSource = File.ReadAllText("Components/Layout/MainLayout.razor");
var layoutStyle = System.Text.RegularExpressions.Regex.Match(layoutSource, "<style>([\\s\\S]*?)</style>").Groups[1].Value.Replace("@@", "@");
File.WriteAllText(Path.Combine(outputDirectory, "layout.css"), layoutStyle);
var geometry = elements.Select(e => new { e.Id, e.X, e.Y, e.Width, e.Height, e.Rotation, e.StrokeWidth, e.ProportionalScale });
var data = JsonSerializer.Serialize(geometry, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
if (args.Length > 1) html = "<style>.canvas-element{border:0;line-height:1;white-space:nowrap;overflow:hidden}</style>" + html;
File.WriteAllText(Path.Combine(outputDirectory, "fixture.html"), """
<!doctype html><html><head><meta name="viewport" content="width=device-width, initial-scale=1"><meta charset="utf-8">
<link rel="stylesheet" href="/mud.css"><link rel="stylesheet" href="/styles.css"><link rel="stylesheet" href="/layout.css"><style>
*{box-sizing:border-box}body{margin:0;padding:0;font:14px Arial;background:#eee}.canvas-element{position:absolute;display:flex;align-items:center;justify-content:center;border:1px solid #94a3b8;user-select:none}.canvas-element svg{width:100%;height:100%}.resize-handle{position:absolute;right:-6px;bottom:-6px;width:12px;height:12px;background:#2563eb;z-index:10}.fixture-header{height:60px}#fixture-cards{height:0;overflow:hidden}.device-connection-status{position:fixed;bottom:0;height:32px;width:100%;background:#ddd}
</style></head><body><main class="mud-main-content monitor-main-content"><div class="monitor-page-content"><div class="fixture-header">Viewport integration check</div><div id="fixture-cards"></div>
""" + html + "</div></main><footer class=\"device-connection-status\"></footer><script type=\"application/json\" id=\"geometry\">" + data + "</script><script type=\"module\" src=\"/bootstrap.mjs\"></script></body></html>");
Console.WriteLine($"PASS: viewport server rendering; generated browser fixture with {elements.Count} elements.");

sealed class StaticJs : IJSRuntime
{
    public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => ValueTask.FromResult(default(T)!);
    public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(T)!);
}
