using System.Security.Claims;
using ApsMonitor.Components.Pages;
using ApsMonitor.Components.Layout;
using ApsMonitor.Data;
using ApsMonitor.Models;
using ApsMonitor.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;

var root = Path.Combine(Path.GetTempPath(), $"aps-projects-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
async Task Reject(Func<Task> action, string message)
{
    try { await action(); }
    catch { checks++; return; }
    throw new Exception(message);
}
try
{
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:DefaultConnection"] = "Data Source=accounts.db;Pooling=False",
        ["Projects:RootDirectory"] = Path.Combine(root, "catalog")
    }).Build();
    var environment = new TestEnvironment(root);
    var application = new ApplicationDbContextFactory(configuration, environment);
    using (var db = application.CreateDbContext())
    {
        ApsDatabaseInitializer.Initialize(db);
        db.Signals.Add(new() { Tag = "ORIGINAL", Nombre = "Original", NodoNumero = 2 });
        db.ProjectBranding.Add(new() { Id = 1, ProjectName = "Instalación" });
        db.Users.Add(new() { Username = "operador", Nombre = "Operador", Role = "Mantenimiento", PasswordHash = "original-hash" });
        db.SaveChanges();
    }
    var projects = new ProjectDatabaseService(environment, NullLogger<ProjectDatabaseService>.Instance, configuration);
    projects.RestoreSelection();
    Check(projects.Current is null && projects.GetProjects().Count == 0,
        "No project is invented when the configured folder has no projects");
    Check(!Directory.Exists(projects.RootDirectory), "Startup does not create the project root or copy the installation database");
    Check(projects.GetCatalog().Count == 0, "A missing root also produces an empty folder catalog");
    await Reject(() => Task.FromResult(projects.CaptureFactory()), "Installation database used as an implicit project");
    var activeFactory = new ActiveProjectDbContextFactory(projects);
    var branding = new ProjectBrandingService(activeFactory, projects);
    await branding.LoadAsync();
    Check(branding.Current.ProjectName == "APS Monitor", "Project selector can load branding before any project is selected");
    var services = new ServiceCollection();
    services.AddSingleton(application);
    services.AddSingleton(projects);
    services.AddScoped(sp => new ApsDataService(projects.CaptureFactory(), application));
    services.AddHttpClient();
    services.Configure<InfluxOptions>(o => o.Enabled = false);
    services.AddSingleton<InfluxWriterService>();
    services.AddSingleton<MonitorStateService>();
    await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    var monitor = provider.GetRequiredService<MonitorStateService>();
    var loader = new ProjectLoadingService(projects, branding, monitor);
    var valenciaPath = Path.Combine(projects.RootDirectory, "Valencia", "Revisions", "1.0", "aps.db");
    Directory.CreateDirectory(Path.GetDirectoryName(valenciaPath)!);
    using (var db = new ProjectDbContextFactory($"Data Source={valenciaPath};Pooling=False").CreateDbContext())
    {
        ApsDatabaseInitializer.Initialize(db);
        db.Signals.Add(new() { Tag = "ORIGINAL", NodoNumero = 2 });
        db.ProjectBranding.Add(new() { Id = 1, ProjectName = "Proyecto Valencia" });
        db.SaveChanges();
    }
    const string initialId = "Valencia/Revisions/1.0/aps.db";
    Check(projects.GetProjects().Single().Id == initialId, "Only existing projects in the configured folder are listed");
    var emptyFolder = Path.Combine(projects.RootDirectory, "MediaDistancia");
    Directory.CreateDirectory(emptyFolder);
    var initialCatalog = projects.GetCatalog();
    Check(initialCatalog.Single(x => x.Name == "MediaDistancia").Revisions.Count == 0,
        "A newly created empty project folder appears without requiring revisions or a database");
    Check(projects.GetProjects().Count == 1, "An empty folder does not become a loadable database");
    var emptyRevision = Path.Combine(emptyFolder, "Revisions", "0.1");
    Directory.CreateDirectory(emptyRevision);
    var laterCatalog = projects.GetCatalog();
    Check(laterCatalog.Single(x => x.Name == "MediaDistancia").Revisions.Single().Version == "0.1" &&
        laterCatalog.Single(x => x.Name == "MediaDistancia").Revisions.Single().Databases.Count == 0,
        "A new revision appears even before its database has been added");
    Check(initialCatalog.Single(x => x.Name == "MediaDistancia").Revisions.Count == 0,
        "Refreshing discovery produces a new snapshot without changing previous selections");
    Directory.CreateDirectory(Path.Combine(projects.RootDirectory, ".uploads"));
    Check(projects.GetCatalog().All(x => x.Name != ".uploads"), "Internal upload storage is excluded from project choices");
    projects.RestoreSelection();
    Check(projects.Current is null, "An existing project is not automatically selected without user choice");
    await loader.LoadAsync(initialId);
    var oldData = new ApsDataService(projects.CaptureFactory(), application);
    await monitor.ConnectAsync();
    Check(monitor.IsConnected && monitor.Signals.Single().Tag == "ORIGINAL", "Monitor loads original project's signals");

    var sourcePath = Path.Combine(root, "source.db");
    var sourceFactory = new ProjectDbContextFactory($"Data Source={sourcePath};Pooling=False");
    using (var db = sourceFactory.CreateDbContext())
    {
        ApsDatabaseInitializer.Initialize(db);
        db.ProjectBranding.Add(new() { Id = 1, ProjectName = "Proyecto Norte" });
        db.Signals.Add(new() { Tag = "NORTE", Nombre = "Norte", NodoNumero = 3 });
        db.Windows.Add(new() { Nombre = "Ventana Norte" });
        db.Commands.Add(new() { Nombre = "Comando Norte" });
        db.TrdpConfigs.Add(new() { Nombre = "TRDP Norte" });
        db.Events.Add(new() { Mensaje = "Evento Norte" });
        db.SaveChanges();
        db.Database.ExecuteSqlRaw("PRAGMA wal_checkpoint(TRUNCATE)");
    }
    var sourceBytes = await File.ReadAllBytesAsync(sourcePath);
    await File.WriteAllBytesAsync(Path.Combine(emptyRevision, "aps.db"), sourceBytes);
    Check(projects.GetCatalog().Single(x => x.Name == "MediaDistancia").Revisions.Single().Databases.Single().Id ==
        "MediaDistancia/Revisions/0.1/aps.db", "Adding a database makes an existing empty revision loadable on the next scan");
    var imported = await projects.ImportAsync("MetroMadrid", "0.1", "../../Proyecto Norte.sqlite", new MemoryStream(sourceBytes));
    Check(projects.Current?.Id == initialId, "Import does not switch the current project");
    Check(projects.GetProjects().Count == 3 && imported.Name == "Proyecto Norte", "Imported project is listed with its saved name");
    Check(imported.ProjectFolder == "MetroMadrid" && imported.Revision == "0.1" &&
        imported.Id == "MetroMadrid/Revisions/0.1/aps.db" &&
        File.Exists(Path.Combine(root, "catalog", "MetroMadrid", "Revisions", "0.1", "aps.db")),
        "Project and revision determine the database location, regardless of upload filename");
    var sourceAfterImport = await File.ReadAllBytesAsync(sourcePath);
    Check(sourceBytes.SequenceEqual(sourceAfterImport), "Source database remains untouched");
    var revision02 = await projects.ImportAsync("MetroMadrid", "0.2", "source.db", new MemoryStream(sourceBytes));
    using (var db = new ProjectDbContextFactory(revision02.ConnectionString).CreateDbContext())
        await db.Signals.ExecuteUpdateAsync(s => s.SetProperty(x => x.Tag, "NORTE02"));
    var revision010 = Path.Combine(root, "catalog", "MetroMadrid", "Revisions", "0.10");
    Directory.CreateDirectory(revision010);
    await File.WriteAllBytesAsync(Path.Combine(revision010, "manual.sqlite3"), sourceBytes);
    var otherRevision = Path.Combine(root, "catalog", "Barcelona", "Revisions", "1.0");
    Directory.CreateDirectory(otherRevision);
    await File.WriteAllBytesAsync(Path.Combine(otherRevision, "manual.sqlite"), sourceBytes);
    Check(projects.GetProjects().Where(x => x.ProjectFolder == "MetroMadrid").Select(x => x.Revision)
        .SequenceEqual(new[] { "0.10", "0.2", "0.1" }), "Revisions on disk are discovered and sorted numerically");
    Check(projects.GetProjects().Any(x => x.ProjectFolder == "Barcelona" && x.Revision == "1.0"),
        "Other projects in the root are discovered independently");
    await Reject(() => projects.ImportAsync("MetroMadrid", "0.1", "source.db", new MemoryStream(sourceBytes)),
        "An existing revision was overwritten");
    foreach (var (name, revision) in new[] { ("../escape", "0.1"), ("MetroMadrid", "../0.1"),
        ("C:\\escape", "0.1"), ("CON", "0.1"), ("MetroMadrid", "invalid"), ("MetroMadrid", "0.1.") })
        await Reject(() => projects.ImportAsync(name, revision, "source.db", new MemoryStream(sourceBytes)),
            "Invalid project or revision path was accepted");

    int notifications = 0;
    projects.Changed += () => notifications++;
    await loader.LoadAsync(imported.Id);
    Check(projects.Current.Id == imported.Id && notifications == 1, "Switch publishes exactly one change to all views");
    Check(!monitor.IsConnected && monitor.Signals.Count == 0 && monitor.GetCurrentValues().Count == 0, "Switch disconnects and clears previous monitor state");
    Check(branding.Current.ProjectName == "Proyecto Norte", "Project identity follows selected database");
    await Reject(() => branding.SaveAsync("Old form", null, initialId), "A stale branding form modified the new project");
    Check(branding.Current.ProjectName == "Proyecto Norte", "Rejected stale branding preserves selected identity");
    var newData = new ApsDataService(projects.CaptureFactory(), application);
    Check((await newData.GetSignalsAsync()).Single().Tag == "NORTE", "Selected project's signals are loaded");
    Check((await newData.GetWindowsAsync()).Single().Nombre == "Ventana Norte", "Selected project's windows are loaded");
    Check((await newData.GetCommandsAsync()).Single().Nombre == "Comando Norte", "Selected project's commands are loaded");
    Check((await newData.GetEventsAsync()).Single().Mensaje == "Evento Norte", "Selected project's events are loaded");
    Check((await new TrdpConfigService(projects.CaptureFactory()).GetAllAsync()).Single().Nombre == "TRDP Norte", "TRDP follows selected project");
    Check((await newData.GetUserByUsernameAsync("operador"))?.PasswordHash == "original-hash", "Application accounts are retained after switching");
    var access = new PageAccessService(application);
    await access.SetAsync("comandos", true);
    Check((await access.GetAsync())["comandos"], "Permissions remain attached to the application database");
    await oldData.AddSignalAsync(new() { Tag = "LATE_ORIGINAL" });
    Check((await newData.GetSignalsAsync()).All(x => x.Tag != "LATE_ORIGINAL"), "A stale view cannot save into the newly selected database");
    Check((await oldData.GetSignalsAsync()).Any(x => x.Tag == "LATE_ORIGINAL"), "A finishing operation stays on its original database");
    await monitor.ConnectAsync();
    Check(monitor.Signals.Single().Tag == "NORTE", "Reconnecting loads the new project's signal mappings");

    var restarted = new ProjectDatabaseService(environment, NullLogger<ProjectDatabaseService>.Instance, configuration);
    restarted.RestoreSelection();
    Check(restarted.Current.Id == imported.Id, "Selection survives application restart");
    await loader.LoadAsync(imported.Id);
    Check(notifications == 1 && monitor.IsConnected, "Loading current project is a no-op");
    await Reject(() => loader.LoadAsync("../../source.db"), "Arbitrary paths must be rejected");
    Check(projects.Current.Id == imported.Id && monitor.IsConnected, "Failed selection leaves current project connected");
    await loader.LoadAsync(revision02.Id);
    Check((await new ApsDataService(projects.CaptureFactory(), application).GetSignalsAsync()).Single().Tag == "NORTE02",
        "Loading another revision reads its own database");
    await loader.LoadAsync(imported.Id);
    Check((await newData.GetSignalsAsync()).Single().Tag == "NORTE", "Switching revisions preserves previous version data");

    var count = projects.GetProjects().Count;
    await Reject(() => projects.ImportAsync("Invalid", "0.1", "bad.db", new MemoryStream("not sqlite"u8.ToArray())), "Invalid SQLite accepted");
    await Reject(() => projects.ImportAsync("Empty", "0.1", "empty.db", new MemoryStream()), "Empty database accepted");
    await Reject(() => projects.ImportAsync("WrongType", "0.1", "project.txt", new MemoryStream(sourceBytes)), "Wrong file type accepted");
    Check(projects.GetProjects().Count == count && !Directory.EnumerateFiles(Path.Combine(root, "catalog", ".uploads"), "*.upload*").Any(), "Failed imports leave no project or staging files");
    var incompatiblePath = Path.Combine(root, "incompatible.db");
    using (var db = new ProjectDbContextFactory($"Data Source={incompatiblePath};Pooling=False").CreateDbContext())
    {
        db.Database.EnsureCreated();
        db.Database.ExecuteSqlRaw("ALTER TABLE Signals DROP COLUMN Tag");
        db.Database.ExecuteSqlRaw("PRAGMA wal_checkpoint(TRUNCATE)");
    }
    await Reject(async () => { await using var input = File.OpenRead(incompatiblePath); await projects.ImportAsync("Incompatible", "0.1", "incompatible.db", input); }, "Incompatible schema accepted");
    Check(projects.Current.Id == imported.Id && projects.GetProjects().Count == count, "Incompatible import preserves selection and catalog");

    var legacyPath = Path.Combine(root, "legacy.db");
    using (var db = new ProjectDbContextFactory($"Data Source={legacyPath};Pooling=False").CreateDbContext())
    {
        db.Database.EnsureCreated();
        foreach (var sql in new[]
        {
            "ALTER TABLE Signals DROP COLUMN IsDeleted",
            "ALTER TABLE Windows DROP COLUMN FailuresConfigJson",
            "ALTER TABLE Windows DROP COLUMN AllowedRolesJson",
            "ALTER TABLE Windows DROP COLUMN PermitirMantenimiento",
            "ALTER TABLE TrdpConfigs DROP COLUMN TcmsOkJson",
            "ALTER TABLE Commands DROP COLUMN FrameType",
            "ALTER TABLE Commands DROP COLUMN Subcommand",
            "ALTER TABLE Commands DROP COLUMN SignalId",
            "ALTER TABLE Commands DROP COLUMN VariableValue",
            "DROP TABLE ProjectBranding",
            "PRAGMA wal_checkpoint(TRUNCATE)"
        }) db.Database.ExecuteSqlRaw(sql);
    }
    ProjectDatabase legacy;
    await using (var stream = File.OpenRead(legacyPath)) legacy = await projects.ImportAsync("Legacy", "0.1", "legacy.db", stream);
    await loader.LoadAsync(legacy.Id);
    Check(branding.Current.ProjectName == "APS Monitor" && branding.Current.LogoDataUrl is null,
        "A legacy project without branding resets the previous project's identity");
    using (var db = projects.CaptureFactory().CreateDbContext())
    {
        Check(!db.Signals.IgnoreQueryFilters().Any() && !db.Windows.Any() && !db.TrdpConfigs.Any() && !db.Commands.Any(),
            "Legacy columns are upgraded before loading the project");
    }

    await loader.LoadAsync(initialId);
    Check(branding.Current.ProjectName == "Proyecto Valencia" && (await oldData.GetSignalsAsync()).Count == 2, "Returning to an existing project preserves its data and branding");
    Check(notifications == 5, "Returning to original notifies connected views");
    await File.WriteAllTextAsync(Path.Combine(root, "catalog", "active-project.txt"), "missing.db");
    var missing = new ProjectDatabaseService(environment, NullLogger<ProjectDatabaseService>.Instance, configuration);
    missing.RestoreSelection();
    Check(missing.Current is null, "Missing saved project requires selecting an existing project");
    Check(!Directory.Exists(Path.Combine(projects.RootDirectory, "ProyectoOriginal")), "No replacement project is created for a missing selection");
    var pageAuth = typeof(Proyectos).GetCustomAttributes(true).OfType<IAuthorizeData>().ToArray();
    var policy = await AuthorizationPolicy.CombineAsync(new DefaultAuthorizationPolicyProvider(
        Microsoft.Extensions.Options.Options.Create(new AuthorizationOptions())), pageAuth);
    var authServices = new ServiceCollection().AddLogging().AddAuthorization().BuildServiceProvider();
    var authorization = authServices.GetRequiredService<IAuthorizationService>();
    foreach (var role in new[] { "Administrador", "Mantenimiento", "Operador", "Sin permisos de páginas" })
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test"));
        Check((await authorization.AuthorizeAsync(user, null, policy!)).Succeeded, $"Any authenticated role can load projects: {role}");
    }
    Check(!(await authorization.AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity()), null, policy!)).Succeeded,
        "Project loading still requires authentication");
    Check(monitor.TryBeginReprogramming(), "Reprogramming guard is acquired");
    await Reject(() => loader.LoadAsync(imported.Id), "Project switched during firmware transfer");
    Check(projects.Current?.Id == initialId, "Firmware transfer keeps the current project selected");

    var legacyDirectory = Path.Combine(root, "Projects");
    Directory.CreateDirectory(legacyDirectory);
    await File.WriteAllBytesAsync(Path.Combine(legacyDirectory, "OldProject.db"), sourceBytes);
    await File.WriteAllTextAsync(Path.Combine(legacyDirectory, "active-project.txt"), "OldProject.db");
    File.Delete(Path.Combine(root, "catalog", "active-project.txt"));
    var fresh = new ProjectDatabaseService(environment, NullLogger<ProjectDatabaseService>.Instance, configuration);
    var previousCount = fresh.GetProjects().Count;
    fresh.RestoreSelection();
    Check(fresh.Current is null, "Old installation selection is ignored");
    Check(fresh.GetProjects().Count == previousCount && !Directory.Exists(Path.Combine(projects.RootDirectory, "OldProject")),
        "Startup does not copy projects from the old installation directory");
    await File.WriteAllTextAsync(Path.Combine(projects.RootDirectory, "active-project.txt"), "default");
    fresh.RestoreSelection();
    Check(fresh.Current is null && fresh.GetProjects().All(x => x.Id != "default"), "Old synthetic default identifier is no longer a project");

    var uiRoot = Path.Combine(root, "ui-catalog");
    var uiConfiguration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Projects:RootDirectory"] = uiRoot }).Build();
    var uiProjects = new ProjectDatabaseService(environment, NullLogger<ProjectDatabaseService>.Instance, uiConfiguration);
    var uiBranding = new ProjectBrandingService(new ActiveProjectDbContextFactory(uiProjects), uiProjects);
    Directory.CreateDirectory(Path.Combine(uiRoot, "MediaDistancia"));
    var renderServices = new ServiceCollection();
    renderServices.AddLogging();
    renderServices.AddMudServices();
    renderServices.AddSingleton<IJSRuntime, PreviewJs>();
    renderServices.AddSingleton<NavigationManager, PreviewNavigation>();
    renderServices.AddSingleton(uiProjects);
    renderServices.AddSingleton(new ProjectLoadingService(uiProjects, uiBranding, monitor));
    renderServices.AddSingleton<ProjectNavigationService>();
    await using (var renderProvider = renderServices.BuildServiceProvider())
    await using (var renderer = new StaticHtmlRenderer(renderProvider, renderProvider.GetRequiredService<ILoggerFactory>()))
    {
        var rendered = await renderer.Dispatcher.InvokeAsync(() => renderer.BeginRenderingComponent(typeof(ProjectSelectorPreview), ParameterView.Empty));
        await rendered.QuiescenceTask;
        async Task<string> Html() => System.Net.WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString));
        async Task<string> Until(Func<string, bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            do
            {
                var html = await Html();
                if (condition(html)) return html;
                await Task.Delay(100);
            } while (DateTime.UtcNow < deadline);
            throw new Exception("Project selector did not refresh after changing folders on disk");
        }
        bool CanLoad(string html)
        {
            var button = System.Text.RegularExpressions.Regex.Match(html,
                "<button\\b(?<attributes>[^>]*)>(?:(?!</button>).)*?Cargar proyecto", System.Text.RegularExpressions.RegexOptions.Singleline);
            if (!button.Success) throw new Exception("Project load button was not rendered");
            return !button.Groups["attributes"].Value.Contains("disabled", StringComparison.OrdinalIgnoreCase);
        }
        var initialHtml = await Html();
        Check(initialHtml.Contains("MediaDistancia") && initialHtml.Contains("todavía no tiene revisiones"),
            "Razor selector displays an empty project folder with a useful status");
        Check(!CanLoad(initialHtml), "Razor selector cannot load an empty project");
        var uiRevision = Path.Combine(uiRoot, "MediaDistancia", "Revisions", "0.1");
        Directory.CreateDirectory(uiRevision);
        var revisionHtml = await Until(html => html.Contains("La revisión 0.1 todavía no tiene una base de datos"));
        Check(!CanLoad(revisionHtml), "Open Razor selector detects a newly created empty revision without reloading");
        await File.WriteAllBytesAsync(Path.Combine(uiRevision, "aps.db"), sourceBytes);
        var readyHtml = await Until(CanLoad);
        Check(!readyHtml.Contains("todavía no tiene una base de datos"), "Adding the database enables loading through the automatic refresh");
        File.Delete(Path.Combine(uiRevision, "aps.db"));
        await Until(html => !CanLoad(html));
        Check(uiProjects.Current is null, "Automatic catalog refresh never activates a project on its own");
        var redirect = await renderer.Dispatcher.InvokeAsync(() => renderer.BeginRenderingComponent(typeof(RedirectToProject), ParameterView.Empty));
        await redirect.QuiescenceTask;
        var redirectHtml = await renderer.Dispatcher.InvokeAsync(redirect.ToHtmlString);
        Check(redirectHtml.Contains("href=\"/proyectos\"") && redirectHtml.Contains("target=\"_top\""),
            "Project redirect always provides a native link if automatic navigation cannot complete");
    }

    int navigationCalls = 0;
    var successfulNavigation = new ProjectNavigationService(new NavigationJs((identifier, token, arguments) =>
    {
        Check(identifier == "apsNavigation.openProjects" && token.CanBeCanceled && arguments is [true],
            "Project navigation uses an awaited, cancellable browser request and preserves replacement behavior");
        navigationCalls++;
        return Task.CompletedTask;
    }));
    Check(await successfulNavigation.OpenAsync(replace: true), "Acknowledged browser navigation succeeds");
    var canceledNavigation = new ProjectNavigationService(new NavigationJs((_, _, _) => Task.FromException(new TaskCanceledException())));
    Check(!await canceledNavigation.OpenAsync(replace: false), "Lost interop acknowledgement does not become an unhandled circuit exception");
    var disconnectedNavigation = new ProjectNavigationService(new NavigationJs((_, _, _) => Task.FromException(new JSDisconnectedException("Tab disconnected"))));
    Check(!await disconnectedNavigation.OpenAsync(replace: false), "Disconnected browser is handled without crashing the circuit");
    using (var canceled = new CancellationTokenSource())
    {
        canceled.Cancel();
        Check(!await successfulNavigation.OpenAsync(replace: true, canceled.Token) && navigationCalls == 1,
            "A disposed redirect does not send another navigation request");
    }
    var stalledNavigation = new ProjectNavigationService(new NavigationJs((_, token, _) => Task.Delay(Timeout.Infinite, token)));
    Check(!await stalledNavigation.OpenAsync(replace: false).WaitAsync(TimeSpan.FromSeconds(8)),
        "A missing browser response has a bounded wait and leaves the manual link available");
    var brokenNavigation = new ProjectNavigationService(new NavigationJs((_, _, _) => Task.FromException(new JSException("Unexpected script error"))));
    await Reject(() => brokenNavigation.OpenAsync(replace: true), "Unexpected script errors were silently hidden");
    Console.WriteLine($"PASS: {checks} project loading checks");
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    // Only files created under our unique test directory are removed.
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}

sealed class TestEnvironment(string root) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development";
    public string ApplicationName { get; set; } = "ProjectLoadingChecks";
    public string ContentRootPath { get; set; } = root;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

sealed class PreviewJs : IJSRuntime
{
    public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => ValueTask.FromResult(default(T)!);
    public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(T)!);
}

sealed class NavigationJs(Func<string, CancellationToken, object?[]?, Task> invoke) : IJSRuntime
{
    public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, default, args);
    public async ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args)
    {
        await invoke(identifier, token, args);
        return default!;
    }
}

sealed class PreviewNavigation : NavigationManager
{
    public PreviewNavigation() => Initialize("http://localhost/", "http://localhost/proyectos");
}

sealed class ProjectSelectorPreview : ComponentBase
{
    protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
    {
        builder.OpenComponent<MudPopoverProvider>(0);
        builder.CloseComponent();
        builder.OpenComponent<Proyectos>(1);
        builder.CloseComponent();
    }
}
