using System.Security.Cryptography;
using System.Text;
using BillionSend.Tests.Support;

namespace BillionSend.Tests;

/// <summary>
/// What the API added with per-domain deliverability: suppressions that name a whole domain
/// (<c>kind</c>, <c>domain</c>, <c>removable</c>, an absent <c>email</c>), a return-path subdomain
/// that cannot change, each sending domain's standing, the <c>domain_sending_paused</c> refusal and
/// the webhook payload's <c>scope</c> and <c>domain</c>.
/// </summary>
public sealed class SuppressionsAndDomainStandingTests : MockTest
{
    // One entry as the API answers it: the schema's own example, with the fields under test set.
    private static JsonObject Entry(string id, string? email, string? domain, bool removable = true)
    {
        var entry = (JsonObject)Spec.Example(Spec.Get("GET /suppressions/{id}").ResponseSchema, full: true)!;
        entry["id"] = id;
        entry["email"] = email;
        entry["kind"] = domain is null ? "email" : "domain";
        entry["domain"] = domain;
        entry["origin"] = "manual";
        entry["removable"] = removable;
        return entry;
    }

    [Fact]
    public async Task BlocksAWholeDomainWithNoEmailAndReadsTheEntryBackWithoutOne()
    {
        var created = Entry(Id, null, "spam.example");
        Mock.Reply("POST /suppressions", 200, created);
        Mock.Reply("GET /suppressions/{id}", 200, Entry(Id, null, "spam.example"));
        using var client = Client();
        var answer = await client.Suppressions.CreateAsync(new { domain = "spam.example" });
        var got = await client.Suppressions.GetAsync(Id);

        var sent = Mock.HitsFor("POST /suppressions").Single().Json!;
        Assert.Equal("spam.example", (string?)sent["domain"]);
        Assert.Null(sent["email"]);
        foreach (var entry in new[] { answer!, got! })
        {
            Assert.Equal("domain", (string?)entry["kind"]);
            Assert.Equal("spam.example", (string?)entry["domain"]);
            Assert.Null((string?)entry["email"]);
            Assert.True((bool)entry["removable"]!);
        }
    }

    [Fact]
    public async Task AddsDomainsInABatchAloneOrBesideEmails()
    {
        Mock.ReplyExample("POST /suppressions/batch/add");
        Mock.ReplyExample("POST /suppressions/batch/add");
        using var client = Client();
        await client.Suppressions.Batch.AddAsync(new { domains = new[] { "spam.example", "junk.example" } });
        await client.Suppressions.Batch.AddAsync(new { emails = new[] { "a@example.com" }, domains = new[] { "spam.example" }, origin = "manual" });

        var hits = Mock.HitsFor("POST /suppressions/batch/add");
        Assert.Equal(["spam.example", "junk.example"], hits[0].Json!["domains"]!.AsArray().Select(d => (string?)d));
        Assert.Null(hits[0].Json!["emails"]);
        Assert.Equal("a@example.com", (string?)hits[1].Json!["emails"]![0]);
        Assert.Equal("spam.example", (string?)hits[1].Json!["domains"]![0]);
    }

    [Fact]
    public async Task ListsTheDomainEntriesByKindAndWalksAMixedListWhoseEmailsCanBeEmpty()
    {
        const string second = "0b8e3c6a-0000-4000-8000-000000000002";
        Mock.ReplyExample("GET /suppressions", page =>
        {
            page["has_more"] = true;
            page["data"] = new JsonArray(Entry(Id, "a@example.com", null), Entry(second, null, "spam.example", removable: false));
        });
        Mock.ReplyExample("GET /suppressions", page =>
        {
            page["has_more"] = false;
            page["data"] = new JsonArray(Entry(Id2, null, "junk.example"));
        });
        using var client = Client();
        var seen = new List<string>();
        await foreach (var entry in client.Suppressions.ListAllAsync(new Dictionary<string, object?> { ["kind"] = "all", ["limit"] = 2 }))
        {
            seen.Add((string?)entry["email"] ?? (string)entry["domain"]!);
        }

        Assert.Equal(["a@example.com", "spam.example", "junk.example"], seen);
        var hits = Mock.HitsFor("GET /suppressions");
        Assert.All(hits, hit => Assert.Equal("all", hit.Query["kind"]));
        Assert.Equal(second, hits[1].Query["after"]);
    }

    [Fact]
    public async Task ReadsAPermanentEntryAsNotRemovable()
    {
        Mock.Reply("GET /suppressions/{id}", 200, Entry(Id, "bounced@example.com", null, removable: false));
        using var client = Client();
        var entry = (await client.Suppressions.GetAsync(Id))!;
        Assert.False((bool)entry["removable"]!);
        Assert.Equal("email", (string?)entry["kind"]);
        Assert.Null((string?)entry["domain"]);
    }

