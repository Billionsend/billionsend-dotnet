using System.Text;
using BillionSend.Tests.Support;

namespace BillionSend.Tests;

public sealed class EventStreamParserTests
{
    private static async Task<List<ServerSentEvent>> Parse(IEnumerable<byte[]> chunks, Action<int>? onRetry = null)
    {
        var stream = new SseStream();
        foreach (var chunk in chunks)
        {
            stream.Write(chunk);
        }

        stream.End();
        var events = new List<ServerSentEvent>();
        await foreach (var message in ServerSentEvents.ParseAsync(stream, onRetry ?? (_ => { }), null, CancellationToken.None))
        {
            events.Add(message);
        }

        return events;
    }

    private static Task<List<ServerSentEvent>> Parse(params string[] chunks) => Parse(chunks.Select(Encoding.UTF8.GetBytes));

    [Fact]
    public async Task ParsesIdEventAndJsonData()
    {
        var message = Assert.Single(await Parse("id: 7\nevent: inbox.email.received\ndata: {\"type\":\"inbox.email.received\",\"inbox_id\":\"x\"}\n\n"));
        Assert.Equal("7", message.Id);
        Assert.Equal("inbox.email.received", message.Event);
        Assert.Equal("x", (string?)message.Data!["inbox_id"]);
        Assert.Equal("{\"type\":\"inbox.email.received\",\"inbox_id\":\"x\"}", message.Raw);
    }

    [Fact]
    public async Task JoinsEventsSplitAcrossChunksIncludingACrlfSplitInTwo()
    {
        var events = await Parse("id: 1\r", "\nda", "ta: {\"a\"", ":1}\r\n\r", "\nid: 2\r\ndata: 2\r\n\r\n");
        Assert.Equal(["1", "2"], events.Select(e => e.Id));
        Assert.Equal(1, (int)events[0].Data!["a"]!);
        Assert.Equal(2, (int)events[1].Data!);
    }

    [Fact]
    public async Task SplitsAMultiByteCharacterAcrossChunksCorrectly()
    {
        var bytes = Encoding.UTF8.GetBytes("data: héllo\n\n");
        var events = await Parse([bytes[..8], bytes[8..]]);
        Assert.Equal("héllo", (string?)Assert.Single(events).Data);
    }

    [Fact]
    public async Task IgnoresCommentsUnknownFieldsAndEventsWithoutData()
    {
        var message = Assert.Single(await Parse(": keep-alive\n\nfoo: bar\nevent: ping\n\n:another\ndata: x\n\n"));
        Assert.Null(message.Id);
        Assert.Equal("message", message.Event);
        Assert.Equal("x", message.Raw);
        Assert.Equal("x", (string?)message.Data);
    }

    [Fact]
    public async Task JoinsMultiLineDataWithNewlinesAndKeepsNonJsonAsText()
    {
        var message = Assert.Single(await Parse("data: first\ndata:second\ndata\n\n"));
        Assert.Equal("first\nsecond\n", message.Raw);
        Assert.Equal("first\nsecond\n", (string?)message.Data);
    }

    [Fact]
    public async Task KeepsTheLastIdForLaterEventsAndReportsRetry()
    {
        var retries = new List<int>();
        var events = await Parse(new[] { "\uFEFFid: 5\ndata: a\n\nretry: 2500\ndata: b\n\nretry: soon\nid\ndata: c\n\n" }.Select(Encoding.UTF8.GetBytes), retries.Add);
        Assert.Equal(["5", "5", ""], events.Select(e => e.Id));
        Assert.Equal([2500], retries);
    }

    [Fact]
    public async Task DropsAnEventTheStreamCutOffBeforeItsBlankLine()
    {
        var events = await Parse("data: complete\n\ndata: partial\n");
        Assert.Equal(["complete"], events.Select(e => e.Raw));
    }
}

public sealed class EventStreamTests : MockTest
{
    private const string Team = "GET /inboxes/events";
    private const string One = "GET /inboxes/{inbox_id}/events";

    private static async Task<List<InboxStreamItem>> Drain(EventStream stream)
    {
        var items = new List<InboxStreamItem>();
        await foreach (var item in stream)
        {
            items.Add(item);
        }

        return items;
    }

