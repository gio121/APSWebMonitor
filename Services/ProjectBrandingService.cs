using ApsMonitor.Data;
using Microsoft.EntityFrameworkCore;

namespace ApsMonitor.Services;

public sealed record ProjectBrandingView(string ProjectName, string? LogoDataUrl)
{
    public string ImageSource => LogoDataUrl ?? "SepsaMedha.jpg";
}

public sealed class ProjectBrandingService(IDbContextFactory<ApsDbContext> factory, ProjectDatabaseService? projects = null)
{
    public const int MaximumImageBytes = 2 * 1024 * 1024;
    public const int MaximumNameLength = 80;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _loaded;
    public ProjectBrandingView Current { get; private set; } = new("APS Monitor", null);
    public event Action? Changed;

    internal async Task ChangeProjectAsync(IDbContextFactory<ApsDbContext> target, Action activate)
    {
        await _gate.WaitAsync();
        try
        {
            await using var context = await target.CreateDbContextAsync();
            var saved = await context.ProjectBranding.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1);
            activate();
            Current = saved is null ? new("APS Monitor", null) : new(saved.ProjectName, saved.LogoDataUrl);
            _loaded = true;
        }
        finally { _gate.Release(); }
        foreach (Action subscriber in Changed?.GetInvocationList() ?? [])
            try { subscriber(); } catch { }
    }

    public static void EnsureSchema(ApsDbContext context) => context.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS ProjectBranding (
            Id INTEGER NOT NULL PRIMARY KEY,
            ProjectName TEXT NOT NULL,
            LogoDataUrl TEXT NULL
        )
        """);

    public async Task LoadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_loaded) return;
            if (projects is not null && projects.Current is null) return;
            await using var context = await factory.CreateDbContextAsync();
            var saved = await context.ProjectBranding.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1);
            if (saved is not null) Current = new(saved.ProjectName, saved.LogoDataUrl);
            _loaded = true;
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(string name, string? logoDataUrl, string? expectedProjectId = null,
        ProjectBrandingView? expectedBranding = null, string? expectedDraftPath = null)
    {
        name = name.Trim();
        if (name.Length is 0 or > MaximumNameLength)
            throw new ArgumentException(ApsMonitor.Resources.MonitorResources.Format("The name must contain 1 to {0} characters.", MaximumNameLength));
        if (logoDataUrl is not null)
        {
            if (logoDataUrl.Length > MaximumImageBytes * 4 / 3 + 128)
                throw new ArgumentException(ApsMonitor.Resources.MonitorResources.Text("The image exceeds 2 MiB."));
            int separator = logoDataUrl.IndexOf(',');
            if (separator < 0) throw new ArgumentException(ApsMonitor.Resources.MonitorResources.Text("Invalid image."));
            byte[] bytes;
            try { bytes = Convert.FromBase64String(logoDataUrl[(separator + 1)..]); }
            catch (FormatException) { throw new ArgumentException(ApsMonitor.Resources.MonitorResources.Text("Invalid image.")); }
            if (CreateImageDataUrl(bytes) != logoDataUrl) throw new ArgumentException(ApsMonitor.Resources.MonitorResources.Text("Invalid image format."));
        }
        await _gate.WaitAsync();
        try
        {
            if (expectedProjectId is not null && projects?.Current?.Id != expectedProjectId)
                throw new InvalidOperationException(ApsMonitor.Resources.MonitorResources.Text("The active project has changed. Reopen branding settings."));
            if (expectedDraftPath is not null && projects?.Current?.DatabasePath != expectedDraftPath)
                throw new InvalidOperationException(ApsMonitor.Resources.MonitorResources.Text("The active draft has changed. Reopen branding settings."));
            if (expectedBranding is not null && expectedBranding != Current)
                throw new InvalidOperationException(ApsMonitor.Resources.MonitorResources.Text("Another user has changed the branding. Reload the page before saving."));
            await using var context = await factory.CreateDbContextAsync();
            var saved = await context.ProjectBranding.SingleOrDefaultAsync(x => x.Id == 1);
            if (saved is null) { saved = new(); context.ProjectBranding.Add(saved); }
            saved.ProjectName = name;
            saved.LogoDataUrl = logoDataUrl;
            await context.SaveChangesAsync();
            Current = new(name, logoDataUrl);
            _loaded = true;
        }
        finally { _gate.Release(); }
        foreach (Action subscriber in Changed?.GetInvocationList() ?? [])
            try { subscriber(); } catch { /* Disconnected views cannot undo a saved setting. */ }
    }

    public static string CreateImageDataUrl(byte[] bytes)
    {
        if (bytes.Length is 0 or > MaximumImageBytes) throw new ArgumentException(ApsMonitor.Resources.MonitorResources.Text("Select an image up to 2 MiB."));
        string mime;
        if (bytes.Length >= 24 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) mime = "image/png";
        else if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF && bytes[^2] == 0xFF && bytes[^1] == 0xD9) mime = "image/jpeg";
        else if (bytes.Length >= 16 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8)) mime = "image/webp";
        else throw new ArgumentException(ApsMonitor.Resources.MonitorResources.Text("Unsupported format. Select a PNG, JPG or WebP image."));
        return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
    }
}
