using System.Globalization;
using System.Net;
using System.Text;
using BillionSend.Internal;
using BillionSend.Tests.Support;

namespace BillionSend.Tests;

/// <summary>What the hand-written runtime does on the wire: encoding, bodies, headers, empty and odd answers.</summary>
public sealed class WireTests
{
    [Theory]
    [InlineData("a/b", "a%2Fb")]
    [InlineData("a b", "a%20b")]
    [InlineData("a.b", "a.b")]
    [InlineData("é+ü@x.com", "%C3%A9%2B%C3%BC%40x.com")]
    [InlineData("a?b#c", "a%3Fb%23c")]
    [InlineData("100%", "100%25")]
    [InlineData("a\\b", "a%5Cb")]
    [InlineData("x;y=z&w", "x%3By%3Dz%26w")]
    [InlineData("\U0001F642", "%F0%9F%99%82")]
    public async Task EncodesAPathParameterAsOneSegment(string id, string encoded)
    {
        var probe = new Probe();
        using var client = probe.Client();
        await client.Segments.GetAsync(id);
        Assert.Equal($"/segments/{encoded}", Assert.Single(probe.Requests).Target);
    }

    [Fact]
    public async Task EncodesEveryPathParameterOfANestedRouteSeparately()
    {
        var probe = new Probe();
        using var client = probe.Client();
        await client.Inboxes.Threads.Emails.GetAsync("a/b@x.com", "t 1", "e.1..2");
        Assert.Equal("/inboxes/a%2Fb%40x.com/threads/t%201/emails/e.1..2", Assert.Single(probe.Requests).Target);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public async Task RefusesADotSegmentThatTheUrlParserWouldTurnIntoAnotherRoute(string id)
    {
        var probe = new Probe();
        using var client = probe.Client();
        var error = await Assert.ThrowsAsync<BillionSendException>(() => client.Segments.DeleteAsync(id));
        Assert.Equal("Invalid path parameter \"id\": a dot segment", error.Message);
        Assert.Null(error.StatusCode);
        Assert.Empty(probe.Requests);
    }

    [Fact]
    public void EncodesQueryValuesAndKeysAndWritesNumbersAndDatesWithoutACulture()
    {
        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var at = new DateTimeOffset(2026, 11, 19, 8, 30, 0, TimeSpan.FromHours(8));
            var query = Urls.QueryString(new Dictionary<string, object?>
            {
                ["q"] = "ana+bob@x.com & co = 100%",
                ["név"] = "é",
                ["ratio"] = 1.5,
                ["count"] = 7L,
                ["since"] = at,
                ["day"] = new DateTime(2026, 11, 19, 0, 0, 0, DateTimeKind.Utc),
                ["flag"] = false,
                ["mode"] = DayOfWeek.Friday,
                ["json"] = JsonValue.Create("text"),
                ["node"] = new JsonObject { ["a"] = 1 },
                ["ids"] = new object?[] { "a", null, 2, true },
                ["none"] = null,
            });
            Assert.Equal(
                "?q=ana%2Bbob%40x.com%20%26%20co%20%3D%20100%25&n%C3%A9v=%C3%A9&ratio=1.5&count=7"
                + "&since=2026-11-19T08%3A30%3A00.0000000%2B08%3A00&day=2026-11-19T00%3A00%3A00.0000000Z"
                + "&flag=false&mode=Friday&json=text&node=%7B%22a%22%3A1%7D&ids=a%2C2%2Ctrue",
                query);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public async Task SendsTheQueryOnTheWireAsTheEncodedStringWithListsInOneKey()
    {
        var probe = new Probe();
        using var client = probe.Client();
        await client.Emails.Metrics.GetAsync(new Dictionary<string, object?>
        {
            ["metrics"] = new[] { "delivered", "opened" },
            ["domain_id"] = new[] { "d1" },
            ["start_date"] = "2026-11-01",
        });
        Assert.Equal(
            "/emails/metrics?metrics=delivered%2Copened&domain_id=d1&start_date=2026-11-01",
            Assert.Single(probe.Requests).Target);
    }

    [Theory]
    [InlineData(200, "")]
    [InlineData(200, "  \n")]
    [InlineData(204, "")]
    public async Task AnswersNullForASuccessWithNoBody(int status, string body)
    {
        var probe = new Probe { Answer = _ => Probe.Reply(status, body) };
        using var client = probe.Client();
        Assert.Null(await client.Segments.DeleteAsync("s"));
    }

    [Fact]
    public async Task AnswersNullForANoContentResponseWithNoContentType()
    {
        var probe = new Probe { Answer = _ => new HttpResponseMessage(HttpStatusCode.NoContent) };
        using var client = probe.Client();
        Assert.Null(await client.Segments.DeleteAsync("s"));
    }

    [Fact]
    public async Task KeepsAnExplicitNullOfAJsonObjectOrDictionaryAndLeavesOutAnUnsetPropertyOfYourOwnType()
    {
        var probe = new Probe();
        using var client = probe.Client();
        await client.Contacts.UpdateAsync("c", new JsonObject { ["first_name"] = null, ["last_name"] = "L" });
        await client.Contacts.UpdateAsync("c", new Dictionary<string, object?> { ["first_name"] = null });
        await client.Contacts.UpdateAsync("c", new Person { FirstName = null, LastName = "L" });
        Assert.Equal("{\"first_name\":null,\"last_name\":\"L\"}", probe.Requests[0].Text);
        Assert.Equal("{\"first_name\":null}", probe.Requests[1].Text);
        Assert.Equal("{\"last_name\":\"L\"}", probe.Requests[2].Text);
    }

    [Fact]
    public async Task CarriesTextThatNeedsEscapingAndALargeBodyUnchanged()
    {
        var probe = new Probe();
        using var client = probe.Client();
        var html = new string('x', 2_000_000) + "é \"q\" \\ \n \U0001F600 </script>";
        await client.Emails.SendAsync(new { from = "a@x.com", to = "b@x.com", subject = "s", html });
        var body = JsonNode.Parse(probe.Requests[0].Body!)!;
        Assert.Equal(html, (string)body["html"]!);
        Assert.Equal("application/json", probe.Requests[0].Header("content-type"));
        Assert.Equal(probe.Requests[0].Body!.Length.ToString(CultureInfo.InvariantCulture), probe.Requests[0].Header("content-length"));
    }

    [Fact]
    public async Task SendsNoBodyAndNoContentTypeOnAGetAndTheKeyAsABearerToken()
    {
        var probe = new Probe();
        using var client = probe.Client();
        await client.Segments.GetAsync("s");
        var request = Assert.Single(probe.Requests);
        Assert.Null(request.Body);
        Assert.Null(request.Header("content-type"));
        Assert.Equal("Bearer bs_probe", request.Header("authorization"));
        Assert.Equal($"billionsend-dotnet/{BillionSendClient.SdkVersion}", request.Header("user-agent"));
        Assert.Equal("application/json", request.Header("accept"));
    }

    [Fact]
    public async Task LetsACallOverrideAClientHeaderAndDropsAuthorizationFromAKeylessOperation()
    {
        var probe = new Probe();
        using var client = probe.Client(o => o.Headers = new Dictionary<string, string> { ["X-A"] = "client", ["User-Agent"] = "mine/1" });
        await client.Segments.GetAsync("s", new RequestOptions { Headers = new Dictionary<string, string> { ["x-a"] = "call" } });
        var request = Assert.Single(probe.Requests);
        Assert.Equal("call", request.Header("x-a"));
        Assert.Equal("mine/1", request.Header("user-agent"));
    }

    [Fact]
    public async Task PutsAContentHeaderGivenAsAnExtraHeaderOnTheBody()
    {
        var probe = new Probe();
        using var client = probe.Client();
        await client.Contacts.UpdateAsync(
            "c",
            new { first_name = "A" },
            new RequestOptions { Headers = new Dictionary<string, string> { ["Content-Language"] = "fr", ["X-B"] = "1" } });
        var request = Assert.Single(probe.Requests);
        Assert.Equal("fr", request.Header("content-language"));
        Assert.Equal("1", request.Header("x-b"));
    }

    [Fact]
    public async Task MapsABodyReadFailureToAConnectionErrorAndLeavesOtherExceptionsAlone()
    {
        var probe = new Probe
        {
            Answer = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new BrokenContent() },
        };
        using var client = probe.Client(o => o.MaxRetries = 0);
        var error = await Assert.ThrowsAsync<ApiConnectionException>(() => client.Segments.GetAsync("s"));
        Assert.Null(error.StatusCode);
        Assert.StartsWith("Network error: ", error.Message);
        Assert.NotNull(error.InnerException);

        probe.Answer = _ => throw new IOException("broken pipe");
        var direct = await Assert.ThrowsAsync<ApiConnectionException>(() => client.Segments.GetAsync("s"));
        Assert.Equal("Network error: broken pipe", direct.Message);

        probe.Answer = _ => throw new InvalidOperationException("not ours");
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.Segments.GetAsync("s"));

        probe.Answer = _ => throw new HttpRequestException("reset", new IOException("by peer"));
        var wrapped = await Assert.ThrowsAsync<ApiConnectionException>(() => client.Segments.GetAsync("s"));
        Assert.Equal("Network error: reset (by peer)", wrapped.Message);
    }

