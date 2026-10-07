using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Potel.Api.Payments;

// Online betalen staat alleen aan als er een API-sleutel van Mollie is ingesteld (Mollie:ApiKey). Begin met een testsleutel (test_...).
public class MollieOptions
{
    public string ApiKey { get; set; } = "";
    // Alleen nodig als Mollie het adres uit App:BaseUrl niet kan bereiken, bijvoorbeeld bij het ontwikkelen via een tunnel.
    public string WebhookUrl { get; set; } = "";
    public bool Enabled => !string.IsNullOrWhiteSpace(ApiKey);
}

public record MollieAmount(string Currency, string Value)
{
    static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static MollieAmount Euro(decimal amount) => new("EUR", amount.ToString("0.00", Invariant));

    public decimal? Euros => Currency == "EUR" && decimal.TryParse(Value, NumberStyles.Number, Invariant, out var d) ? d : null;
}

public class MollieLink
{
    public string? Href { get; set; }
}

public class MollieLinks
{
    public MollieLink? Checkout { get; set; }
}

public class MolliePaymentInfo
{
    public string Id { get; set; } = "";
    public string Status { get; set; } = "";
    public string? SequenceType { get; set; }
    public string? Method { get; set; }
    public string? CustomerId { get; set; }
    public string? SubscriptionId { get; set; }
    public string? MandateId { get; set; }
    public MollieAmount? Amount { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public JsonElement? Metadata { get; set; }
    [JsonPropertyName("_links")] public MollieLinks? Links { get; set; }

    public string? Meta(string key) =>
        Metadata is { ValueKind: JsonValueKind.Object } m && m.TryGetProperty(key, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()
            : null;
}

public class MollieSubscriptionInfo
{
    public string Id { get; set; } = "";
    public string Status { get; set; } = "";
    public string? CustomerId { get; set; }
    public MollieAmount? Amount { get; set; }
    public string? NextPaymentDate { get; set; }
    public JsonElement? Metadata { get; set; }

    public string? Meta(string key) =>
        Metadata is { ValueKind: JsonValueKind.Object } m && m.TryGetProperty(key, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()
            : null;
}

public class MollieCustomerInfo
{
    public string Id { get; set; } = "";
}

public class MollieException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

// Praat met de API van Mollie (https://api.mollie.com/v2). Alleen wat Potel nodig heeft: klanten, betalingen en abonnementen.
public class MollieClient(HttpClient http, IOptions<MollieOptions> options)
{
    public const string BaseUrl = "https://api.mollie.com/v2/";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public bool Enabled => options.Value.Enabled;

    async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body = null, string? idempotencyKey = null, bool allowNotFound = false) where T : class
    {
        if (!Enabled) throw new InvalidOperationException("Mollie:ApiKey ontbreekt");
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.ApiKey.Trim());
        if (idempotencyKey is not null) req.Headers.Add("Idempotency-Key", idempotencyKey);
        if (body is not null) req.Content = JsonContent.Create(body, options: Json);
        using var res = await http.SendAsync(req);
        if (allowNotFound && res.StatusCode == HttpStatusCode.NotFound) return null;
        if (!res.IsSuccessStatusCode)
        {
            var detail = "";
            try { detail = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString() ?? ""; } catch { /* geen JSON */ }
            throw new MollieException(res.StatusCode, $"Mollie gaf {(int)res.StatusCode} op {method} {path}: {detail}");
        }
        return await res.Content.ReadFromJsonAsync<T>(Json);
    }

    public async Task<MollieCustomerInfo> CreateCustomerAsync(string name, string? email, int workspaceId) =>
        (await SendAsync<MollieCustomerInfo>(HttpMethod.Post, "customers", new { name, email, metadata = new { workspaceId } }))!;

    public async Task<MolliePaymentInfo> CreateFirstPaymentAsync(string customerId, MollieAmount amount, string description, string redirectUrl, string? webhookUrl, object metadata) =>
        (await SendAsync<MolliePaymentInfo>(HttpMethod.Post, "payments", new
        {
            amount, description, redirectUrl, webhookUrl, metadata, customerId,
            method = "ideal", sequenceType = "first", locale = "nl_NL",
        }))!;

    // Null als Mollie deze betaling niet kent, bijvoorbeeld bij een verzonnen id.
    public Task<MolliePaymentInfo?> GetPaymentAsync(string id) =>
        SendAsync<MolliePaymentInfo>(HttpMethod.Get, $"payments/{Uri.EscapeDataString(id)}", allowNotFound: true);

    public async Task<MollieSubscriptionInfo> CreateSubscriptionAsync(string customerId, MollieAmount amount, DateOnly startDate, string description,
        string? webhookUrl, object metadata, string idempotencyKey) =>
        (await SendAsync<MollieSubscriptionInfo>(HttpMethod.Post, $"customers/{Uri.EscapeDataString(customerId)}/subscriptions", new
        {
            amount, description, webhookUrl, metadata, interval = "1 month", startDate = startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        }, idempotencyKey))!;

    public Task<MollieSubscriptionInfo?> GetSubscriptionAsync(string customerId, string subscriptionId) =>
        SendAsync<MollieSubscriptionInfo>(HttpMethod.Get, $"customers/{Uri.EscapeDataString(customerId)}/subscriptions/{Uri.EscapeDataString(subscriptionId)}", allowNotFound: true);

    // Opzeggen bij Mollie. Al opgezegd of onbekend telt ook als opgezegd.
    public async Task CancelSubscriptionAsync(string customerId, string subscriptionId)
    {
        try
        {
            await SendAsync<MollieSubscriptionInfo>(HttpMethod.Delete, $"customers/{Uri.EscapeDataString(customerId)}/subscriptions/{Uri.EscapeDataString(subscriptionId)}", allowNotFound: true);
        }
        catch (MollieException e) when (e.Status == HttpStatusCode.UnprocessableEntity || e.Status == HttpStatusCode.Gone) { }
    }
}
