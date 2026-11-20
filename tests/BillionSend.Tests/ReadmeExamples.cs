// The README's examples, kept here so the build fails when they drift from the SDK, and checked
// line by line against the README by ReadmeTests. The ones that take a client are run against the
// contract mock by ReadmeRunTests (every request they send is validated against the OpenAPI
// schemas) and against the live API by the BillionSend.E2E scenarios; the rest need a real
// account, a web server or an inbox, and are only compiled.
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
            to = new[] { "success@simulator.amazonses.com" },
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

    public static async Task ImportContacts(BillionSendClient client)
    {
        var file = await FileUpload.FromFileAsync("people.csv");
        var queued = await client.Contacts.Imports.CreateAsync(new Dictionary<string, object?>
        {
            ["file"] = file,
            ["column_map"] = new { email = "Email", first_name = "First Name" },
            ["on_conflict"] = "skip",
        });

        var import = await client.Contacts.Imports.GetAsync((string)queued!["id"]!);
        Console.WriteLine($"{import!["status"]}");
    }

    public static async Task BrandAndLogos(BillionSendClient client, string domainId)
    {
        await client.Brand.UpdateAsync(new { accent_color = "#2B44FF", font = "serif", powered_by = false });

        var logo = await FileUpload.FromFileAsync("logo.png", "image/png");
        var team = await client.Brand.Logo.UploadAsync(new Dictionary<string, object?> { ["file"] = logo });
        Console.WriteLine($"{team!["logo_url"]}");

        var own = await client.Domains.UnsubscribePage.Logo.UploadAsync(domainId, new Dictionary<string, object?> { ["file"] = logo });
        await client.Domains.UpdateAsync(domainId, new { unsubscribe_page = new { support_url = "https://acme.dev/help" } });
        await client.Domains.UnsubscribePage.Logo.DeleteAsync(domainId); // back to the team's logo
    }

    public static async Task RequestsAndAnswers(BillionSendClient client)
    {
        var page = await client.Emails.Receiving.ListAsync(new Dictionary<string, object?>
        {
            ["limit"] = 20,
            ["inbox_id"] = "6a1f1d38-3c55-4a4e-9a57-0f2b3c4d5e6f", // an inbox's ID or its email address; unread needs one, or a key restricted to one
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

            if (item is not InboxStreamEvent { Event: "inbox.email.received" } received)
            {
                // `types` does not filter inbox.deleted and inbox.expired, and they carry no thread.
                continue;
            }

            var ids = received.Payload;
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

    public static async Task Suppressions(BillionSendClient client)
    {
        await client.Suppressions.CreateAsync(new { domain = "spam.example" });
        await client.Suppressions.Batch.AddAsync(new { emails = new[] { "a@example.com" }, domains = new[] { "junk.example" } });

        await foreach (var entry in client.Suppressions.ListAllAsync(new Dictionary<string, object?> { ["kind"] = "all" }))
        {
            Console.WriteLine((string?)entry["email"] ?? (string?)entry["domain"]);
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

/// <summary>The README examples that take a client, run: what they send is what the API's schemas accept.</summary>
public sealed class ReadmeRunTests : Support.MockTest
{
    [Fact]
    public async Task RunsTheRequestAndAnswerExample()
    {
        using var client = Client();
        await ReadmeExamples.RequestsAndAnswers(client);
        var hit = Assert.Single(Mock.Hits);
        Assert.Equal("GET /emails/receiving", hit.Key);
        Assert.Equal("20", hit.Query["limit"]);
        Assert.Equal("6a1f1d38-3c55-4a4e-9a57-0f2b3c4d5e6f", hit.Query["inbox_id"]);
        Assert.Equal("true", hit.Query["unread"]);
    }

    [Fact]
    public async Task RunsTheCsvImportExample()
    {
        var path = Path.Combine(Directory.GetCurrentDirectory(), "people.csv");
        await File.WriteAllTextAsync(path, "Email,First Name\nana@example.com,Ana\n");
        try
        {
            using var client = Client();
            await ReadmeExamples.ImportContacts(client);
            Assert.Equal(["POST /contacts/imports", "GET /contacts/imports/{id}"], Mock.Hits.Select(hit => hit.Key));
            Assert.Contains("filename=\"people.csv\"", Mock.Hits[0].Body);
            Assert.Contains("Ana", Mock.Hits[0].Body);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RunsTheBrandAndLogoExample()
    {
        var path = Path.Combine(Directory.GetCurrentDirectory(), "logo.png");
        await File.WriteAllBytesAsync(path, Support.TestImages.Png);
        try
        {
            using var client = Client();
            await ReadmeExamples.BrandAndLogos(client, Support.MockTest.Id);
            Assert.Equal(
                ["PATCH /brand", "PUT /brand/logo", "PUT /domains/{id}/unsubscribe-page/logo", "PATCH /domains/{id}", "DELETE /domains/{id}/unsubscribe-page/logo"],
                Mock.Hits.Select(hit => hit.Key));
            Assert.Equal("""{"accent_color":"#2B44FF","font":"serif","powered_by":false}""", Mock.Hits[0].Body);
            foreach (var upload in new[] { Mock.Hits[1], Mock.Hits[2] })
            {
                Assert.Contains("name=\"file\"; filename=\"logo.png\"", upload.Body);
                Assert.Contains("Content-Type: image/png", upload.Body);
            }

            Assert.Equal("""{"unsubscribe_page":{"support_url":"https://acme.dev/help"}}""", Mock.Hits[3].Body);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RunsTheLiveEventsExampleUntilItsTokenIsCancelled()
    {
        using var client = Client();
        using var stopping = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadmeExamples.LiveEvents(client, "1.1.1", stopping.Token));
        var hit = Mock.Hits[0];
        Assert.Equal("GET /inboxes/events", hit.Key);
        Assert.Equal("inbox.email.received,inbox.thread.created", hit.Query["types"]);
        Assert.Equal("1.1.1", hit.Header("last-event-id"));
    }

    [Fact]
    public async Task RunsTheSuppressionsExample()
    {
        using var client = Client();
        await ReadmeExamples.Suppressions(client);
        Assert.Equal(["POST /suppressions", "POST /suppressions/batch/add", "GET /suppressions"], Mock.Hits.Select(hit => hit.Key));
        Assert.Equal("spam.example", (string?)Mock.Hits[0].Json!["domain"]);
        Assert.Equal("junk.example", (string?)Mock.Hits[1].Json!["domains"]![0]);
        Assert.Equal("all", Mock.Hits[2].Query["kind"]);
    }

    [Fact]
    public async Task RunsTheIdempotencyPaginationAndErrorExamples()
    {
        using var client = Client();
        var message = new { from = "Acme <hello@acme.dev>", to = new[] { "a@example.com" }, subject = "Hi", text = "Hi" };
        await ReadmeExamples.Idempotency(client, message, new ReadmeExamples.User("42"));
        await ReadmeExamples.Pagination(client);
        await ReadmeExamples.Errors(client, Support.MockTest.Id);
        Assert.Equal(["POST /emails", "GET /contacts", "GET /emails/{id}"], Mock.Hits.Select(hit => hit.Key));
        Assert.Equal("welcome/42", Mock.Hits[0].Header("idempotency-key"));
        Assert.Equal("100", Mock.Hits[1].Query["limit"]);
    }
}
