using ApsMonitor.Components;
using MudBlazor.Services;
using Microsoft.EntityFrameworkCore;
using ApsMonitor.Data;
using ApsMonitor.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices();

builder.Services.AddDbContextFactory<ApsDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=aps.db"));

builder.Services.AddScoped<ApsDataService>();
builder.Services.AddScoped<SessionStateService>();

var app = builder.Build();

// Inicializar la base de datos
using (var scope = app.Services.CreateScope())
{
    var contextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApsDbContext>>();
    using var context = contextFactory.CreateDbContext();
    context.Database.EnsureCreated();

    // Migración manual de columnas
    try
    {
        using var connection = context.Database.GetDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(Windows)";
        using var reader = command.ExecuteReader();
        bool hasColumn = false;
        while (reader.Read()) { if (reader.GetString(1) == "FailuresConfigJson") { hasColumn = true; break; } }
        reader.Close();
        if (!hasColumn) { command.CommandText = "ALTER TABLE Windows ADD COLUMN FailuresConfigJson TEXT NOT NULL DEFAULT '[]'"; command.ExecuteNonQuery(); }
    }
    catch (Exception ex)
    {
        Console.WriteLine("Error al migrar DB manual: " + ex.Message);
    }
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
