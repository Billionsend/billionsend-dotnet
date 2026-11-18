// Every operation in the OpenAPI snapshot must be reachable through exactly one SDK call in this
// table, and every call must hit the operation it claims. The calls run through the contract mock
// twice: once with answers that carry every property, once with only the required ones. A new
// operation in the snapshot without an entry here fails the build; so does an SDK call that
// reaches an endpoint the snapshot does not have, or a request that breaks the snapshot's schemas.
using System.Reflection;
using BillionSend.Tests.Support;

namespace BillionSend.Tests;

public sealed class CoverageTests : MockTest
{
    private static readonly object Send = new
    {
        from = "Acme <hello@example.com>",
        to = new[] { "person@example.com" },
        subject = "Hi",
        html = "<p>Hi</p>",
    };

    private static readonly EventStreamOptions NoReconnect = new() { Reconnect = false };

    public static readonly IReadOnlyDictionary<string, Func<BillionSendClient, Task<object?>>> Calls =
        new Dictionary<string, Func<BillionSendClient, Task<object?>>>
        {
            ["POST /agent/claim-email"] = c => J(c.Agent.ClaimEmailAsync()),
            ["POST /agent/signup"] = c => J(c.Agent.SignupAsync(Body("POST /agent/signup"))),
            ["GET /agent/status"] = c => J(c.Agent.StatusAsync()),
            ["GET /api-keys"] = c => J(c.ApiKeys.ListAsync()),
            ["POST /api-keys"] = c => J(c.ApiKeys.CreateAsync(Body("POST /api-keys"))),
            ["DELETE /api-keys/{id}"] = c => J(c.ApiKeys.DeleteAsync(Id)),
            ["POST /audiences/{audienceId}/contacts"] = c => J(c.Audiences.Contacts.CreateAsync(Id, Body("POST /audiences/{audienceId}/contacts"))),
            ["DELETE /audiences/{audienceId}/contacts/{id}"] = c => J(c.Audiences.Contacts.DeleteAsync(Id, Id2)),
            ["GET /audiences/{audienceId}/contacts/{id}"] = c => J(c.Audiences.Contacts.GetAsync(Id, Id2)),
            ["PATCH /audiences/{audienceId}/contacts/{id}"] = c => J(c.Audiences.Contacts.UpdateAsync(Id, Id2, null)),
            ["GET /broadcasts"] = c => J(c.Broadcasts.ListAsync()),
            ["POST /broadcasts"] = c => J(c.Broadcasts.CreateAsync(Body("POST /broadcasts"))),
            ["DELETE /broadcasts/{id}"] = c => J(c.Broadcasts.DeleteAsync(Id)),
            ["GET /broadcasts/{id}"] = c => J(c.Broadcasts.GetAsync(Id)),
            ["PATCH /broadcasts/{id}"] = c => J(c.Broadcasts.UpdateAsync(Id, null)),
            ["POST /broadcasts/{id}/cancel"] = c => J(c.Broadcasts.CancelAsync(Id)),
            ["POST /broadcasts/{id}/duplicate"] = c => J(c.Broadcasts.DuplicateAsync(Id)),
            ["POST /broadcasts/{id}/send"] = c => J(c.Broadcasts.SendAsync(Id, null)),
            ["GET /contact-properties"] = c => J(c.ContactProperties.ListAsync()),
            ["POST /contact-properties"] = c => J(c.ContactProperties.CreateAsync(Body("POST /contact-properties"))),
            ["DELETE /contact-properties/{id}"] = c => J(c.ContactProperties.DeleteAsync(Id)),
            ["GET /contact-properties/{id}"] = c => J(c.ContactProperties.GetAsync(Id)),
            ["PATCH /contact-properties/{id}"] = c => J(c.ContactProperties.UpdateAsync(Id, null)),
            ["GET /contacts"] = c => J(c.Contacts.ListAsync()),
            ["POST /contacts"] = c => J(c.Contacts.CreateAsync(Body("POST /contacts"))),
            ["POST /contacts/batch"] = c => J(c.Contacts.Batch.CreateAsync(Body("POST /contacts/batch"), Q(("on_conflict", "upsert")), new BatchRequestOptions { BatchValidation = BatchValidation.Strict })),
            ["POST /contacts/batch/get"] = c => J(c.Contacts.Batch.GetAsync(Body("POST /contacts/batch/get"))),
            ["POST /contacts/batch/remove"] = c => J(c.Contacts.Batch.RemoveAsync(Body("POST /contacts/batch/remove"))),
            ["DELETE /contacts/{id}"] = c => J(c.Contacts.DeleteAsync(Id, Q(("erase", "true")))),
            ["GET /contacts/{id}"] = c => J(c.Contacts.GetAsync(Id)),
            ["PATCH /contacts/{id}"] = c => J(c.Contacts.UpdateAsync(Id, null)),
            ["POST /contacts/{id}/preferences-link"] = c => J(c.Contacts.PreferencesLinkAsync(Id)),
            ["DELETE /contacts/{id}/segments/{segmentId}"] = c => J(c.Contacts.Segments.RemoveAsync(Id, Id2)),
            ["POST /contacts/{id}/segments/{segmentId}"] = c => J(c.Contacts.Segments.AddAsync(Id, Id2)),
            ["GET /contacts/{id}/topics"] = c => J(c.Contacts.Topics.ListAsync(Id)),
            ["PATCH /contacts/{id}/topics"] = c => J(c.Contacts.Topics.UpdateAsync(Id, Body("PATCH /contacts/{id}/topics"))),
            ["GET /deliverability"] = c => J(c.Deliverability.GetAsync()),
            ["GET /domains"] = c => J(c.Domains.ListAsync()),
            ["POST /domains"] = c => J(c.Domains.CreateAsync(Body("POST /domains"))),
            ["DELETE /domains/{id}"] = c => J(c.Domains.DeleteAsync(Id)),
            ["GET /domains/{id}"] = c => J(c.Domains.GetAsync(Id)),
            ["PATCH /domains/{id}"] = c => J(c.Domains.UpdateAsync(Id, null)),
            ["POST /domains/{id}/verify"] = c => J(c.Domains.VerifyAsync(Id)),
            ["GET /emails"] = c => J(c.Emails.ListAsync(Q(("limit", 10)))),
            ["POST /emails"] = c => J(c.Emails.SendAsync(Send, new IdempotentRequestOptions { IdempotencyKey = "welcome-1" })),
            ["POST /emails/batch"] = c => J(c.Emails.BatchAsync(new[] { Send, Send }, new BatchSendOptions { BatchValidation = BatchValidation.Permissive })),
            ["GET /emails/metrics"] = c => J(c.Emails.Metrics.GetAsync(Q(("metrics", new[] { "delivered", "opened" }), ("dimensions", new[] { "period" }), ("granularity", "daily")))),
            ["GET /emails/metrics/clients"] = c => J(c.Emails.Metrics.ClientsAsync()),
            ["GET /emails/metrics/countries"] = c => J(c.Emails.Metrics.CountriesAsync()),
            ["GET /emails/metrics/links"] = c => J(c.Emails.Metrics.LinksAsync(Q(("domain_id", new[] { Id, Id2 })))),
            ["GET /emails/receiving"] = c => J(c.Emails.Receiving.ListAsync(Q(("limit", 5), ("unread", "true")))),
            ["GET /emails/receiving/{email_id}/attachments"] = c => J(c.Emails.Receiving.Attachments.ListAsync(Id)),
            ["GET /emails/receiving/{email_id}/attachments/{id}"] = c => J(c.Emails.Receiving.Attachments.GetAsync(Id, Id2)),
            ["GET /emails/receiving/{email_id}/attachments/{id}/text"] = c => J(c.Emails.Receiving.Attachments.TextAsync(Id, Id2)),
            ["DELETE /emails/receiving/{id}"] = c => J(c.Emails.Receiving.DeleteAsync(Id)),
            ["GET /emails/receiving/{id}"] = c => J(c.Emails.Receiving.GetAsync(Id, Q(("html_format", "cid")))),
            ["POST /emails/receiving/{id}/forward"] = c => J(c.Emails.Receiving.ForwardAsync(Id, Body("POST /emails/receiving/{id}/forward"))),
            ["POST /emails/receiving/{id}/reply"] = c => J(c.Emails.Receiving.ReplyAsync(Id, new { text = "Thanks!" }, new IdempotentRequestOptions { IdempotencyKey = "r-1" })),
            ["DELETE /emails/{id}"] = c => J(c.Emails.DeleteAsync(Id)),
            ["GET /emails/{id}"] = c => J(c.Emails.GetAsync(Id)),
            ["PATCH /emails/{id}"] = c => J(c.Emails.UpdateAsync(Id, Body("PATCH /emails/{id}"))),
            ["POST /emails/{id}/cancel"] = c => J(c.Emails.CancelAsync(Id)),
            ["GET /emails/{id}/insights"] = c => J(c.Emails.InsightsAsync(Id)),
            ["GET /inboxes"] = c => J(c.Inboxes.ListAsync(Q(("kind", "agent"), ("limit", 5)))),
            ["POST /inboxes"] = c => J(c.Inboxes.CreateAsync(null)),
            ["GET /inboxes/events"] = c => S(c.Inboxes.Events.StreamAsync(Q(("types", "inbox.email.received")), NoReconnect)),
            ["DELETE /inboxes/{inbox_id}"] = c => J(c.Inboxes.DeleteAsync(Inbox)),
            ["GET /inboxes/{inbox_id}"] = c => J(c.Inboxes.GetAsync(Inbox)),
            ["PATCH /inboxes/{inbox_id}"] = c => J(c.Inboxes.UpdateAsync(Inbox, null)),
            ["GET /inboxes/{inbox_id}/agent"] = c => J(c.Inboxes.Agent.GetAsync(Inbox)),
            ["GET /inboxes/{inbox_id}/codes/latest"] = c => J(c.Inboxes.Codes.LatestAsync(Inbox, Q(("from", "example.com"), ("wait", 30)))),
            ["GET /inboxes/{inbox_id}/drafts"] = c => J(c.Inboxes.Drafts.ListAsync(Inbox, Q(("limit", 20)))),
            ["POST /inboxes/{inbox_id}/drafts"] = c => J(c.Inboxes.Drafts.CreateAsync(Inbox, null)),
            ["DELETE /inboxes/{inbox_id}/drafts/{draft_id}"] = c => J(c.Inboxes.Drafts.DeleteAsync(Inbox, Id)),
            ["GET /inboxes/{inbox_id}/drafts/{draft_id}"] = c => J(c.Inboxes.Drafts.GetAsync(Inbox, Id)),
            ["PATCH /inboxes/{inbox_id}/drafts/{draft_id}"] = c => J(c.Inboxes.Drafts.UpdateAsync(Inbox, Id, null)),
            ["POST /inboxes/{inbox_id}/drafts/{draft_id}/reject"] = c => J(c.Inboxes.Drafts.RejectAsync(Inbox, Id)),
            ["POST /inboxes/{inbox_id}/drafts/{draft_id}/send"] = c => J(c.Inboxes.Drafts.SendAsync(Inbox, Id, null, new IdempotentRequestOptions { IdempotencyKey = "d-1" })),
            ["POST /inboxes/{inbox_id}/emails"] = c => J(c.Inboxes.SendAsync(Inbox, Body("POST /inboxes/{inbox_id}/emails"))),
            ["GET /inboxes/{inbox_id}/events"] = c => S(c.Inboxes.Events.StreamInboxAsync(Inbox, null, NoReconnect)),
            ["GET /inboxes/{inbox_id}/labels"] = c => J(c.Inboxes.Labels.ListAsync(Inbox)),
            ["POST /inboxes/{inbox_id}/labels"] = c => J(c.Inboxes.Labels.CreateAsync(Inbox, Body("POST /inboxes/{inbox_id}/labels"))),
            ["DELETE /inboxes/{inbox_id}/labels/{label_id}"] = c => J(c.Inboxes.Labels.DeleteAsync(Inbox, Id)),
            ["PATCH /inboxes/{inbox_id}/labels/{label_id}"] = c => J(c.Inboxes.Labels.UpdateAsync(Inbox, Id, null)),
            ["GET /inboxes/{inbox_id}/quarantine"] = c => J(c.Inboxes.Quarantine.ListAsync(Inbox)),
            ["POST /inboxes/{inbox_id}/quarantine/{email_id}/release"] = c => J(c.Inboxes.Quarantine.ReleaseAsync(Inbox, Id, null)),
            ["GET /inboxes/{inbox_id}/safety"] = c => J(c.Inboxes.Safety.GetAsync(Inbox)),
            ["PATCH /inboxes/{inbox_id}/safety"] = c => J(c.Inboxes.Safety.UpdateAsync(Inbox, null)),
            ["GET /inboxes/{inbox_id}/threads"] = c => J(c.Inboxes.Threads.ListAsync(Inbox, Q(("read", false)))),
            ["GET /inboxes/{inbox_id}/threads/search"] = c => J(c.Inboxes.Threads.SearchAsync(Inbox, Q(("query", "invoice"), ("from", "billing@example.com"), ("has_attachment", true)))),
            ["DELETE /inboxes/{inbox_id}/threads/{thread_id}"] = c => J(c.Inboxes.Threads.DeleteAsync(Inbox, Id)),
            ["GET /inboxes/{inbox_id}/threads/{thread_id}"] = c => J(c.Inboxes.Threads.GetAsync(Inbox, Id)),
            ["PATCH /inboxes/{inbox_id}/threads/{thread_id}"] = c => J(c.Inboxes.Threads.UpdateAsync(Inbox, Id, null)),
            ["GET /inboxes/{inbox_id}/threads/{thread_id}/emails"] = c => J(c.Inboxes.Threads.Emails.ListAsync(Inbox, Id)),
            ["GET /inboxes/{inbox_id}/threads/{thread_id}/emails/{email_id}"] = c => J(c.Inboxes.Threads.Emails.GetAsync(Inbox, Id, Id2)),
            ["POST /inboxes/{inbox_id}/threads/{thread_id}/emails/{email_id}/forward"] = c => J(c.Inboxes.Threads.Emails.ForwardAsync(Inbox, Id, Id2, Body("POST /inboxes/{inbox_id}/threads/{thread_id}/emails/{email_id}/forward"))),
            ["POST /inboxes/{inbox_id}/threads/{thread_id}/emails/{email_id}/reply"] = c => J(c.Inboxes.Threads.Emails.ReplyAsync(Inbox, Id, Id2, new { text = "On it." })),
            ["POST /inboxes/{inbox_id}/threads/{thread_id}/labels"] = c => J(c.Inboxes.Threads.Labels.AddAsync(Inbox, Id, Body("POST /inboxes/{inbox_id}/threads/{thread_id}/labels"))),
            ["PUT /inboxes/{inbox_id}/threads/{thread_id}/labels"] = c => J(c.Inboxes.Threads.Labels.SetAsync(Inbox, Id, Body("PUT /inboxes/{inbox_id}/threads/{thread_id}/labels"))),
            ["DELETE /inboxes/{inbox_id}/threads/{thread_id}/labels/{label_id}"] = c => J(c.Inboxes.Threads.Labels.RemoveAsync(Inbox, Id, Id2)),
            ["GET /segments"] = c => J(c.Segments.ListAsync()),
            ["POST /segments"] = c => J(c.Segments.CreateAsync(Body("POST /segments"))),
            ["DELETE /segments/{id}"] = c => J(c.Segments.DeleteAsync(Id)),
            ["GET /segments/{id}"] = c => J(c.Segments.GetAsync(Id)),
            ["PATCH /segments/{id}"] = c => J(c.Segments.UpdateAsync(Id, null)),
            ["GET /segments/{id}/contacts"] = c => J(c.Segments.Contacts.ListAsync(Id)),
            ["GET /suppressions"] = c => J(c.Suppressions.ListAsync(Q(("origin", "bounce")))),
            ["POST /suppressions"] = c => J(c.Suppressions.CreateAsync(Body("POST /suppressions"))),
            ["POST /suppressions/batch/add"] = c => J(c.Suppressions.Batch.AddAsync(Body("POST /suppressions/batch/add"))),
            ["POST /suppressions/batch/remove"] = c => J(c.Suppressions.Batch.RemoveAsync(Body("POST /suppressions/batch/remove"))),
            ["DELETE /suppressions/{id}"] = c => J(c.Suppressions.DeleteAsync(Id)),
            ["GET /suppressions/{id}"] = c => J(c.Suppressions.GetAsync(Id)),
            ["GET /templates"] = c => J(c.Templates.ListAsync()),
            ["POST /templates"] = c => J(c.Templates.CreateAsync(Body("POST /templates"))),
            ["DELETE /templates/{id}"] = c => J(c.Templates.DeleteAsync(Id)),
            ["GET /templates/{id}"] = c => J(c.Templates.GetAsync(Id)),
            ["PATCH /templates/{id}"] = c => J(c.Templates.UpdateAsync(Id, null)),
            ["POST /templates/{id}/duplicate"] = c => J(c.Templates.DuplicateAsync(Id)),
            ["POST /templates/{id}/publish"] = c => J(c.Templates.PublishAsync(Id)),
            ["GET /topics"] = c => J(c.Topics.ListAsync()),
            ["POST /topics"] = c => J(c.Topics.CreateAsync(Body("POST /topics"))),
            ["DELETE /topics/{id}"] = c => J(c.Topics.DeleteAsync(Id)),
            ["GET /topics/{id}"] = c => J(c.Topics.GetAsync(Id)),
            ["PATCH /topics/{id}"] = c => J(c.Topics.UpdateAsync(Id, null)),
            ["GET /unsubscribes"] = c => J(c.Unsubscribes.ListAsync(Q(("domain_id", Id)))),
            ["POST /unsubscribes"] = c => J(c.Unsubscribes.CreateAsync(Body("POST /unsubscribes"))),
            ["GET /usage"] = c => J(c.Usage.GetAsync()),
            ["GET /verifications"] = c => J(c.Verifications.ListAsync()),
            ["POST /verifications"] = c => J(c.Verifications.CreateAsync(Body("POST /verifications"))),
            ["GET /verifications/availability"] = c => J(c.Verifications.AvailabilityAsync()),
            ["POST /verifications/estimate"] = c => J(c.Verifications.EstimateAsync(Body("POST /verifications/estimate"))),
            ["GET /verifications/{id}"] = c => J(c.Verifications.GetAsync(Id)),
            ["GET /automations"] = c => J(c.Automations.ListAsync(Q(("status", "enabled"), ("limit", 5)))),
            ["POST /automations"] = c => J(c.Automations.CreateAsync(Body("POST /automations"))),
            ["POST /automations/validate"] = c => J(c.Automations.ValidateAsync(Body("POST /automations/validate"))),
            ["DELETE /automations/{id}"] = c => J(c.Automations.RemoveAsync(Id)),
            ["GET /automations/{id}"] = c => J(c.Automations.GetAsync(Id)),
            ["PATCH /automations/{id}"] = c => J(c.Automations.UpdateAsync(Id, Body("PATCH /automations/{id}"))),
            ["POST /automations/{id}/duplicate"] = c => J(c.Automations.DuplicateAsync(Id)),
            ["GET /automations/{id}/metrics"] = c => J(c.Automations.MetricsAsync(Id, Q(("start_date", "2026-11-01"), ("end_date", "2026-11-18")))),
            ["GET /automations/{id}/runs"] = c => J(c.Automations.Runs.ListAsync(Id, Q(("status", "running")))),
            ["GET /automations/{id}/runs/{run_id}"] = c => J(c.Automations.Runs.GetAsync(Id, Id2)),
            ["POST /automations/{id}/runs/{run_id}/cancel"] = c => J(c.Automations.Runs.CancelAsync(Id, Id2)),
            ["POST /automations/{id}/simulate"] = c => J(c.Automations.SimulateAsync(Id, Body("POST /automations/{id}/simulate"))),
            ["POST /automations/{id}/stop"] = c => J(c.Automations.StopAsync(Id)),
            ["PATCH /api-keys/{id}"] = c => J(c.ApiKeys.UpdateAsync(Id, Body("PATCH /api-keys/{id}"))),
            ["GET /broadcasts/{id}/clicked-links"] = c => J(c.Broadcasts.ClickedLinksAsync(Id)),
            ["GET /broadcasts/{id}/recipients"] = c => J(c.Broadcasts.RecipientsAsync(Id, Q(("type", "clicked"), ("bounce_type", "permanent")))),
            ["GET /contacts/imports"] = c => J(c.Contacts.Imports.ListAsync(Q(("status", "completed")))),
            ["POST /contacts/imports"] = c => J(c.Contacts.Imports.CreateAsync(ImportForm())),
            ["GET /contacts/imports/{id}"] = c => J(c.Contacts.Imports.GetAsync(Id)),
            ["GET /contacts/{id}/segments"] = c => J(c.Contacts.Segments.ListAsync(Id)),
            ["POST /domains/claim"] = c => J(c.Domains.Claims.CreateAsync(Body("POST /domains/claim"))),
            ["GET /domains/{id}/claim"] = c => J(c.Domains.Claims.GetAsync(Id)),
            ["POST /domains/{id}/claim/verify"] = c => J(c.Domains.Claims.VerifyAsync(Id)),
            ["GET /emails/{email_id}/attachments"] = c => J(c.Emails.Attachments.ListAsync(Id)),
            ["GET /emails/{email_id}/attachments/{id}"] = c => J(c.Emails.Attachments.GetAsync(Id, Id2)),
            ["POST /emails/{id}/share"] = c => J(c.Emails.ShareAsync(Id, null)),
            ["DELETE /emails/{id}/share"] = c => J(c.Emails.UnshareAsync(Id)),
            ["GET /events"] = c => J(c.Events.ListAsync()),
            ["POST /events"] = c => J(c.Events.CreateAsync(Body("POST /events"))),
            ["POST /events/send"] = c => J(c.Events.SendAsync(Body("POST /events/send"), new IdempotentRequestOptions { IdempotencyKey = "ev-1" })),
            ["DELETE /events/{id_or_name}"] = c => J(c.Events.RemoveAsync("order.paid")),
            ["GET /events/{id_or_name}"] = c => J(c.Events.GetAsync("order.paid")),
            ["PATCH /events/{id_or_name}"] = c => J(c.Events.UpdateAsync("order.paid", Body("PATCH /events/{id_or_name}"))),
            ["PATCH /inboxes/{inbox_id}/agent"] = c => J(c.Inboxes.Agent.UpdateAsync(Inbox, Body("PATCH /inboxes/{inbox_id}/agent"))),
            ["GET /logs"] = c => J(c.Logs.ListAsync(Q(("limit", 5)))),
            ["GET /logs/{id}"] = c => J(c.Logs.GetAsync(Id)),
            ["GET /oauth/grants"] = c => J(c.OauthGrants.ListAsync()),
            ["DELETE /oauth/grants/{id}"] = c => J(c.OauthGrants.RevokeAsync(Id)),
            ["POST /webhooks/{id}/signing-secret/rotate"] = c => J(c.Webhooks.RotateSigningSecretAsync(Id, null)),
            ["GET /webhooks/{id}/events"] = c => J(c.Webhooks.Events.ListAsync(Id)),
            ["GET /webhooks/{id}/events/{event_id}"] = c => J(c.Webhooks.Events.GetAsync(Id, Id2)),
            ["POST /webhooks/{id}/events/{event_id}/replay"] = c => J(c.Webhooks.Events.ReplayAsync(Id, Id2)),
            ["GET /webhooks/{id}/events/{event_id}/attempts"] = c => J(c.Webhooks.Events.Attempts.ListAsync(Id, Id2)),
            ["GET /webhooks"] = c => J(c.Webhooks.ListAsync()),
            ["POST /webhooks"] = c => J(c.Webhooks.CreateAsync(Body("POST /webhooks"))),
            ["DELETE /webhooks/{id}"] = c => J(c.Webhooks.DeleteAsync(Id)),
            ["GET /webhooks/{id}"] = c => J(c.Webhooks.GetAsync(Id)),
            ["PATCH /webhooks/{id}"] = c => J(c.Webhooks.UpdateAsync(Id, null)),
        };

