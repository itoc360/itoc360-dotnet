using System.Globalization;
using System.Text.Json.Nodes;

namespace Itoc360;

/// <summary>
/// Urgency of an alert.
/// </summary>
/// <remarks>
/// ITOC360 maps it to the alert's priority: <c>critical</c> to CRITICAL,
/// <c>error</c> to HIGH, <c>warning</c> to MEDIUM and <c>info</c> to LOW. Any
/// other value leaves the alert without a priority.
/// </remarks>
public static class Severity
{
    /// <summary>Highest urgency; ITOC360 records it as CRITICAL.</summary>
    public const string Critical = "critical";

    /// <summary>ITOC360 records it as HIGH.</summary>
    public const string Error = "error";

    /// <summary>The default; ITOC360 records it as MEDIUM.</summary>
    public const string Warning = "warning";

    /// <summary>Lowest urgency; ITOC360 records it as LOW.</summary>
    public const string Info = "info";
}

/// <summary>
/// Whether an alert is starting or ending. <c>firing</c> opens or updates an
/// alert and <c>resolved</c> closes it. No other value is accepted by ITOC360.
/// </summary>
public static class Status
{
    /// <summary>Opens the alert, or updates it if it is already open.</summary>
    public const string Firing = "firing";

    /// <summary>Closes the alert that carries the same fingerprint.</summary>
    public const string Resolved = "resolved";
}

/// <summary>
/// A single alert to send to ITOC360.
/// </summary>
public sealed class Alert
{
    /// <summary>
    /// Identifies the alert across its lifetime. Required.
    /// </summary>
    /// <remarks>
    /// ITOC360 deduplicates on this value: an event whose fingerprint matches an
    /// open alert updates that alert instead of raising a new one. Derive it
    /// from what makes the condition unique — the host, the check and the object
    /// it watches, say — and keep it stable between the firing and the resolved
    /// event.
    /// </remarks>
    public required string Fingerprint { get; init; }

    /// <summary>
    /// One-line description shown as the alert's title in ITOC360 and in the
    /// notifications it sends. Required.
    /// </summary>
    public required string Summary { get; init; }

    /// <summary>Optional longer text shown alongside the summary.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Sets the alert's priority. One of the <see cref="Itoc360.Severity"/>
    /// constants. Defaults to <see cref="Itoc360.Severity.Warning"/>.
    /// </summary>
    public string? Severity { get; init; }

    /// <summary>
    /// Whether the alert is firing or resolved. One of the
    /// <see cref="Itoc360.Status"/> constants. Defaults to
    /// <see cref="Itoc360.Status.Firing"/>.
    /// </summary>
    public string? Status { get; init; }

    /// <summary>
    /// Arbitrary key-value pairs describing the alert, such as the instance or
    /// the service it concerns. Escalation rules can match on them.
    /// </summary>
    /// <remarks>
    /// A <c>severity</c> key is added from <see cref="Severity"/>; an explicit
    /// entry here does not override it.
    /// </remarks>
    public IReadOnlyDictionary<string, string>? Labels { get; init; }

    /// <summary>
    /// Additional key-value pairs carried with the alert. <c>summary</c> and
    /// <c>description</c> are filled in from the fields above.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Annotations { get; init; }

    /// <summary>When the condition began. Defaults to the time of sending.</summary>
    public DateTimeOffset? StartsAt { get; init; }

    /// <summary>
    /// When the condition cleared. Sent only for resolved alerts, where it
    /// defaults to the time of sending.
    /// </summary>
    public DateTimeOffset? EndsAt { get; init; }

    /// <summary>
    /// Optional link back to whatever raised the alert — a dashboard, a run, a
    /// log query.
    /// </summary>
    public string? GeneratorUrl { get; init; }

    /// <summary>
    /// Names the sender in the stored payload. Defaults to
    /// <c>itoc360-dotnet</c>.
    /// </summary>
    public string? Receiver { get; init; }
}

