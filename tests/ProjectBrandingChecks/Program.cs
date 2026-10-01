using ApsMonitor.Data;
using ApsMonitor.Services;
using Microsoft.EntityFrameworkCore;

var path = Path.Combine(Path.GetTempPath(), $"aps-branding-{Guid.NewGuid():N}.db");
var options = new DbContextOptionsBuilder<ApsDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
int checks = 0;
void Check(bool result, string message)
{
    if (!result) throw new Exception(message);
    checks++;
}
async Task Reject(Func<Task> action)
{
    try { await action(); }
    catch (ArgumentException) { checks++; return; }
    throw new Exception("Invalid input was accepted");
}
try
{
    using (var db = new ApsDbContext(options))
    {
        // Upgrade an existing installation without the branding table.
        db.Database.ExecuteSqlRaw("CREATE TABLE LegacyData (Id INTEGER PRIMARY KEY)");
        db.Database.ExecuteSqlRaw("INSERT INTO LegacyData VALUES (1)");
        ProjectBrandingService.EnsureSchema(db);
        ProjectBrandingService.EnsureSchema(db);
        Check(db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM LegacyData").Single() == 1, "Upgrade preserves existing data");
    }
    var factory = new Factory(options);
    var service = new ProjectBrandingService(factory);
    await service.LoadAsync();
    Check(service.Current.ProjectName == "APS Monitor" && service.Current.ImageSource == "SepsaMedha.jpg", "Default identity");
    var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aP1sAAAAASUVORK5CYII=");
    string logo = ProjectBrandingService.CreateImageDataUrl(png);
    int updates = 0;
    service.Changed += () => throw new InvalidOperationException("Disconnected view");
    service.Changed += () => updates++;
    await service.SaveAsync("  Proyecto Madrid  ", logo);
    Check(updates == 1 && service.Current.ProjectName == "Proyecto Madrid", "Save notifies connected views and trims name");
    var restarted = new ProjectBrandingService(factory);
    await restarted.LoadAsync();
    Check(restarted.Current == service.Current && restarted.Current.ImageSource == logo, "Name and image survive restart");
    await Reject(() => service.SaveAsync(" ", logo));
    await Reject(() => service.SaveAsync(new string('a', 81), logo));
    await Reject(() => service.SaveAsync("Invalid", "data:image/svg+xml;base64,PHN2Zy8+"));
    await Reject(() => service.SaveAsync("Invalid", "data:image/jpeg;base64," + Convert.ToBase64String(png)));
    await Reject(() => Task.FromResult(ProjectBrandingService.CreateImageDataUrl(new byte[ProjectBrandingService.MaximumImageBytes + 1])));
    Check(service.Current.ProjectName == "Proyecto Madrid" && updates == 1, "Rejected changes preserve saved identity");
    await service.SaveAsync("Otro proyecto", null);
    var restored = new ProjectBrandingService(factory);
    await restored.LoadAsync();
    Check(restored.Current.ProjectName == "Otro proyecto" && restored.Current.ImageSource == "SepsaMedha.jpg", "Default image restoration persists");
    using var saved = new ApsDbContext(options);
    Check(saved.ProjectBranding.Count() == 1, "Updates reuse one settings row");
    Console.WriteLine($"PASS: {checks} project branding checks");
}
finally { File.Delete(path); }

sealed class Factory(DbContextOptions<ApsDbContext> options) : IDbContextFactory<ApsDbContext>
{
    public ApsDbContext CreateDbContext() => new(options);
}
