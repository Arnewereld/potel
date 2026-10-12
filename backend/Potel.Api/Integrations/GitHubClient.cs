using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Potel.Api.Data;

namespace Potel.Api.Integrations;

// Een repository op github.com, uit de repository-link van een project. Alleen https://github.com/eigenaar/naam telt;
// al het andere (een ander domein, extra paden, een poort, inloggegevens in de link) is voor Potel geen GitHub-repository.
public sealed partial record GitHubRepo(string Owner, string Name)
{
    [GeneratedRegex(@"^https://(?i:(?:www\.)?github\.com)/(?<owner>[A-Za-z0-9](?:[A-Za-z0-9]|-(?=[A-Za-z0-9])){0,38})/(?<repo>[A-Za-z0-9._-]{1,100}?)(?:\.git)?/?$")]
    private static partial Regex Pattern();

    public static GitHubRepo? Parse(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 300) return null;
        var m = Pattern().Match(url.Trim());
        if (!m.Success) return null;
        var repo = m.Groups["repo"].Value;
        if (repo is "." or ".." or ".git") return null;
        return new GitHubRepo(m.Groups["owner"].Value, repo);
    }

    // Is dit een link naar github.com? Dan moet hij het patroon hierboven volgen.
    public static bool LooksLikeGitHub(string url) =>
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) && (u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase));

    public string FullName => $"{Owner}/{Name}";
}

public record GitHubCommitInfo(string Sha, string Message, string? AuthorEmail, DateTime TimeUtc, string? Url, bool IsMerge);

public record GitHubCommitPage(List<GitHubCommitInfo> Commits, bool Truncated);

public enum GitHubProblem { Unauthorized, RateLimited, NotFound, Moved, Forbidden, Unavailable, Timeout, TooLarge, Invalid }

public class GitHubException(GitHubProblem problem, string message) : Exception(message)
{
    public GitHubProblem Problem { get; } = problem;
    // Bij deze problemen heeft het geen zin om de andere repositories nog te proberen.
    public bool StopsEverything => Problem is GitHubProblem.Unauthorized or GitHubProblem.RateLimited or GitHubProblem.Timeout or GitHubProblem.Unavailable;
}

