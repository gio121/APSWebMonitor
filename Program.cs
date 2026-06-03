using ApsMonitor.Components;
using MudBlazor.Services;
using Microsoft.EntityFrameworkCore;
using ApsMonitor.Data;
using ApsMonitor.Services;
using Microsoft.AspNetCore.Components.Authorization;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices();

builder.Services.AddDbContextFactory<ApsDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=aps.db"));

builder.Services.AddScoped<ApsDataService>();
builder.Services.AddScoped<SessionStateService>();

// Authentication & Authorization
builder.Services.AddAuthentication();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<CustomAuthenticationStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<CustomAuthenticationStateProvider>());
builder.Services.AddAuthorization();

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

        // --- Migrar columna FailuresConfigJson en Windows ---
        command.CommandText = "PRAGMA table_info(Windows)";
        using var reader = command.ExecuteReader();
        bool hasFailuresColumn = false;
        bool hasPermitirColumn = false;
        while (reader.Read()) 
        { 
            var colName = reader.GetString(1);
            if (colName == "FailuresConfigJson") { hasFailuresColumn = true; } 
            if (colName == "PermitirMantenimiento") { hasPermitirColumn = true; } 
        }
        reader.Close();
        
        if (!hasFailuresColumn) 
        { 
            command.CommandText = "ALTER TABLE Windows ADD COLUMN FailuresConfigJson TEXT NOT NULL DEFAULT '[]'"; 
            command.ExecuteNonQuery(); 
        }

        // --- Migrar columna PermitirMantenimiento en Windows ---
        if (!hasPermitirColumn)
        {
            command.CommandText = "ALTER TABLE Windows ADD COLUMN PermitirMantenimiento INTEGER NOT NULL DEFAULT 0";
            command.ExecuteNonQuery();
        }

        // --- Crear tabla Users si no existe ---
        command.CommandText = @"CREATE TABLE IF NOT EXISTS Users (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Username TEXT NOT NULL,
            PasswordHash TEXT NOT NULL,
            Nombre TEXT NOT NULL,
            Role TEXT NOT NULL
        )";
        command.ExecuteNonQuery();

        // --- Seeding: asegurar usuarios por defecto ---
        EnsureDefaultUser(command, "admin", "admin", "Administrador", "Administrador");
        EnsureDefaultUser(command, "mantenimiento", "mantenimiento", "Mantenimiento", "Mantenimiento");
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
    .AddInteractiveServerRenderMode()
    .AllowAnonymous();

app.Run();

static void EnsureDefaultUser(System.Data.Common.DbCommand command, string username, string password, string nombre, string role)
{
    command.Parameters.Clear();
    command.CommandText = "SELECT COUNT(*) FROM Users WHERE Username = $username";
    AddParameter(command, "$username", username);

    var userExists = Convert.ToInt64(command.ExecuteScalar()) > 0;
    if (userExists)
        return;

    command.Parameters.Clear();
    command.CommandText = @"INSERT INTO Users (Username, PasswordHash, Nombre, Role)
        VALUES ($username, $passwordHash, $nombre, $role)";

    AddParameter(command, "$username", username);
    AddParameter(command, "$passwordHash", PasswordHasher.Hash(password));
    AddParameter(command, "$nombre", nombre);
    AddParameter(command, "$role", role);
    command.ExecuteNonQuery();

    Console.WriteLine($"Usuario por defecto creado: {username}");
}

static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
{
    var parameter = command.CreateParameter();
    parameter.ParameterName = name;
    parameter.Value = value;
    command.Parameters.Add(parameter);
}
