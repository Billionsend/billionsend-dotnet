using BillionSend.Tests.Support;

namespace BillionSend.Tests;

public sealed class ResourceTests : MockTest
{
    private static readonly object Message = new
    {
        from = "Acme <hello@example.com>",
        to = new[] { "person@example.com" },
        subject = "Hi",
        html = "<p>Hi</p>",
    };

    [Fact]
    public async Task SendsOneEmailWithAttachmentsTagsAndAnIdempotencyKey()
    {
        using var client = Client();
        var answer = await client.Emails.SendAsync(
            new JsonObject
            {
                ["from"] = "Acme <hello@example.com>",
                ["to"] = new JsonArray("person@example.com"),
                ["subject"] = "Invoice",
                ["text"] = "Attached.",
                ["attachments"] = new JsonArray(new JsonObject { ["filename"] = "invoice.txt", ["content"] = "aGVsbG8=" }),
                ["tags"] = new JsonArray(new JsonObject { ["name"] = "kind", ["value"] = "invoice" }),
            },
            new IdempotentRequestOptions { IdempotencyKey = "invoice/42" });
        var hit = Assert.Single(Mock.Hits);
        Assert.Equal("invoice/42", hit.Header("idempotency-key"));
        Assert.Equal("invoice.txt", (string?)hit.Json!["attachments"]![0]!["filename"]);
        Assert.True(JsonNode.DeepEquals(hit.Response, answer));
    }

    [Fact]
    public async Task SendsABatchWithTheBatchValidationHeader()
    {
        using var client = Client();
        await client.Emails.BatchAsync(new[] { Message, Message }, new BatchSendOptions { BatchValidation = BatchValidation.Permissive });
        await client.Emails.BatchAsync(new List<object> { Message });
        var hits = Mock.Hits;
        Assert.Equal("permissive", hits[0].Header("x-batch-validation"));
        Assert.Equal(2, hits[0].Json!.AsArray().Count);
        Assert.Null(hits[1].Header("x-batch-validation"));
        Assert.NotNull(hits[1].Header("idempotency-key"));
    }

    [Fact]
    public async Task SendsArrayQueryParametersCommaSeparatedInOneKey()
    {
        using var client = Client();
        await client.Emails.Metrics.GetAsync(new Dictionary<string, object?>
        {
            ["metrics"] = new[] { "delivered", "opened" },
            ["domain_id"] = new List<string> { Id, Id2 },
            ["granularity"] = "daily",
            ["start_date"] = null,
        });
        var hit = Assert.Single(Mock.Hits);
        Assert.Equal("delivered,opened", hit.Query["metrics"]);
        Assert.Equal($"{Id},{Id2}", hit.Query["domain_id"]);
        Assert.False(hit.Query.ContainsKey("start_date"));
        Assert.Contains("metrics=delivered%2Copened", hit.Url.Query);
    }

    [Fact]
    public async Task ImportsContactsWithOnConflictAndTheValidationHeader()
    {
        using var client = Client();
        await client.Contacts.Batch.CreateAsync(
            new[] { new { email = "a@example.com" } },
            new Dictionary<string, object?> { ["on_conflict"] = "skip" },
            new BatchRequestOptions { BatchValidation = BatchValidation.Strict });
        var hit = Assert.Single(Mock.Hits);
        Assert.Equal("skip", hit.Query["on_conflict"]);
        Assert.Equal("strict", hit.Header("x-batch-validation"));
        Assert.Null(hit.Header("idempotency-key"));
    }

    [Fact]
    public async Task SendsNoBodyWhenAnOptionalBodyIsLeftOut()
    {
        using var client = Client();
        await client.Inboxes.Drafts.SendAsync(Inbox, Id);
        await client.Broadcasts.SendAsync(Id, new { scheduled_at = "in 1 hour" });
        var hits = Mock.Hits;
        Assert.Null(hits[0].Body);
        Assert.Null(hits[0].Header("content-type"));
        Assert.NotNull(hits[0].Header("idempotency-key"));
        Assert.Equal("in 1 hour", (string?)hits[1].Json!["scheduled_at"]);
    }