    public static TheoryData<string, bool> Keys()
    {
        var data = new TheoryData<string, bool>();
        foreach (var key in Calls.Keys.Order(StringComparer.Ordinal))
        {
            data.Add(key, false);
            data.Add(key, true);
        }

        return data;
    }

    [Fact]
    public void HasExactlyOneCallPerOperationOfTheSnapshot()
    {
        Assert.Equal(180, Spec.OperationKeys.Count);
        Assert.Empty(Spec.OperationKeys.Except(Calls.Keys).Order());
        Assert.Empty(Calls.Keys.Except(Spec.OperationKeys).Order());
    }

    [Fact]
    public void NamesEachMethodAfterItsOperationId()
    {
        var client = Client();
        Assert.NotNull(typeof(BillionSend.Resources.EmailsMetricsResource).GetMethod("GetAsync"));
        Assert.Null(typeof(BillionSend.Resources.EmailsResource).GetMethod("MetricsAsync"));
        var missing = Spec.Operations
            .Where(op => FindMethod(client, op.OperationId, "Async") is null)
            .Select(op => op.OperationId);
        Assert.Empty(missing);
    }

    [Fact]
    public void GivesEveryCursorPaginatedListAnIterator()
    {
        var client = Client();
        var lists = Spec.Operations.Where(op => op.Paginated).ToList();
        Assert.Equal(33, lists.Count);
        var missing = lists
            .Where(op => FindMethod(client, op.OperationId, "AllAsync") is not { } method ||
                method.ReturnType != typeof(IAsyncEnumerable<JsonNode>))
            .Select(op => op.OperationId);
        Assert.Empty(missing);
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public async Task ReachesTheOperationWithAContractValidRequest(string key, bool minimalAnswers)
    {
        Mock.Minimal = minimalAnswers;
        var result = await Calls[key](Client());
        var hit = Assert.Single(Mock.Hits);
        Assert.Equal(key, hit.Key);
        if (result is List<InboxStreamItem> events)
        {
            var item = Assert.IsType<InboxStreamEvent>(Assert.Single(events));
            Assert.Equal("inbox.email.received", item.Event);
            Assert.Equal("e", (string?)item.Payload?["email_id"]);
            return;
        }

        Assert.NotNull(hit.Response);
        Assert.True(JsonNode.DeepEquals(hit.Response, (JsonNode?)result), $"{key}: the answer differs from what the mock sent");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReachesEveryOperationOfTheSnapshotAndNothingElse(bool minimalAnswers)
    {
        Mock.Minimal = minimalAnswers;
        var client = Client();
        foreach (var (key, call) in Calls)
        {
            var before = Mock.Hits.Count;
            await call(client);
            Assert.Equal([key], Mock.Hits.Skip(before).Select(hit => hit.Key));
        }

        var reached = Mock.Hits.Select(hit => hit.Key).ToHashSet();
        Assert.Equal(Calls.Count, Mock.Hits.Count);
        Assert.Empty(Spec.OperationKeys.Except(reached).Order());
        Assert.Empty(reached.Except(Spec.OperationKeys).Order());
        Assert.Equal(180, reached.Count);
    }

    private static MethodInfo? FindMethod(BillionSendClient client, string operationId, string suffix)
    {
        object? target = client;
        var parts = operationId.Split('.');
        foreach (var part in parts[..^1])
        {
            target = target?.GetType().GetProperty(Pascal(part))?.GetValue(target);
        }

        var method = target?.GetType().GetMethod(Pascal(parts[^1]) + suffix);
        if (method is null && suffix == "Async" && target?.GetType().GetProperty(Pascal(parts[^1]))?.GetValue(target) is { } group)
        {
            // An operation that is also the group of others is that group's GetAsync (emails.metrics).
            method = group.GetType().GetMethod("GetAsync");
        }

        return method;
    }

    private static string Pascal(string name) => char.ToUpperInvariant(name[0]) + name[1..];

    // The CSV import is a multipart form: the file, a JSON-text column map and plain fields.
    private static Dictionary<string, object?> ImportForm() => new()
    {
        ["file"] = new FileUpload("people.csv", System.Text.Encoding.UTF8.GetBytes("Email,First Name\nana@example.com,Ana\n")),
        ["column_map"] = new { email = "Email", first_name = "First Name" },
        ["on_conflict"] = "skip",
        ["segments"] = new[] { new { id = Id } },
        ["topics"] = new[] { new { id = Id2, subscription = "opt_in" } },
    };

    private static Dictionary<string, object?> Q(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(pair => pair.Key, pair => pair.Value);

    private static async Task<object?> J(Task<JsonNode?> call) => await call;

    private static async Task<object?> S(Task<EventStream> call)
    {
        await using var stream = await call;
        var events = new List<InboxStreamItem>();
        await foreach (var item in stream)
        {
            events.Add(item);
        }

        return events;
    }
}
