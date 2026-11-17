using System.Net.Http;
using System.Text.RegularExpressions;
using BillionSend.Tests.Support;

namespace BillionSend.Tests;

public sealed class ClientTests : MockTest
{
    private static readonly object SendBody = new { from = "hello@example.com", to = "person@example.com", subject = "Hi", text = "Hi" };

    private static readonly Regex Uuid4 = new("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$");

    private readonly string? _savedKey = Environment.GetEnvironmentVariable("BILLIONSEND_API_KEY");
    private readonly string? _savedUrl = Environment.GetEnvironmentVariable("BILLIONSEND_BASE_URL");

    public override void Dispose()
    {
        Environment.SetEnvironmentVariable("BILLIONSEND_API_KEY", _savedKey);
        Environment.SetEnvironmentVariable("BILLIONSEND_BASE_URL", _savedUrl);
        base.Dispose();
    }

    private static JsonObject ErrorBody(int status, string name, string message = "m") =>
        new() { ["statusCode"] = status, ["name"] = name, ["message"] = message };

    // ---------------------------------------------------------------- configuration

    [Fact]
    public void TakesTheKeyAsAStringAndDefaultsTheBaseUrl()
    {
        Environment.SetEnvironmentVariable("BILLIONSEND_BASE_URL", null);
        using var client = new BillionSendClient("bs_x");
        Assert.Equal("https://api.billionsend.com", client.BaseUrl);
        Assert.Equal(BillionSendClient.DefaultBaseUrl, client.BaseUrl);
    }

    [Fact]
    public async Task ReadsTheKeyAndBaseUrlFromTheEnvironment()
    {
        Environment.SetEnvironmentVariable("BILLIONSEND_API_KEY", ContractMock.ApiKey);
        Environment.SetEnvironmentVariable("BILLIONSEND_BASE_URL", ContractMock.BaseUrl + "/");
        using var client = new BillionSendClient(new BillionSendOptions { HttpClient = new HttpClient(Mock, false) });
        Assert.Equal(ContractMock.BaseUrl, client.BaseUrl);
        await client.Usage.GetAsync();
        Assert.Equal($"Bearer {ContractMock.ApiKey}", Assert.Single(Mock.Hits).Header("authorization"));
    }

    [Fact]
    public async Task ThrowsMissingApiKeyWithoutCallingTheApi()
    {
        Environment.SetEnvironmentVariable("BILLIONSEND_API_KEY", null);
        using var client = new BillionSendClient(new BillionSendOptions { BaseUrl = ContractMock.BaseUrl, HttpClient = new HttpClient(Mock, false) });
        var error = await Assert.ThrowsAsync<AuthenticationException>(() => client.Emails.SendAsync(SendBody));
        Assert.Null(error.StatusCode);
        Assert.Equal("missing_api_key", error.Name);
        Assert.Contains("BILLIONSEND_API_KEY", error.Message);
        Assert.Null(error.RequestId);
        Assert.Empty(Mock.Hits);
    }

