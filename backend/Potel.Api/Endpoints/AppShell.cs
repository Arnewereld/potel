using Microsoft.AspNetCore.StaticFiles;

namespace Potel.Api.Endpoints;

// De gebouwde frontend uit wwwroot, met cacheregels die passen bij Potel als app op je telefoon (manifest en service worker).
public static class AppShell
{
    public const string Immutable = "public, max-age=31536000, immutable";
    public const string Revalidate = "no-cache";
    public const string OneDay = "public, max-age=86400";

    public static StaticFileOptions Options()
    {
        var types = new FileExtensionContentTypeProvider();
        types.Mappings[".webmanifest"] = "application/manifest+json";
        return new StaticFileOptions
        {
            ContentTypeProvider = types,
            OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = CacheControl(ctx.Context.Request.Path),
        };
    }

    // Gebouwde bestanden met een hash in de naam veranderen nooit en mogen een jaar blijven. De pagina zelf, de service worker,
    // het manifest en de offlinepagina vraagt de browser elke keer na, zodat een nieuwe versie meteen telt. Iconen mogen een dag blijven.
    public static string CacheControl(PathString path)
    {
        if (path.StartsWithSegments("/assets")) return Immutable;
        if (path.StartsWithSegments("/icons") || path.Equals("/favicon.svg", StringComparison.OrdinalIgnoreCase)) return OneDay;
        return Revalidate;
    }
}
