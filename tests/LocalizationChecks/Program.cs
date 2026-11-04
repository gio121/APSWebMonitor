using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ApsMonitor.Models;
using ApsMonitor.Resources;
using ApsMonitor.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using ApsMonitor.Components.Layout;
using MudBlazor.Services;
using System.Net;
using System.Reflection;
using Microsoft.Data.Sqlite;

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Interlocked.Increment(ref checks);
}

var previousCulture = CultureInfo.CurrentCulture;
var previousUiCulture = CultureInfo.CurrentUICulture;
try
{
    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-ES");
    CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("es-ES");
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddLocalization();
    services.AddMudServices();
    services.AddSingleton<NavigationManager, TestNavigation>();
    services.AddSingleton<IJSRuntime, TestJs>();
    using var provider = services.BuildServiceProvider();
    var localizer = provider.GetRequiredService<IStringLocalizer<MonitorResources>>();
    Check(!localizer["Save"].ResourceNotFound && localizer["Save"] == "Guardar", "Spanish UI culture resolves the Spanish resource catalogue.");
    Check(!localizer["Firmware update"].ResourceNotFound, "Navigation labels resolve from the embedded resource catalogue.");

    var repository = new DirectoryInfo(AppContext.BaseDirectory);
    while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "ApsMonitor.csproj"))) repository = repository.Parent;
    var root = repository?.FullName ?? throw new DirectoryNotFoundException("Cannot locate the monitor sources.");
    var resourceFile = XDocument.Load(Path.Combine(root, "Resources/MonitorResources.resx"));
    var resourceKeys = resourceFile.Root!.Elements("data").Select(d => d.Attribute("name")!.Value).ToHashSet(StringComparer.Ordinal);
    var spanishFile = XDocument.Load(Path.Combine(root, "Resources/MonitorResources.es.resx"));
    var spanishResources = spanishFile.Root!.Elements("data").ToDictionary(d => d.Attribute("name")!.Value, d => d.Element("value")!.Value);
    Check(resourceKeys.SetEquals(spanishResources.Keys), "English and Spanish catalogues have identical coverage.");
    Check(resourceKeys.Count == resourceKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "Resource keys do not collide when compiled without case sensitivity.");
    foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "Components"), "*.razor", SearchOption.AllDirectories))
        foreach (Match match in Regex.Matches(File.ReadAllText(file), "\\bUi\\[\"((?:\\\\.|[^\"\\\\])*)\""))
            Check(resourceKeys.Contains(Regex.Unescape(match.Groups[1].Value)) && !localizer[Regex.Unescape(match.Groups[1].Value)].ResourceNotFound,
                $"Resource lookup resolves: {match.Groups[1].Value}");

    foreach (var directory in new[] { "Components", "Services", "Data", "Models" })
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, directory), "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal)))
            foreach (Match match in Regex.Matches(File.ReadAllText(file), "MonitorResources\\.(?:Text|Format)\\(\\s*\"((?:\\\\.|[^\"\\\\])*)\""))
                Check(resourceKeys.Contains(Regex.Unescape(match.Groups[1].Value)),
                    $"Static resource lookup resolves in {Path.GetFileName(file)}: {match.Groups[1].Value}");

    foreach (var entry in resourceFile.Root.Elements("data"))
    {
        var value = entry.Element("value")!.Value;
        if (Regex.IsMatch(value, @"(?<!\{)\{\d"))
            Check(System.Text.CompositeFormat.Parse(value).MinimumArgumentCount == System.Text.CompositeFormat.Parse(spanishResources[entry.Attribute("name")!.Value]).MinimumArgumentCount,
                $"Formatted translations preserve arguments: {entry.Attribute("name")!.Value}");
    }

    var options = new RequestLocalizationOptions();
    MonitorLocalization.Configure(options);
    Check(options.SupportedCultures!.Select(c => c.Name).SequenceEqual(new[] { "en-GB", "es-ES" }) && options.SupportedUICultures!.Count == 2, "English and Spanish cultures are supported.");
    Check(options.RequestCultureProviders.Single() is CookieRequestCultureProvider, "An explicit browser preference controls the language.");
    var context = new DefaultHttpContext();
    context.Request.Headers.AcceptLanguage = "es-ES,es;q=0.9";
    context.Request.QueryString = new QueryString("?culture=es-ES&ui-culture=es-ES");
    var middleware = new RequestLocalizationMiddleware(_ =>
    {
        Check(CultureInfo.CurrentCulture.Name == "en-GB" && CultureInfo.CurrentUICulture.Name == "en-GB", "English remains the default until a language is explicitly selected.");
        Check(1234.5.ToString("N1") == "1,234.5", "English number formatting uses a decimal point and comma thousands separator.");
        Check(new DateTime(2026, 11, 3).ToString("d") == "03/11/2026", "English dates retain the day/month/year convention.");
        return Task.CompletedTask;
    }, Options.Create(options), NullLoggerFactory.Instance);
    await middleware.Invoke(context);

    async Task VerifyRequest(string culture)
    {
        var request = new DefaultHttpContext();
        request.Request.Headers.Cookie = CookieRequestCultureProvider.DefaultCookieName + "=" + CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture));
        await new RequestLocalizationMiddleware(async _ =>
        {
            await Task.Yield();
            var expected = culture == "es-ES" ? "es-ES" : "en-GB";
            Check(CultureInfo.CurrentCulture.Name == expected && CultureInfo.CurrentUICulture.Name == expected, "Each request uses its own selected culture.");
            Check(localizer["Save"] == (culture == "es-ES" ? "Guardar" : "Save"), "Each browser gets its own translated labels.");
            Check(1234.5.ToString("N1") == (culture == "es-ES" ? "1.234,5" : "1,234.5"), "Numbers follow the selected culture.");
        }, Options.Create(options), NullLoggerFactory.Instance).Invoke(request);
    }
    await Task.WhenAll(VerifyRequest("es-ES"), VerifyRequest("en-GB"), VerifyRequest("es-ES"), VerifyRequest("en-GB"));
    await VerifyRequest("fr-FR");

    var cookieContext = new DefaultHttpContext { RequestServices = provider };
    cookieContext.Request.Scheme = "https";
    cookieContext.Request.PathBase = "/monitor-app";
    await MonitorLocalization.ChangeCulture(cookieContext, "es-ES", "/monitor-app/ventanas?mode=live#details").ExecuteAsync(cookieContext);
    Check(cookieContext.Response.StatusCode == 302 && cookieContext.Response.Headers.Location == "/monitor-app/ventanas?mode=live#details", "Switching returns to the same local page, query and fragment.");
    var cookie = cookieContext.Response.Headers.SetCookie.ToString();
    Check(cookie.Contains("c%3Des-ES%7Cuic%3Des-ES") && cookie.Contains("expires=", StringComparison.OrdinalIgnoreCase) && cookie.Contains("path=/monitor-app"), "The selected language persists in a browser cookie and respects the application base path.");
    Check(cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase) && cookie.Contains("secure", StringComparison.OrdinalIgnoreCase), "The preference cookie uses the request's security settings.");
    foreach (var target in new[] { "https://example.com", "//example.com", "/\\example.com", "/monitor\r\nLocation: https://example.com" })
        Check(MonitorLocalization.ChangeCulture(new DefaultHttpContext(), "es-ES", target) is Microsoft.AspNetCore.Http.IStatusCodeHttpResult { StatusCode: 400 }, "External and malformed return URLs are rejected.");
    Check(MonitorLocalization.ChangeCulture(new DefaultHttpContext(), "fr-FR", "/") is Microsoft.AspNetCore.Http.IStatusCodeHttpResult { StatusCode: 400 }, "Unsupported languages are rejected.");

    foreach (var language in new[] { "en-GB", "es-ES" })
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => WebUtility.HtmlDecode((await renderer.RenderComponentAsync<MonitorLanguageToggle>()).ToHtmlString()));
        Check(html.Contains("data-language-switch") && html.Contains(language == "es-ES" ? "Cambiar a inglés" : "Switch to Spanish"), "The compact button renders an accessible target-language label.");
        var toggle = new MonitorLanguageToggle();
        var navigation = (TestNavigation)provider.GetRequiredService<NavigationManager>();
        typeof(MonitorLanguageToggle).GetProperty("Navigation", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(toggle, navigation);
        typeof(MonitorLanguageToggle).GetMethod("SwitchLanguage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(toggle, null);
        Check(navigation.ForceLoad && navigation.Destination!.Contains(language == "es-ES" ? "culture=en-GB" : "culture=es-ES"), "Clicking switches to the other language with a new server circuit.");
        Check(navigation.Destination!.Contains("returnUrl=%2Fmonitor-app%2Fmonitor%3Fview%3Dlive%23details"), "The button preserves the current page when hosted below a base path.");
    }
    CultureInfo.CurrentUICulture = MonitorLocalization.Culture;

    XElement Description(params (string Language, string Text)[] labels) => new("description", labels.Select(l =>
        new XElement("localized_string_t", new XElement("langid", l.Language), new XElement("stringvalue", l.Text))));
    Check(MonitorLocalization.EnglishDescription(Description(("es", "Español"), ("en", "English"))) == "English", "English wins over an earlier Spanish description.");
    Check(MonitorLocalization.EnglishDescription(Description(("ES", "Español"), ("EN_us", " Regional English "))) == "Regional English", "Regional English tags are matched without case sensitivity and whitespace is trimmed.");
    Check(MonitorLocalization.EnglishDescription(Description(("en", " "), ("es", "Original"))) == "Original", "Empty English descriptions fall back to available content.");
    Check(MonitorLocalization.EnglishDescription(Description(("de", "Original"))) == "Original", "Other original languages remain readable.");
    Check(MonitorLocalization.EnglishDescription(null, "TAG") == "TAG", "Missing descriptions fall back to a stable identifier.");
    Check(MonitorLocalization.Description(Description(("es", "Español"), ("en", "English")), "es") == "Español", "The original Spanish description can still be stored separately.");

    var analyzer = new SessionAnalyzerService();
    var cmfx = """
        <root><node_t><pte_address_node_number>2</pte_address_node_number><value_elements>
        <value_element_t><keyname>status</keyname><value><valtype>BYTE</valtype></value><info><info_elements>
        <info_value_element_t><value>0</value><value_description>
        <localized_string_t><langid>es</langid><stringvalue>Marcha</stringvalue></localized_string_t>
        <localized_string_t><langid>en-GB</langid><stringvalue>Running</stringvalue></localized_string_t>
        </value_description></info_value_element_t></info_elements></info></value_element_t>
        </value_elements></node_t></root>
        """;
    Check(analyzer.LoadConfig(Encoding.UTF8.GetBytes(cmfx)).Single().BitNames[0] == "Running", "Session analyser bit labels prefer English too.");

    Check(MonitorLocalization.RoleName("Administrador") == "Administrator" && MonitorLocalization.RoleName("Mantenimiento") == "Maintenance", "Built-in roles have English display names.");
    Check(MonitorLocalization.RoleName("Operador personalizado") == "Operador personalizado", "Custom role names remain unchanged.");
    var element = JsonSerializer.Deserialize<SinopticoElement>("{\"Type\":\"Señal\",\"Text\":\"Texto propio\"}")!;
    Check(element.Type == "Señal" && element.Text == "Texto propio" && MonitorLocalization.ElementName(element.Type) == "Signal", "Legacy synoptic values and authored text survive deserialisation.");
    Check(SinopticoIconProvider.GetIcon(element.Type) != SinopticoIconProvider.GetIcon("missing"), "Legacy signal element keys still resolve their icons.");
    Check(MonitorLocalization.EventStatus("ENVIADO") == "SENT", "Legacy event statuses display in English.");
    Check(PageAccessService.Pages.Single(p => p.Key == "ventanas").Path == "/ventanas", "Existing routes and permission identifiers are preserved.");
    Check(MonitorLocalization.HtmlLanguage == "en", "English documents declare the current language.");
    CultureInfo.CurrentUICulture = MonitorLocalization.SpanishCulture;
    Check(MonitorLocalization.HtmlLanguage == "es" && MonitorLocalization.RoleName("Administrador") == "Administrador", "Spanish documents and role labels reflect the selected language.");
    Check(PageAccessService.Pages.Single(p => p.Key == "ventanas").Name == "Ventanas", "Permission labels follow the current browser culture without changing their keys.");
    var databasePath = Path.Combine(Path.GetTempPath(), $"aps-language-hash-{Guid.NewGuid():N}.db");
    var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
    try
    {
        using (var database = new SqliteConnection(connectionString))
        {
            database.Open();
            using var setup = database.CreateCommand();
            setup.CommandText = "CREATE TABLE Signals (Id INTEGER, Nombre TEXT, ValorActual REAL); CREATE TABLE Windows (Id INTEGER, Nombre TEXT); CREATE TABLE Commands (Id INTEGER, Nombre TEXT); INSERT INTO Signals VALUES (1, 'Original signal', 0); INSERT INTO Windows VALUES (1, 'Original window'); INSERT INTO Commands VALUES (1, 'Original command');";
            setup.ExecuteNonQuery();
        }
        var hashMethod = typeof(ProjectDatabaseService).GetMethod("ConfigurationHash", BindingFlags.NonPublic | BindingFlags.Static)!;
        string Hash() => (string)hashMethod.Invoke(null, new object[] { connectionString })!;
        CultureInfo.CurrentUICulture = MonitorLocalization.Culture;
        var originalHash = Hash();
        CultureInfo.CurrentUICulture = MonitorLocalization.SpanishCulture;
        Check(Hash() == originalHash, "Changing the interface language cannot mark the project source or draft as modified.");
        using (var database = new SqliteConnection(connectionString))
        {
            database.Open();
            using var edit = database.CreateCommand();
            edit.CommandText = "UPDATE Signals SET Nombre = 'Edited signal' WHERE Id = 1";
            edit.ExecuteNonQuery();
        }
        var editedHash = Hash();
        Check(editedHash != originalHash, "Real signal edits are still detected when the interface is Spanish.");
        CultureInfo.CurrentUICulture = MonitorLocalization.Culture;
        Check(Hash() == editedHash, "The same edited configuration has the same fingerprint in both languages.");
    }
    finally { File.Delete(databasePath); }
    foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "Components"), "*.razor", SearchOption.AllDirectories))
        Check(!Regex.IsMatch(File.ReadAllText(file), @"\[\s*ApsMonitor\.Resources\.MonitorResources\.(?:Text|Format)\("), "Dialog parameter and data dictionary keys cannot be translated: " + Path.GetFileName(file));
    Console.WriteLine($"PASS: {checks} localization, culture, resource, import and compatibility checks.");
}
finally
{
    CultureInfo.CurrentCulture = previousCulture;
    CultureInfo.CurrentUICulture = previousUiCulture;
}

sealed class TestNavigation : NavigationManager
{
    public string? Destination { get; private set; }
    public bool ForceLoad { get; private set; }
    public TestNavigation() => Initialize("https://localhost/monitor-app/", "https://localhost/monitor-app/monitor?view=live#details");
    protected override void NavigateToCore(string uri, bool forceLoad) { Destination = uri; ForceLoad = forceLoad; }
    protected override void NavigateToCore(string uri, NavigationOptions options) { Destination = uri; ForceLoad = options.ForceLoad; }
}
sealed class TestJs : IJSRuntime
{
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
}
