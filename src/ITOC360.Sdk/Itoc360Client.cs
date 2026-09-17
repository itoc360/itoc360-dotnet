using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Itoc360;

/// <summary>Options for <see cref="Itoc360Client"/>.</summary>
public sealed class Itoc360ClientOptions
{
    /// <summary>
    /// The source token, created alongside a source in the ITOC360 web
    /// application. It identifies both the organization the events belong to and
    /// the payload format the endpoint expects.
    /// </summary>
    /// <remarks>
    /// Read it from the environment or a secret store. Never commit it, and
    /// never ship it to a client application — anyone who can read it can raise
    /// alerts in your organization.
    /// </remarks>
    public required string Token { get; init; }

    /// <summary>
    /// A different ITOC360 deployment, for self-hosted installations and for
    /// tests. A trailing slash is ignored.
    /// </summary>
    public string? BaseUrl { get; init; }

    /// <summary>How long to wait for each request. Defaults to 30 seconds.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Replaces the default User-Agent, which identifies this SDK and its
    /// version. Prefer appending to it over replacing it.
    /// </summary>
    public string? UserAgent { get; init; }

    /// <summary>
    /// The <see cref="System.Net.Http.HttpClient"/> to send through, which is
    /// where proxies, retries and instrumentation belong.
    /// </summary>
    /// <remarks>
    /// A client supplied here is not disposed by <see cref="Itoc360Client"/> and
    /// its own <see cref="System.Net.Http.HttpClient.Timeout"/> is left
    /// untouched; <see cref="Timeout"/> is applied per request instead. When
    /// this is left unset the SDK creates and owns one.
    /// </remarks>
    public HttpClient? HttpClient { get; init; }
}

/// <summary>
/// Sends events to a single ITOC360 source.
/// </summary>
/// <remarks>
/// An instance is safe to share across concurrent calls, and sharing one is the
/// intended use: it holds the underlying connection pool.
/// </remarks>
public sealed class Itoc360Client : IDisposable
{
    /// <summary>ITOC360 API endpoint used unless a base URL says otherwise.</summary>
    public const string DefaultBaseUrl = "https://api.itoc360.app";

    /// <summary>Version of this SDK, reported in the User-Agent header.</summary>
    public const string Version = "0.1.0";

    private const string EventsPath = "/functions/v1/events";

    /// <summary>
    /// Caps how much of an error response is kept, so a misbehaving proxy cannot
    /// push an unbounded reply into an exception message.
    /// </summary>
    private const int MaxErrorDetail = 8 * 1024;

    private readonly string _token;
    private readonly Uri _endpoint;
    private readonly TimeSpan _timeout;
    private readonly string _userAgent;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private bool _disposed;

    /// <summary>Creates a client from a source token, using every default.</summary>
    /// <exception cref="MissingTokenException">The token is empty.</exception>
    public Itoc360Client(string token) : this(new Itoc360ClientOptions { Token = token }) { }

