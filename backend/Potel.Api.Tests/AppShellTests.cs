using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;

namespace Potel.Api.Tests;

// Een eigen wwwroot met een nagebootste build, zodat de test niet afhangt van npm run build.
public class AppShellFactory : PortalFactory
{
    public const string Asset = "/assets/index-Ab12Cd34.js";
    readonly string webRoot = Path.Combine(Path.GetTempPath(), $"potel-wwwroot-{Guid.NewGuid():N}");

    public AppShellFactory()
    {
        Directory.CreateDirectory(Path.Combine(webRoot, "assets"));
        Directory.CreateDirectory(Path.Combine(webRoot, "icons"));
        File.WriteAllText(Path.Combine(webRoot, "index.html"), "<!doctype html><title>Potel</title><div id=\"root\"></div>");
        File.WriteAllText(Path.Combine(webRoot, "offline.html"), "<!doctype html><title>Je bent offline</title>");
        File.WriteAllText(Path.Combine(webRoot, "sw.js"), "const VERSION = 'test'");
        File.WriteAllText(Path.Combine(webRoot, "manifest.webmanifest"), "{\"name\":\"Potel\"}");
        File.WriteAllText(Path.Combine(webRoot, "favicon.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        File.WriteAllBytes(Path.Combine(webRoot, "icons", "icon-192.png"), [0x89, 0x50, 0x4E, 0x47]);
        File.WriteAllText(Path.Combine(webRoot, Asset.TrimStart('/')), "console.log('potel')");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseWebRoot(webRoot);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (Directory.Exists(webRoot)) Directory.Delete(webRoot, recursive: true);
    }
}

// Potel als app: het manifest, de iconen en de service worker komen met het juiste type en passende cacheregels van de server.
// De service worker zelf en de pagina's worden altijd nagevraagd, zodat een nieuwe versie meteen telt.
public class AppShellTests(AppShellFactory factory) : IClassFixture<AppShellFactory>
{
    async Task<HttpResponseMessage> GetAsync(string path)
    {
        var res = await factory.CreateClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return res;
    }

    static void AlwaysRevalidated(HttpResponseMessage res)
    {
        Assert.True(res.Headers.CacheControl?.NoCache, $"{res.RequestMessage?.RequestUri} hoort no-cache te hebben");
        Assert.Null(res.Headers.CacheControl?.MaxAge);
    }

    [Fact]
    public async Task The_service_worker_is_javascript_and_never_cached_long()
    {
        var res = await GetAsync("/sw.js");
        Assert.Equal("text/javascript", res.Content.Headers.ContentType?.MediaType);
        AlwaysRevalidated(res);
    }

    [Fact]
    public async Task The_manifest_has_its_own_type_and_is_revalidated()
    {
        var res = await GetAsync("/manifest.webmanifest");
        Assert.Equal("application/manifest+json", res.Content.Headers.ContentType?.MediaType);
        AlwaysRevalidated(res);
    }

    [Fact]
    public async Task Icons_are_png_and_may_be_cached_for_a_day()
    {
        var res = await GetAsync("/icons/icon-192.png");
        Assert.Equal("image/png", res.Content.Headers.ContentType?.MediaType);
        Assert.Equal(TimeSpan.FromDays(1), res.Headers.CacheControl?.MaxAge);
        Assert.Equal(TimeSpan.FromDays(1), (await GetAsync("/favicon.svg")).Headers.CacheControl?.MaxAge);
    }

    [Fact]
    public async Task Built_files_with_a_hash_are_cached_for_a_year()
    {
        var res = await GetAsync(AppShellFactory.Asset);
        Assert.Equal(TimeSpan.FromDays(365), res.Headers.CacheControl?.MaxAge);
        Assert.True(res.Headers.CacheControl?.Public);
        Assert.Contains("immutable", res.Headers.CacheControl?.Extensions.Select(e => e.Name) ?? []);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/uren")]
    [InlineData("/factuur?token=geheim")]
    [InlineData("/portaal?token=geheim")]
    [InlineData("/offline.html")]
    public async Task Pages_are_always_asked_again(string path)
    {
        var res = await GetAsync(path);
        Assert.Equal("text/html", res.Content.Headers.ContentType?.MediaType);
        AlwaysRevalidated(res);
    }

    // Een gebouwd bestand dat niet (meer) bestaat, is een 404 en niet de app-pagina: anders bewaart de service worker een pagina als script.
    [Fact]
    public async Task A_missing_built_file_is_not_answered_with_the_app_page()
    {
        var res = await factory.CreateClient().GetAsync("/assets/weg-0000.js");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task The_api_gets_none_of_these_cache_rules()
    {
        var res = await factory.CreateClient().GetAsync("/api/timer");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Null(res.Headers.CacheControl?.MaxAge);
        Assert.False(res.Headers.CacheControl?.Public ?? false);
    }
}

// Het manifest, de iconen en de service worker in de frontend zelf.
public class AppManifestTests
{
    static DirectoryInfo? Frontend()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "frontend", "public", "manifest.webmanifest"))) dir = dir.Parent;
        return dir is null ? null : new DirectoryInfo(Path.Combine(dir.FullName, "frontend"));
    }

