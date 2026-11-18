using System.Net;
using System.Text;
using BillionSend.Tests.Support;

namespace BillionSend.E2E;

/// <summary>The live run's own safeguards, tried offline: if they did nothing, a clean live run would mean nothing.</summary>
public sealed class HarnessTests
{
    private sealed class Answers(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Json(int status, string body, string? retryAfter = null)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (retryAfter is not null)
        {
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        }

        return response;
    }

    private static BillionSendClient Client(LiveHandler handler, int maxRetries = 0) => new(new BillionSendOptions
    {
        ApiKey = "bs_harness_key_0123456789",
        BaseUrl = "https://api.test",
        MaxRetries = maxRetries,
        HttpClient = new HttpClient(handler, disposeHandler: false),
    });

    [Fact]
    public async Task RecordsAnAnswerThatBreaksTheOperationsSchemaAndAnUndeclaredStatus()
    {
        var inner = new Answers(request => request.RequestUri!.AbsolutePath == "/usage" ? Json(200, "{\"object\":\"usage\"}") : Json(201, "{}"));
        var handler = new LiveHandler(inner, TimeSpan.Zero, 10, 12);
        using var client = Client(handler);
        await client.Usage.GetAsync();
        await client.Domains.DeleteAsync("d");
        Assert.Contains(handler.Findings, f => f.StartsWith("GET /usage: answer 200"));
        Assert.Contains(handler.Findings, f => f.Contains("answered 201, which the document does not declare"));
    }

    [Fact]
    public async Task RecordsARequestThatBreaksTheSchemaAndARouteTheDocumentDoesNotHave()
    {
        var inner = new Answers(_ => Json(200, "{}"));
        var handler = new LiveHandler(inner, TimeSpan.Zero, 10, 12);
        using var client = Client(handler);
        await client.Domains.CreateAsync(new { name = 42 });
        using var raw = new HttpClient(handler, disposeHandler: false);
        await raw.GetAsync("https://api.test/nowhere");
        Assert.Contains(handler.Findings, f => f.StartsWith("POST /domains: request:"));
        Assert.Contains(handler.Findings, f => f.Contains("/nowhere") && f.Contains("route the document does not have"));
    }

    [Fact]
    public async Task AcceptsAnAnswerThatMatchesTheSchemaAndNeverWritesTheKeyOrABodyInTheLog()
    {
        var operation = Spec.Get("GET /usage");
        var inner = new Answers(_ => Json(200, Spec.Example(operation.ResponseSchema, full: true)!.ToJsonString()));
        var handler = new LiveHandler(inner, TimeSpan.Zero, 10, 12);
        using var client = Client(handler);
        await client.Usage.GetAsync();
        Assert.Empty(handler.Findings);
        Assert.Equal(["001 GET /usage -> 200"], handler.Log);
        Assert.DoesNotContain(handler.Log.Concat(handler.Findings), line => line.Contains("bs_harness"));
    }

    [Fact]
    public async Task StopsAfterTwo429AnswersInARowAndSendsNothingMore()
    {
        var inner = new Answers(_ => Json(429, "{\"statusCode\":429,\"name\":\"rate_limit_exceeded\",\"message\":\"slow\"}", "0"));
        var handler = new LiveHandler(inner, TimeSpan.Zero, 10, 12);
        using var client = Client(handler, maxRetries: 5);
        await Assert.ThrowsAsync<LiveAbort>(() => client.Usage.GetAsync());
        Assert.True(handler.Aborted);
        Assert.Equal(2, inner.Calls);
        await Assert.ThrowsAsync<LiveAbort>(() => client.Usage.GetAsync());
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task AllowsOneOf429AndCountsAgainAfterAnAnswerThatIsNot()
    {
        var calls = 0;
        var inner = new Answers(_ => ++calls switch
        {
            1 or 3 => Json(429, "{\"statusCode\":429,\"name\":\"rate_limit_exceeded\",\"message\":\"slow\"}", "0"),
            _ => Json(200, Spec.Example(Spec.Get("GET /usage").ResponseSchema, full: true)!.ToJsonString()),
        });
        var handler = new LiveHandler(inner, TimeSpan.Zero, 10, 12);
        using var client = Client(handler, maxRetries: 3);
        await client.Usage.GetAsync();
        await client.Usage.GetAsync();
        Assert.False(handler.Aborted);
        Assert.Equal(4, handler.Count);
    }

    [Fact]
    public async Task StopsAtTheScenarioBudgetAndLeavesTheRestForTheCleanUp()
    {
        var inner = new Answers(_ => Json(200, Spec.Example(Spec.Get("GET /usage").ResponseSchema, full: true)!.ToJsonString()));
        var handler = new LiveHandler(inner, TimeSpan.Zero, 2, 4);
        using var client = Client(handler);
        await client.Usage.GetAsync();
        await client.Usage.GetAsync();
        Assert.False(handler.BudgetLeft);
        await Assert.ThrowsAsync<LiveAbort>(() => client.Usage.GetAsync());
        handler.Cleaning = true;
        await client.Usage.GetAsync();
        await client.Usage.GetAsync();
        await Assert.ThrowsAsync<LiveAbort>(() => client.Usage.GetAsync());
        Assert.Equal(4, inner.Calls);
    }

    [Fact]
    public async Task KeepsAtLeastTheGapBetweenRequests()
    {
        var inner = new Answers(_ => Json(200, Spec.Example(Spec.Get("GET /usage").ResponseSchema, full: true)!.ToJsonString()));
        var handler = new LiveHandler(inner, TimeSpan.FromMilliseconds(250), 10, 12);
        using var client = Client(handler);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await client.Usage.GetAsync();
        await client.Usage.GetAsync();
        await client.Usage.GetAsync();
        Assert.True(watch.ElapsedMilliseconds >= 480, $"{watch.ElapsedMilliseconds} ms for three requests");
    }

    [Fact]
    public void TakesSecretsOutOfText()
    {
        Environment.SetEnvironmentVariable(LiveFactAttribute.KeyVariable, "k-secret-value-123");
        try
        {
            var text = Redact.Text("key k-secret-value-123 token bs_AbCd1234567890 secret whsec_AAAAAAAAAAAAAAAAAAAAAAAA header Bearer abc.def ok");
            Assert.Equal("key [redacted] token [redacted] secret [redacted] header [redacted] ok", text);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LiveFactAttribute.KeyVariable, null);
        }
    }
}
