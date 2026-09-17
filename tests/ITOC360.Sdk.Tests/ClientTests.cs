using System.Text.Json.Nodes;
using Xunit;

namespace Itoc360.Tests;

public class ConstructorTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RefusesAnEmptyToken(string token)
    {
        Assert.Throws<MissingTokenException>(() => new Itoc360Client(token));
    }

    [Fact]
    public void RefusesNullOptions()
    {
        Assert.Throws<ArgumentNullException>(() => new Itoc360Client((Itoc360ClientOptions)null!));
    }

    [Fact]
    public void DefaultsToThePublicEndpoint()
    {
        Assert.Equal("https://api.itoc360.app", Itoc360Client.DefaultBaseUrl);
    }

    [Fact]
    public void DisposingTwiceIsHarmless()
    {
        var client = new Itoc360Client("token");
        client.Dispose();
        client.Dispose();
    }

    [Fact]
    public void LeavesACallerSuppliedHttpClientUsable()
    {
        using var http = new HttpClient();
        var client = new Itoc360Client(new Itoc360ClientOptions { Token = "t", HttpClient = http });

        client.Dispose();

        // Disposing an HttpClient makes further sends throw; this one still works.
        Assert.Equal(TimeSpan.FromSeconds(100), http.Timeout);
    }

    [Fact]
    public async Task RefusesToSendAfterDisposal()
    {
        var client = new Itoc360Client("token");
        client.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => client.SendRawAsync(new JsonObject()));
    }
}

public class SendTests
{
    private const string Stored = """
        {
          "id": "ee4567ad-4eb6-4e4e-8b5e-e3474ee06736",
          "tenant_id": "2a0ebc42-51e6-4e45-a0eb-4dd0c635cd41",
          "provider_id": 57,
          "source_id": "042cce30-7d6c-4f9b-8198-a9a3813682cb",
          "type": "ALERT",
          "fingerprint": "f7d5f89b4a657d5ca7c79ef8c4341303",
          "payload": {"title": "Order sync failed"},
          "created_at": "2026-09-16T09:30:09.64973+00:00"
        }
        """;

    private static Alert Sample() =>
        new() { Fingerprint = "db-01:disk", Summary = "Disk almost full" };

    private static Itoc360Client ClientFor(TestServer server, string token = "src_test") =>
        new(new Itoc360ClientOptions { Token = token, BaseUrl = server.BaseUrl });

    [Fact]
    public async Task PostsToTheEventsEndpoint()
    {
        using var server = new TestServer(new Reply(200, Stored));
        using var client = ClientFor(server);

        await client.SendAlertAsync(Sample());

        Assert.Equal("POST", server.LastRequest.Method);
        Assert.Equal("/functions/v1/events", server.LastRequest.Path);
    }

