using System.Text.Json;
using ApsMonitor.Data;
using ApsMonitor.Models;
using ApsMonitor.Services;
using Microsoft.EntityFrameworkCore;

var path = Path.Combine(Path.GetTempPath(), $"aps-bindings-{Guid.NewGuid():N}.db");
var options = new DbContextOptionsBuilder<ApsDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
int checks = 0;
void Check(bool value, string message)
{
    if (!value) throw new Exception(message);
    checks++;
}
Signal MakeSignal(string tag, int node, int position = 0) => new()
{
    Tag = tag, Nombre = tag, NodoNumero = node, BytePosicion = position,
    BitTextoActivo = ["Activo", .. new string?[15]]
};
try
{
    using (var db = new ApsDbContext(options))
    {
        db.Database.EnsureCreated();
        // Exercise upgrading an installation that predates persistent signal identities.
        db.Database.ExecuteSqlRaw("ALTER TABLE Signals DROP COLUMN IsDeleted");
        SignalSchema.EnsurePersistentBindings(db);
        SignalSchema.EnsurePersistentBindings(db);
        Check(!db.Signals.Any(), "Schema upgrade is idempotent");
    }
    var service = new ApsDataService(new Factory(options));
    await service.ImportSignalsAsync([MakeSignal("VOLTAGE", 2), MakeSignal("VOLTAGE", 3), MakeSignal("STATE", 2)]);
    var initial = await service.GetSignalsAsync();
    int voltage = initial.Single(s => s.Tag == "VOLTAGE" && s.NodoNumero == 2).Id;
    int state = initial.Single(s => s.Tag == "STATE").Id;
    int otherNode = initial.Single(s => s.NodoNumero == 3).Id;
    var window = new Window
    {
        Nombre = "Retained bindings",
        ContentJson = JsonSerializer.Serialize(new[] { new SinopticoElement
        {
            SignalId = voltage, CommandSignalId = state, StateSignalId = otherNode,
            BoxColorRules = [new ColorRule { SignalId = state, SignalBit = 1 }]
        }}),
        FailuresConfigJson = JsonSerializer.Serialize(new[] { new FailureCategoryConfig
        {
            StatusSignalId = state, CountSignalId = voltage, SignalIds = [voltage, state, otherNode]
        }})
    };
    await service.AddWindowAsync(window);
    await service.DeleteAllSignalsAsync();
    Check((await service.GetSignalsAsync()).Count == 0, "Deleted signals are excluded from monitoring");
    Check((await service.GetSignalsAsync(true)).Count == 3, "Editor retains signal identities");
    Check((await service.GetSignalsAsync(true)).All(s => s.IsDeleted && s.BindingLabel.Contains("CMFX no cargado")), "Missing CMFX is marked");
    // Simulate a restart: no in-memory mapping may be needed to restore links.
    service = new ApsDataService(new Factory(options));
    await service.ImportSignalsAsync([MakeSignal("STATE", 2, 80), MakeSignal(" voltage ", 2, 40)]);
    var restored = await service.GetSignalsAsync();
    Check(restored.Count == 2, "Partial import only restores matching signals");
    Check(restored.Single(s => s.Id == voltage).BytePosicion == 40, "Same ID with updated protocol position");
    Check(restored.Single(s => s.Id == state).BytePosicion == 80, "Reordered import retains IDs");
    Check((await service.GetSignalsAsync(true)).Single(s => s.Id == otherNode).IsDeleted, "Same tag on a different node is not relinked");
    await service.ImportSignalsAsync([MakeSignal("VOLTAGE", 2, 42), MakeSignal("VOLTAGE", 3), MakeSignal("STATE", 2)]);
    Check((await service.GetSignalsAsync()).Count == 3, "Repeated import does not duplicate signals");
    await service.DeleteSignalAsync(voltage);
    Check((await service.GetSignalsAsync()).All(s => s.Id != voltage), "Single deletion hides signal");
    await service.ImportSignalsAsync([MakeSignal("VOLTAGE", 2)]);
    Check((await service.GetSignalsAsync()).Any(s => s.Id == voltage), "Single deletion is recoverable");
    var saved = (await service.GetWindowsAsync()).Single();
    Check(saved.ContentJson == window.ContentJson, "Signal, state, command and color bindings remain unchanged");
    Check(saved.FailuresConfigJson == window.FailuresConfigJson, "Failure status/count/list bindings remain unchanged");
    // Older versions allowed duplicate imports; preserve links to both existing IDs.
    await service.AddSignalAsync(MakeSignal("VOLTAGE", 2));
    var duplicateIds = (await service.GetSignalsAsync()).Where(s => s.Tag == "VOLTAGE" && s.NodoNumero == 2).Select(s => s.Id).ToArray();
    await service.DeleteAllSignalsAsync();
    await service.ImportSignalsAsync([MakeSignal("VOLTAGE", 2, 99)]);
    Check((await service.GetSignalsAsync()).Select(s => s.Id).Order().SequenceEqual(duplicateIds.Order()), "Legacy duplicate IDs retained");
    Check((await service.GetSignalsAsync()).All(s => s.BytePosicion == 99), "Legacy duplicate metadata updated");
    var count = (await service.GetSignalsAsync(true)).Count;
    try { await service.ImportSignalsAsync([MakeSignal("NEW", 2), MakeSignal("", 2)]); throw new Exception("Invalid import accepted"); }
    catch (ArgumentException) { }
    Check((await service.GetSignalsAsync(true)).Count == count, "Invalid import does not partially apply");
    Console.WriteLine($"PASS: {checks} persistent signal binding checks.");
}
finally { if (File.Exists(path)) File.Delete(path); }

sealed class Factory(DbContextOptions<ApsDbContext> options) : IDbContextFactory<ApsDbContext>
{
    public ApsDbContext CreateDbContext() => new(options);
}
