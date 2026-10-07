namespace Potel.Api.Endpoints;

// Het openbare adres van Potel, voor links in mails en voor de betaalprovider. Komt uit App:BaseUrl.
// Alleen in ontwikkelmodus valt het terug op het adres van het verzoek; in productie nooit, want de Host-kop kan iedereen verzinnen.
public sealed class AppUrls(IConfiguration config, IWebHostEnvironment env, ILogger<AppUrls> logger)
{
    public string? Configured
    {
        get
        {
            var value = config["App:BaseUrl"]?.Trim().TrimEnd('/');
            if (string.IsNullOrEmpty(value)) return null;
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && uri.Query == "" && uri.Fragment == "")
                return value;
            logger.LogError("App:BaseUrl \"{BaseUrl}\" is geen geldig adres. Gebruik bijvoorbeeld https://potel.jouwdomein.nl", value);
            return null;
        }
    }

    // Null als er geen veilig adres is; dan kunnen we geen links maken.
    public string? Base(HttpRequest? request)
    {
        if (Configured is { } configured) return configured;
        if (env.IsDevelopment() && request is not null) return $"{request.Scheme}://{request.Host}{request.PathBase}";
        logger.LogError("App:BaseUrl ontbreekt. Zonder openbaar adres kan Potel geen links in mails of betalingen maken.");
        return null;
    }
}
