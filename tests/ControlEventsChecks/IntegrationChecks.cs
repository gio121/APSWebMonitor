using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using ApsMonitor.Components.Pages;
using ApsMonitor.Data;
using ApsMonitor.Models;
using ApsMonitor.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;

static class IntegrationChecks
{
    public static async Task RunAsync(ControlEventConfig config, byte[] image)
    {
        int checks = 0;
        void Check(bool condition, string text) { if (!condition) throw new Exception(text); checks++; }
        var dbPath = Path.Combine(Path.GetTempPath(), $"aps-events-{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddLogging(); services.AddHttpClient(); services.AddMudServices();
        services.AddSingleton<NavigationManager, TestNavigation>();
        services.AddSingleton<IJSRuntime, TestJs>();
        services.AddDbContextFactory<ApsDbContext>(o => o.UseSqlite($"Data Source={dbPath};Pooling=False"));
        services.AddScoped<ApsDataService>();
        services.Configure<InfluxOptions>(o => o.Enabled = false);
        services.AddSingleton<InfluxWriterService>(); services.AddSingleton<MonitorStateService>();
        await using var provider = services.BuildServiceProvider();
        try
        {
            using (var db = provider.GetRequiredService<IDbContextFactory<ApsDbContext>>().CreateDbContext())
            {
                db.Database.EnsureCreated();
                db.Database.ExecuteSqlRaw("DROP TABLE ImportedControlEventConfigurations");
                ControlEventsSchema.EnsureImportedConfiguration(db);
                ControlEventsSchema.EnsureImportedConfiguration(db);
            }
            var monitor = provider.GetRequiredService<MonitorStateService>();
            using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var firstBlock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool holdReplies = false;
            int polls = 0;
            var device = Task.Run(async () =>
            {
                try
                {
                    while (!timeout.IsCancellationRequested)
                    {
                        var packet = await server.ReceiveAsync(timeout.Token);
                        if (packet.Buffer[5] != 0x5C)
                        {
                            Interlocked.Increment(ref polls);
                            var reply = ReprogrammingProtocol.Frame(1, 0x12, [0, 0]); reply[4] = 3; reply[^1] += 2;
                            await server.SendAsync(reply, packet.RemoteEndPoint, timeout.Token);
                            continue;
                        }
                        firstBlock.TrySetResult();
                        await release.Task.WaitAsync(timeout.Token);
                        if (holdReplies) continue;
                        int address = BinaryPrimitives.ReadInt32LittleEndian(packet.Buffer.AsSpan(8));
                        int size = BinaryPrimitives.ReadUInt16LittleEndian(packet.Buffer.AsSpan(12));
                        var payload = new byte[size + 14]; payload[7] = 2;
                        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8), address);
                        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(12), (ushort)size);
                        image.AsSpan(address, size).CopyTo(payload.AsSpan(14));
                        var frame = ReprogrammingProtocol.Frame(1, 0x54, payload); frame[4] = config.Node;
                        frame[^1] = unchecked((byte)frame.AsSpan(0, frame.Length - 1).ToArray().Sum(b => b));
                        await server.SendAsync(frame, packet.RemoteEndPoint, timeout.Token);
                    }
                }
                catch (OperationCanceledException) { }
            });
            monitor.IpAddress = "127.0.0.1"; monitor.Port = ((IPEndPoint)server.Client.LocalEndPoint!).Port.ToString();
            await monitor.ConnectAsync(); monitor.StartMonitoring();
            while (polls == 0) await Task.Delay(10, timeout.Token);
            var download = monitor.DownloadControlEventsAsync(config, null, timeout.Token);
            await firstBlock.Task.WaitAsync(timeout.Token);
            Check(monitor.IsDownloadingControlEvents && !monitor.IsMonitoring, "Download pauses monitoring.");
            Check(!monitor.TryBeginReprogramming(), "Download excludes reprogramming globally.");
            monitor.StartMonitoring(); Check(!monitor.IsMonitoring, "Cannot start polling during download.");
            try { await monitor.DownloadControlEventsAsync(config, null, timeout.Token); throw new Exception("Second download allowed"); }
            catch (InvalidOperationException) { checks++; }
            Check(!await monitor.SendControlCommandAsync(0x7A, 0), "Commands are blocked during download.");
            release.SetResult();
            Check((await download).SequenceEqual(image) && monitor.IsMonitoring && !monitor.IsDownloadingControlEvents, "Complete image restores monitoring and releases state.");
            holdReplies = true;
            using var cancel = new CancellationTokenSource();
            var cancelledDownload = monitor.DownloadControlEventsAsync(config, null, cancel.Token);
            cancel.Cancel();
            try { await cancelledDownload; throw new Exception("Cancelled download succeeded"); } catch (OperationCanceledException) { checks++; }
            Check(!monitor.IsDownloadingControlEvents && monitor.IsMonitoring, "Cancellation releases transfer state and restores monitoring.");
            Check(monitor.TryBeginReprogramming(), "Reprogramming acquires state after download.");
            try { await monitor.DownloadControlEventsAsync(config, null, timeout.Token); throw new Exception("Download allowed during reprogramming"); }
            catch (InvalidOperationException) { checks++; }
            monitor.EndReprogramming(); monitor.Disconnect(); timeout.Cancel(); await device;