    [Fact]
    public async Task CarriesTheTokenAsABearerHeaderAndNeverInTheUrl()
    {
        using var server = new TestServer(new Reply(200, Stored));
        using var client = ClientFor(server, "src_secret");

        await client.SendAlertAsync(Sample());

        Assert.Equal("Bearer src_secret", server.LastRequest.Headers["Authorization"]);
        Assert.DoesNotContain("src_secret", server.LastRequest.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdentifiesItselfInTheUserAgent()
    {
        using var server = new TestServer(new Reply(200, Stored));
        using var client = ClientFor(server);

        await client.SendAlertAsync(Sample());

        Assert.Equal($"itoc360-dotnet/{Itoc360Client.Version}", server.LastRequest.Headers["User-Agent"]);
    }

    [Fact]
    public async Task LetsTheCallerReplaceTheUserAgent()
    {
        using var server = new TestServer(new Reply(200, Stored));
        using var client = new Itoc360Client(new Itoc360ClientOptions
        {
            Token = "t",
            BaseUrl = server.BaseUrl,
            UserAgent = "billing-worker/2.1",
        });

        await client.SendAlertAsync(Sample());

        Assert.Equal("billing-worker/2.1", server.LastRequest.Headers["User-Agent"]);
    }

    [Fact]
    public async Task SendsJson()
    {
        using var server = new TestServer(new Reply(200, Stored));
        using var client = ClientFor(server);

        await client.SendAlertAsync(Sample());

        Assert.Contains("application/json", server.LastRequest.Headers["Content-Type"], StringComparison.Ordinal);
        Assert.Contains("application/json", server.LastRequest.Headers["Accept"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task IgnoresATrailingSlashOnTheBaseUrl()
    {
        using var server = new TestServer(new Reply(200, Stored));
        using var client = new Itoc360Client(new Itoc360ClientOptions
        {
            Token = "t",
            BaseUrl = server.BaseUrl + "/",
        });

        await client.SendAlertAsync(Sample());

        Assert.Equal("/functions/v1/events", server.LastRequest.Path);
    }

    [Fact]
    public async Task ReadsTheStoredEventOutOfTheReply()
    {
        using var server = new TestServer(new Reply(200, Stored));
        using var client = ClientFor(server);

        var stored = await client.SendAlertAsync(Sample());

        Assert.Equal("ee4567ad-4eb6-4e4e-8b5e-e3474ee06736", stored.Id);
        Assert.Equal("2a0ebc42-51e6-4e45-a0eb-4dd0c635cd41", stored.TenantId);
        Assert.Equal(57, stored.ProviderId);
        Assert.Equal("042cce30-7d6c-4f9b-8198-a9a3813682cb", stored.SourceId);
        Assert.Equal("ALERT", stored.Type);
        Assert.Equal("f7d5f89b4a657d5ca7c79ef8c4341303", stored.Fingerprint);
        Assert.Equal(new DateTimeOffset(2026, 9, 16, 9, 30, 9, 649, TimeSpan.Zero),
            stored.CreatedAt!.Value.ToUniversalTime(), TimeSpan.FromMilliseconds(1));
        Assert.Equal("Order sync failed", (string?)stored.Payload!["title"]);
    }

    [Fact]
    public async Task KeepsTheWholeReplyAvailable()
    {
        using var server = new TestServer(new Reply(200, """{"id":"e1","surprise":"new field"}"""));
        using var client = ClientFor(server);

        var stored = await client.SendAlertAsync(Sample());

        Assert.Equal("new field", (string?)stored.Raw["surprise"]);
    }

    [Fact]
    public async Task SurvivesAReplyMissingEveryDocumentedField()
    {
        using var server = new TestServer(new Reply(200, "{}"));
        using var client = ClientFor(server);

        var stored = await client.SendAlertAsync(Sample());

        Assert.Equal("", stored.Id);
        Assert.Null(stored.ProviderId);
        Assert.Null(stored.CreatedAt);
    }

    [Fact]
    public async Task IgnoresATimestampItCannotParse()
    {
        using var server = new TestServer(new Reply(200, """{"created_at":"last tuesday"}"""));
        using var client = ClientFor(server);

        var stored = await client.SendAlertAsync(Sample());

        Assert.Null(stored.CreatedAt);
    }

    [Fact]
    public async Task ValidatesBeforeTouchingTheNetwork()
    {
        using var server = new TestServer(new Reply(200, Stored));
        using var client = ClientFor(server);

        await Assert.ThrowsAsync<ValidationException>(
            () => client.SendAlertAsync(new Alert { Fingerprint = "", Summary = "s" }));

        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task SendsARawPayloadUntouched()
    {
        using var server = new TestServer(new Reply(200, Stored));
        using var client = ClientFor(server);

        var payload = new JsonObject
        {
            ["title"] = "Order sync failed",
            ["status"] = "trigger",
            ["id"] = "shopify-sync",
        };

        await client.SendRawAsync(payload);

        var sent = JsonNode.Parse(server.LastRequest.Body)!.AsObject();
        Assert.Equal("Order sync failed", (string?)sent["title"]);
        Assert.Equal("trigger", (string?)sent["status"]);
        Assert.Equal("shopify-sync", (string?)sent["id"]);
        Assert.Null(sent["alerts"]);
    }

    [Fact]
    public async Task SendsTheAlertmanagerShapeForAnAlert()
    {
        using var server = new TestServer(new Reply(200, Stored));
        using var client = ClientFor(server);

        await client.SendAlertAsync(Sample());

        var sent = JsonNode.Parse(server.LastRequest.Body)!.AsObject();
        Assert.Equal("firing", (string?)sent["status"]);
        Assert.Single(sent["alerts"]!.AsArray());
        Assert.Equal("db-01:disk", (string?)sent["alerts"]!.AsArray()[0]!["fingerprint"]);
    }
}

public class FailureTests
{
    private static Alert Sample() =>
        new() { Fingerprint = "f", Summary = "s" };

    private static Itoc360Client ClientFor(TestServer server) =>
        new(new Itoc360ClientOptions { Token = "t", BaseUrl = server.BaseUrl });

    [Fact]
    public async Task ReportsTheServersOwnExplanation()
    {
        using var server = new TestServer(new Reply(400, """{"error":"Invalid payload"}"""));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<ApiException>(() => client.SendAlertAsync(Sample()));

        Assert.Equal(400, error.Status);
        Assert.Equal("Invalid payload", error.Detail);
        Assert.Contains("400", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadsAMessageFieldWhenThereIsNoErrorField()
    {
        using var server = new TestServer(new Reply(400, """{"message":"bad request"}"""));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<ApiException>(() => client.SendAlertAsync(Sample()));

        Assert.Equal("bad request", error.Detail);
    }

    [Fact]
    public async Task FallsBackToAnExcerptWhenAProxyAnswers()
    {
        using var server = new TestServer(
            new Reply(502, "<html>\n  <body>Bad Gateway</body>\n</html>", "text/html"));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<ApiException>(() => client.SendAlertAsync(Sample()));

        Assert.Equal(502, error.Status);
        Assert.Equal("<html> <body>Bad Gateway</body> </html>", error.Detail);
    }

    [Fact]
    public async Task FallsBackToTheStatusWhenTheBodyIsEmpty()
    {
        using var server = new TestServer(new Reply(503, ""));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<ApiException>(() => client.SendAlertAsync(Sample()));

        Assert.Equal(503, error.Status);
        Assert.NotEmpty(error.Detail);
    }

    [Fact]
    public async Task NeverLeaksTheTokenIntoAnErrorMessage()
    {
        using var server = new TestServer(new Reply(401, """{"error":"Unauthorized"}"""));
        using var client = new Itoc360Client(new Itoc360ClientOptions
        {
            Token = "src_super_secret",
            BaseUrl = server.BaseUrl,
        });

        var error = await Assert.ThrowsAsync<ApiException>(() => client.SendAlertAsync(Sample()));

        Assert.DoesNotContain("src_super_secret", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(401, """{"error":"Unauthorized"}""")]
    [InlineData(404, """{"error":"Source not found"}""")]
    public async Task RecognisesACredentialFailure(int status, string body)
    {
        using var server = new TestServer(new Reply(status, body));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<ApiException>(() => client.SendAlertAsync(Sample()));

        Assert.True(error.Unauthorized);
    }

    [Fact]
    public async Task DoesNotTreatEveryNotFoundAsACredentialFailure()
    {
        using var server = new TestServer(new Reply(404, """{"error":"Tenant not found"}"""));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<ApiException>(() => client.SendAlertAsync(Sample()));

        Assert.False(error.Unauthorized);
    }

    [Fact]
    public async Task RecognisesALapsedSubscription()
    {
        using var server = new TestServer(new Reply(402, """{"error":"Subscription inactive"}"""));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<ApiException>(() => client.SendAlertAsync(Sample()));

        Assert.True(error.SubscriptionInactive);
    }

    [Theory]
    [InlineData(408, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(404, false)]
    public async Task KnowsWhichFailuresAreWorthRetrying(int status, bool expected)
    {
        using var server = new TestServer(new Reply(status, """{"error":"nope"}"""));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<ApiException>(() => client.SendAlertAsync(Sample()));

        Assert.Equal(expected, error.Retryable);
    }

    [Fact]
    public async Task ReportsABodyThatIsNotJson()
    {
        using var server = new TestServer(new Reply(200, "not json at all", "text/plain"));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<TransportException>(() => client.SendAlertAsync(Sample()));

        Assert.Contains("not JSON", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsABodyThatIsNotAnObject()
    {
        using var server = new TestServer(new Reply(200, "[1, 2, 3]"));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<TransportException>(() => client.SendAlertAsync(Sample()));

        Assert.Contains("not an object", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsAnUnreachableServer()
    {
        // Port 1 on loopback refuses immediately on every supported platform.
        using var client = new Itoc360Client(new Itoc360ClientOptions
        {
            Token = "t",
            BaseUrl = "http://127.0.0.1:1",
        });

        var error = await Assert.ThrowsAsync<TransportException>(() => client.SendAlertAsync(Sample()));

        Assert.Contains("could not reach", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsATimeoutSeparatelyFromACancellation()
    {
        using var server = new TestServer(
            new Reply(200, "{}") { Delay = TimeSpan.FromSeconds(5) });
        using var client = new Itoc360Client(new Itoc360ClientOptions
        {
            Token = "t",
            BaseUrl = server.BaseUrl,
            Timeout = TimeSpan.FromMilliseconds(150),
        });

        var error = await Assert.ThrowsAsync<TransportException>(() => client.SendAlertAsync(Sample()));

        Assert.Contains("timed out", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LetsACallerCancellationSurfaceAsItself()
    {
        using var server = new TestServer(
            new Reply(200, "{}") { Delay = TimeSpan.FromSeconds(5) });
        using var client = ClientFor(server);
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.SendAlertAsync(Sample(), caller.Token));
    }
}
