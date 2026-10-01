using ApsMonitor.Data;
using Microsoft.EntityFrameworkCore;

namespace ApsMonitor.Services;

public sealed record ProjectBrandingView(string ProjectName, string? LogoDataUrl)
{
    public string ImageSource => LogoDataUrl ?? "SepsaMedha.jpg";
}

public sealed class ProjectBrandingService(IDbContextFactory<ApsDbContext> factory)
{
    public const int MaximumImageBytes = 2 * 1024 * 1024;
    public const int MaximumNameLength = 80;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _loaded;
    public ProjectBrandingView Current { get; private set; } = new("APS Monitor", null);
    public event Action? Changed;

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
            await using var context = await factory.CreateDbContextAsync();
            var saved = await context.ProjectBranding.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1);
            if (saved is not null) Current = new(saved.ProjectName, saved.LogoDataUrl);
            _loaded = true;
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(string name, string? logoDataUrl)
    {
        name = name.Trim();
        if (name.Length is 0 or > MaximumNameLength)
            throw new ArgumentException($"El nombre debe tener entre 1 y {MaximumNameLength} caracteres.");
        if (logoDataUrl is not null)
        {
            if (logoDataUrl.Length > MaximumImageBytes * 4 / 3 + 128)
                throw new ArgumentException("La imagen supera 2 MiB.");
            int separator = logoDataUrl.IndexOf(',');
            if (separator < 0) throw new ArgumentException("Imagen no válida.");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(logoDataUrl[(separator + 1)..]); }
            catch (FormatException) { throw new ArgumentException("Imagen no válida."); }
            if (CreateImageDataUrl(bytes) != logoDataUrl) throw new ArgumentException("Formato de imagen no válido.");
        }
        await _gate.WaitAsync();
        try
        {
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
        if (bytes.Length is 0 or > MaximumImageBytes) throw new ArgumentException("Seleccione una imagen de hasta 2 MiB.");
        string mime;
        if (bytes.Length >= 24 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) mime = "image/png";
        else if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF && bytes[^2] == 0xFF && bytes[^1] == 0xD9) mime = "image/jpeg";
        else if (bytes.Length >= 16 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8)) mime = "image/webp";
        else throw new ArgumentException("Formato no admitido. Seleccione una imagen PNG, JPG o WebP.");
        return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
    }
}
