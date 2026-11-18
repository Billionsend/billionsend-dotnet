using BillionSend.Tests.Support;

namespace BillionSend.Tests;

public sealed class PaginationTests : MockTest
{
    private static readonly string[] Ids =
    [
        "0b8e3c6a-0000-4000-8000-000000000001",
        "0b8e3c6a-0000-4000-8000-000000000002",
        "0b8e3c6a-0000-4000-8000-000000000003",
    ];

    private static JsonObject Page(bool hasMore, params string[] ids) => new()
    {
        ["object"] = "list",
        ["has_more"] = hasMore,
        ["data"] = new JsonArray(ids.Select(id => (JsonNode)new JsonObject { ["id"] = id }).ToArray()),
    };

    private static async Task<List<string>> Ids_(IAsyncEnumerable<JsonNode> items)
    {
        var seen = new List<string>();
        await foreach (var item in items)
        {
            seen.Add((string)item["id"]!);
        }

        return seen;
    }

    [Fact]
    public async Task WalksForwardWithAfterTheLastIdUntilHasMoreIsFalse()
    {
        Mock.Reply("GET /contacts", 200, Page(true, Ids[0], Ids[1]));
        Mock.Reply("GET /contacts", 200, Page(false, Ids[2]));
        using var client = Client();
        var seen = await Ids_(client.Contacts.ListAllAsync(new Dictionary<string, object?> { ["limit"] = 2 }));
        Assert.Equal(Ids, seen);
        var queries = Mock.HitsFor("GET /contacts").Select(hit => hit.Query).ToList();
        Assert.Equal(new Dictionary<string, string> { ["limit"] = "2" }, queries[0]);
        Assert.Equal(new Dictionary<string, string> { ["limit"] = "2", ["after"] = Ids[1] }, queries[1]);
    }

    [Fact]
    public async Task WalksBackwardWithBeforeTheFirstIdWhenStartedWithBefore()
    {
        Mock.Reply("GET /domains", 200, Page(true, Ids[1], Ids[2]));
        Mock.Reply("GET /domains", 200, Page(false, Ids[0]));
        using var client = Client();
        var seen = await Ids_(client.Domains.ListAllAsync(new Dictionary<string, object?> { ["before"] = Id }));
        Assert.Equal([Ids[1], Ids[2], Ids[0]], seen);
        var queries = Mock.HitsFor("GET /domains").Select(hit => hit.Query).ToList();
        Assert.Equal(new Dictionary<string, string> { ["before"] = Id }, queries[0]);
        Assert.Equal(new Dictionary<string, string> { ["before"] = Ids[1] }, queries[1]);
    }

    [Fact]
    public async Task StopsEarlyWhenTheCallerBreaksOut()
    {
        Mock.Reply("GET /webhooks", 200, Page(true, Ids[0], Ids[1]));
        using var client = Client();
        await foreach (var item in client.Webhooks.ListAllAsync())
        {
            Assert.Equal(Ids[0], (string)item["id"]!);
            break;
        }

        Assert.Single(Mock.Hits);
    }

    [Fact]
    public async Task FollowsNestedListsWithTheirPathParameters()
    {
        const string key = "GET /inboxes/{inbox_id}/threads/{thread_id}/emails";
        Mock.Reply(key, 200, Page(true, Ids[0]));
        Mock.Reply(key, 200, Page(false, Ids[1]));
        using var client = Client();
        var seen = await Ids_(client.Inboxes.Threads.Emails.ListAllAsync(Inbox, Id));
        Assert.Equal([Ids[0], Ids[1]], seen);
        var last = Mock.HitsFor(key)[1];
        Assert.Equal($"/inboxes/support%40example.com/threads/{Id}/emails", last.Url.AbsolutePath);
        Assert.Equal(Ids[0], last.Query["after"]);
    }

    [Fact]
    public async Task ThrowsThePageErrorAsItsType()
    {
        Mock.Error("GET /segments", 403, "restricted_api_key", "No");
        using var client = Client();
        var error = await Assert.ThrowsAsync<PermissionDeniedException>(async () =>
        {
            await foreach (var _ in client.Segments.ListAllAsync())
            {
            }
        });
        Assert.Equal(403, error.StatusCode);
        Assert.Equal("restricted_api_key", error.Name);
        Assert.Equal("No", error.Message);
    }

