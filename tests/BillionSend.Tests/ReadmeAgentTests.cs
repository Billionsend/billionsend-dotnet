namespace BillionSend.Tests;

/// <summary>The README's agent example, run on the contract mock with the events a real inbox stream sends.</summary>
public sealed class ReadmeAgentTests : Support.MockTest
{
    [Fact]
    public async Task AnswersAReceivedEmailAndIgnoresTheEventsATypesFilterDoesNotHold()
    {
        Mock.Stream("GET /inboxes/{inbox_id}/events", stream =>
        {
            // The server opens a fresh stream with its position, then sends what the inbox holds.
            stream.Write("retry: 1000\n\nid: 1.1.1\n\n");
            stream.Write($"id: 1.1.2\nevent: inbox.email.received\ndata: {{\"type\":\"inbox.email.received\",\"inbox_id\":\"{Id}\",\"data\":{{\"thread_id\":\"{Id}\",\"email_id\":\"{Id2}\"}}}}\n\n");
            // inbox.deleted and inbox.expired always arrive, whatever `types` says, and have no thread.
            stream.Write($"id: 1.1.3\nevent: inbox.deleted\ndata: {{\"type\":\"inbox.deleted\",\"inbox_id\":\"{Id}\",\"data\":{{}}}}\n\n");
            return Task.CompletedTask;
        });
        using var client = Client();
        await ReadmeExamples.AgentLoop(client);
        var keys = Mock.Hits.Select(hit => hit.Key).ToList();
        Assert.Equal("GET /inboxes/{inbox_id}/events", keys[0]);
        Assert.Equal(1, keys.Count(key => key.StartsWith("POST ", StringComparison.Ordinal) && key.EndsWith("/reply", StringComparison.Ordinal)));
        Assert.Equal(1, keys.Count(key => key.Contains("/threads/", StringComparison.Ordinal) && key.StartsWith("GET ", StringComparison.Ordinal)));
        var reply = Mock.Hits.Single(hit => hit.Key.EndsWith("/reply", StringComparison.Ordinal));
        Assert.Equal($"reply/{Id2}", reply.Header("idempotency-key"));
        Assert.EndsWith($"/emails/{Id2}/reply", reply.Url.AbsolutePath);
    }
}