    [Fact]
    public async Task ThrowsA403ThatNamesAPausedDomainAsTheSameTypeAsATeamPause()
    {
        using var client = Client(o => o.MaxRetries = 0);
        foreach (var name in new[] { "domain_sending_paused", "sending_paused" })
        {
            Mock.Error("POST /emails", 403, name, $"refused: {name}");
            var error = await Assert.ThrowsAsync<PermissionDeniedException>(() => client.Emails.SendAsync(new
            {
                from = "Acme <hello@example.com>",
                to = new[] { "person@example.com" },
                subject = "Hi",
                text = "Hi",
            }));
            Assert.Equal(403, error.StatusCode);
            Assert.Equal(name, error.Name);
        }
    }

    [Fact]
    public async Task RefusesToChangeTheReturnPathAfterCreationWith422AndCreatesWithOrWithoutOne()
    {
        Mock.Error("PATCH /domains/{id}", 422, "validation_error", "custom_return_path cannot be changed");
        Mock.ReplyExample("POST /domains");
        Mock.ReplyExample("POST /domains");
        using var client = Client(o => o.MaxRetries = 0);
        var error = await Assert.ThrowsAsync<ValidationException>(() => client.Domains.UpdateAsync(Id, new { custom_return_path = "mail" }));
        Assert.Equal(422, error.StatusCode);
        Assert.Equal("mail", (string?)Mock.HitsFor("PATCH /domains/{id}").Single().Json!["custom_return_path"]);

        await client.Domains.CreateAsync(new { name = "example.com" });
        await client.Domains.CreateAsync(new { name = "example.org", custom_return_path = "bounce" });
        var hits = Mock.HitsFor("POST /domains");
        Assert.Null(hits[0].Json!["custom_return_path"]);
        Assert.Equal("bounce", (string?)hits[1].Json!["custom_return_path"]);
    }

    [Fact]
    public async Task ReadsEachSendingDomainsStandingNextToTheTeams()
    {
        Mock.ReplyExample("GET /deliverability", body =>
        {
            body["domains"] = new JsonArray(
                Standing(Id, "news.example.com", "paused", "domain"),
                Standing(null, null, "ok", null));
        });
        using var client = Client();
        var answer = await client.Deliverability.GetAsync();
        var domains = answer!["domains"]!.AsArray();
        Assert.Equal(2, domains.Count);
        Assert.Equal("news.example.com", (string?)domains[0]!["domain"]);
        Assert.Equal("paused", (string?)domains[0]!["status"]);
        Assert.Equal("domain", (string?)domains[0]!["paused_by"]);
        Assert.Equal(0.02, (double)domains[0]!["recent_hard_bounce_rate"]!, 5);
        Assert.Equal(-0.5, (double)domains[0]!["pause_margin"]!, 5);
        Assert.Null((string?)domains[1]!["domain_id"]);
        Assert.Null((string?)domains[1]!["paused_by"]);
    }

    private static JsonObject Standing(string? id, string? domain, string status, string? pausedBy) => new()
    {
        ["domain_id"] = id,
        ["domain"] = domain,
        ["status"] = status,
        ["tier"] = status,
        ["paused_by"] = pausedBy,
        ["emails_sent"] = 1200,
        ["hard_bounce_rate"] = 0.01,
        ["complaint_rate"] = 0.0,
        ["recent_hard_bounce_rate"] = 0.02,
        ["recent_complaint_rate"] = 0.0,
        ["pause_margin"] = -0.5,
        ["hard_bounces"] = 12,
        ["complaints"] = 0,
        ["delivered"] = 1100,
        ["window_days"] = 7,
    };

    // A delivery as the API signs it: HMAC-SHA256 over "<id>.<timestamp>.<body>".
    private static JsonNode Verified(string payload)
    {
        const string id = "msg_1";
        var secret = "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        using var hmac = new HMACSHA256(Convert.FromBase64String(secret["whsec_".Length..]));
        var signature = "v1," + Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{id}.{timestamp}.{payload}")));
        var headers = new Dictionary<string, string>
        {
            ["webhook-id"] = id,
            ["webhook-timestamp"] = timestamp,
            ["webhook-signature"] = signature,
        };
        return Webhook.Verify(payload, headers, secret);
    }

    [Theory]
    [InlineData("deliverability.warning", "domain", "{\"id\":\"d1\",\"name\":\"news.example.com\"}")]
    [InlineData("deliverability.paused", "domain", "{\"id\":\"d1\",\"name\":\"news.example.com\"}")]
    [InlineData("deliverability.warning", "team", "null")]
    [InlineData("deliverability.paused", "team", "null")]
    public void VerifiesADeliverabilityDeliveryAndExposesItsScopeAndDomain(string type, string scope, string domain)
    {
        var body = Verified($"{{\"type\":\"{type}\",\"created_at\":\"2026-11-19T10:00:00.000Z\",\"data\":{{\"scope\":\"{scope}\",\"domain\":{domain},\"status\":\"paused\"}}}}");
        Assert.Equal(type, (string?)body["type"]);
        var data = body["data"]!;
        Assert.Equal(scope, (string?)data["scope"]);
        if (scope == "domain")
        {
            Assert.Equal("d1", (string?)data["domain"]!["id"]);
            Assert.Equal("news.example.com", (string?)data["domain"]!["name"]);
        }
        else
        {
            Assert.Null(data["domain"]);
        }
    }
}
