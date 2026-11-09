using System.Reflection;
using System.Runtime.CompilerServices;
using ApsMonitor.Components.Pages;
using ApsMonitor.Models;
using ApsMonitor.Resources;
using ApsMonitor.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using ApsMonitor.Components.Shared;
using Microsoft.JSInterop;

const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
var editor = new SinopticoEditor();
void Field(string name, object? value) => typeof(SinopticoEditor).GetField(name, flags)!.SetValue(editor, value);
void Property(string name, object value) => typeof(SinopticoEditor).GetProperty(name, flags)!.SetValue(editor, value);
Property("Ui", services.GetRequiredService<IStringLocalizer<MonitorResources>>());
Property("SessionState", new SessionStateService());
// Rendering reads IsMonitoring only; no network or database is needed for these checks.
Property("MonitorState", RuntimeHelpers.GetUninitializedObject(typeof(MonitorStateService)));
var original = new SinopticoElement { X = 100 };
Field("_selectedElement", original);
using var tree = new RenderTreeBuilder();
typeof(SinopticoEditor).GetMethod("BuildRenderTree", flags)!.Invoke(editor, [tree]);
var setters = new List<Delegate>();
void Visit(RenderTreeBuilder builder)
{
    var frames = builder.GetFrames();
    for (var i = 0; i < frames.Count; i++)
    {
        var frame = frames.Array[i];
        if (frame.FrameType != RenderTreeFrameType.Attribute) continue;
        if (frame.AttributeValue is RenderFragment fragment)
        {
            using var child = new RenderTreeBuilder();
            fragment(child);
            Visit(child);
        }
        if (frame.AttributeName == "ValueChanged" && frame.AttributeValue is EventCallback<double> callback)
            setters.Add((Delegate)typeof(EventCallback<double>).GetField("Delegate", flags | BindingFlags.Public)!.GetValue(callback)!);
    }
}
Visit(tree);
if (setters.Count < 4) throw new Exception("Geometry controls were not captured.");
Field("_selectedElement", null);
setters[0].DynamicInvoke(125d);
if (original.X != 125) throw new Exception("A pending edit must retain its original target after deletion.");
var next = new SinopticoElement { X = 500 };
Field("_selectedElement", next);
setters[0].DynamicInvoke(150d);
if (original.X != 150 || next.X != 500) throw new Exception("A pending edit must not overwrite a newly selected element.");
Console.WriteLine("PASS: pending property callbacks survive deletion and do not modify a different selection.");

// Exercise the real post-render interop boundary. An array passed directly to
// params object[] spreads its elements into arguments and breaks update(next).
var controller = new GeometryController();
var viewport = new SynopticViewport();
typeof(SynopticViewport).GetField("_controller", flags)!.SetValue(viewport, controller);
var geometry = new List<SinopticoElement> { new() { X = 10 } };
typeof(SynopticViewport).GetProperty(nameof(SynopticViewport.Elements))!.SetValue(viewport, geometry);
async Task RenderGeometry(int expectedCount)
{
    await (Task)typeof(SynopticViewport).GetMethod("OnAfterRenderAsync", flags)!.Invoke(viewport, [false])!;
    if (controller.Geometry.GetArrayLength() != expectedCount) throw new Exception("Geometry count differs.");
}
await RenderGeometry(1);
geometry.Add(new() { X = 20 });
await RenderGeometry(2);
geometry[0].X = 125;
await RenderGeometry(2);
if (controller.Geometry[0].GetProperty("X").GetDouble() != 125) throw new Exception("Move was not sent.");
geometry.RemoveAt(0);
await RenderGeometry(1);
geometry.Clear();
await RenderGeometry(0);
if (controller.Updates != 5) throw new Exception("An edit did not reach JavaScript.");
await RenderGeometry(0);
if (controller.Updates != 5) throw new Exception("Unchanged geometry should not be resent.");
await viewport.DisposeAsync();
Console.WriteLine("PASS: add, move, delete and empty canvas send one geometry array to JavaScript.");

sealed class GeometryController : IJSObjectReference
{
    public System.Text.Json.JsonElement Geometry { get; private set; }
    public int Updates { get; private set; }
    public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args)
    {
        if (identifier == "update")
        {
            if (args is not { Length: 1 } || args[0] is not Array)
                throw new Exception("update(next) requires a single array argument; spreading elements disconnects the circuit.");
            Geometry = System.Text.Json.JsonSerializer.SerializeToElement(args[0]);
            Updates++;
        }
        return ValueTask.FromResult(default(T)!);
    }
    public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) => InvokeAsync<T>(identifier, args);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