    [Fact]
    public async Task LetsAgentSignupRunWithoutAKeyAndNeverSendsOne()
    {
        Environment.SetEnvironmentVariable("BILLIONSEND_API_KEY", null);
        var signup = new { human_email = "me@example.com", name = "Agent" };
        using var keyless = new BillionSendClient(new BillionSendOptions { BaseUrl = ContractMock.BaseUrl, HttpClient = new HttpClient(Mock, false) });
        await keyless.Agent.SignupAsync(signup);
        using var keyed = Client(o => o.Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer leaked" });
        await keyed.Agent.SignupAsync(signup);
        Assert.All(Mock.Hits, hit => Assert.Null(hit.Header("authorization")));
        Assert.Equal(2, Mock.Hits.Count);
    }

    [Fact]
    public async Task SendsTheUserAgentJsonHeadersAndCustomHeaders()
    {
        using var client = Client(o => o.Headers = new Dictionary<string, string> { ["X-Trace"] = "client" });
        await client.Emails.SendAsync(SendBody, new IdempotentRequestOptions { Headers = new Dictionary<string, string> { ["x-request-tag"] = "one" } });
        var hit = Assert.Single(Mock.Hits);
        Assert.Equal($"billionsend-dotnet/{BillionSendClient.SdkVersion}", hit.Header("user-agent"));
        Assert.Equal("application/json", hit.Header("content-type"));
        Assert.Equal("application/json", hit.Header("accept"));
        Assert.Equal("client", hit.Header("x-trace"));
        Assert.Equal("one", hit.Header("x-request-tag"));
    }

    [Fact]
    public void KeepsTheSdkVersionInStepWithThePackage()
    {
        var project = File.ReadAllText(Path.Combine(Spec.RepositoryRoot, "src", "BillionSend", "BillionSend.csproj"));
        Assert.Contains($"<Version>{BillionSendClient.SdkVersion}</Version>", project);
        Assert.Contains($"## {BillionSendClient.SdkVersion} ", File.ReadAllText(Path.Combine(Spec.RepositoryRoot, "CHANGELOG.md")));
    }

    [Fact]
    public async Task SendsANewIdempotencyKeyWithEveryCallOfASendingOperationAndNoneElsewhere()
    {
        using var client = Client();
        await client.Emails.SendAsync(SendBody);
        await client.Emails.SendAsync(SendBody);
        await client.Emails.SendAsync(SendBody, new IdempotentRequestOptions { IdempotencyKey = "welcome/42" });
        await client.Emails.SendAsync(SendBody, new IdempotentRequestOptions { Headers = new Dictionary<string, string> { ["Idempotency-Key"] = "from-headers" } });
        await client.Domains.CreateAsync(new { name = "example.com" });
        var keys = Mock.HitsFor("POST /emails").Select(hit => hit.Header("idempotency-key")).ToList();
        Assert.Matches(Uuid4, keys[0]!);
        Assert.Matches(Uuid4, keys[1]!);
        Assert.NotEqual(keys[0], keys[1]);
        Assert.Equal(["welcome/42", "from-headers"], keys.Skip(2));
        Assert.Null(Assert.Single(Mock.HitsFor("POST /domains")).Header("idempotency-key"));
    }

    [Fact]
    public async Task UsesAnInjectedHttpClient()
    {
        var calls = new List<string>();
        var recording = new RecordingHandler(Mock, calls);
        using var client = Client(o => o.HttpClient = new HttpClient(recording));
        await client.Domains.GetAsync(Id);
        Assert.Equal([$"GET {ContractMock.BaseUrl}/domains/{Id}"], calls);
    }

    [Fact]
    public void RefusesInvalidOptions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BillionSendClient(new BillionSendOptions { ApiKey = "bs_x", Timeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BillionSendClient(new BillionSendOptions { ApiKey = "bs_x", MaxRetries = -1 }));
        Assert.Throws<ArgumentException>(() => new BillionSendClient(new BillionSendOptions { ApiKey = "bs_x", BaseUrl = "not a url" }));
        Assert.Throws<ArgumentNullException>(() => new BillionSendClient((BillionSendOptions)null!));
    }

    [Fact]
    public async Task EncodesPathParametersAndRefusesEmptyOnes()
    {
        using var client = Client();
        await client.Inboxes.GetAsync("support+team@example.com");
        Assert.Equal("/inboxes/support%2Bteam%40example.com", Assert.Single(Mock.Hits).Url.AbsolutePath);
        var error = await Assert.ThrowsAsync<BillionSendException>(() => client.Emails.GetAsync(""));
        Assert.Null(error.StatusCode);
        Assert.Equal("application_error", error.Name);
        Assert.Equal("Missing path parameter \"id\"", error.Message);
        Assert.Single(Mock.Hits);
    }

    [Fact]
    public async Task SerializesYourOwnTypesInSnakeCaseAndKeepsDictionaryKeys()
    {
        using var client = Client();
        await client.Emails.SendAsync(new Message
        {
            From = "hello@example.com",
            To = ["person@example.com"],
            Subject = "Hi",
            ReplyTo = "support@example.com",
            Headers = new Dictionary<string, string> { ["X-Entity-Ref-ID"] = "42" },
        });
        var body = Assert.Single(Mock.Hits).Json!.AsObject();
        Assert.Equal("support@example.com", (string?)body["reply_to"]);
        Assert.Equal("42", (string?)body["headers"]?["X-Entity-Ref-ID"]);
        Assert.False(body.ContainsKey("ReplyTo"));
    }

    // ---------------------------------------------------------------- errors

    [Fact]
    public async Task PassesTheApisErrorResponseThroughWithTheRequestId()
    {
        Mock.Reply("GET /emails/{id}", 404, ErrorBody(404, "not_found", "Email not found"), new Dictionary<string, string> { ["x-request-id"] = "req_1" });
        using var client = Client();
        var error = await Assert.ThrowsAsync<NotFoundException>(() => client.Emails.GetAsync(Id));
        Assert.IsAssignableFrom<BillionSendException>(error);
        Assert.Equal(404, error.StatusCode);
        Assert.Equal("not_found", error.Name);
        Assert.Equal("Email not found", error.Message);
        Assert.Equal("req_1", error.RequestId);
    }

    [Theory]
    [InlineData(400, "invalid_parameter", typeof(ValidationException))]
    [InlineData(401, "invalid_api_key", typeof(AuthenticationException))]
    [InlineData(403, "restricted_api_key", typeof(PermissionDeniedException))]
    [InlineData(404, "not_found", typeof(NotFoundException))]
    [InlineData(409, "invalid_idempotent_request", typeof(ConflictException))]
    [InlineData(422, "validation_error", typeof(ValidationException))]
    [InlineData(429, "rate_limit_exceeded", typeof(RateLimitException))]
    [InlineData(500, "internal_server_error", typeof(ApiException))]
    [InlineData(418, "teapot", typeof(BillionSendException))]
    public async Task GivesEachStatusItsErrorType(int status, string name, Type kind)
    {
        Mock.Reply("POST /domains", status, ErrorBody(status, name), new Dictionary<string, string> { ["retry-after"] = "7" });
        using var client = Client(o => o.MaxRetries = 0);
        var error = await Assert.ThrowsAnyAsync<BillionSendException>(() => client.Domains.CreateAsync(new { name = "example.com" }));
        Assert.Equal(kind, error.GetType());
        Assert.Equal(status, error.StatusCode);
        Assert.Equal(name, error.Name);
        Assert.Equal(ContractMock.RequestId, error.RequestId);
    }

    [Fact]
    public async Task CarriesRetryAfterOnARateLimitError()
    {
        Mock.Reply("GET /usage", 429, ErrorBody(429, "rate_limit_exceeded", "Slow down"), new Dictionary<string, string> { ["retry-after"] = "120" });
        using var client = Client();
        var error = await Assert.ThrowsAsync<RateLimitException>(() => client.Usage.GetAsync());
        Assert.Equal(120, error.RetryAfter);
    }

    [Fact]
    public async Task MapsANonJsonErrorBodyToApplicationError()
    {
        Mock.ReplyText("GET /emails/{id}", 502, "<html>Bad gateway</html>", "text/html");
        using var client = Client(o => o.MaxRetries = 0);
        var error = await Assert.ThrowsAsync<ApiException>(() => client.Emails.GetAsync(Id));
        Assert.Equal(502, error.StatusCode);
        Assert.Equal("application_error", error.Name);
        Assert.Equal("Unexpected 502 Bad Gateway response: <html>Bad gateway</html>", error.Message);
    }

    [Fact]
    public async Task MapsAnUnreadableSuccessBodyToApplicationError()
    {
        Mock.ReplyText("GET /usage", 200, "not json");
        using var client = Client();
        var error = await Assert.ThrowsAsync<BillionSendException>(() => client.Usage.GetAsync());
        Assert.Equal(200, error.StatusCode);
        Assert.Equal("application_error", error.Name);
        Assert.Equal("The response body is not valid JSON", error.Message);
        Assert.Equal(ContractMock.RequestId, error.RequestId);
    }

    [Fact]
    public async Task MapsANetworkFailureToApiConnectionErrorWithNoStatus()
    {
        using var client = new BillionSendClient(new BillionSendOptions { ApiKey = "bs_x", BaseUrl = "http://127.0.0.1:1", MaxRetries = 0 });
        var error = await Assert.ThrowsAsync<ApiConnectionException>(() => client.Usage.GetAsync());
        Assert.Null(error.StatusCode);
        Assert.Equal("application_error", error.Name);
        Assert.StartsWith("Network error", error.Message);
    }

    [Fact]
    public async Task TimesOut()
    {
        Mock.Slow("GET /usage", TimeSpan.FromMilliseconds(500));
        using var client = Client(o =>
        {
            o.Timeout = TimeSpan.FromMilliseconds(50);
            o.MaxRetries = 0;
        });
        var error = await Assert.ThrowsAsync<ApiTimeoutException>(() => client.Usage.GetAsync());
        Assert.IsAssignableFrom<ApiConnectionException>(error);
        Assert.Null(error.StatusCode);
        Assert.Equal("application_error", error.Name);
        Assert.Equal("Request timed out after 50 ms", error.Message);
    }

    [Fact]
    public async Task StopsOnTheCallersCancellationToken()
    {
        Mock.Slow("GET /usage", TimeSpan.FromMilliseconds(500));
        using var client = Client();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.Usage.GetAsync(cancellationToken: cts.Token));
    }