    [Fact]
    public async Task AddressesAnInboxByItsEmailAddress()
    {
        using var client = Client();
        await client.Inboxes.Threads.Emails.GetAsync(Inbox, Id, Id2, new Dictionary<string, object?> { ["include_quarantined"] = true });
        var hit = Assert.Single(Mock.Hits);
        Assert.Equal($"/inboxes/support%40example.com/threads/{Id}/emails/{Id2}", hit.Url.AbsolutePath);
        Assert.Equal(Inbox, hit.PathParams["inbox_id"]);
        Assert.Equal("true", hit.Query["include_quarantined"]);
    }

    [Fact]
    public async Task SignsUpWithoutAKeyThenReadsItsStatus()
    {
        using var client = Client();
        var signup = await client.Agent.SignupAsync(new { human_email = "owner@example.com", name = "Support agent" });
        await client.Agent.StatusAsync();
        Assert.NotNull(signup);
        Assert.Null(Mock.Hits[0].Header("authorization"));
        Assert.Equal($"Bearer {ContractMock.ApiKey}", Mock.Hits[1].Header("authorization"));
    }

    [Fact]
    public async Task PassesPerCallHeadersAndTimeout()
    {
        using var client = Client();
        await client.Usage.GetAsync(new RequestOptions
        {
            Headers = new Dictionary<string, string> { ["X-Debug"] = "1" },
            Timeout = TimeSpan.FromSeconds(5),
        });
        Assert.Equal("1", Assert.Single(Mock.Hits).Header("x-debug"));
    }
}

public sealed class ContractMockTests : MockTest
{
    public static TheoryData<string, bool> Keys()
    {
        var data = new TheoryData<string, bool>();
        foreach (var key in Spec.OperationKeys.Order(StringComparer.Ordinal))
        {
            data.Add(key, true);
            data.Add(key, false);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void BuildsExamplesThatMatchTheOperationsSchemas(string key, bool full)
    {
        var op = Spec.Get(key);
        if (op.ResponseSchema is { } response)
        {
            Assert.Empty(Spec.Validate(JsonNode.Parse(Spec.Example(response, full)!.ToJsonString()), response));
        }

        if (op.RequestSchema is { } request)
        {
            Assert.Empty(Spec.Validate(JsonNode.Parse(Spec.Example(request, full)?.ToJsonString() ?? "null"), request));
        }
    }

    [Fact]
    public async Task PrefersLiteralPathSegmentsOverParameters()
    {
        using var client = Client();
        await client.Inboxes.Threads.SearchAsync(Id);
        await client.Emails.Metrics.GetAsync();
        await client.Contacts.Batch.GetAsync(new { contacts = new[] { new { id = Id } } });
        Assert.Equal(
            ["GET /inboxes/{inbox_id}/threads/search", "GET /emails/metrics", "POST /contacts/batch/get"],
            Mock.Hits.Select(hit => hit.Key));
    }

    [Fact]
    public async Task FlagsWhatBreaksTheContract()
    {
        using var keyless = Mock.Client(o => o.ApiKey = "bs_other");
        await keyless.Usage.GetAsync();
        using var client = Client();
        await client.Contacts.ListAsync(new Dictionary<string, object?> { ["nope"] = 1 });
        await client.Suppressions.ListAsync(new Dictionary<string, object?> { ["origin"] = "spam" });
        await client.Domains.CreateAsync(new { name = 42 });
        await client.Domains.CreateAsync(new JsonObject());
        using var http = new HttpClient(Mock, false);
        await http.GetAsync($"{ContractMock.BaseUrl}/nowhere");
        var violations = Mock.Violations;
        Assert.Contains(violations, v => v.StartsWith("GET /usage: expected the API key"));
        Assert.Contains(violations, v => v.StartsWith("GET /contacts: undeclared query parameter nope"));
        Assert.Contains(violations, v => v.StartsWith("GET /suppressions: query origin:"));
        Assert.Contains(violations, v => v.StartsWith("POST /domains: body/name:"));
        Assert.Contains(violations, v => v.StartsWith("POST /domains: body:") && v.Contains("name"));
        await client.Emails.GetAsync("not-a-uuid");
        Assert.Contains(Mock.Violations, v => v.StartsWith("GET /emails/{id}: path id:"));
        Assert.Contains(violations, v => v.StartsWith("GET /nowhere: not an operation"));
        Mock.Reset();
    }
}
