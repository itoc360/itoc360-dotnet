using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Itoc360.Tests;

/// <summary>What the SDK actually put on the wire.</summary>
public sealed record RecordedRequest(
    string Method,
    string Path,
    string Body,
    IReadOnlyDictionary<string, string> Headers);

/// <summary>How the server should answer one request.</summary>
public sealed record Reply(int Status, string Body, string ContentType = "application/json")
{
    /// <summary>Held before answering, to exercise the client timeout.</summary>
    public TimeSpan Delay { get; init; } = TimeSpan.Zero;
}

/// <summary>
/// A real HTTP server on the loopback interface.
/// </summary>
/// <remarks>
/// The SDK is exercised through an actual socket rather than a stubbed handler,
/// so header encoding, status handling and body framing are all covered by the
/// same tests.
/// </remarks>
public sealed class TestServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Func<RecordedRequest, Reply> _responder;
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _loop;

    /// <summary>Base URL to hand to the client under test.</summary>
    public string BaseUrl { get; }

    /// <summary>Every request the server received, in arrival order.</summary>
    public IReadOnlyCollection<RecordedRequest> Requests => _requests.ToArray();

    /// <summary>The single request the server received.</summary>
    public RecordedRequest LastRequest =>
        _requests.LastOrDefault() ?? throw new InvalidOperationException("no request was received");

    public TestServer(Func<RecordedRequest, Reply> responder)
    {
        _responder = responder;

        var port = FreePort();
        BaseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add($"{BaseUrl}/");
        _listener.Start();

        _loop = Task.Run(AcceptAsync);
    }

    /// <summary>Answers every request the same way.</summary>
    public TestServer(Reply reply) : this(_ => reply) { }

    private async Task AcceptAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (_stopping.IsCancellationRequested)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => RespondAsync(context));
        }
    }

    private async Task RespondAsync(HttpListenerContext context)
    {
        var request = context.Request;

        using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
        var body = await reader.ReadToEndAsync().ConfigureAwait(false);

        var headers = request.Headers.AllKeys
            .Where(key => key is not null)
            .ToDictionary(key => key!, key => request.Headers[key] ?? "", StringComparer.OrdinalIgnoreCase);

        var recorded = new RecordedRequest(
            request.HttpMethod, request.Url?.AbsolutePath ?? "", body, headers);
        _requests.Enqueue(recorded);

        Reply reply;
        try
        {
            reply = _responder(recorded);
        }
        catch (Exception error)
        {
            reply = new Reply(500, $"{{\"error\":\"test responder threw: {error.Message}\"}}");
        }

        if (reply.Delay > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(reply.Delay, _stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutting down mid-delay; the client has given up already.
            }
        }

        try
        {
            var payload = Encoding.UTF8.GetBytes(reply.Body);
            context.Response.StatusCode = reply.Status;
            context.Response.ContentType = reply.ContentType;
            context.Response.ContentLength64 = payload.Length;
            await context.Response.OutputStream.WriteAsync(payload).ConfigureAwait(false);
            context.Response.Close();
        }
        catch (Exception)
        {
            // The client disconnected — a timeout test, most likely.
        }
    }

    /// <summary>
    /// Asks the operating system for an unused port by binding to port 0 and
    /// reading back what it assigned.
    /// </summary>
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public void Dispose()
    {
        _stopping.Cancel();

        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
            // Already closed.
        }

        try
        {
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The accept loop was torn down with the listener.
        }

        _stopping.Dispose();
    }
}