    [Fact]
    public async Task StreamsTheTeamsEventsAndClosesWhenTheLoopBreaks()
    {
        SseStream? body = null;
        Mock.Stream(Team, stream =>
        {
            body = stream;
            stream.Write("id: 10\nevent: inbox.thread.created\ndata: {\"type\":\"inbox.thread.created\"}\n\n");
            stream.Write(": keep-alive\n\n");
            stream.Write("id: 11\nevent: email.received\ndata: {\"type\":\"email.received\"}\n\n");
            return Task.CompletedTask; // stays open
        });
        using var client = Client();
        await using var stream = await client.Inboxes.Events.StreamAsync(new Dictionary<string, object?> { ["types"] = new[] { "inbox.thread.created", "email.received" } });
        var names = new List<string>();
        await foreach (var item in stream)
        {
            names.Add(item.Event);
            if (names.Count == 2)
            {
                break;
            }
        }

        Assert.Equal(["inbox.thread.created", "email.received"], names);
        Assert.Equal("11", stream.LastEventId);
        var hit = Assert.Single(Mock.Hits);
        Assert.Equal("inbox.thread.created,email.received", hit.Query["types"]);
        Assert.Equal("text/event-stream", hit.Header("accept"));
        await body!.Closed.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task IsNotCutByTheRequestTimeoutOnceOpen()
    {
        Mock.Stream(One, async stream =>
        {
            await Task.Delay(150);
            stream.Write("id: 1\ndata: {}\n\n");
            stream.End();
        });
        using var client = Client(o => o.Timeout = TimeSpan.FromMilliseconds(50));
        await using var stream = await client.Inboxes.Events.StreamInboxAsync(Id, null, new EventStreamOptions { Reconnect = false });
        Assert.Single(await Drain(stream));
    }

    [Fact]
    public async Task ReconnectsWithLastEventIdAfterADrop()
    {
        Mock.Stream(One, stream =>
        {
            stream.Write("retry: 10\nid: 41\ndata: {\"n\":41}\n\n");
            stream.End();
            return Task.CompletedTask;
        });
        Mock.Stream(One, stream =>
        {
            stream.Write("id: 42\ndata: {\"n\":42}\n\n");
            stream.End();
            return Task.CompletedTask;
        });
        Mock.Error(One, 503, "unavailable", "Busy");
        Mock.Error(One, 503, "unavailable", "Busy");
        using var client = Client();
        await using var stream = await client.Inboxes.Events.StreamInboxAsync(
            Inbox,
            new Dictionary<string, object?> { ["types"] = "inbox.email.received" },
            new EventStreamOptions { LastEventId = "40", ReconnectDelay = TimeSpan.FromSeconds(5), MaxReconnects = 1 });
        var data = new List<int>();
        // The reconnects are refused: the last refusal is what is thrown.
        var error = await Assert.ThrowsAsync<ApiException>(async () =>
        {
            await foreach (var item in stream)
            {
                data.Add((int)item.Data!["n"]!);
            }
        });
        Assert.Equal(503, error.StatusCode);
        Assert.Equal([41, 42], data);
        var hits = Mock.HitsFor(One);
        Assert.Equal(["40", "41", "42", "42"], hits.Select(hit => hit.Header("last-event-id")));
        Assert.All(hits, hit => Assert.Equal("inbox.email.received", hit.Query["types"]));
        Assert.All(hits, hit => Assert.Equal("/inboxes/support%40example.com/events", hit.Url.AbsolutePath));
    }

    [Fact]
    public async Task KeepsAQuietStreamGoingThroughEveryScheduledCloseAndCountsOnlyFailedOpens()
    {
        // The server closes a quiet stream about every ten minutes: eight in a row, each opened
        // and ended with nothing but keep-alives, are no failure with MaxReconnects = 2.
        for (var i = 0; i < 8; i++)
        {
            Mock.Stream(Team, stream =>
            {
                stream.Write(": keep-alive\n\n");
                stream.End();
                return Task.CompletedTask;
            });
        }

        // A failed open between two that open does not add up either.
        Mock.Error(Team, 503, "unavailable", "Busy");
        Mock.Stream(Team, stream =>
        {
            stream.Write("id: 7\ndata: {\"n\":7}\n\n");
            stream.End();
            return Task.CompletedTask;
        });
        using var client = Client();
        await using var stream = await client.Inboxes.Events.StreamAsync(
            options: new EventStreamOptions { MaxReconnects = 2, ReconnectDelay = TimeSpan.FromMilliseconds(1) });
        var data = new List<int>();
        await foreach (var item in stream)
        {
            data.Add((int)item.Data!["n"]!);
            break;
        }

        Assert.Equal([7], data);
        Assert.Equal(10, Mock.HitsFor(Team).Count);
    }

    [Fact]
    public async Task WaitsTheRetryAfterOfARefusedReconnectAndThrowsTheLastRefusalWhenTheyRunOut()
    {
        Mock.Stream(Team, stream =>
        {
            stream.Write(": keep-alive\n\n");
            stream.End();
            return Task.CompletedTask;
        });
        Mock.Error(Team, 503, "unavailable", "Busy", new Dictionary<string, string> { ["Retry-After"] = "1" });
        Mock.Error(Team, 429, "rate_limit_exceeded", "Slow down", new Dictionary<string, string> { ["Retry-After"] = "1" });
        using var client = Client();
        await using var stream = await client.Inboxes.Events.StreamAsync(
            options: new EventStreamOptions { MaxReconnects = 1, ReconnectDelay = TimeSpan.FromMilliseconds(1) });
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<RateLimitException>(() => Drain(stream));
        Assert.Equal("rate_limit_exceeded", error.Name);
        // The second reconnect waited the first refusal's retry-after, not the 1 ms delay.
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(950), clock.Elapsed.ToString());
        Assert.Equal(3, Mock.HitsFor(Team).Count);
    }

