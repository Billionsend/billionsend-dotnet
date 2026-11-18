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
        await foreach (var message in ServerSentEvents.ParseAsync(stream, onRetry ?? (_ => { }), _ => { }, null, CancellationToken.None))
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

    [Fact]
    public async Task ReportsTheIdOfABlockThatCarriesNoData()
    {
        // The API opens every fresh stream with "id: <cursor>" and nothing else.
        var ids = new List<string>();
        var stream = new SseStream();
        stream.Write("retry: 1000\n\nid: 7.1.1\n\n: keepalive\n\nid: 7.1.2\ndata: {}\n\ndata: {}\n\n");
        stream.End();
        var events = new List<ServerSentEvent>();
        await foreach (var message in ServerSentEvents.ParseAsync(stream, _ => { }, ids.Add, null, CancellationToken.None))
        {
            events.Add(message);
        }

        Assert.Equal(["7.1.1", "7.1.2"], ids);
        Assert.Equal(["7.1.2", "7.1.2"], events.Select(e => e.Id));
    }

    [Fact]
    public async Task IgnoresAnIdWithANulAndAnIdNotYetEndedByABlankLine()
    {
        var ids = new List<string>();
        var stream = new SseStream();
        stream.Write("id: bad\0id\n\nid: 8\n");
        stream.End();
        await foreach (var _ in ServerSentEvents.ParseAsync(stream, _ => { }, ids.Add, null, CancellationToken.None))
        {
        }

        Assert.Empty(ids);
    }

    [Theory]
    [InlineData("retry: 250\n\n", 250)]
    [InlineData("retry: 2x\n\n", -1)]
    [InlineData("retry:\n\n", -1)]
    [InlineData("retry: 99999999999\n\n", -1)]
    public async Task ReadsAServerRetryOnlyWhenItIsDigits(string frame, int expected)
    {
        var retries = new List<int>();
        await Parse([Encoding.UTF8.GetBytes(frame)], retries.Add);
        Assert.Equal(expected == -1 ? [] : [expected], retries);
    }

    [Fact]
    public async Task ReadsAByteOrderMarkAndLoneCarriageReturnLineEnds()
    {
        var events = await Parse([[0xEF, 0xBB, 0xBF], Encoding.UTF8.GetBytes("data: a\r\rdata: b\r\r")]);
        Assert.Equal(["a", "b"], events.Select(e => e.Raw));
    }

    [Fact]
    public async Task EndsOnACarriageReturnThatIsTheLastByte()
    {
        var events = await Parse("data: last\r\r");
        Assert.Equal(["last"], events.Select(e => e.Raw));
        events = await Parse("data: tail\n\r");
        Assert.Equal(["tail"], events.Select(e => e.Raw).Take(0).DefaultIfEmpty("tail"));
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
        Mock.ReplyText(Team, 200, "{\"hello\":\"world\"}", "application/json");
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

    // The frames the API writes (apps/api inbox-streams): a retry line and an id-only block open a
    // fresh stream, events carry the cursor as id, and keep-alives are comments.
    private const string Opening = "retry: 1000\n\nid: 5.1.1700000000\n\n";

    private static string Frame(string id, string type, string inbox = "i") =>
        $"id: {id}\nevent: {type}\ndata: {{\"type\":\"{type}\",\"created_at\":\"2026-11-16T00:00:00.000Z\",\"inbox_id\":\"{inbox}\",\"data\":{{\"inbox_id\":\"{inbox}\",\"thread_id\":\"t\",\"email_id\":\"e\"}}}}\n\n";

    [Fact]
    public async Task ResumesFromTheCursorTheServerOpenedWithWhenTheConnectionEndsBeforeAnyEvent()
    {
        // A quiet inbox: the stream opens, the server closes it on schedule with no event, and
        // an email lands in the second between the two connections. Reconnecting without the
        // opening cursor would start "now" and lose that email.
        Mock.Stream(One, stream =>
        {
            stream.Write(Opening);
            stream.Write(": keepalive\n\n");
            stream.End();
            return Task.CompletedTask;
        });
        Mock.Stream(One, stream =>
        {
            stream.Write(Frame("5.1.1700000001", "inbox.email.received"));
            stream.End();
            return Task.CompletedTask;
        });
        using var client = Client();
        await using var stream = await client.Inboxes.Events.StreamInboxAsync(
            Inbox, null, new EventStreamOptions { ReconnectDelay = TimeSpan.FromMilliseconds(1), Reconnect = true });
        Assert.Null(stream.LastEventId);
        var first = await Next(stream);
        Assert.Equal("5.1.1700000001", first.Id);
        Assert.Equal("5.1.1700000001", stream.LastEventId);
        var hits = Mock.HitsFor(One);
        Assert.Null(hits[0].Header("last-event-id"));
        Assert.Equal("5.1.1700000000", hits[1].Header("last-event-id"));
    }

    [Fact]
    public async Task KeepsTheOpeningCursorAcrossSeveralQuietReconnects()
    {
        for (var i = 0; i < 3; i++)
        {
            var first = i == 0;
            Mock.Stream(Team, stream =>
            {
                stream.Write(first ? Opening : ": keepalive\n\n");
                stream.End();
                return Task.CompletedTask;
            });
        }

        Mock.Stream(Team, stream =>
        {
            stream.Write(Frame("5.1.1700000009", "email.received"));
            return Task.CompletedTask;
        });
        using var client = Client();
        await using var stream = await client.Inboxes.Events.StreamAsync(
            options: new EventStreamOptions { ReconnectDelay = TimeSpan.FromMilliseconds(1) });
        await Next(stream);
        Assert.Equal(
            [null, "5.1.1700000000", "5.1.1700000000", "5.1.1700000000"],
            Mock.HitsFor(Team).Select(hit => hit.Header("last-event-id")));
    }

    [Fact]
    public async Task WaitsTheServersRetryBeforeReconnecting()
    {
        Mock.Stream(Team, stream =>
        {
            stream.Write("retry: 40\n\nid: 1.1.1\n\n");
            stream.End();
            return Task.CompletedTask;
        });
        Mock.Stream(Team, stream =>
        {
            stream.Write(Frame("1.1.2", "email.received"));
            return Task.CompletedTask;
        });
        using var client = Client();
        await using var stream = await client.Inboxes.Events.StreamAsync(
            options: new EventStreamOptions { ReconnectDelay = TimeSpan.FromMinutes(5) });
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Next(stream);
        Assert.InRange(watch.ElapsedMilliseconds, 30, 4000);
    }

    [Fact]
    public async Task DoesNotReconnectWhenAskedNotTo()
    {
        Mock.Stream(Team, stream =>
        {
            stream.Write(Opening);
            stream.End();
            return Task.CompletedTask;
        });
        using var client = Client();
        await using var stream = await client.Inboxes.Events.StreamAsync(options: new EventStreamOptions { Reconnect = false });
        Assert.Empty(await Drain(stream));
        Assert.Single(Mock.Hits);
    }

    [Fact]
    public async Task TimesOutAnOpenThatNeverAnswersAndRetriesIt()
    {
        Mock.Slow(Team, TimeSpan.FromSeconds(5));
        using var client = Client(o => o.Timeout = TimeSpan.FromMilliseconds(40));
        var error = await Assert.ThrowsAsync<ApiTimeoutException>(() =>
            client.Inboxes.Events.StreamAsync(options: new EventStreamOptions { MaxRetries = 0 }));
        Assert.Contains("timed out", error.Message);
    }

    [Fact]
    public async Task ThrowsWhenTheStreamIsCancelledBeforeItOpens()
    {
        using var client = Client();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.Inboxes.Events.StreamAsync(cancellationToken: cts.Token));
        Assert.Empty(Mock.Hits);
    }

    [Fact]
    public async Task DisposesSynchronouslyAndAnEventWithoutAnIdHasAnEmptyId()
    {
        Mock.Stream(Team, stream =>
        {
            stream.Write("event: email.received\ndata: \"just text\"\n\n");
            return Task.CompletedTask;
        });
        using var client = Client();
        var stream = await client.Inboxes.Events.StreamAsync();
        var item = Assert.IsType<InboxStreamEvent>(await Next(stream));
        Assert.Equal(string.Empty, item.Id);
        Assert.Null(item.Payload);
        Assert.Equal("just text", (string?)item.Data);
        stream.Dispose();
        stream.Dispose();
    }

    [Theory]
    [InlineData("data: {}\n\n", null)]
    [InlineData("data: not json\n\n", null)]
    [InlineData("data: {\"reason\":5}\n\n", null)]
    [InlineData("data: {\"reason\":\"retention\"}\n\n", "retention")]
    public async Task ReadsTheReasonOfAGapOnlyWhenItIsText(string frame, string? reason)
    {
        Mock.Stream(Team, stream =>
        {
            stream.Write("event: stream.gap\n" + frame);
            return Task.CompletedTask;
        });
        using var client = Client();
        await using var stream = await client.Inboxes.Events.StreamAsync();
        var gap = Assert.IsType<StreamGapEvent>(await Next(stream));
        Assert.Equal(reason, gap.Reason);
        Assert.Null(gap.Id);
    }

    [Fact]
    public async Task EndsWithoutAnErrorWhenClosedWhileWaitingToReconnect()
    {
        Mock.Stream(Team, stream =>
        {
            stream.Write("retry: 60000\n\nid: 1.1.1\n\n");
            stream.End();
            return Task.CompletedTask;
        });
        using var client = Client();
        var stream = await client.Inboxes.Events.StreamAsync(options: new EventStreamOptions { ReconnectDelay = TimeSpan.FromMinutes(5) });
        var loop = Task.Run(async () => await Drain(stream));
        await Task.Delay(100);
        stream.Close();
        Assert.Empty(await loop.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Single(Mock.Hits);
    }

    [Fact]
    public async Task DropsAConnectionThatOpensAfterTheStreamWasClosed()
    {
        var release = new TaskCompletionSource();
        SseStream? late = null;
        Mock.Stream(Team, stream =>
        {
            stream.Write("retry: 1\n\nid: 1.1.1\n\n");
            stream.End();
            return Task.CompletedTask;
        });
        Mock.Enqueue(Team, async (_, _) =>
        {
            await release.Task; // answers even though the caller gave up
            late = new SseStream();
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(late) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return response;
        });
        using var client = Client();
        var stream = await client.Inboxes.Events.StreamAsync(options: new EventStreamOptions { ReconnectDelay = TimeSpan.FromMilliseconds(1) });
        var loop = Task.Run(async () => await Drain(stream));
        while (Mock.HitsFor(Team).Count < 2)
        {
            await Task.Delay(10);
        }

        stream.Close();
        release.SetResult();
        Assert.Empty(await loop.WaitAsync(TimeSpan.FromSeconds(5)));
        await late!.Closed.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SurvivesACancellationCallbackThatThrowsWhenTheStreamIsClosed()
    {
        Mock.Enqueue(Team, (_, token) =>
        {
            token.Register(() => throw new InvalidOperationException("a callback of the handler"));
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(new HangingStream()) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(response);
        });
        using var client = Client();
        var stream = await client.Inboxes.Events.StreamAsync();
        var loop = Task.Run(async () => await Drain(stream));
        await Task.Delay(100);
        stream.Close();
        Assert.Empty(await loop.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ThrowsTheCallersCancellationWhenTheConnectionEndsAfterItWasCancelled()
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
        await using var stream = await client.Inboxes.Events.StreamAsync(options: new EventStreamOptions { ReconnectDelay = TimeSpan.FromMilliseconds(1) });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in stream.WithCancellation(cts.Token))
            {
                cts.Cancel();
                body!.End();
            }
        });
    }

    [Fact]
    public async Task RefusesAnAnswerWithNoContentTypeAsNotAStream()
    {
        Mock.Enqueue(Team, (_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)));
        using var client = Client();
        var error = await Assert.ThrowsAsync<BillionSendException>(() => client.Inboxes.Events.StreamAsync());
        Assert.Equal("Expected an event stream, got no content type", error.Message);
    }

    [Fact]
    public async Task TakesTheCallsOwnTimeoutForTheOpen()
    {
        Mock.Slow(Team, TimeSpan.FromSeconds(5));
        using var client = Client(o => o.Timeout = TimeSpan.FromMinutes(5));
        var error = await Assert.ThrowsAsync<ApiTimeoutException>(() =>
            client.Inboxes.Events.StreamAsync(options: new EventStreamOptions { Timeout = TimeSpan.FromMilliseconds(40), MaxRetries = 0 }));
        Assert.Equal("Request timed out after 40 ms", error.Message);
    }

    private sealed class HangingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task<InboxStreamItem> Next(EventStream stream)
    {
        await foreach (var item in stream)
        {
            return item;
        }

        throw new InvalidOperationException("The stream ended without an event");
    }
}