    [Theory]
    [InlineData("{\"statusCode\":500,\"name\":5,\"message\":\"x\"}")]
    [InlineData("{\"statusCode\":500,\"name\":\"boom\"}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("")]
    public async Task FallsBackToTheGenericErrorForABodyThatIsNotTheApisErrorShape(string body)
    {
        var probe = new Probe { Answer = _ => Probe.Reply(500, body) };
        using var client = probe.Client(o => o.MaxRetries = 0);
        var error = await Assert.ThrowsAsync<ApiException>(() => client.Segments.GetAsync("s"));
        Assert.Equal("application_error", error.Name);
        Assert.Equal(500, error.StatusCode);
        Assert.Equal("req_probe", error.RequestId);
        Assert.StartsWith("Unexpected 500 Internal Server Error response", error.Message);
    }

    [Fact]
    public async Task CutsALongNonJsonErrorBodyShort()
    {
        var probe = new Probe { Answer = _ => Probe.Reply(502, "<html>" + new string('x', 5000) + "</html>") };
        using var client = probe.Client(o => o.MaxRetries = 0);
        var error = await Assert.ThrowsAsync<ApiException>(() => client.Segments.GetAsync("s"));
        Assert.InRange(error.Message.Length, 200, 260);
    }

    [Fact]
    public async Task ReadsARetryAfterHttpDateAgainstTheClockOnAnErrorAndOnTheRetryWait()
    {
        var now = DateTimeOffset.Parse("2026-11-19T12:00:00Z");
        var probe = new Probe
        {
            Answer = _ =>
            {
                var response = Probe.Reply(429, "{\"statusCode\":429,\"name\":\"rate_limit_exceeded\",\"message\":\"slow\"}");
                response.Headers.TryAddWithoutValidation("Retry-After", "Thu, 19 Nov 2026 12:00:07 GMT");
                return response;
            },
        };
        using var client = probe.Client(o => o.MaxRetries = 1);
        client.Api.Now = () => now;
        var waits = new List<TimeSpan>();
        client.Api.Delay = (wait, _) =>
        {
            waits.Add(wait);
            return Task.CompletedTask;
        };
        var error = await Assert.ThrowsAsync<RateLimitException>(() => client.Segments.GetAsync("s"));
        Assert.Equal(7, error.RetryAfter);
        Assert.Equal([TimeSpan.FromSeconds(7)], waits);
        Assert.Equal(2, probe.Requests.Count);
    }

    [Fact]
    public async Task RetriesAnHtmlBadGatewayThenReturnsTheNextAnswer()
    {
        var calls = 0;
        var probe = new Probe
        {
            Answer = _ => ++calls < 3
                ? new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>502</html>", Encoding.UTF8, "text/html") }
                : Probe.Reply(200, "{\"id\":\"s\"}"),
        };
        using var client = probe.Client();
        Assert.Equal("s", (string)(await client.Segments.GetAsync("s"))!["id"]!);
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData("DELETE", true)]
    [InlineData("PUT", true)]
    [InlineData("GET", true)]
    [InlineData("PATCH", false)]
    [InlineData("POST", false)]
    public async Task RetriesOnlyTheMethodsThatAreSafeToRepeat(string method, bool retried)
    {
        var probe = new Probe { Answer = _ => Probe.Reply(503, "{\"statusCode\":503,\"name\":\"unavailable\",\"message\":\"busy\"}") };
        using var client = probe.Client();
        var call = method switch
        {
            "DELETE" => client.Segments.DeleteAsync("s"),
            "PUT" => client.Inboxes.Threads.Labels.SetAsync("i", "t", new { labels = new[] { "l" } }),
            "GET" => client.Segments.GetAsync("s"),
            "PATCH" => client.Contacts.UpdateAsync("c", new { first_name = "A" }),
            _ => client.Domains.CreateAsync(new { name = "x.com" }),
        };
        await Assert.ThrowsAsync<ApiException>(() => call);
        Assert.Equal(retried ? 3 : 1, probe.Requests.Count);
    }

    [Fact]
    public async Task RetriesABatchSendWithItsKeyAndItsValidationHeader()
    {
        var calls = 0;
        var probe = new Probe
        {
            Answer = _ => ++calls == 1
                ? Probe.Reply(500, "{\"statusCode\":500,\"name\":\"internal_server_error\",\"message\":\"x\"}")
                : Probe.Reply(200, "{\"data\":[]}"),
        };
        using var client = probe.Client();
        await client.Emails.BatchAsync(
            new object[] { new { from = "a@x.com", to = "b@x.com", subject = "s", text = "t" } },
            new BatchSendOptions { IdempotencyKey = "batch/1", BatchValidation = BatchValidation.Permissive });
        Assert.Equal(["batch/1", "batch/1"], probe.Requests.Select(r => r.Header("idempotency-key")));
        Assert.All(probe.Requests, r => Assert.Equal("permissive", r.Header("x-batch-validation")));
        Assert.Equal(probe.Requests[0].Text, probe.Requests[1].Text);
    }

    [Fact]
    public async Task StopsWaitingForARetryWhenTheCallerCancels()
    {
        var probe = new Probe { Answer = _ => Probe.Reply(503, "{\"statusCode\":503,\"name\":\"unavailable\",\"message\":\"busy\"}") };
        using var client = probe.Client();
        using var cts = new CancellationTokenSource();
        client.Api.Delay = async (wait, token) =>
        {
            cts.Cancel();
            await Task.Delay(wait, token);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.Segments.GetAsync("s", cancellationToken: cts.Token));
        Assert.Single(probe.Requests);
    }

    [Fact]
    public async Task LetsACallTimeOutOnItsOwnTimeoutNotTheClients()
    {
        var probe = new Probe();
        using var client = new BillionSendClient(new BillionSendOptions
        {
            ApiKey = "bs_x",
            BaseUrl = "https://probe.test",
            Timeout = TimeSpan.FromMinutes(5),
            MaxRetries = 0,
            HttpClient = new HttpClient(new Stalled()),
        });
        var error = await Assert.ThrowsAsync<ApiTimeoutException>(() =>
            client.Segments.GetAsync("s", new RequestOptions { Timeout = TimeSpan.FromMilliseconds(40) }));
        Assert.Equal("Request timed out after 40 ms", error.Message);
        Assert.Empty(probe.Requests);
    }

    [Fact]
    public async Task SharesOneClientAcrossParallelCallsWithEachItsOwnIdempotencyKey()
    {
        var probe = new Probe();
        using var client = probe.Client();
        await Task.WhenAll(Enumerable.Range(0, 40).Select(i =>
            client.Emails.SendAsync(new { from = "a@x.com", to = "b@x.com", subject = $"s{i}", text = "t" })));
        Assert.Equal(40, probe.Requests.Select(r => r.Header("idempotency-key")).Distinct().Count());
        Assert.Equal(40, probe.Requests.Select(r => JsonNode.Parse(r.Body!)!["subject"]!.ToString()).Distinct().Count());
    }

    [Fact]
    public void RefusesABaseUrlThatIsNotHttpAndKeepsAPathPrefix()
    {
        Assert.Throws<ArgumentException>(() => new BillionSendClient(new BillionSendOptions { ApiKey = "bs_x", BaseUrl = "ftp://x.test" }));
        using var client = new BillionSendClient(new BillionSendOptions { ApiKey = "bs_x", BaseUrl = "https://x.test/api///" });
        Assert.Equal("https://x.test/api", client.BaseUrl);
    }

    [Fact]
    public async Task PutsAPathPrefixOfTheBaseUrlInFrontOfEveryRoute()
    {
        var probe = new Probe();
        using var client = probe.Client(o => o.BaseUrl = "https://probe.test/api/");
        await client.Segments.GetAsync("s");
        Assert.Equal("/api/segments/s", Assert.Single(probe.Requests).Target);
    }

    [Fact]
    public void DisposesTheHttpClientOnlyWhenItMadeIt()
    {
        var disposed = false;
        var mine = new HttpClient(new Disposing(() => disposed = true));
        new BillionSendClient(new BillionSendOptions { ApiKey = "bs_x", HttpClient = mine }).Dispose();
        Assert.False(disposed);
        using (new BillionSendClient("bs_x"))
        {
        }

        mine.Dispose();
        Assert.True(disposed);
    }

    private sealed class Person
    {
        public string? FirstName { get; set; }

        public string? LastName { get; set; }
    }

    private sealed class BrokenContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.FromException(new IOException("connection reset"));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => throw new IOException("connection reset");
    }

    private sealed class Stalled : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class Disposing(Action onDispose) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage());

