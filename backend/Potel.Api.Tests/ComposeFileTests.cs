namespace Potel.Api.Tests;

// docker-compose.yml: de app mag alleen via je eigen proxy bereikbaar zijn, en alleen die proxy mag een IP-adres doorgeven.
public class ComposeFileTests
{
    static string ComposeFile()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "docker-compose.yml");
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException("docker-compose.yml niet gevonden");
    }

    // Alleen de regels die echt meedoen, zonder commentaar.
    static List<string> Settings() => ComposeFile().Split('\n')
        .Select(l => l.Split('#')[0].Trim()).Where(l => l.Length > 0).ToList();

    [Fact]
    public void The_port_is_only_published_on_localhost()
    {
        var settings = Settings();
        Assert.Contains("- \"127.0.0.1:8080:8080\"", settings);
        Assert.DoesNotContain(settings, l => l.Contains("\"8080:8080\"") || l.Contains("0.0.0.0:8080"));
    }

    [Fact]
    public void Only_the_gateway_of_its_own_network_is_trusted_as_proxy()
    {
        var settings = Settings();
        Assert.DoesNotContain(settings, l => l.Contains("172.16.0.0/12") || l.Contains("KnownNetworks"));
        Assert.Contains("KnownProxies__0: \"172.30.0.1\"", settings);
        Assert.Contains("gateway: 172.30.0.1", settings);
    }
}
