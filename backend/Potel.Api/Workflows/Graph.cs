using System.Text.Json;

namespace Potel.Api.Workflows;

public record GraphNode(string Id, string Type, string Label, double X, double Y, Dictionary<string, string>? Config)
{
    public string Get(string key, string fallback = "") =>
        Config is not null && Config.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;
}

public record GraphEdge(string From, string To, string? Branch);

public record Graph(List<GraphNode> Nodes, List<GraphEdge> Edges)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Graph Parse(string json)
    {
        try
        {
            var g = JsonSerializer.Deserialize<Graph>(json, Json);
            return new Graph(g?.Nodes ?? [], g?.Edges ?? []);
        }
        catch (JsonException)
        {
            return new Graph([], []);
        }
    }
}

public record LogEntry(string NodeId, string Label, string Type, string Status, string Message, DateTime At);

public record PendingStep(string NodeId, DateTime ResumeAt);