// Praat met de REST-API van GitHub, en alleen daarmee: elk adres wordt hier opgebouwd uit https://api.github.com en een
// gecontroleerde eigenaar en naam, doorverwijzingen worden niet gevolgd, er is een tijdslimiet en een maximale grootte.
public class GitHubClient(HttpClient http, BusinessClock clock)
{
    public const string Host = "api.github.com";
    public const string BaseUrl = "https://api.github.com/";
    public const int PageSize = 100;
    public const int MaxResponseBytes = 4 * 1024 * 1024;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    // De handler voor echte verzoeken: geen doorverwijzingen (ook niet naar hetzelfde domein) en geen cookies.
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        AutomaticDecompression = DecompressionMethods.All,
    };

    // De commits van één auteur in een repository tussen twee momenten, nieuwste eerst, hooguit één pagina.
    public async Task<GitHubCommitPage> CommitsAsync(string token, GitHubRepo repo, string author, DateTime sinceUtc, DateTime untilUtc, CancellationToken ct = default)
    {
        var query = $"author={Uri.EscapeDataString(author)}&since={Iso(sinceUtc)}&until={Iso(untilUtc)}&per_page={PageSize}";
        using var res = await SendAsync(token, $"repos/{Uri.EscapeDataString(repo.Owner)}/{Uri.EscapeDataString(repo.Name)}/commits?{query}", repo, ct);
        // Een lege repository geeft 409: dan zijn er gewoon geen commits.
        if (res.StatusCode == HttpStatusCode.Conflict) return new([], false);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        }
        catch (JsonException)
        {
            throw new GitHubException(GitHubProblem.Invalid, "GitHub gaf een antwoord dat Potel niet kan lezen. Probeer het later nog eens.");
        }
        using var _ = doc;
        var json = doc.RootElement;
        if (json.ValueKind != JsonValueKind.Array) throw new GitHubException(GitHubProblem.Invalid, "GitHub gaf een antwoord dat Potel niet kan lezen. Probeer het later nog eens.");
        var commits = new List<GitHubCommitInfo>();
        foreach (var c in json.EnumerateArray())
        {
            if (Str(c, "sha") is not { } sha || !IsSha(sha)) continue;
            if (!c.TryGetProperty("commit", out var commit) || commit.ValueKind != JsonValueKind.Object) continue;
            var who = commit.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
            if (who.ValueKind != JsonValueKind.Object || Str(who, "date") is not { } date
                || !DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when)) continue;
            var parents = c.TryGetProperty("parents", out var p) && p.ValueKind == JsonValueKind.Array ? p.GetArrayLength() : 0;
            var url = Str(c, "html_url") is { } u && u.StartsWith("https://github.com/", StringComparison.Ordinal) ? u : null;
            commits.Add(new GitHubCommitInfo(sha.ToLowerInvariant(), Str(commit, "message") ?? "", Str(who, "email"), when.UtcDateTime, url, parents > 1));
        }
        return new(commits, json.GetArrayLength() >= PageSize);
    }

    async Task<HttpResponseMessage> SendAsync(string token, string path, GitHubRepo repo, CancellationToken ct)
    {
        var uri = new Uri(new Uri(BaseUrl), path);
        // Nooit een ander domein, wat er ook in de link van het project stond.
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != Host || !uri.IsDefaultPort)
            throw new GitHubException(GitHubProblem.Invalid, "Deze repository-link kan Potel niet gebruiken.");
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("Potel", "1.0"));
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        HttpResponseMessage res;
        try
        {
            res = await http.SendAsync(req, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GitHubException(GitHubProblem.Timeout, "GitHub reageerde niet op tijd. Probeer het zo nog eens.");
        }
        catch (HttpRequestException e) when (e.Message.Contains("buffer", StringComparison.OrdinalIgnoreCase))
        {
            throw new GitHubException(GitHubProblem.TooLarge, $"Het antwoord van GitHub over {repo.FullName} was te groot. Kies een kortere periode.");
        }
        catch (HttpRequestException)
        {
            throw new GitHubException(GitHubProblem.Unavailable, "GitHub is nu niet bereikbaar. Probeer het later nog eens.");
        }
        if (res.IsSuccessStatusCode || res.StatusCode == HttpStatusCode.Conflict) return res;
        using (res) throw Problem(res, repo);
    }

    GitHubException Problem(HttpResponseMessage res, GitHubRepo repo)
    {
        var status = (int)res.StatusCode;
        if (res.StatusCode == HttpStatusCode.Unauthorized)
            return new(GitHubProblem.Unauthorized, "GitHub accepteert het token niet. Misschien is het verlopen of ingetrokken. Een beheerder kan bij Instellingen > GitHub een nieuw token instellen.");
        if (res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests && RateLimit(res) is { Limited: true } limit)
            return new(GitHubProblem.RateLimited, limit.Until is { } t
                ? $"Je hebt de limiet van GitHub voor nu bereikt. Probeer het na {TimeZoneInfo.ConvertTimeFromUtc(t, BusinessClock.Zone):HH:mm} uur opnieuw."
                : "Je hebt de limiet van GitHub voor nu bereikt. Probeer het over een paar minuten opnieuw.");
        if (res.StatusCode == HttpStatusCode.Forbidden)
            return new(GitHubProblem.Forbidden, $"Het token heeft geen toegang tot {repo.FullName}. Geef het token leesrechten op deze repository.");
        if (res.StatusCode == HttpStatusCode.NotFound)
            return new(GitHubProblem.NotFound, $"Repository {repo.FullName} bestaat niet, of het token heeft er geen toegang toe. Controleer de repository-link van het project.");
        if (status is >= 300 and < 400)
            return new(GitHubProblem.Moved, $"Repository {repo.FullName} is verhuisd of heeft een andere naam gekregen. Pas de repository-link van het project aan.");
        if (status >= 500)
            return new(GitHubProblem.Unavailable, "GitHub heeft op dit moment een storing. Probeer het later nog eens.");
        return new(GitHubProblem.Invalid, $"GitHub kon de commits van {repo.FullName} niet geven (foutcode {status}).");
    }

    // Een limiet is bereikt als GitHub dat zegt: 429, nog 0 verzoeken over, of wacht even. Met het moment waarop het weer kan.
    (bool Limited, DateTime? Until) RateLimit(HttpResponseMessage res)
    {
        static string? Header(HttpResponseMessage r, string name) => r.Headers.TryGetValues(name, out var v) ? v.FirstOrDefault() : null;
        if (Header(res, "Retry-After") is { } retry && int.TryParse(retry, out var seconds))
            return (true, clock.UtcNow.AddSeconds(Math.Clamp(seconds, 0, 24 * 3600)));
        var empty = Header(res, "X-RateLimit-Remaining") == "0";
        if (!empty && res.StatusCode != HttpStatusCode.TooManyRequests) return (false, null);
        if (Header(res, "X-RateLimit-Reset") is { } reset && long.TryParse(reset, out var epoch))
            return (true, DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(epoch, 0, 253402300799)).UtcDateTime);
        return (true, null);
    }

    static string Iso(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static bool IsSha(string s) => s.Length == 40 && s.All(Uri.IsHexDigit);
}