/// <summary>
/// Turns an <see cref="Alert"/> into the Prometheus Alertmanager webhook body
/// ITOC360 reads.
/// </summary>
public static class AlertPayload
{
    /// <summary>Labels the sender in the payload ITOC360 stores.</summary>
    internal const string DefaultReceiver = "itoc360-dotnet";

    /// <summary>
    /// Checks that an alert can be sent. <see cref="Itoc360Client.SendAlertAsync"/>
    /// calls this before every request, so calling it directly is only useful to
    /// report a problem earlier.
    /// </summary>
    /// <exception cref="ValidationException">
    /// A required field is missing, or the status is not one ITOC360 accepts.
    /// </exception>
    public static void Validate(Alert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);

        if (string.IsNullOrEmpty(alert.Fingerprint))
        {
            throw new ValidationException(
                "fingerprint",
                "required: ITOC360 deduplicates alerts on this value, and events " +
                "sent without one collapse into a single alert");
        }

        if (string.IsNullOrEmpty(alert.Summary))
        {
            throw new ValidationException(
                "summary",
                "required: it is the alert title shown in ITOC360 and in notifications");
        }

        // Read as a plain string: Status is not a closed enum, and an unknown
        // value makes ITOC360 answer 500 rather than 400.
        if (alert.Status is { } status &&
            status != Itoc360.Status.Firing &&
            status != Itoc360.Status.Resolved)
        {
            throw new ValidationException(
                "status", $"{status}: must be either \"firing\" or \"resolved\"");
        }
    }

    /// <summary>
    /// Expands an alert into the Alertmanager body ITOC360 reads, applying the
    /// documented defaults.
    /// </summary>
    /// <param name="alert">The alert to expand.</param>
    /// <param name="now">
    /// Clock reading used for the timestamps the alert leaves unset. Defaults to
    /// the current UTC time.
    /// </param>
    public static JsonObject Build(Alert alert, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(alert);

        var clock = now ?? DateTimeOffset.UtcNow;
        var status = alert.Status ?? Itoc360.Status.Firing;
        var severity = alert.Severity ?? Itoc360.Severity.Warning;
        var receiver = alert.Receiver ?? DefaultReceiver;

        // ITOC360 reads the priority from alerts[0].labels.severity, so the
        // field wins over any entry the caller put in Labels.
        var labels = ToJsonObject(alert.Labels);
        labels["severity"] = severity;

        // ITOC360 renders the alert title from commonAnnotations.summary.
        var annotations = ToJsonObject(alert.Annotations);
        annotations["summary"] = alert.Summary;
        if (!string.IsNullOrEmpty(alert.Description))
        {
            annotations["description"] = alert.Description;
        }

        var entry = new JsonObject
        {
            ["status"] = status,
            ["labels"] = labels.DeepClone(),
            ["annotations"] = annotations.DeepClone(),
            ["startsAt"] = Rfc3339(alert.StartsAt ?? clock),
            ["fingerprint"] = alert.Fingerprint,
        };

        if (!string.IsNullOrEmpty(alert.GeneratorUrl))
        {
            entry["generatorURL"] = alert.GeneratorUrl;
        }

        if (status == Itoc360.Status.Resolved)
        {
            entry["endsAt"] = Rfc3339(alert.EndsAt ?? clock);
        }
        else if (alert.EndsAt is { } endsAt)
        {
            entry["endsAt"] = Rfc3339(endsAt);
        }

        return new JsonObject
        {
            ["receiver"] = receiver,
            ["status"] = status,
            ["alerts"] = new JsonArray(entry),
            ["groupLabels"] = new JsonObject { ["fingerprint"] = alert.Fingerprint },
            ["commonLabels"] = labels,
            ["commonAnnotations"] = annotations,
            ["version"] = "4",
            ["groupKey"] = alert.Fingerprint,
        };
    }

    /// <summary>Renders a moment as RFC 3339 in UTC, to the second.</summary>
    private static string Rfc3339(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static JsonObject ToJsonObject(IReadOnlyDictionary<string, string>? source)
    {
        var target = new JsonObject();
        if (source is null) return target;

        foreach (var (key, value) in source)
        {
            target[key] = value;
        }

        return target;
    }
}