        protected override void Dispose(bool disposing)
        {
            onDispose();
            base.Dispose(disposing);
        }
    }
}

/// <summary>Multipart bodies on the wire.</summary>
public sealed class MultipartWireTests
{
    private static async Task<Probe.Seen> Upload(Dictionary<string, object?> form)
    {
        var probe = new Probe();
        using var client = probe.Client();
        await client.Contacts.Imports.CreateAsync(form);
        return Assert.Single(probe.Requests);
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int from = 0)
    {
        for (var i = from; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return i;
            }
        }

        return -1;
    }

    [Fact]
    public async Task WritesANonAsciiFileNameAsRawUtf8AndNotAnEncodedWord()
    {
        var seen = await Upload(new() { ["file"] = new FileUpload("联系人 é.csv", "Email\nx@y.z\n"u8.ToArray()) });
        Assert.Contains("Content-Disposition: form-data; name=\"file\"; filename=\"联系人 é.csv\"", seen.Text);
        Assert.DoesNotContain("=?utf-8?", seen.Text);
        Assert.DoesNotContain("filename*=", seen.Text);
    }

    [Fact]
    public async Task PercentEncodesAQuoteAndLineBreaksInAFileName()
    {
        var seen = await Upload(new() { ["file"] = new FileUpload("a \"b\"\r\nc.csv", "x"u8.ToArray()) });
        Assert.Contains("filename=\"a %22b%22%0D%0Ac.csv\"", seen.Text);
    }

    [Fact]
    public async Task SendsTheFileBytesExactlyWithItsContentTypeAndTextFieldsAsTheirText()
    {
        var bytes = Enumerable.Range(0, 256).Select(i => (byte)i).Concat("\r\n--not-a-boundary\r\n"u8.ToArray()).ToArray();
        var seen = await Upload(new()
        {
            ["file"] = new FileUpload("data.bin", bytes, "application/octet-stream"),
            ["on_conflict"] = "skip",
            ["count"] = 7,
            ["ratio"] = 1.5,
            ["flag"] = true,
            ["off"] = false,
            ["column_map"] = new { email = "Email" },
            ["segments"] = new[] { new { id = "s1" } },
            ["raw"] = JsonValue.Create("plain"),
            ["tree"] = new JsonObject { ["a"] = 1 },
            ["left_out"] = null,
        });
        Assert.StartsWith("multipart/form-data; boundary=", seen.Header("content-type"));
        Assert.True(IndexOf(seen.Body!, bytes) > 0);
        var text = seen.Text;
        Assert.Contains("Content-Type: application/octet-stream", text);
        foreach (var (name, value) in new[]
        {
            ("on_conflict", "skip"), ("count", "7"), ("ratio", "1.5"), ("flag", "true"), ("off", "false"),
            ("column_map", "{\"email\":\"Email\"}"), ("segments", "[{\"id\":\"s1\"}]"), ("raw", "plain"), ("tree", "{\"a\":1}"),
        })
        {
            Assert.Matches($"name=\"?{name}\"?\r\n(Content-Type: [^\r\n]*\r\n)?\r\n{System.Text.RegularExpressions.Regex.Escape(value)}\r\n", text);
        }

        Assert.DoesNotContain("left_out", text);
    }

    [Fact]
    public async Task UploadsAFileOfSeveralMegabytesIntact()
    {
        var bytes = new byte[8 * 1024 * 1024];
        new Random(7).NextBytes(bytes);
        var seen = await Upload(new() { ["file"] = new FileUpload("big.csv", bytes) });
        var start = IndexOf(seen.Body!, bytes[..64]);
        Assert.True(start > 0);
        Assert.True(seen.Body!.AsSpan(start, bytes.Length).SequenceEqual(bytes));
        Assert.Equal(seen.Body!.Length.ToString(CultureInfo.InvariantCulture), seen.Header("content-length"));
    }

    [Fact]
    public void RefusesABodyThatIsNotADictionaryOfFields()
    {
        var error = Assert.Throws<ArgumentException>(() => Multipart.Encode(new { file = "x" }, new System.Text.Json.JsonSerializerOptions()));
        Assert.Contains("dictionary of form fields", error.Message);
    }

    [Fact]
    public async Task DoesNotRetryAFileUploadThatCarriesNoIdempotencyKey()
    {
        var calls = 0;
        var probe = new Probe
        {
            Answer = _ => ++calls == 1
                ? Probe.Reply(503, "{\"statusCode\":503,\"name\":\"unavailable\",\"message\":\"busy\"}")
                : Probe.Reply(200, "{}"),
        };
        using var client = probe.Client();
        // Imports.CreateAsync is a POST without an idempotency key: not retried, so one request.
        await Assert.ThrowsAsync<ApiException>(() => client.Contacts.Imports.CreateAsync(new Dictionary<string, object?>
        {
            ["file"] = new FileUpload("a.csv", "x"u8.ToArray()),
        }));
        Assert.Single(probe.Requests);
    }

    [Fact]
    public void ChecksTheFileUploadArgumentsAndReadsAFileFromDisk()
    {
        Assert.Throws<ArgumentNullException>(() => new FileUpload(null!, []));
        Assert.Throws<ArgumentNullException>(() => new FileUpload("a.csv", null!));
        Assert.Throws<ArgumentNullException>(() => new FileUpload("a.csv", [], null!));
        var upload = new FileUpload("a.csv", [1, 2]);
        Assert.Equal("text/csv", upload.ContentType);
        Assert.Equal(new byte[] { 1, 2 }, upload.Content);
    }
}
