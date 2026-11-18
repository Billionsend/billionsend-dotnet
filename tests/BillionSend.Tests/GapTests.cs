using System.Net;
using System.Text;
using BillionSend.Internal;
using BillionSend.Tests.Support;

namespace BillionSend.Tests;

/// <summary>Configuration read from the environment: the order is the call's options, then the environment, then the default.</summary>
public sealed class EnvironmentTests : IDisposable
{
    private readonly string? _key = Environment.GetEnvironmentVariable("BILLIONSEND_API_KEY");
    private readonly string? _url = Environment.GetEnvironmentVariable("BILLIONSEND_BASE_URL");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("BILLIONSEND_API_KEY", _key);
        Environment.SetEnvironmentVariable("BILLIONSEND_BASE_URL", _url);
    }

    [Fact]
    public async Task ReadsBothFromTheEnvironmentWhenCreatedWithNoArguments()
    {
        Environment.SetEnvironmentVariable("BILLIONSEND_API_KEY", "bs_env");
        Environment.SetEnvironmentVariable("BILLIONSEND_BASE_URL", "http://127.0.0.1:1/");
        using var client = new BillionSendClient();
        Assert.Equal("http://127.0.0.1:1", client.BaseUrl);
        // Nothing listens there: the request is sent, with the environment's key, and fails to connect.
        var error = await Assert.ThrowsAsync<ApiConnectionException>(() => client.Usage.GetAsync(new RequestOptions { MaxRetries = 0 }));
        Assert.Null(error.StatusCode);
    }

    [Fact]
    public async Task PrefersTheOptionsOverTheEnvironmentForTheKeyAndTheBaseUrl()
    {
        Environment.SetEnvironmentVariable("BILLIONSEND_API_KEY", "bs_env");
        Environment.SetEnvironmentVariable("BILLIONSEND_BASE_URL", "https://env.test");
        var probe = new Probe();
        using var client = new BillionSendClient(new BillionSendOptions
        {
            ApiKey = "bs_option",
            BaseUrl = "https://option.test",
            HttpClient = new HttpClient(probe),
        });
        Assert.Equal("https://option.test", client.BaseUrl);
        await client.Usage.GetAsync();
        Assert.Equal("Bearer bs_option", Assert.Single(probe.Requests).Header("authorization"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task TreatsAnEmptyOrMissingEnvironmentValueAsUnset(string? value)
    {
        Environment.SetEnvironmentVariable("BILLIONSEND_API_KEY", value);
        Environment.SetEnvironmentVariable("BILLIONSEND_BASE_URL", value);
        using var client = new BillionSendClient(new BillionSendOptions { ApiKey = "" });
        Assert.Equal("https://api.billionsend.com", client.BaseUrl);
        var error = await Assert.ThrowsAsync<AuthenticationException>(() => client.Usage.GetAsync());
        Assert.Equal("missing_api_key", error.Name);
        Assert.Null(error.StatusCode);
    }
}

public sealed class UnitGapTests
{
    [Fact]
    public void ExplainsAPathWithMoreSlotsThanValues()
    {
        var error = Assert.Throws<BillionSendException>(() => Urls.FillPath("/a/{x}/b/{y}", ["1"]));
        Assert.Equal("Missing path parameter \"y\"", error.Message);
        Assert.Equal("/a/1/b/2", Urls.FillPath("/a/{x}/b/{y}", ["1", "2"]));
        Assert.Equal("/plain", Urls.FillPath("/plain", ApiCall.NoPath));
    }

    [Fact]
    public void FormatsEveryKindOfQueryValue()
    {
        Assert.Equal("5", Urls.Format(JsonValue.Create(5)));
        Assert.Equal("a", Urls.Format(JsonValue.Create("a")));
        Assert.Equal("https://x.test/", Urls.Format(new Uri("https://x.test")));
        Assert.Equal("{\"k\":[1,2]}", Urls.Format(new JsonObject { ["k"] = new JsonArray(1, 2) }));
        Assert.Equal("true", Urls.Format(true));
        Assert.Equal("false", Urls.Format(false));
        Assert.Equal("7", Urls.Format(7));
        Assert.Equal("1.25", Urls.Format(1.25m));
        Assert.Equal("tag:x", Urls.Format(new Tag("x")));
    }

    private sealed record Tag(string Name)
    {
        public override string ToString() => $"tag:{Name}";
    }

    [Theory]
    [InlineData(599, null, "Unexpected 599 response")]
    [InlineData(599, "", "Unexpected 599 response")]
    [InlineData(418, "I'm a teapot", "Unexpected 418 I'm a teapot response")]
    public void NamesAnUnexpectedAnswerByItsStatusAndReason(int status, string? reason, string message)
    {
        var error = Errors.FromResponse(status, reason, string.Empty, null, null, DateTimeOffset.UtcNow);
        Assert.Equal(message, error.Message);
        Assert.Equal("application_error", error.Name);
        Assert.Equal(status, error.StatusCode);
    }

    [Fact]
    public void MapsEachStatusToItsErrorTypeAndKeepsTheRetryAfterHint()
    {
        var now = DateTimeOffset.Parse("2026-11-19T00:00:00Z");
        var limited = Assert.IsType<RateLimitException>(Errors.FromResponse(429, null, "{\"statusCode\":429,\"name\":\"rate_limit_exceeded\",\"message\":\"m\"}", "req", "3", now));
        Assert.Equal(3, limited.RetryAfter);
        Assert.Equal("3", limited.RetryAfterHeader);
        Assert.Equal("req", limited.RequestId);
        Assert.Null(Assert.IsType<RateLimitException>(Errors.FromResponse(429, null, "{}", null, null, now)).RetryAfter);
        Assert.IsType<BillionSendException>(Errors.FromResponse(410, "Gone", "{\"statusCode\":410,\"name\":\"gone\",\"message\":\"m\"}", null, null, now));
        Assert.IsType<ApiException>(Errors.FromResponse(504, null, "x", null, null, now));
    }

    [Fact]
    public void ParsesRetryAfterInSecondsAndHttpDatesAndNothingElse()
    {
        var now = DateTimeOffset.Parse("2026-11-19T12:00:00Z");
        Assert.Equal(3, Retry.ParseRetryAfter(" 3 ", now));
        Assert.Equal(0, Retry.ParseRetryAfter("-5", now));
        Assert.Equal(90, Retry.ParseRetryAfter("Thu, 19 Nov 2026 12:01:30 GMT", now));
        Assert.Equal(90, Retry.ParseRetryAfter("Thursday, 19-Nov-26 12:01:30 GMT", now));
        Assert.Null(Retry.ParseRetryAfter("NaN", now));
        Assert.Null(Retry.ParseRetryAfter("Infinity", now));
        Assert.Null(Retry.ParseRetryAfter("later", now));
        Assert.Null(Retry.ParseRetryAfter("", now));
        Assert.Null(Retry.ParseRetryAfter(null, now));
    }

    [Fact]
    public async Task ThrowsOnANullPageFetcher()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        {
            await foreach (var _ in Pagination.IterateAsync(null!))
            {
            }
        });
    }

    [Fact]
    public async Task WalksForwardFromACursorGivenInTheStartQuery()
    {
        var queries = new List<IReadOnlyDictionary<string, object?>>();
        var items = new List<string>();
        await foreach (var item in Pagination.IterateAsync(
            (query, _) =>
            {
                queries.Add(query);
                return Task.FromResult<JsonNode?>(queries.Count == 1
                    ? JsonNode.Parse("{\"data\":[{\"id\":\"b\"},{\"id\":\"c\"}],\"has_more\":true}")
                    : JsonNode.Parse("{\"data\":[{\"id\":\"d\"}],\"has_more\":\"yes\"}"));
            },
            new Dictionary<string, object?> { ["after"] = "a", ["limit"] = 2, ["before"] = null }))
        {
            items.Add((string)item["id"]!);
        }

        // "yes" is not a boolean: the walk ends there rather than guessing.
        Assert.Equal(["b", "c", "d"], items);
        Assert.Equal("a", queries[0]["after"]);
        Assert.Equal("c", queries[1]["after"]);
        Assert.Equal(2, queries[1]["limit"]);
        Assert.False(queries[1].ContainsKey("before") && queries[1]["before"] is not null);
    }

    [Theory]
    [InlineData("{\"data\":[{\"id\":\"a\"}]}")]
    [InlineData("{\"data\":[{\"id\":\"a\"}],\"has_more\":null}")]
    public async Task TreatsAMissingOrNullHasMoreAsTheLastPage(string page)
    {
        var calls = 0;
        await foreach (var _ in Pagination.IterateAsync((_, _) =>
        {
            calls++;
            return Task.FromResult(JsonNode.Parse(page));
        }))
        {
        }

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ThrowsWhenTheItemToContinueFromIsNull()
    {
        var error = await Assert.ThrowsAsync<BillionSendException>(async () =>
        {
            await foreach (var _ in Pagination.IterateAsync((_, _) =>
                Task.FromResult(JsonNode.Parse("{\"data\":[{\"id\":\"a\"},null],\"has_more\":true}"))))
            {
            }
        });
        Assert.Contains("no id to continue from", error.Message);
    }

    [Fact]
    public async Task StopsWhenAPageHasMoreButNoItems()
    {
        var calls = 0;
        await foreach (var _ in Pagination.IterateAsync((_, _) =>
        {
            calls++;
            return Task.FromResult<JsonNode?>(JsonNode.Parse("{\"data\":[],\"has_more\":true}"));
        }))
        {
        }

        Assert.Equal(1, calls);
    }

    [Fact]
    public void ChecksTheArgumentsOfWebhookVerify()
    {
        var headers = new Dictionary<string, string>();
        const string secret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";
        Assert.Throws<ArgumentNullException>(() => Webhook.Verify((string)null!, headers, secret));
        Assert.Throws<ArgumentNullException>(() => Webhook.Verify((byte[])null!, headers, secret));
        Assert.Throws<ArgumentNullException>(() => Webhook.Verify("{}", (IEnumerable<KeyValuePair<string, string>>)null!, secret));
        Assert.Throws<ArgumentNullException>(() => Webhook.Verify("{}", (Func<string, string?>)null!, secret));
        Assert.Throws<ArgumentNullException>(() => Webhook.Verify((string)null!, _ => null, secret));
        Assert.Throws<ArgumentNullException>(() => Webhook.Verify((byte[])null!, _ => null, secret));
    }

    [Fact]
    public void RefusesASignedBodyThatIsTheJsonNull()
    {
        const string secret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";
        var key = Convert.FromBase64String(secret["whsec_".Length..]);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = "v1," + Convert.ToBase64String(System.Security.Cryptography.HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"m.{timestamp}.null")));
        var headers = new Dictionary<string, string> { ["webhook-id"] = "m", ["webhook-timestamp"] = timestamp, ["webhook-signature"] = signature };
        Assert.Equal("The webhook body is not JSON", Assert.Throws<WebhookVerificationException>(() => Webhook.Verify("null", headers, secret)).Message);
    }

    private const string WhSecret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";

    private static string Mac(string secret, string id, string timestamp, byte[] payload)
    {
        var key = Convert.FromBase64String(secret["whsec_".Length..]);
        var signed = Encoding.UTF8.GetBytes($"{id}.{timestamp}.").Concat(payload).ToArray();
        return "v1," + Convert.ToBase64String(System.Security.Cryptography.HMACSHA256.HashData(key, signed));
    }

    private static Dictionary<string, string> Signed(string signature, string timestamp) => new()
    {
        ["webhook-id"] = "msg_1",
        ["webhook-timestamp"] = timestamp,
        ["webhook-signature"] = signature,
    };

    [Fact]
    public void ReadsTheSignaturesOfARotationWhateverTheSpacingAndIgnoresJunkBesideThem()
    {
        var payload = Encoding.UTF8.GetBytes("{\"type\":\"email.sent\"}");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var good = Mac(WhSecret, "msg_1", timestamp, payload);
        var other = Mac("whsec_" + Convert.ToBase64String(new byte[32]), "msg_1", timestamp, payload);
        foreach (var header in new[]
        {
            $"{other}  {good}", $" {good} ", $"v2,abc {good}", $"v1 {good}", $"v1, {good}", $"{good} v1,", $"{good}\t", $"{other} {good} {other}",
        })
        {
            Assert.Equal("email.sent", (string?)Webhook.Verify(payload, Signed(header, timestamp), WhSecret)["type"]);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("v1,")]
    [InlineData("v1")]
    [InlineData(",")]
    [InlineData("V1,AAAA")]
    [InlineData("v1,AAAA")]
    [InlineData("v1,!!!!")]
    public void RefusesASignatureHeaderThatHoldsNoMatchingSignature(string header)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        Assert.Throws<WebhookVerificationException>(() => Webhook.Verify("{}", Signed(header, timestamp), WhSecret));
    }

    [Fact]
    public void RefusesAUppercaseVersionTagAndASecretWithoutItsPrefixOrInOtherCase()
    {
        var payload = "{}"u8.ToArray();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var good = Mac(WhSecret, "msg_1", timestamp, payload);
        Assert.Throws<WebhookVerificationException>(() => Webhook.Verify(payload, Signed("V1" + good[2..], timestamp), WhSecret));
        Assert.Throws<WebhookVerificationException>(() => Webhook.Verify(payload, Signed(good, timestamp), WhSecret["whsec_".Length..]));
        Assert.Throws<WebhookVerificationException>(() => Webhook.Verify(payload, Signed(good, timestamp), "WHSEC_" + WhSecret["whsec_".Length..]));
        Assert.Throws<WebhookVerificationException>(() => Webhook.Verify(payload, Signed(good, timestamp), WhSecret.ToUpperInvariant()));
        Assert.Throws<WebhookVerificationException>(() => Webhook.Verify(payload, Signed(good, timestamp), string.Empty));
        Assert.Throws<WebhookVerificationException>(() => Webhook.Verify(payload, Signed(good, timestamp), null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("+123")]
    [InlineData("1e9")]
    [InlineData("1.5")]
    [InlineData(" 1700000000")]
    [InlineData("99999999999999999999")]
    public void RefusesATimestampThatIsNotWholeSeconds(string timestamp)
    {
        var headers = Signed("v1,AAAA", timestamp);
        Assert.Throws<WebhookVerificationException>(() => Webhook.Verify("{}", headers, WhSecret));
    }

    [Fact]
    public void KeepsTheBodyBytesAsTheyArrivedWhenTheyAreNotValidUtf8Text()
    {
        // Signed over the raw bytes: the signature verifies, the body is then refused as not JSON.
        var payload = new byte[] { 0x7B, 0xFF, 0xFE, 0x7D };
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var headers = Signed(Mac(WhSecret, "msg_1", timestamp, payload), timestamp);
        Assert.Equal("The webhook body is not JSON", Assert.Throws<WebhookVerificationException>(() => Webhook.Verify(payload, headers, WhSecret)).Message);
        // One byte changed and the signature no longer matches.
        var tampered = (byte[])payload.Clone();
        tampered[1] = 0x20;
        Assert.Equal("No webhook signature matches", Assert.Throws<WebhookVerificationException>(() => Webhook.Verify(tampered, headers, WhSecret)).Message);
    }

    [Fact]
    public void VerifiesABodyWithCarriageReturnsAndNonAsciiTextAsAString()
    {
        var text = "{\"type\":\"email.sent\",\r\n\"note\":\"caf\u00e9 \U0001F600\"}";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var headers = Signed(Mac(WhSecret, "msg_1", timestamp, Encoding.UTF8.GetBytes(text)), timestamp);
        Assert.Equal("caf\u00e9 \U0001F600", (string?)Webhook.Verify(text, headers, WhSecret)["note"]);
    }

    [Fact]
    public void HoldsAZeroToleranceToTheSecondAndAMissingHeaderToItsOwnError()
    {
        var payload = "{}"u8.ToArray();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var headers = Signed(Mac(WhSecret, "msg_1", "1800000000", payload), "1800000000");
        var exact = new WebhookVerifyOptions { Now = now, Tolerance = TimeSpan.Zero };
        Assert.NotNull(Webhook.Verify(payload, headers, WhSecret, exact));
        Assert.Throws<WebhookVerificationException>(() => Webhook.Verify(payload, headers, WhSecret, new WebhookVerifyOptions { Now = now.AddSeconds(1), Tolerance = TimeSpan.Zero }));
        Assert.Throws<WebhookVerificationException>(() => Webhook.Verify(payload, headers, WhSecret, new WebhookVerifyOptions { Now = now.AddSeconds(-1), Tolerance = TimeSpan.Zero }));
        foreach (var missing in new[] { "webhook-id", "webhook-timestamp", "webhook-signature" })
        {
            var partial = new Dictionary<string, string>(headers);
            partial.Remove(missing);
            Assert.Equal(
                "Missing the webhook-id, webhook-timestamp or webhook-signature header",
                Assert.Throws<WebhookVerificationException>(() => Webhook.Verify(payload, partial, WhSecret, exact)).Message);
            partial[missing] = string.Empty;
            Assert.Throws<WebhookVerificationException>(() => Webhook.Verify(payload, partial, WhSecret, exact));
        }
    }

    [Fact]
    public void DefaultsTheWebhookToleranceToFiveMinutes()
    {
        Assert.Equal(300, Webhook.DefaultToleranceSeconds);
        Assert.Null(new WebhookVerifyOptions().Tolerance);
    }
}

public sealed class CancellationGapTests : MockTest
{
    [Fact]
    public async Task ThrowsTheCallersCancellationWhenTheConnectionEndsRightAfterIt()
    {
        using var cts = new CancellationTokenSource();
        Mock.Enqueue("GET /inboxes/events", (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new CancelThenEnd(cts)) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(response);
        });
        using var client = Client();
        await using var stream = await client.Inboxes.Events.StreamAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in stream.WithCancellation(cts.Token))
            {
            }
        });
    }

    [Fact]
    public async Task TakesTheClientsTimeoutWhenTheCallsOptionsSetNone()
    {
        using var client = Client();
        await client.Usage.GetAsync(new RequestOptions { MaxRetries = 0 });
        Assert.Single(Mock.Hits);
    }

    private sealed class CancelThenEnd(CancellationTokenSource source) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            source.Cancel();
            return new ValueTask<int>(0);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task ReportsTheCallersCancellationEvenWhenTheTransportFailedWithSomethingElse()
    {
        using var cts = new CancellationTokenSource();
        var probe = new Probe
        {
            Answer = _ =>
            {
                cts.Cancel();
                throw new IOException("closed by the cancellation");
            },
        };
        using var client = probe.Client();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.Segments.GetAsync("s", cancellationToken: cts.Token));
        Assert.Equal(cts.Token, error.CancellationToken);
    }
}

