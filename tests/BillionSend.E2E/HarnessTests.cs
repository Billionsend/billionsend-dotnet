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

    private static object Mail(object to, object? cc = null, object? bcc = null) =>
        new { from = "SDK E2E <sdk-e2e@mail.billionsend.dev>", to, cc, bcc, subject = "s", text = "t" };

    private static (LiveHandler Handler, Answers Inner, BillionSendClient Client) Guarded()
    {
        var inner = new Answers(_ => Json(200, "{}"));
        var handler = new LiveHandler(inner, TimeSpan.Zero, 20, 22);
        return (handler, inner, Client(handler));
    }

    [Theory]
    [InlineData("bounce@simulator.amazonses.com")]
    [InlineData("complaint@simulator.amazonses.com")]
    [InlineData("suppressionlist@simulator.amazonses.com")]
    [InlineData("ooto@simulator.amazonses.com")]
    [InlineData("ana@example.com")]
    [InlineData("success@simulator.amazonses.com.evil.test")]
    [InlineData("evil-success@simulator.amazonses.com")]
    [InlineData("Ana <ana@example.com>")]
    [InlineData("success+a b@simulator.amazonses.com")]
    [InlineData("")]
    public async Task RefusesAnyRecipientButTheSimulatorsSuccessMailboxBeforeARequestIsSent(string address)
    {
        var (handler, inner, client) = Guarded();
        await Assert.ThrowsAsync<LiveGuardException>(() => client.Emails.SendAsync(Mail(address)));
        await Assert.ThrowsAsync<LiveGuardException>(() => client.Emails.SendAsync(Mail("success@simulator.amazonses.com", cc: new[] { address })));
        await Assert.ThrowsAsync<LiveGuardException>(() => client.Emails.SendAsync(Mail("success@simulator.amazonses.com", bcc: address)));
        await Assert.ThrowsAsync<LiveGuardException>(() => client.Emails.BatchAsync(new[] { Mail("success@simulator.amazonses.com"), Mail(new[] { "success@simulator.amazonses.com", address }) }));
        Assert.Equal(0, inner.Calls);
        Assert.Equal(0, handler.Count);
        Assert.Equal(4, handler.Refused.Count);
    }

    [Theory]
    [InlineData("success@simulator.amazonses.com")]
    [InlineData("success+sdk-e2e-dotnet-1a2b3c4d-mail@simulator.amazonses.com")]
    [InlineData("SUCCESS+Tag@Simulator.Amazonses.com")]
    [InlineData("SDK E2E <success+x@simulator.amazonses.com>")]
    public async Task SendsToTheSuccessMailboxAndItsPlusAliases(string address)
    {
        var (handler, inner, client) = Guarded();
        await client.Emails.SendAsync(Mail(address));
        await client.Emails.BatchAsync(new[] { Mail(new[] { address }, cc: address) });
        Assert.Equal(2, inner.Calls);
        Assert.Empty(handler.Refused);
    }

    [Fact]
    public async Task NeverSendsToAnAddressTheRunPutOnTheSuppressionList()
    {
        var (handler, inner, client) = Guarded();
        const string single = "success+sdk-e2e-dotnet-x-sup1@simulator.amazonses.com";
        const string batch = "success+sdk-e2e-dotnet-x-sup2@simulator.amazonses.com";
        const string clean = "success+sdk-e2e-dotnet-x-ok@simulator.amazonses.com";
        await client.Suppressions.CreateAsync(new { email = single, origin = "manual" });
        await client.Suppressions.Batch.AddAsync(new { emails = new[] { batch }, origin = "manual" });
        Assert.Equal(2, handler.Guard.Suppressed.Count);
        foreach (var address in new[] { single, batch, single.ToUpperInvariant(), $"Ana <{batch}>" })
        {
            await Assert.ThrowsAsync<LiveGuardException>(() => client.Emails.SendAsync(Mail(address)));
            await Assert.ThrowsAsync<LiveGuardException>(() => client.Emails.SendAsync(Mail(clean, cc: new[] { address })));
            await Assert.ThrowsAsync<LiveGuardException>(() => client.Emails.SendAsync(Mail(clean, bcc: address)));
            await Assert.ThrowsAsync<LiveGuardException>(() => client.Emails.BatchAsync(new[] { Mail(clean), Mail(address) }));
        }

        // Taking it off the list again does not make it a recipient: the run never mails it.
        await client.Suppressions.Batch.RemoveAsync(new { emails = new[] { single, batch } });
        await Assert.ThrowsAsync<LiveGuardException>(() => client.Emails.SendAsync(Mail(single)));
        await client.Emails.SendAsync(Mail(clean));
        Assert.Equal(4, inner.Calls);
        Assert.Equal(17, handler.Refused.Count);
    }

    [Fact]
    public async Task NeverSendsToADomainTheRunPutOnTheSuppressionList()
    {
        var (handler, inner, client) = Guarded();
        const string clean = "success+sdk-e2e-dotnet-x-dom@simulator.amazonses.com";
        // An entry for a domain has no email: it is a valid body, and it blocks nothing the run sends to.
        await client.Suppressions.CreateAsync(new { domain = "other.example", origin = "manual" });
        await client.Suppressions.Batch.AddAsync(new { domains = new[] { "another.example" } });
        Assert.Empty(handler.Guard.Suppressed);
        Assert.Equal(2, handler.Guard.SuppressedDomains.Count);
        await client.Emails.SendAsync(Mail(clean));
        Assert.Equal(3, inner.Calls);

        // A domain that holds the run's recipients is never mailed afterwards, however it was suppressed.
        await client.Suppressions.CreateAsync(new { domain = "Simulator.Amazonses.com" });
        await Assert.ThrowsAsync<LiveGuardException>(() => client.Emails.SendAsync(Mail(clean)));
        await Assert.ThrowsAsync<LiveGuardException>(() => client.Emails.BatchAsync(new[] { Mail(clean) }));
        Assert.Equal(4, inner.Calls);
        Assert.Equal(2, handler.Refused.Count);
    }

    [Theory]
    [InlineData("POST", "/emails", "not json")]
    [InlineData("POST", "/emails", "\"success@simulator.amazonses.com\"")]
    [InlineData("POST", "/emails", "{\"to\": ")]
    [InlineData("POST", "/emails/batch", "[1, 2]")]
    [InlineData("POST", "/emails/batch", "[\"success@simulator.amazonses.com\"]")]
    [InlineData("POST", "/emails", "{\"to\": 5}")]
    [InlineData("POST", "/emails", "{\"to\": {\"email\": \"success@simulator.amazonses.com\"}}")]
    [InlineData("POST", "/emails", "{\"to\": [\"success@simulator.amazonses.com\", null]}")]
    [InlineData("POST", "/emails", "{\"to\": \"success@simulator.amazonses.com\", \"cc\": [[\"success@simulator.amazonses.com\"]]}")]
    [InlineData("POST", "/emails", "{\"to\": \"success@simulator.amazonses.com\", \"bcc\": false}")]
    [InlineData("POST", "/suppressions", "not json")]
    [InlineData("POST", "/suppressions/batch/add", "[")]
    [InlineData("POST", "/elsewhere", "not json")]
    public async Task RefusesASendOrASuppressionWhoseBodyOrRecipientsCannotBeChecked(string method, string path, string body)
    {
        var inner = new Answers(_ => Json(200, "{}"));
        var handler = new LiveHandler(inner, TimeSpan.Zero, 20, 22);
        using var raw = new HttpClient(handler, disposeHandler: false);
        using var request = new HttpRequestMessage(new HttpMethod(method), "https://api.test" + path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        await Assert.ThrowsAsync<LiveGuardException>(() => raw.SendAsync(request));
        Assert.Equal(0, inner.Calls);
        Assert.Equal(0, handler.Count);
        Assert.Single(handler.Refused);
    }

    [Fact]
    public async Task LeavesAnUnreadableBodyAloneWhenTheRequestIsNotASend()
    {
        // events.send takes the caller's own payload; a request with no body has no recipient.
        var inner = new Answers(_ => Json(200, "{}"));
        var handler = new LiveHandler(inner, TimeSpan.Zero, 20, 22);
        using var raw = new HttpClient(handler, disposeHandler: false);
        using var events = new HttpRequestMessage(HttpMethod.Post, "https://api.test/events/send") { Content = new StringContent("not json", Encoding.UTF8, "application/json") };
        await raw.SendAsync(events);
        using var empty = new HttpRequestMessage(HttpMethod.Post, "https://api.test/emails");
        await raw.SendAsync(empty);
        Assert.Equal(2, inner.Calls);
        Assert.Empty(handler.Refused);
    }

    [Fact]
    public async Task LeavesRequestsThatAreNotSendsAlone()
    {
        // events.send has a payload of the caller's own keys; an automation's connections have a "to": neither is a recipient.
        var (handler, inner, client) = Guarded();
        await client.Events.SendAsync(new { @event = "e", contact_id = Spec.SampleUuid, payload = new { to = "ana@example.com" } });
        await client.Segments.CreateAsync(new { name = "to ana@example.com" });
        Assert.Equal(2, inner.Calls);
        Assert.Empty(handler.Refused);
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