            async Task<string> RenderAsync()
            {
                await using var scope = provider.CreateAsyncScope();
                await using var renderer = new PreviewRenderer(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<ILoggerFactory>());
                return await renderer.Dispatcher.InvokeAsync(async () =>
                {
                    var root = renderer.BeginRenderingComponent(typeof(EventsPreview), ParameterView.Empty);
                    await root.QuiescenceTask;
                    return WebUtility.HtmlDecode(root.ToHtmlString());
                });
            }
            string html = await RenderAsync();
            Check(html.Contains("No hay nodos de eventos disponibles") && !html.Contains("Cargar CMFX"), "Initial page uses existing signals and has no CMFX picker.");
            var data = provider.GetRequiredService<ApsDataService>();
            var signals = config.Values.Values.Select(v => new Signal
            {
                Tag = v.Key, Nombre = v.Name, NodoNumero = config.Node, NodoDescripcion = "Control importado",
                TipoVariable = v.Type switch { "INT" => "INT16", "UINT" => "UINT16", "ULONG" => "UINT32", _ => v.Type },
                Escala = v.Scale, Offset = v.Offset, Unidad = v.Units,
                BytePosicion = 9000, // Monitoring offsets must never replace event offsets.
                BitTextoActivo = Enumerable.Range(0, 16).Select(bit => v.IsBitfield ? v.Labels.GetValueOrDefault(bit) : null).ToArray()
            }).ToList();
            await data.ImportSignalsAsync(signals, [config]);
            var persisted = await new ApsDataService(provider.GetRequiredService<IDbContextFactory<ApsDbContext>>()).GetControlEventConfigurationsAsync();
            Check(persisted.Count == 1 && persisted[0].Signature == config.Signature && persisted[0].Blocks[0].References.First(r => r.Key == "signed").Position == 20, "Retain event layout in the signal import, independently of monitoring offsets.");
            var signed = (await data.GetSignalsAsync()).Single(s => s.Tag == "signed");
            signed.Nombre = "Señal editada"; signed.Escala = 2; signed.Offset = 3; signed.Unidad = "A"; signed.TipoVariable = "UINT16";
            await data.UpdateSignalAsync(signed);
            var state = (await data.GetSignalsAsync()).Single(s => s.Tag == "status");
            state.BitTextoActivo[0] = "Texto editado"; await data.UpdateSignalAsync(state);
            var edited = (await data.GetControlEventConfigurationsAsync()).Single();
            Check(edited.Values["signed"].Name == "Señal editada" && edited.Values["signed"].Scale == 2 && edited.Values["signed"].Offset == 3 && edited.Values["signed"].Units == "A" && edited.Values["signed"].Type == "UINT", "Resolve names, types, scales, offsets and units from current saved signals.");
            Check(edited.Values["status"].Labels[0] == "Texto editado", "Resolve current saved bit labels.");
            await data.DeleteSignalAsync(state.Id);
            var deleted = (await data.GetControlEventConfigurationsAsync()).Single();
            Check(!deleted.Values.ContainsKey("status") && deleted.Blocks.All(b => b.References.All(r => r.Key != "status")), "Deleted signals are excluded from event values and traces.");
            try { await data.ImportSignalsAsync([new Signal { Tag = "signed", Nombre = "Rollback", NodoNumero = 2 }], [config with { Node = 3 }]); throw new Exception("Invalid metadata import succeeded"); }
            catch (ArgumentException) { checks++; }
            Check((await data.GetSignalsAsync()).Single(s => s.Tag == "signed").Nombre == "Señal editada", "Invalid metadata rolls back signal changes atomically.");
            await data.ImportSignalsAsync(signals, []);
            Check((await data.GetControlEventConfigurationsAsync()).Count == 0, "Import without event structure removes the old node layout.");
            await data.ImportSignalsAsync(signals, [config]);
            EventsPreview.Image = image;
            html = await RenderAsync();
            Check(html.Contains("Fallo de prueba") && html.Contains("0042") && html.Contains("Trazas del evento"), "Render parsed event, details and trace controls.");
            Check(html.Contains("19/07/2026 14:34:56") && html.Contains("signed ES") && html.Contains("Control importado") && !html.Contains("Cargar CMFX"), "Render database signals and localized timestamp without a second CMFX import.");
            await data.DeleteAllSignalsAsync();
            Check((await data.GetControlEventConfigurationsAsync()).Count == 0, "Removing all signals also makes event nodes unavailable.");
            Console.WriteLine($"PASS: {checks} monitoring coordination and Razor rendering checks.");
        }
        finally { File.Delete(dbPath); EventsPreview.Image = null; }
    }
}

// Render the interactive page as static HTML for this server-side snapshot check.
sealed class PreviewRenderer(IServiceProvider services, ILoggerFactory logging) : StaticHtmlRenderer(services, logging)
{
    protected override IComponent ResolveComponentForRenderMode(Type type, int? parentId, IComponentActivator activator, IComponentRenderMode mode) => activator.CreateInstance(type);
}

sealed class TestNavigation : NavigationManager { public TestNavigation() => Initialize("http://localhost/", "http://localhost/eventos-control"); }
sealed class TestJs : IJSRuntime
{
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
}
sealed class EventsPreview : EventosControl
{
    public static byte[]? Image;
    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();
        if (Image is null) return;
        typeof(EventosControl).GetMethod("LoadDat", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, [Image, "test.DAT"]);
    }
    protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
    {
        builder.OpenComponent<MudPopoverProvider>(0); builder.CloseComponent();
        base.BuildRenderTree(builder);
    }
}
