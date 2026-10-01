using ApsMonitor.Data;
using ApsMonitor.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace ApsMonitor.Services;

public sealed record MonitorPage(string Key, string Name, string Path, bool DefaultAllowed);

public sealed class PageAccessService(IDbContextFactory<ApsDbContext> factory)
{
    public static readonly MonitorPage[] Pages =
    [
        new("inicio", "Inicio", "/", true),
        new("ventanas", "Ventanas", "/ventanas", true),
        new("sesiones", "Sesiones", "/sesiones", true),
        new("senales", "Señales", "/senales", true),
        new("monitor", "Monitor", "/monitor", false),
        new("reprogramacion", "Reprogramación", "/reprogramacion", false),
        new("logs", "Logs", "/logs", false),
        new("analizador-sesiones", "Analizador de sesiones", "/analizador-sesiones", false),
        new("comandos", "Comandos", "/comandos", false),
        new("trdp", "TRDP Config", "/trdp", false),
        new("trdp-live", "TRDP Live", "/trdp-live", false),
        new("configuracion-red", "Red y DHCP", "/configuracion-red", false)
    ];
    public event Action? Changed;
    public static void EnsureSchema(ApsDbContext db)
    {
        db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS MaintenancePagePermissions (PageKey TEXT NOT NULL PRIMARY KEY, Allowed INTEGER NOT NULL)");
        db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS Roles (Name TEXT NOT NULL PRIMARY KEY COLLATE NOCASE)");
        db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS RolePagePermissions (RoleName TEXT NOT NULL, PageKey TEXT NOT NULL, Allowed INTEGER NOT NULL, PRIMARY KEY (RoleName, PageKey))");
        db.Database.ExecuteSqlRaw("INSERT OR IGNORE INTO Roles (Name) VALUES ('Administrador'), ('Mantenimiento')");
        if (db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sqlite_master WHERE type='table' AND name='Users'").Single() > 0)
            db.Database.ExecuteSqlRaw("INSERT OR IGNORE INTO Roles (Name) SELECT DISTINCT TRIM(Role) FROM Users WHERE TRIM(Role) <> ''");
        db.Database.ExecuteSqlRaw("INSERT OR IGNORE INTO RolePagePermissions (RoleName, PageKey, Allowed) SELECT 'Mantenimiento', PageKey, Allowed FROM MaintenancePagePermissions");
    }

    public async Task<List<string>> GetRolesAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Roles.OrderBy(x => x.Name).Select(x => x.Name).ToListAsync();
    }

    public async Task<string> CreateRoleAsync(string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 40 || name.Any(c => !char.IsLetterOrDigit(c) && c != ' ' && c != '-' && c != '_'))
            throw new ArgumentException("El rol debe tener entre 1 y 40 caracteres: letras, números, espacios, guiones o guiones bajos.");
        await using var db = await factory.CreateDbContextAsync();
        var roles = await db.Roles.ToListAsync();
        if (roles.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Ya existe un rol con ese nombre.");
        db.Roles.Add(new() { Name = name });
        await db.SaveChangesAsync();
        return name;
    }

    public async Task<Dictionary<string, bool>> GetAsync(string role = "Mantenimiento")
    {
        await using var db = await factory.CreateDbContextAsync();
        var exists = await db.Roles.AnyAsync(x => x.Name == role);
        var saved = await db.RolePagePermissions.AsNoTracking().Where(x => x.RoleName == role).ToDictionaryAsync(x => x.PageKey, x => x.Allowed);
        return Pages.ToDictionary(x => x.Key, x => exists && (role == "Administrador" || saved.GetValueOrDefault(x.Key, role == "Mantenimiento" && x.DefaultAllowed)));
    }

    public Task SetAsync(string key, bool allowed) => SetAsync("Mantenimiento", key, allowed);

    public async Task SetAsync(string role, string key, bool allowed)
    {
        if (!Pages.Any(x => x.Key == key)) throw new ArgumentException("Página no configurable.");
        await using var db = await factory.CreateDbContextAsync();
        if (string.Equals(role, "Administrador", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("El administrador conserva acceso completo.");
        if (!await db.Roles.AnyAsync(x => x.Name == role)) throw new ArgumentException("El rol no existe.");
        var row = await db.RolePagePermissions.FindAsync(role, key);
        if (row is null) { row = new() { RoleName = role, PageKey = key }; db.Add(row); }
        row.Allowed = allowed;
        await db.SaveChangesAsync();
        foreach (Action handler in Changed?.GetInvocationList() ?? [])
            try { handler(); } catch { /* A closed circuit does not undo a saved permission. */ }
    }
}

public sealed record PageAccessRequirement(string Key) : IAuthorizationRequirement;

public sealed class PageAccessHandler(PageAccessService access) : AuthorizationHandler<PageAccessRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PageAccessRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true) return;
        if (context.User.IsInRole("Administrador")) { context.Succeed(requirement); return; }
        var role = context.User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value;
        if (role is not null && (await access.GetAsync(role)).GetValueOrDefault(requirement.Key))
            context.Succeed(requirement);
    }
}
