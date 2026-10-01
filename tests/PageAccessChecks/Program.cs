using System.Security.Claims;
using ApsMonitor.Data;
using ApsMonitor.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

var path = Path.Combine(Path.GetTempPath(), $"aps-access-{Guid.NewGuid():N}.db");
var options = new DbContextOptionsBuilder<ApsDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
int checks = 0;
void Check(bool result, string description) { if (!result) throw new Exception(description); checks++; }
async Task<bool> Allowed(PageAccessService service, string role, string key, bool authenticated = true)
{
    var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], authenticated ? "test" : null));
    var requirement = new PageAccessRequirement(key);
    var context = new AuthorizationHandlerContext([requirement], user, null);
    await new PageAccessHandler(service).HandleAsync(context);
    return context.HasSucceeded;
}
try
{
    using (var db = new ApsDbContext(options))
    {
        db.Database.ExecuteSqlRaw("CREATE TABLE MaintenancePagePermissions (PageKey TEXT PRIMARY KEY, Allowed INTEGER NOT NULL)");
        db.Database.ExecuteSqlRaw("INSERT INTO MaintenancePagePermissions VALUES ('logs', 1), ('sesiones', 0)");
        db.Database.ExecuteSqlRaw("CREATE TABLE Users (Role TEXT NOT NULL)");
        db.Database.ExecuteSqlRaw("INSERT INTO Users VALUES ('Operador existente')");
        PageAccessService.EnsureSchema(db);
        PageAccessService.EnsureSchema(db);
    }
    var service = new PageAccessService(new Factory(options));
    Check((await service.GetRolesAsync()).Contains("Operador existente"), "Existing user roles are imported");
    Check(await Allowed(service, "Mantenimiento", "logs") && !await Allowed(service, "Mantenimiento", "sesiones"), "Migration preserves both grants and denials");
    Check(await Allowed(service, "Mantenimiento", "senales"), "Existing visible pages remain available");
    Check(!await Allowed(service, "Mantenimiento", "comandos"), "Restricted pages default to denied");
    int notifications = 0;
    service.Changed += () => notifications++;
    await service.SetAsync("comandos", true);
    Check(notifications == 1, "Connected circuits receive permission changes");
    service = new PageAccessService(new Factory(options));
    Check(await Allowed(service, "Mantenimiento", "comandos"), "Grant persists after restart");
    Check(!await Allowed(service, "OtroRol", "comandos"), "Grant applies only to maintenance");
    Check(!await Allowed(service, "Mantenimiento", "comandos", false), "Anonymous callers cannot use permissions");
    await service.SetAsync("comandos", false);
    Check(!await Allowed(service, "Mantenimiento", "comandos"), "Revocation blocks route policy");
    Check(await Allowed(service, "Administrador", "comandos"), "Administrator retains access");
    Check(!await Allowed(service, "Mantenimiento", "administracion"), "Administration cannot be granted");
    try { await service.SetAsync("administracion", true); throw new Exception("Invalid permission accepted"); }
    catch (ArgumentException) { checks++; }
    Check((await service.GetAsync())["senales"], "Changing one permission preserves others");
    var newRole = await service.CreateRoleAsync("  Operación  ");
    Check(newRole == "Operación" && (await service.GetRolesAsync()).Contains(newRole), "New roles persist with trimmed names");
    Check((await service.GetAsync(newRole)).Values.All(x => !x), "New roles start without page access");
    await service.SetAsync(newRole, "comandos", true);
    service = new PageAccessService(new Factory(options));
    Check(await Allowed(service, newRole, "comandos") && !await Allowed(service, "Mantenimiento", "comandos"), "Custom role permissions persist independently");
    await service.SetAsync(newRole, "comandos", false);
    Check(!await Allowed(service, newRole, "comandos"), "Custom role revocation takes effect");
    Check(!await Allowed(service, "NoExiste", "inicio"), "Unknown roles cannot access pages");
    foreach (var name in new[] { "operación", "administrador", " ", "rol,invalido", new string('x', 41) })
    {
        try { await service.CreateRoleAsync(name); throw new Exception("Invalid or duplicate role accepted"); }
        catch (ArgumentException) { checks++; }
    }
    try { await service.SetAsync("Administrador", "comandos", false); throw new Exception("Administrator access changed"); }
    catch (ArgumentException) { checks++; }
    using (var db = new ApsDbContext(options)) { PageAccessService.EnsureSchema(db); }
    Check(!(await service.GetAsync(newRole))["comandos"], "Repeated migration preserves role settings");
    var components = typeof(PageAccessService).Assembly.GetTypes();
    foreach (var page in PageAccessService.Pages)
    {
        var component = components.Single(t => t.GetCustomAttributes(typeof(Microsoft.AspNetCore.Components.RouteAttribute), true)
            .Cast<Microsoft.AspNetCore.Components.RouteAttribute>().Any(r => r.Template == page.Path));
        Check(component.GetCustomAttributes(true).OfType<IAuthorizeData>().Any(a => a.Policy == "Page:" + page.Key),
            $"Direct route {page.Path} enforces its page permission");
    }
    Console.WriteLine($"PASS: {checks} page access checks");
}
finally { File.Delete(path); }

sealed class Factory(DbContextOptions<ApsDbContext> options) : IDbContextFactory<ApsDbContext>
{
    public ApsDbContext CreateDbContext() => new(options);
}
