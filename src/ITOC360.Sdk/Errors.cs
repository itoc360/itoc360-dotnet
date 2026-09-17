namespace Itoc360;

/// <summary>
/// Base class for every exception this package throws, so a caller can catch
/// everything from the SDK with a single clause.
/// </summary>
public class Itoc360Exception : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public Itoc360Exception(string message) : base(message) { }

    /// <summary>Creates the exception with a message and an underlying cause.</summary>
    public Itoc360Exception(string message, Exception? innerException)
        : base(message, innerException) { }
}

/// <summary>
/// Thrown when a client is created without a source token.
/// </summary>
public sealed class MissingTokenException : Itoc360Exception
{
    /// <summary>Creates the exception.</summary>
    public MissingTokenException() : base("a source token is required") { }
}

/// <summary>
/// Thrown when the request never produced an HTTP response.
/// </summary>
/// <remarks>
/// A refused connection, a DNS failure, a TLS problem, a cancellation or a
/// timeout all end up here. The originating exception, when there is one, is
/// available as <see cref="Exception.InnerException"/>.
/// </remarks>
public sealed class TransportException : Itoc360Exception
{
    /// <summary>Creates the exception.</summary>
    public TransportException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}

/// <summary>
/// Thrown when an <see cref="Alert"/> cannot be sent as written. The request is
/// rejected locally and never reaches ITOC360.
/// </summary>
public sealed class ValidationException : Itoc360Exception
{
    /// <summary>Name of the offending <see cref="Alert"/> field.</summary>
    public string Field { get; }

    /// <summary>Creates the exception for a named field.</summary>
    public ValidationException(string field, string message)
        : base($"invalid alert: {field}: {message}")
    {
        Field = field;
    }
}

/// <summary>
/// Thrown when ITOC360 answers with a non-2xx status.
/// </summary>
/// <remarks>
/// <see cref="Exception.Message"/> carries the server's own explanation when the
/// response body is the documented <c>{"error": "..."}</c> object, and a short
/// generic description otherwise. Neither the source token nor any request
/// header is ever included.
/// </remarks>
public sealed class ApiException : Itoc360Exception
{
    /// <summary>HTTP status returned by ITOC360.</summary>
    public int Status { get; }

    /// <summary>Server-supplied explanation, without the status prefix.</summary>
    public string Detail { get; }

    /// <summary>Creates the exception from a status and an explanation.</summary>
    public ApiException(int status, string detail)
        : base($"request failed with status {status}: {detail}")
    {
        Status = status;
        Detail = detail;
    }

    /// <summary>
    /// Whether the token was missing or not recognised.
    /// </summary>
    /// <remarks>
    /// ITOC360 answers 401 when no token is presented and 404 when the token
    /// matches no source, so both count as credential failures. Telling that 404
    /// apart from a genuinely missing resource means reading the server's
    /// wording; should ITOC360 rephrase it, this stops recognising the case but
    /// <see cref="Status"/> and <see cref="Detail"/> still describe what
    /// happened.
    /// </remarks>
    public bool Unauthorized =>
        Status == 401 ||
        (Status == 404 &&
         Detail.Trim().Equals("source not found", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether the organization's ITOC360 subscription has lapsed. ITOC360
    /// answers 402 in that case and drops the event.
    /// </summary>
    public bool SubscriptionInactive => Status == 402;

    /// <summary>
    /// Whether the request is worth sending again unchanged. Server-side
    /// failures, rate limiting and a timed-out request are; a rejected payload
    /// is not.
    /// </summary>
    public bool Retryable => Status == 408 || Status == 429 || Status >= 500;
}
