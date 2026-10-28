using ApsMonitor.Components;
using MudBlazor.Services;
using Microsoft.EntityFrameworkCore;
using ApsMonitor.Data;
using ApsMonitor.Services;
using Microsoft.AspNetCore.Components.Authorization;

// Cerrar instancias previas huerfanas en segundo plano para liberar los puertos (7211, 5005, etc.)
try
{
    var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
    var existingProcesses = System.Diagnostics.Process.GetProcessesByName(currentProcess.ProcessName)
        .Where(p => p.Id != currentProcess.Id)
        .ToList();
    foreach (var proc in existingProcesses)
    {
        try { proc.Kill(); proc.WaitForExit(1000); } catch { }
    }
}
catch { }

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices();

builder.Services.AddSingleton<ApplicationDbContextFactory>();
builder.Services.AddSingleton<ProjectDatabaseService>();
builder.Services.AddSingleton<IDbContextFactory<ApsDbContext>, ActiveProjectDbContextFactory>();
builder.Services.AddSingleton<ProjectLoadingService>();
builder.Services.AddScoped<ProjectNavigationService>();

builder.Services.AddScoped(sp => new ApsDataService(
    sp.GetRequiredService<ProjectDatabaseService>().CaptureFactory(),
    sp.GetRequiredService<ApplicationDbContextFactory>()));
builder.Services.AddSingleton<ProjectBrandingService>();
builder.Services.AddSingleton(sp => new PageAccessService(sp.GetRequiredService<ApplicationDbContextFactory>()));
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, PageAccessHandler>();
builder.Services.AddScoped<SessionStateService>();
builder.Services.AddScoped<MonitorThemeService>();
builder.Services.AddScoped<SessionAnalyzerService>();
builder.Services.AddScoped(sp => new TrdpConfigService(sp.GetRequiredService<ProjectDatabaseService>().CaptureFactory()));
builder.Services.AddHttpClient<SepsaProtocolClient>();
builder.Services.AddHttpClient("SepsaMonitor"); // cliente nombrado para MonitorStateService (Singleton)
builder.Services.AddHttpClient("TrdpBackend");  // cliente nombrado para TrdpBackendService
builder.Services.AddScoped<TrdpBackendService>();
builder.Services.AddScoped<TrdpWebSocketService>();
builder.Services.AddScoped<TrdpPcapParserService>();
builder.Services.AddScoped<TrdpSnifferService>();
builder.Services.AddSingleton<MonitorStateService>();
builder.Services.AddSingleton<ReprogrammingService>();
builder.Services.AddSingleton<ISftpFirmwareUploader, SftpFirmwareUploader>();
builder.Services.AddSingleton<NetworkAdapterService>();
builder.Services.AddSingleton<DhcpServerService>();
builder.Services.AddSingleton<PacketSenderService>();
builder.Services.Configure<InfluxOptions>(builder.Configuration.GetSection("InfluxDB"));
builder.Services.AddHttpClient("InfluxDB", client => client.Timeout = TimeSpan.FromSeconds(5))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<InfluxWriterService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<InfluxWriterService>());

// Authentication & Authorization
builder.Services.AddAuthentication();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped(sp => new CustomAuthenticationStateProvider(
    sp.GetRequiredService<Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage.ProtectedSessionStorage>(),
    sp.GetRequiredService<ApplicationDbContextFactory>()));
builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<CustomAuthenticationStateProvider>());
builder.Services.AddAuthorization(options =>
{
    foreach (var page in PageAccessService.Pages)
        options.AddPolicy("Page:" + page.Key, policy => policy.RequireAuthenticatedUser().AddRequirements(new PageAccessRequirement(page.Key)));
});

var app = builder.Build();

// Keep application accounts and permissions in the original database.
using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<ApplicationDbContextFactory>();
    using var context = factory.CreateDbContext();
    ApsDatabaseInitializer.Initialize(context);
}
app.Services.GetRequiredService<ProjectDatabaseService>().RestoreSelection();

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