    [Fact]
    public async Task StopsOnTheCancellationToken()
    {
        Mock.Reply("GET /emails", 200, Page(true, Ids[0], Ids[1]));
        using var client = Client();
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in client.Emails.ListAllAsync(cancellationToken: cts.Token))
            {
                cts.Cancel();
            }
        });
        Assert.Single(Mock.Hits);
    }

    [Theory]
    [InlineData("{\"object\":\"list\"}")]
    [InlineData("{\"data\":{}}")]
    [InlineData("[]")]
    public async Task ThrowsOnAnAnswerThatIsNotAListPage(string answer)
    {
        var error = await Assert.ThrowsAsync<BillionSendException>(async () =>
        {
            await foreach (var _ in Pagination.IterateAsync((_, _) => Task.FromResult(JsonNode.Parse(answer))))
            {
            }
        });
        Assert.Equal("application_error", error.Name);
        Assert.Null(error.StatusCode);
    }

    [Theory]
    [InlineData("{\"name\":\"x\"}")]
    [InlineData("{\"id\":\"\"}")]
    [InlineData("{\"id\":null}")]
    [InlineData("{\"id\":7}")]
    [InlineData("{\"id\":{}}")]
    public async Task ThrowsWhenTheItemToContinueFromHasNoId(string item)
    {
        var page = new JsonObject { ["data"] = new JsonArray(JsonNode.Parse(item)), ["has_more"] = true };
        var seen = 0;
        var error = await Assert.ThrowsAsync<BillionSendException>(async () =>
        {
            await foreach (var _ in Pagination.IterateAsync((_, _) => Task.FromResult<JsonNode?>(page)))
            {
                seen++;
            }
        });
        Assert.Equal(1, seen);
        Assert.Equal("application_error", error.Name);
        Assert.Contains("last item", error.Message);
    }

    [Fact]
    public async Task WalkingBackwardNamesTheFirstItem()
    {
        var page = new JsonObject
        {
            ["data"] = new JsonArray(new JsonObject { ["id"] = "" }, new JsonObject { ["id"] = "b" }),
            ["has_more"] = true,
        };
        var start = new Dictionary<string, object?> { ["before"] = "z" };
        var error = await Assert.ThrowsAsync<BillionSendException>(async () =>
        {
            await foreach (var _ in Pagination.IterateAsync((_, _) => Task.FromResult<JsonNode?>(page), start))
            {
            }
        });
        Assert.Contains("first item", error.Message);
    }

    [Fact]
    public async Task IterateWorksWithAnyPageFetcherAndDetachesItems()
    {
        var pages = new Queue<JsonNode>([Page(true, "a", "b"), Page(true)]);
        var queries = new List<IReadOnlyDictionary<string, object?>>();
        var seen = new List<JsonNode>();
        await foreach (var item in Pagination.IterateAsync((query, _) =>
        {
            queries.Add(query);
            return Task.FromResult<JsonNode?>(pages.Count > 0 ? pages.Dequeue() : Page(false));
        }))
        {
            seen.Add(item);
        }

        Assert.Equal(["a", "b"], seen.Select(item => (string)item["id"]!));
        Assert.Empty(queries[0]);
        Assert.Equal("b", queries[1]["after"]);
        Assert.Equal(2, queries.Count);
        var moved = new JsonArray(seen.ToArray());
        Assert.Equal(2, moved.Count);
    }

    public static TheoryData<string> PaginatedOperations()
    {
        var data = new TheoryData<string>();
        foreach (var op in Spec.Operations.Where(op => op.Paginated).OrderBy(op => op.Key, StringComparer.Ordinal))
        {
            data.Add(op.Key);
        }

        return data;
    }

    /// <summary>Every iterator, called with its path parameters, walks two pages of its own list.</summary>
    [Theory]
    [MemberData(nameof(PaginatedOperations))]
    public async Task EveryIteratorWalksItsList(string key)
    {
        var op = Spec.Get(key);
        var page = (JsonObject)Spec.Example(op.ResponseSchema, full: false)!;
        var dataSchema = Spec.Resolve(op.ResponseSchema!)["properties"]!["data"]!;
        var item = Spec.Example(Spec.Resolve(dataSchema)["items"], full: false) as JsonObject ?? new JsonObject();
        JsonObject Of(string id, bool more)
        {
            var copy = (JsonObject)page.DeepClone();
            var entry = (JsonObject)item.DeepClone();
            entry["id"] = id;
            copy["data"] = new JsonArray(entry);
            copy["has_more"] = more;
            return copy;
        }

        Mock.Reply(key, 200, Of(Ids[0], true));
        Mock.Reply(key, 200, Of(Ids[1], false));
        using var client = Client();
        object? target = client;
        var parts = op.OperationId.Split('.');
        foreach (var part in parts[..^1])
        {
            target = target!.GetType().GetProperty(char.ToUpperInvariant(part[0]) + part[1..])!.GetValue(target);
        }

        var method = target!.GetType().GetMethod(char.ToUpperInvariant(parts[^1][0]) + parts[^1][1..] + "AllAsync")!;
        var args = method.GetParameters()
            .Select(p => p.ParameterType == typeof(string)
                ? (object?)(p.Name == "inboxId" ? Inbox : Id)
                : p.Name == "query" && !p.HasDefaultValue
                    ? RequiredQuery(op)
                    : p.HasDefaultValue ? p.DefaultValue : null)
            .ToArray();
        var items = (IAsyncEnumerable<JsonNode>)method.Invoke(target, args)!;
        Assert.Equal([Ids[0], Ids[1]], await Ids_(items));
        var hits = Mock.HitsFor(key);
        Assert.Equal(2, hits.Count);
        Assert.False(hits[0].Query.ContainsKey("after"));
        Assert.Equal(Ids[0], hits[1].Query["after"]);
    }

    /// <summary>A list whose query has required parameters (the recipients' <c>type</c>) takes them as its first argument.</summary>
    private static Dictionary<string, object?> RequiredQuery(Operation op) =>
        op.Parameters("query")
            .Where(p => (bool?)p["required"] == true)
            .ToDictionary(p => (string)p["name"]!, p => (object?)Spec.Example(p["schema"], full: false)!.GetValue<string>());
}