/// <summary>What a list walk does when a later page fails.</summary>
public sealed class WalkTests : MockTest
{
    [Fact]
    public async Task YieldsTheItemsOfTheFirstPageThenThrowsThePageErrorOfTheSecond()
    {
        Mock.Page("GET /contacts", true, "0b8e3c6a-0000-4000-8000-000000000001", "0b8e3c6a-0000-4000-8000-000000000002");
        Mock.Error("GET /contacts", 500, "internal_server_error", "Boom");
        using var client = Client(o => o.MaxRetries = 0);
        var seen = new List<string>();
        var error = await Assert.ThrowsAsync<ApiException>(async () =>
        {
            await foreach (var item in client.Contacts.ListAllAsync())
            {
                seen.Add((string)item["id"]!);
            }
        });
        Assert.Equal(2, seen.Count);
        Assert.Equal(500, error.StatusCode);
        Assert.Equal(2, Mock.Hits.Count);
        Assert.Equal("0b8e3c6a-0000-4000-8000-000000000002", Mock.Hits[1].Query["after"]);
    }

    [Fact]
    public async Task RetriesAFailedPageBeforeGivingUpOnTheWalk()
    {
        Mock.Page("GET /contacts", true, "0b8e3c6a-0000-4000-8000-000000000001");
        Mock.Error("GET /contacts", 503, "service_unavailable", "Busy");
        Mock.Page("GET /contacts", false, "0b8e3c6a-0000-4000-8000-000000000002");
        using var client = Client();
        var seen = new List<string>();
        await foreach (var item in client.Contacts.ListAllAsync())
        {
            seen.Add((string)item["id"]!);
        }

        Assert.Equal(2, seen.Count);
        Assert.Equal(3, Mock.Hits.Count);
        Assert.Equal(Mock.Hits[1].Query, Mock.Hits[2].Query);
    }

    [Fact]
    public void RefusesAFixtureThatTheSchemaDoesNotAllow()
    {
        var error = Assert.Throws<ArgumentException>(() => Mock.Reply("GET /usage", 200, new JsonObject { ["object"] = 5 }));
        Assert.Contains("GET /usage", error.Message);
        Assert.Throws<ArgumentException>(() => Mock.Reply("GET /usage", 200, new JsonObject()));
        Assert.Throws<ArgumentException>(() => Mock.Reply("GET /usage", 201, Spec.Example(Spec.Get("GET /usage").ResponseSchema, true)));
        Assert.Throws<ArgumentException>(() => Mock.Reply("GET /usage", 500, new JsonObject { ["message"] = "no name" }));
        Mock.Error("GET /usage", 500, "internal_server_error", "valid");
    }
}
