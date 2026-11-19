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

    [Fact]
    public async Task UploadsAFileAndFormFieldsAsMultipart()
    {
        using var client = Client();
        var csv = "Email,First Name\nana@example.com,Ana\n";
        var answer = await client.Contacts.Imports.CreateAsync(new Dictionary<string, object?>
        {
            ["file"] = new FileUpload("people.csv", System.Text.Encoding.UTF8.GetBytes(csv)),
            ["column_map"] = new { email = "Email", first_name = "First Name" },
            ["on_conflict"] = "skip",
            ["segments"] = new[] { new { id = Id } },
            ["topics"] = null,
        });
        var hit = Assert.Single(Mock.Hits);
        Assert.StartsWith("multipart/form-data; boundary=", hit.Header("content-type"));
        var form = ContractMock.ParseMultipart(hit.Header("content-type")!, hit.Body!, out var error);
        Assert.Null(error);
        Assert.Equal("people.csv", (string?)form!["file"]);
        Assert.Contains(csv, hit.Body);
        Assert.Contains("text/csv", hit.Body);
        Assert.Equal("{\"email\":\"Email\",\"first_name\":\"First Name\"}", (string?)form["column_map"]);
        Assert.Equal("skip", (string?)form["on_conflict"]);
        Assert.Equal($"[{{\"id\":\"{Id}\"}}]", (string?)form["segments"]);
        Assert.False(form.ContainsKey("topics"));
        Assert.True(JsonNode.DeepEquals(hit.Response, answer));
    }

    [Fact]
    public async Task ReadsAFileFromDiskForUpload()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.csv");
        await File.WriteAllTextAsync(path, "Email\nana@example.com\n");
        try
        {
            var file = await FileUpload.FromFileAsync(path);
            Assert.Equal(Path.GetFileName(path), file.FileName);
            Assert.Equal("text/csv", file.ContentType);
            Assert.Equal("Email\nana@example.com\n", System.Text.Encoding.UTF8.GetString(file.Content));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SendsTheRequiredQueryOfBroadcastRecipientsAndPagesThem()
    {
        using var client = Client();
        await client.Broadcasts.RecipientsAsync(Id, new Dictionary<string, object?> { ["type"] = "clicked", ["limit"] = 50 });
        var hit = Assert.Single(Mock.Hits);
        Assert.Equal("clicked", hit.Query["type"]);
        Assert.Equal("50", hit.Query["limit"]);
        Mock.Reset();
        Mock.ReplyExample("GET /broadcasts/{id}/recipients", page =>
        {
            page["has_more"] = false;
            page["data"] = new JsonArray();
        });
        var seen = 0;
        await foreach (var _ in client.Broadcasts.RecipientsAllAsync(Id, new Dictionary<string, object?> { ["type"] = "bounced" }))
        {
            seen++;
        }

        Assert.Equal(0, seen);
        Assert.Equal("bounced", Assert.Single(Mock.Hits).Query["type"]);
    }

    [Fact]
    public async Task HandsBackTheAnswerAsTheApiSentItWithFieldsItDoesNotKnow()
    {
        // The usage answer as the document shows it, plus what a later API version might add:
        // an unknown object, a number that is not an integer, a null and text that needs escaping.
        Mock.ReplyExample("GET /usage", usage =>
        {
            usage["added_later"] = new JsonObject
            {
                ["ratio"] = 0.125,
                ["big"] = 9007199254740993L,
                ["nothing"] = null,
                ["text"] = "caf\u00e9 \"quoted\" \U0001F600",
            };
        });
        using var client = Client();
        var usage = await client.Usage.GetAsync();
        var hit = Assert.Single(Mock.Hits);
        Assert.True(JsonNode.DeepEquals(hit.Response, usage));
        var extra = usage!["added_later"]!.AsObject();
        Assert.Equal(0.125, extra["ratio"]!.GetValue<double>());
        Assert.Equal(9007199254740993L, extra["big"]!.GetValue<long>());
        Assert.True(extra.ContainsKey("nothing"));
        Assert.Null(extra["nothing"]);
        Assert.Equal("caf\u00e9 \"quoted\" \U0001F600", extra["text"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("6a1f1d38-3c55-4a4e-9a57-0f2b3c4d5e6f")]
    [InlineData("agent@acme.billionsend.app")]
    public async Task FiltersReceivedMailByAnInboxIdOrItsEmailAddress(string inbox)
    {
        using var client = Client();
        await client.Emails.Receiving.ListAsync(new Dictionary<string, object?> { ["inbox_id"] = inbox });
        var hit = Assert.Single(Mock.Hits);
        Assert.Equal("GET /emails/receiving", hit.Key);
        Assert.Equal(inbox, hit.Query["inbox_id"]);
    }

    [Fact]
    public void HasNoMethodsForTheOperationsTheApiDropped()
    {
        Assert.Null(typeof(BillionSend.Resources.UnsubscribesResource).GetMethod("DeleteAsync"));
        Assert.Null(typeof(BillionSend.Resources.WebhooksResource).GetMethod("RotateAsync"));
        Assert.NotNull(typeof(BillionSend.Resources.WebhooksResource).GetMethod("RotateSigningSecretAsync"));
    }
}