    // ---------------------------------------------------------------- retries

    private void Busy(string key, string retryAfter = "0") =>
        Mock.Reply(key, 503, ErrorBody(503, "service_unavailable", "Busy"), new Dictionary<string, string> { ["retry-after"] = retryAfter });

    [Fact]
    public async Task RetriesAnIdempotentRequestOn5xxAnd429HonouringRetryAfter()
    {
        Busy("GET /emails/{id}", "2");
        Mock.Reply("GET /emails/{id}", 429, ErrorBody(429, "rate_limit_exceeded"), new Dictionary<string, string> { ["retry-after"] = "0" });
        using var client = Client();
        var delays = new List<TimeSpan>();
        client.Api.Delay = (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        };
        await client.Emails.GetAsync(Id);
        Assert.Equal(3, Mock.HitsFor("GET /emails/{id}").Count);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.Zero], delays);
    }

    [Fact]
    public async Task BacksOffExponentiallyWithoutRetryAfter()
    {
        for (var i = 0; i < 3; i++)
        {
            Mock.Reply("GET /usage", 500, ErrorBody(500, "internal_server_error"));
        }

        using var client = Client(o => o.MaxRetries = 3);
        var delays = new List<TimeSpan>();
        client.Api.Delay = (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        };
        client.Api.Random = () => 0;
        await client.Usage.GetAsync();
        Assert.Equal([TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000), TimeSpan.FromMilliseconds(2000)], delays);
    }

    [Fact]
    public async Task ThrowsTheLastErrorOnceTheRetriesAreSpent()
    {
        for (var i = 0; i < 3; i++)
        {
            Busy("DELETE /emails/{id}");
        }

        using var client = Client();
        var error = await Assert.ThrowsAsync<ApiException>(() => client.Emails.DeleteAsync(Id));
        Assert.Equal(503, error.StatusCode);
        Assert.Equal(3, Mock.HitsFor("DELETE /emails/{id}").Count);
    }

    [Fact]
    public async Task NeverRetriesAPostThatDoesNotHonourIdempotencyKey()
    {
        Busy("POST /domains");
        using var client = Client();
        var error = await Assert.ThrowsAsync<ApiException>(() => client.Domains.CreateAsync(new { name = "example.com" }));
        Assert.Equal(503, error.StatusCode);
        Assert.Single(Mock.HitsFor("POST /domains"));
    }

    [Fact]
    public async Task RetriesASendWithTheKeyItMadeForTheCallOrTheCallers()
    {
        Busy("POST /emails");
        using var client = Client();
        await client.Emails.SendAsync(SendBody);
        var made = Mock.HitsFor("POST /emails").Select(hit => hit.Header("idempotency-key")).ToList();
        Assert.Equal(2, made.Count);
        Assert.Equal(made[0], made[1]);

        Mock.Reset();
        Busy("POST /emails");
        await client.Emails.SendAsync(SendBody, new IdempotentRequestOptions { IdempotencyKey = "order-42" });
        Assert.Equal(["order-42", "order-42"], Mock.HitsFor("POST /emails").Select(hit => hit.Header("idempotency-key")));
    }

    [Fact]
    public async Task RetriesASendWhoseFirstAttemptIsStillRunningOnly()
    {
        var reply = new { text = "Thanks" };
        Mock.Reply("POST /emails/receiving/{id}/reply", 409, ErrorBody(409, "concurrent_idempotent_requests", "Still processing"), new Dictionary<string, string> { ["retry-after"] = "0" });
        using var client = Client();
        await client.Emails.Receiving.ReplyAsync(Id, reply);
        Assert.Equal(2, Mock.HitsFor("POST /emails/receiving/{id}/reply").Count);

        Mock.Reset();
        Mock.Reply("POST /emails/receiving/{id}/reply", 409, ErrorBody(409, "invalid_idempotent_request", "Different payload"));
        await Assert.ThrowsAsync<ConflictException>(() => client.Emails.Receiving.ReplyAsync(Id, reply));
        Assert.Single(Mock.HitsFor("POST /emails/receiving/{id}/reply"));

        // Without a key a 409 is final, even the concurrent one.
        Mock.Reset();
        Mock.Reply("PUT /inboxes/{inbox_id}/threads/{thread_id}/labels", 409, ErrorBody(409, "concurrent_idempotent_requests"));
        await Assert.ThrowsAsync<ConflictException>(() => client.Inboxes.Threads.Labels.SetAsync(Inbox, Id, new { labels = new[] { Id2 } }));
        Assert.Single(Mock.Hits);
    }

    [Fact]
    public async Task RetriesASendAfterANetworkFailureWithTheSameKey()
    {
        Mock.Fail("POST /inboxes/{inbox_id}/emails", new HttpRequestException("connection reset"));
        using var client = Client();
        await client.Inboxes.SendAsync(Inbox, new { to = "x@example.com", subject = "s", text = "t" });
        var keys = Mock.Hits.Select(hit => hit.Header("idempotency-key")).ToList();
        Assert.Equal(2, keys.Count);
        Assert.NotNull(keys[0]);
        Assert.Equal(keys[0], keys[1]);
    }

    [Fact]
    public async Task DoesNotRetry4xxAnswersOtherThan429()
    {
        Mock.Reply("GET /emails/{id}", 422, ErrorBody(422, "validation_error", "Bad"));
        using var client = Client();
        var error = await Assert.ThrowsAsync<ValidationException>(() => client.Emails.GetAsync(Id));
        Assert.Equal("validation_error", error.Name);
        Assert.Single(Mock.Hits);
    }

    [Fact]
    public async Task DoesNotWaitForARetryAfterLongerThanAMinute()
    {
        Busy("GET /usage", "3600");
        using var client = Client();
        var error = await Assert.ThrowsAsync<ApiException>(() => client.Usage.GetAsync());
        Assert.Equal(503, error.StatusCode);
        Assert.Single(Mock.Hits);
    }

    [Fact]
    public async Task RespectsMaxRetriesPerClientAndPerRequest()
    {
        Busy("GET /usage");
        using var client = Client(o => o.MaxRetries = 0);
        await Assert.ThrowsAsync<ApiException>(() => client.Usage.GetAsync());
        Busy("GET /usage");
        await client.Usage.GetAsync(new RequestOptions { MaxRetries = 1 });
        Assert.Equal(3, Mock.Hits.Count);
    }

    [Fact]
    public async Task RetriesATimedOutIdempotentRequest()
    {
        Mock.Slow("GET /usage", TimeSpan.FromMilliseconds(500));
        using var client = Client(o =>
        {
            o.Timeout = TimeSpan.FromMilliseconds(100);
            o.MaxRetries = 1;
        });
        await client.Usage.GetAsync();
        Assert.Equal(2, Mock.Hits.Count);
    }

    private sealed class Message
    {
        public string From { get; set; } = "";

        public string[] To { get; set; } = [];

        public string Subject { get; set; } = "";

        public string? ReplyTo { get; set; }

        public Dictionary<string, string>? Headers { get; set; }
    }

    private sealed class RecordingHandler(HttpMessageHandler inner, List<string> calls) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            calls.Add($"{request.Method} {request.RequestUri}");
            return base.SendAsync(request, cancellationToken);
        }
    }
}
