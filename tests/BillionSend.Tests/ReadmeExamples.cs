// The README's examples, kept here so the build fails when they drift from the SDK, and checked
// line by line against the README by ReadmeTests. Never executed.
using BillionSend;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace BillionSend.Tests;

#pragma warning disable CS0162, CS0168, CS0219, CS8321 // examples, not code paths

internal static class ReadmeExamples
{
    internal sealed record User(string Id);

    private static Task<string> DraftAnswerAsync(string? subject, string? text) => Task.FromResult("");

    public static async Task QuickStart()
    {
        using var client = new BillionSendClient(); // reads BILLIONSEND_API_KEY

        var sent = await client.Emails.SendAsync(new
        {
            from = "Acme <hello@acme.dev>",
            to = new[] { "ana@example.com" },
            subject = "Welcome to Acme",
            html = "<p>Glad you are here.</p>",
        });

        Console.WriteLine($"queued {sent?["id"]}");
    }

    public static async Task Authentication()
    {
        var client = new BillionSendClient("bs_...");
        // or: new BillionSendClient() with BILLIONSEND_API_KEY set

        using var anonymous = new BillionSendClient();
        var signup = await anonymous.Agent.SignupAsync(new
        {
            human_email = "owner@acme.dev",
            name = "Support agent",
        });

        using var agent = new BillionSendClient((string)signup!["api_key"]!);
        Console.WriteLine(signup["inbox"]?["email_address"]);
    }

    public static async Task RequestsAndAnswers(BillionSendClient client)
    {
        var page = await client.Emails.Receiving.ListAsync(new Dictionary<string, object?>
        {
            ["limit"] = 20,
            ["unread"] = "true",
        });

        foreach (var email in page!["data"]!.AsArray())
        {
            Console.WriteLine($"{email!["from"]}: {email["subject"]}");
        }
    }

    public static async Task Idempotency(BillionSendClient client, object message, User user)
    {
        await client.Emails.SendAsync(message, new IdempotentRequestOptions
        {
            IdempotencyKey = $"welcome/{user.Id}",
        });
    }

    public static async Task AgentLoop(BillionSendClient client)
    {
        const string inboxId = "support@acme.dev"; // an inbox id or its address

        await using var events = await client.Inboxes.Events.StreamInboxAsync(
            inboxId,
            new Dictionary<string, object?> { ["types"] = "inbox.email.received" });

        await foreach (var item in events)
        {
            if (item is StreamGapEvent)
            {
                // Resumed from too far back: events may be missing. Re-read the threads.
                continue;
            }

            var ids = ((InboxStreamEvent)item).Payload;
            var threadId = (string?)ids?["thread_id"];
            var emailId = (string?)ids?["email_id"];
            if (threadId is null || emailId is null)
            {
                continue;
            }

            var email = await client.Inboxes.Threads.Emails.GetAsync(inboxId, threadId, emailId);
            var answer = await DraftAnswerAsync((string?)email?["subject"], (string?)email?["text"]); // your model

            await client.Inboxes.Threads.Emails.ReplyAsync(
                inboxId,
                threadId,
                emailId,
                new { text = answer },
                new IdempotentRequestOptions { IdempotencyKey = $"reply/{emailId}" }); // a replayed event cannot reply twice
        }

        var code = await client.Inboxes.Codes.LatestAsync("support@acme.dev", new Dictionary<string, object?>
        {
            ["from"] = "example.com",
            ["wait"] = 30,
        });
    }

    public static async Task LiveEvents(BillionSendClient client, string? saved, CancellationToken stopping)
    {
        await using var stream = await client.Inboxes.Events.StreamAsync(
            new Dictionary<string, object?> { ["types"] = new[] { "inbox.email.received", "inbox.thread.created" } },
            new EventStreamOptions { LastEventId = saved }); // resume where a previous run stopped

        await foreach (var item in stream.WithCancellation(stopping))
        {
            saved = stream.LastEventId;
            // break, stream.Close() or disposing the stream ends it
        }
    }

    public static async Task Pagination(BillionSendClient client)
    {
        await foreach (var contact in client.Contacts.ListAllAsync(new Dictionary<string, object?> { ["limit"] = 100 }))
        {
            Console.WriteLine(contact["email"]);
        }
    }

    public static async Task Errors(BillionSendClient client, string id)
    {
        try
        {
            var email = await client.Emails.GetAsync(id);
        }
        catch (NotFoundException)
        {
            // gone
        }
        catch (RateLimitException error)
        {
            Console.WriteLine($"retry in {error.RetryAfter} s");
        }
        catch (BillionSendException error)
        {
            Console.WriteLine($"{error.StatusCode} {error.Name}: {error.Message} ({error.RequestId})");
            throw;
        }
    }

    public static async Task Retries(object message)
    {
        var client = new BillionSendClient(new BillionSendOptions
        {
            ApiKey = "bs_...",
            MaxRetries = 4,
            Timeout = TimeSpan.FromSeconds(30),
        });

        await client.Emails.SendAsync(message, new IdempotentRequestOptions { MaxRetries = 0 }); // per call
    }

    public static void Webhooks(WebApplication app, string secret)
    {
        app.MapPost("/webhooks/billionsend", async (HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync();
            try
            {
                var evt = Webhook.Verify(body, name => request.Headers[name], secret);
                if ((string?)evt["type"] == "email.bounced")
                {
                    // ...
                }

                return Results.NoContent();
            }
            catch (WebhookVerificationException)
            {
                return Results.BadRequest();
            }
        });
    }

    public static void Configuration(HttpClient httpClient)
    {
        var client = new BillionSendClient(new BillionSendOptions
        {
            ApiKey = "bs_...",                        // default: BILLIONSEND_API_KEY
            BaseUrl = "https://api.billionsend.com",  // default: BILLIONSEND_BASE_URL, then this
            Timeout = TimeSpan.FromSeconds(60),       // per attempt; event streams are timed only until they open
            MaxRetries = 2,                           // see Retries and timeouts
            HttpClient = httpClient,                  // default: one the client creates and disposes
            Headers = new Dictionary<string, string> { ["X-My-Header"] = "value" }, // sent with every request
        });
    }
}

public sealed class ReadmeTests
{
    /// <summary>Every line of every C# example in the README is in <see cref="ReadmeExamples"/>, which the build compiles.</summary>
    [Fact]
    public void EveryReadmeExampleIsCompiled()
    {
        var root = Support.Spec.RepositoryRoot;
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));
        var examples = File.ReadAllLines(Path.Combine(root, "tests", "BillionSend.Tests", "ReadmeExamples.cs"))
            .Select(line => line.Trim())
            .ToHashSet();
        var blocks = System.Text.RegularExpressions.Regex.Matches(readme, "```csharp\n(.*?)```", System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(blocks.Count >= 12, $"found {blocks.Count} C# blocks");
        var missing = blocks
            .SelectMany(block => block.Groups[1].Value.Split('\n'))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !examples.Contains(line))
            .ToList();
        Assert.Empty(missing);
    }
}