    [Fact]
    public async Task ThrowsARefusalWhoseRetryAfterIsOverAMinuteAtOnce()
    {
        Mock.Stream(Team, stream =>
        {
            stream.Write(": keep-alive\n\n");
            stream.End();
            return Task.CompletedTask;
        });
        Mock.Error(Team, 429, "rate_limit_exceeded", "Slow down", new Dictionary<string, string> { ["Retry-After"] = "120" });
        using var client = Client();
        await using var stream = await client.Inboxes.Events.StreamAsync(
            options: new EventStreamOptions { ReconnectDelay = TimeSpan.FromMilliseconds(1) });
        // Bounded, so a stream that kept reconnecting would fail here rather than hang.
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<RateLimitException>(async () =>
        {
            await foreach (var _ in stream.WithCancellation(bound.Token))
            {
            }
        });
        Assert.Equal(2, Mock.HitsFor(Team).Count);
    }

    [Fact]
    public async Task GivesUpAfterMaxReconnectsThatCannotConnect()
    {
        Mock.Stream(Team, stream =>
        {
            stream.Write(": keep-alive\n\n");
            stream.End();
            return Task.CompletedTask;
        });
        for (var i = 0; i < 3; i++)
        {
            Mock.Fail(Team, new HttpRequestException("Connection refused"));
        }

        using var client = Client();
        await using var stream = await client.Inboxes.Events.StreamAsync(
            options: new EventStreamOptions { MaxReconnects = 2, ReconnectDelay = TimeSpan.FromMilliseconds(1) });
        var error = await Assert.ThrowsAsync<ApiConnectionException>(() => Drain(stream));
        Assert.Contains("gave up after 2 reconnects", error.Message);
        Assert.Equal(4, Mock.HitsFor(Team).Count);
    }

    [Fact]
    public async Task RetriesAReconnectRefusedWith503AndThrowsARefusalThatRetryingCannotFix()
    {
        Mock.Stream(Team, stream =>
        {
            stream.Write("retry: 1\nid: 1\ndata: {}\n\n");
            stream.End();
            return Task.CompletedTask;
        });
        Mock.Error(Team, 503, "unavailable", "Busy");
        Mock.Error(Team, 403, "restricted_api_key", "Team-wide key needed");
        using var client = Client();
        await using var stream = await client.Inboxes.Events.StreamAsync();
        var error = await Assert.ThrowsAsync<PermissionDeniedException>(() => Drain(stream));
        Assert.Equal(403, error.StatusCode);
        Assert.Equal("restricted_api_key", error.Name);
        Assert.Equal([null, "1", "1"], Mock.HitsFor(Team).Select(hit => hit.Header("last-event-id")));
    }

    [Fact]
    public async Task ThrowsTheRefusalWhenTheStreamCannotOpen()
    {
        Mock.Error(Team, 403, "restricted_api_key", "No");
        using var client = Client();
        var error = await Assert.ThrowsAsync<PermissionDeniedException>(() => client.Inboxes.Events.StreamAsync());
        Assert.Equal(403, error.StatusCode);
        Assert.Equal("restricted_api_key", error.Name);
    }

    [Fact]
    public async Task RetriesTheFirstConnectionOn5xx()
    {
        Mock.Error(Team, 503, "unavailable", "Busy");
        using var client = Client();
        await using var stream = await client.Inboxes.Events.StreamAsync(options: new EventStreamOptions { Reconnect = false });
        Assert.Single(await Drain(stream));
        Assert.Equal(2, Mock.Hits.Count);
    }

    [Fact]
    public async Task RefusesAnAnswerThatIsNotAnEventStream()
    {
        Mock.Reply(Team, 200, new JsonObject { ["hello"] = "world" });
        using var client = Client();
        var error = await Assert.ThrowsAsync<BillionSendException>(() => client.Inboxes.Events.StreamAsync());
        Assert.Equal(200, error.StatusCode);
        Assert.Equal("application_error", error.Name);
        Assert.Equal("Expected an event stream, got application/json", error.Message);
    }

