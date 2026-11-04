using System.Globalization;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Http;
using ApsMonitor.Resources;

namespace ApsMonitor.Services;

public static class MonitorLocalization
{
    public static CultureInfo Culture { get; } = CultureInfo.GetCultureInfo("en-GB");
    public static CultureInfo SpanishCulture { get; } = CultureInfo.GetCultureInfo("es-ES");
    public static string HtmlLanguage => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
    public static string Text(string key) => MonitorResources.Text(key);

    public static void Configure(RequestLocalizationOptions options)
    {
        options.DefaultRequestCulture = new RequestCulture(Culture);
        options.SupportedCultures = [Culture, SpanishCulture];
        options.SupportedUICultures = [Culture, SpanishCulture];
        options.RequestCultureProviders.Clear();
        options.RequestCultureProviders.Add(new CookieRequestCultureProvider());
    }

    public static IResult ChangeCulture(HttpContext context, string culture, string? returnUrl)
    {
        if (culture is not ("en-GB" or "es-ES")) return Results.BadRequest("Unsupported language.");
        var target = string.IsNullOrEmpty(returnUrl) ? context.Request.PathBase + "/" : returnUrl;
        if (!target.StartsWith('/') || target.StartsWith("//") || target.Contains('\\') || target.Any(char.IsControl))
            return Results.BadRequest("Invalid return URL.");
        context.Response.Cookies.Append(CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)), new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddYears(1), HttpOnly = true, IsEssential = true,
                SameSite = SameSiteMode.Lax, Secure = context.Request.IsHttps,
                Path = context.Request.PathBase.HasValue ? context.Request.PathBase.Value : "/"
            });
        return Results.LocalRedirect(target);
    }

    // These are display labels only. Stored roles and element types remain compatible.
    public static string RoleName(string? role) => role switch
    {
        "Administrador" => Text("Administrator"),
        "Mantenimiento" => Text("Maintenance"),
        _ => role ?? ""
    };

    public static string ElementName(string type) => Text(type switch
    {
        "Etiqueta" => "Label", "Señal" => "Signal", "Comando" => "Command",
        "Caja" => "Box", "Barra" => "Progress bar", "Linea H" => "Horizontal line",
        "Linea V" => "Vertical line", "Dotline" => "Terminal line", "Diodo" => "Diode",
        "Resistencia" => "Resistor", "Bobina" => "Coil", "Descargador" => "Surge arrester",
        "Transformador" => "Transformer", "Fusible" => "Fuse", "Interruptor" => "Circuit breaker",
        "Seccionador" => "Disconnector", "Contactor V" => "Vertical contactor", "Borne" => "Terminal",
        "Rectificador" => "Rectifier", "Inversor" => "Inverter", "Convertidor" => "Converter",
        "Trifasico" => "Three-phase", "Ventilador" => "Fan", "Condensador" => "Capacitor",
        "ACpresence" => "AC presence", "Sinóptico" => "Synoptic view", _ => type
    });

    public static string? Description(XElement? element, string language) => element?.Elements("localized_string_t")
        .Where(s => (s.Element("langid")?.Value.Trim().Replace('_', '-') ?? "").Split('-')[0]
            .Equals(language, StringComparison.OrdinalIgnoreCase))
        .Select(s => s.Element("stringvalue")?.Value.Trim())
        .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));

    public static string EventStatus(string status) => status switch
    {
        "ENVIADO" => Text("SENT"), _ => status
    };

    public static string EnglishDescription(XElement? element, string fallback = "") =>
        Description(element, "en") ?? Description(element, "es") ?? element?.Elements("localized_string_t")
            .Select(s => s.Element("stringvalue")?.Value.Trim()).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? fallback;
}
