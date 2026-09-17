using System.Globalization;
using System.Text.Json.Nodes;

namespace Itoc360;

/// <summary>The event ITOC360 recorded, returned on success.</summary>
public sealed class EventResponse
{
    /// <summary>Identifier of the stored event.</summary>
    public string Id { get; init; } = "";

    /// <summary>Organization the source belongs to.</summary>
    public string TenantId { get; init; } = "";

    /// <summary>Provider the source is configured for.</summary>
    public int? ProviderId { get; init; }

    /// <summary>Source the token resolved to.</summary>
    public string SourceId { get; init; } = "";

    /// <summary>
    /// <c>ALERT</c> for a firing event and <c>RESOLVE</c> for a resolved one.
    /// </summary>
    public string Type { get; init; } = "";

    /// <summary>Deduplication key ITOC360 derived from the payload.</summary>
    public string Fingerprint { get; init; } = "";

    /// <summary>The body as ITOC360 stored it.</summary>
    public JsonNode? Payload { get; init; }

    /// <summary>
    /// When the event was recorded, or <see langword="null"/> if the server sent
    /// no timestamp or one that could not be parsed.
    /// </summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>The response exactly as ITOC360 sent it.</summary>
    public JsonObject Raw { get; init; } = new();

    /// <summary>Reads the documented fields out of a response body.</summary>
    internal static EventResponse From(JsonObject body) => new()
    {
        Id = AsString(body, "id"),
        TenantId = AsString(body, "tenant_id"),
        ProviderId = AsInt(body, "provider_id"),
        SourceId = AsString(body, "source_id"),
        Type = AsString(body, "type"),
        Fingerprint = AsString(body, "fingerprint"),
        Payload = body["payload"]?.DeepClone(),
        CreatedAt = AsTimestamp(body, "created_at"),
        Raw = body,
    };

    private static string AsString(JsonObject body, string key) =>
        body[key] is JsonValue value && value.TryGetValue(out string? text) ? text : "";

    private static int? AsInt(JsonObject body, string key) =>
        body[key] is JsonValue value && value.TryGetValue(out int number) ? number : null;

    private static DateTimeOffset? AsTimestamp(JsonObject body, string key)
    {
        var text = AsString(body, key);
        if (text.Length == 0) return null;

        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : null;
    }
}