    [Fact]
    public async Task HandsOutStreamGapOncePerPositionTypedWithoutMovingThePosition()
    {
        const string gap = "event: stream.gap\ndata: {\"reason\":\"retention\"}\n\n";
        // The server repeats the gap on each reconnect from the same old position.
        Mock.Stream(Team, stream =>
        {
            stream.Write($"retry: 1\n\n{gap}");
            stream.End();
            return Task.CompletedTask;
        });
        Mock.Stream(Team, stream =>
        {
            stream.Write(gap);
            stream.Write("id: 9.1.1790000000\nevent: inbox.email.received\ndata: {\"type\":\"inbox.email.received\",\"created_at\":\"2026-11-16T00:00:00.000Z\",\"inbox_id\":\"i\",\"data\":{\"inbox_id\":\"i\",\"thread_id\":\"t\",\"email_id\":\"e\"}}\n\n");
            stream.End();
            return Task.CompletedTask;
        });
        // A gap after a newer position is a new gap.
        Mock.Stream(Team, stream =>
        {
            stream.Write(gap);
            stream.End();
            return Task.CompletedTask;
        });
        Mock.Error(Team, 403, "restricted_api_key", "stop");
        using var client = Client();
        await using var stream = await client.Inboxes.Events.StreamAsync(options: new EventStreamOptions { LastEventId = "1.1.1" });
        var items = new List<InboxStreamItem>();
        await Assert.ThrowsAsync<PermissionDeniedException>(async () =>
        {
            await foreach (var item in stream)
            {
                items.Add(item);
            }
        });
        Assert.Equal(
            [("stream.gap", (string?)null), ("inbox.email.received", "9.1.1790000000"), ("stream.gap", null)],
            items.Select(item => (item.Event, item.Id)));
        Assert.Equal("retention", Assert.IsType<StreamGapEvent>(items[0]).Reason);
        Assert.Equal("e", (string?)Assert.IsType<InboxStreamEvent>(items[1]).Payload!["email_id"]);
        Assert.Equal(["1.1.1", "1.1.1", "9.1.1790000000", "9.1.1790000000"], Mock.HitsFor(Team).Select(hit => hit.Header("last-event-id")));
    }

    [Fact]
    public async Task EndsOneInboxsStreamAfterInboxDeletedWithoutReconnectingTheTeamStreamGoesOn()
    {
        Mock.Stream(One, stream =>
        {
            stream.Write("id: 1\nevent: inbox.deleted\ndata: {\"type\":\"inbox.deleted\",\"created_at\":\"x\",\"inbox_id\":\"i\",\"data\":{}}\n\n");
            stream.End();
            return Task.CompletedTask;
        });
        using var client = Client();
        await using (var one = await client.Inboxes.Events.StreamInboxAsync(Id, null, new EventStreamOptions { ReconnectDelay = TimeSpan.FromMilliseconds(1) }))
        {
            Assert.Equal(["inbox.deleted"], (await Drain(one)).Select(item => item.Event));
        }

        Assert.Single(Mock.HitsFor(One));

        Mock.Stream(Team, stream =>
        {
            stream.Write("id: 1\nevent: inbox.expired\ndata: {\"type\":\"inbox.expired\",\"created_at\":\"x\",\"inbox_id\":\"i\",\"data\":{}}\n\n");
            stream.Write("id: 2\nevent: inbox.created\ndata: {\"type\":\"inbox.created\",\"created_at\":\"x\",\"inbox_id\":\"j\",\"data\":{}}\n\n");
            return Task.CompletedTask;
        });
        await using var team = await client.Inboxes.Events.StreamAsync();
        var names = new List<string>();
        await foreach (var item in team)
        {
            names.Add(item.Event);
            if (names.Count == 2)
            {
                break;
            }
        }

        Assert.Equal(["inbox.expired", "inbox.created"], names);
    }

    [Fact]
    public async Task EndsQuietlyOnCloseAndCanBeIteratedOnce()
    {
        Mock.Stream(Team, stream =>
        {
            stream.Write("id: 1\ndata: {}\n\n");
            return Task.CompletedTask;
        });
        using var client = Client();
        var stream = await client.Inboxes.Events.StreamAsync();
        var seen = new List<string?>();
        await foreach (var item in stream)
        {
            seen.Add(item.Id);
            stream.Close();
        }

        Assert.Equal(["1"], seen);
        Assert.Throws<InvalidOperationException>(() => stream.GetAsyncEnumerator());
    }

    [Fact]
    public async Task ThrowsOperationCanceledOnTheCallersToken()
    {
        SseStream? body = null;
        Mock.Stream(Team, stream =>
        {
            body = stream;
            stream.Write("id: 2\ndata: {}\n\n");
            return Task.CompletedTask;
        });
        using var client = Client();
        using var cts = new CancellationTokenSource();
        await using var stream = await client.Inboxes.Events.StreamAsync(cancellationToken: cts.Token);
        var ids = new List<string?>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var item in stream)
            {
                ids.Add(item.Id);
                cts.Cancel();
            }
        });
        Assert.Equal(["2"], ids);
        await body!.Closed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(Mock.Hits);
    }
}