    /// <summary>Creates a client.</summary>
    /// <exception cref="MissingTokenException">The token is empty.</exception>
    public Itoc360Client(Itoc360ClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Token))
        {
            throw new MissingTokenException();
        }

        _token = options.Token;
        _endpoint = new Uri((options.BaseUrl ?? DefaultBaseUrl).TrimEnd('/') + EventsPath);
        _timeout = options.Timeout ?? TimeSpan.FromSeconds(30);
        _userAgent = options.UserAgent ?? $"itoc360-dotnet/{Version}";

        // A caller-supplied client belongs to the caller: its lifetime, its
        // timeout and its handler pipeline all stay as they were.
        _ownsHttpClient = options.HttpClient is null;
        _http = options.HttpClient ?? new HttpClient();
    }

    /// <summary>
    /// Sends an alert to ITOC360.
    /// </summary>
    /// <remarks>
    /// The alert is expanded into the Prometheus Alertmanager webhook payload, so
    /// the source must be configured for a provider that speaks it: prometheus,
    /// mimir, cortex, loki, signoz or grafana. For any other provider, build that
    /// vendor's payload and use <see cref="SendRawAsync"/>.
    /// </remarks>
    /// <exception cref="ValidationException">
    /// The alert is missing a fingerprint or a summary, in which case ITOC360 is
    /// never contacted.
    /// </exception>
    /// <exception cref="ApiException">ITOC360 rejected the request.</exception>
    /// <exception cref="TransportException">The request never reached ITOC360.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled.
    /// </exception>
    public Task<EventResponse> SendAlertAsync(
        Alert alert, CancellationToken cancellationToken = default)
    {
        AlertPayload.Validate(alert);
        return SendRawAsync(AlertPayload.Build(alert), cancellationToken);
    }

    /// <summary>
    /// Sends <paramref name="payload"/> to ITOC360 as the event body, encoded as
    /// JSON.
    /// </summary>
    /// <remarks>
    /// It applies no shape of its own, so it works with every provider ITOC360
    /// supports: pass the payload that provider documents. Use
    /// <see cref="SendAlertAsync"/> instead when the source speaks the
    /// Alertmanager format.
    /// <para>
    /// A plain object becomes a <see cref="JsonNode"/> through
    /// <c>JsonSerializer.SerializeToNode</c>, and a JSON string through
    /// <c>JsonNode.Parse</c>. Taking the node rather than the object keeps this
    /// package free of reflection, so it survives trimming and native AOT.
    /// </para>
    /// </remarks>
    /// <exception cref="ApiException">ITOC360 rejected the request.</exception>
    /// <exception cref="TransportException">The request never reached ITOC360.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled.
    /// </exception>
    public async Task<EventResponse> SendRawAsync(
        JsonNode payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);

        // The token travels in a header, never in the URL, so it stays out of
        // proxy and server logs.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        request.Content = new StringContent(
            payload.ToJsonString(), Encoding.UTF8, "application/json");

        // The timeout is applied here rather than on HttpClient so that a
        // client shared with the rest of the application is never reconfigured.
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(_timeout);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseContentRead, attempt.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TransportException(
                $"the request to ITOC360 timed out after {_timeout.TotalMilliseconds:0}ms");
        }
        catch (HttpRequestException cause)
        {
            throw new TransportException("could not reach ITOC360", cause);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw await ApiErrorAsync(response, attempt.Token).ConfigureAwait(false);
            }

            return await ReadEventAsync(response, attempt.Token).ConfigureAwait(false);
        }
    }

    private static async Task<EventResponse> ReadEventAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception cause) when (cause is HttpRequestException or IOException)
        {
            throw new TransportException("the reply from ITOC360 could not be read", cause);
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(body);
        }
        catch (JsonException cause)
        {
            throw new TransportException("ITOC360 returned a body that is not JSON", cause);
        }

        if (parsed is not JsonObject stored)
        {
            throw new TransportException("ITOC360 returned a body that is not an object");
        }

        return EventResponse.From(stored);
    }

    /// <summary>Turns a rejected response into an <see cref="ApiException"/>.</summary>
    private static async Task<ApiException> ApiErrorAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;

        string text;
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            text = body.Length > MaxErrorDetail ? body[..MaxErrorDetail] : body;
            text = text.Trim();
        }
        catch (Exception cause) when (cause is HttpRequestException or IOException or OperationCanceledException)
        {
            text = "";
        }

        if (text.Length == 0)
        {
            var phrase = response.ReasonPhrase;
            return new ApiException(
                status, string.IsNullOrEmpty(phrase) ? "no message returned" : phrase);
        }

        try
        {
            if (JsonNode.Parse(text) is JsonObject problem)
            {
                foreach (var key in new[] { "error", "message" })
                {
                    if (problem[key] is JsonValue value &&
                        value.TryGetValue(out string? explanation) &&
                        !string.IsNullOrEmpty(explanation))
                    {
                        return new ApiException(status, explanation);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Not the documented shape — a gateway or proxy replied.
        }

        var excerpt = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return new ApiException(
            status, excerpt.Length > 200 ? excerpt[..200] + "..." : excerpt);
    }

    /// <summary>
    /// Releases the <see cref="HttpClient"/> this client created. One supplied
    /// through <see cref="Itoc360ClientOptions.HttpClient"/> is left alone.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}