    static (int Width, int Height) PngSize(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], bytes[..4]);
        int Read(int at) => (bytes[at] << 24) | (bytes[at + 1] << 16) | (bytes[at + 2] << 8) | bytes[at + 3];
        return (Read(16), Read(20));
    }

    [Fact]
    public void The_manifest_is_dutch_orange_standalone_and_its_icons_exist_in_every_size()
    {
        if (Frontend() is not { } frontend) return; // alleen de backend uitgecheckt
        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(frontend.FullName, "public", "manifest.webmanifest"))).RootElement;
        Assert.Equal("Potel", manifest.GetProperty("short_name").GetString());
        Assert.Equal("nl", manifest.GetProperty("lang").GetString());
        Assert.Equal("/", manifest.GetProperty("start_url").GetString());
        Assert.Equal("standalone", manifest.GetProperty("display").GetString());
        // Dezelfde oranje accentkleur als in de app (--primary in index.css).
        var css = File.ReadAllText(Path.Combine(frontend.FullName, "src", "index.css"));
        var primary = Regex.Match(css, @"--primary:\s*(#[0-9a-fA-F]{6})").Groups[1].Value;
        Assert.Equal(primary, manifest.GetProperty("theme_color").GetString());

        var icons = manifest.GetProperty("icons").EnumerateArray().ToList();
        foreach (var icon in icons)
        {
            var file = Path.Combine(frontend.FullName, "public", icon.GetProperty("src").GetString()!.TrimStart('/'));
            Assert.True(File.Exists(file), $"{file} ontbreekt");
            var size = PngSize(file);
            Assert.Equal(icon.GetProperty("sizes").GetString(), $"{size.Width}x{size.Height}");
        }
        Assert.Contains(icons, i => i.GetProperty("purpose").GetString() == "maskable" && i.GetProperty("sizes").GetString() == "512x512");
        Assert.Contains(icons, i => i.GetProperty("purpose").GetString() == "any" && i.GetProperty("sizes").GetString() == "192x192");
        Assert.Contains(icons, i => i.GetProperty("purpose").GetString() == "any" && i.GetProperty("sizes").GetString() == "512x512");
    }

    [Fact]
    public void The_page_links_the_manifest_and_an_icon_for_iphone()
    {
        if (Frontend() is not { } frontend) return;
        var html = File.ReadAllText(Path.Combine(frontend.FullName, "index.html"));
        Assert.Contains("<link rel=\"manifest\" href=\"/manifest.webmanifest\" />", html);
        Assert.Contains("name=\"theme-color\"", html);
        Assert.Contains("viewport-fit=cover", html);
        var apple = Regex.Match(html, "rel=\"apple-touch-icon\" href=\"([^\"]+)\"").Groups[1].Value;
        Assert.Equal((180, 180), PngSize(Path.Combine(frontend.FullName, "public", apple.TrimStart('/'))));
    }

    // De service worker laat /api en pagina's met rust en bewaart alleen gebouwde bestanden. In de browser nagekeken; dit bewaakt de regels in de code.
    [Fact]
    public void The_service_worker_never_keeps_api_answers_or_pages()
    {
        if (Frontend() is not { } frontend) return;
        var sw = File.ReadAllText(Path.Combine(frontend.FullName, "src", "sw.js"));
        Assert.Contains("url.pathname.startsWith('/api/')", sw);
        Assert.Contains("if (request.method !== 'GET') return", sw);
        Assert.Matches(@"request\.mode === 'navigate'\) \{\s*event\.respondWith\(fetch\(request\)\.catch", sw);
        // Alleen bestanden uit /assets komen er later bij, en nooit een HTML-pagina.
        var puts = Regex.Matches(sw, @"cache\.put\(");
        Assert.Single(puts);
        var guard = sw[..puts[0].Index];
        Assert.Contains("url.pathname.startsWith('/assets/') && !type.startsWith('text/html')", guard[guard.LastIndexOf("if (", StringComparison.Ordinal)..]);
        // Een nieuwe versie heeft een eigen cache en ruimt de oude op.
        Assert.Contains("const CACHE = PREFIX + VERSION", sw);
        Assert.Contains("caches.delete(key)", sw);
    }
}
