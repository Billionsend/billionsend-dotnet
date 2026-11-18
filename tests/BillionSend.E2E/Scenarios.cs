using System.Text;
using BillionSend.Tests.Support;

namespace BillionSend.E2E;

/// <summary>
/// The live scenarios. They run in the company's own account, beside its real objects: every
/// object they create carries the run's tag in its name, is deleted by the scenario that made it
/// (or, failing that, by the clean-up at the end), and nothing that exists already is changed.
/// Mail goes only from the SDK E2E sender to the SES mailbox simulator's success address, and no
/// contact is ever unsubscribed or suppressed by a bounce or a complaint.
/// </summary>
public sealed class Scenarios
{
    public const string Sender = "SDK E2E <sdk-e2e@mail.billionsend.dev>";

    private readonly BillionSendClient _c;
    private readonly Func<Action<BillionSendOptions>?, BillionSendClient> _newClient;
    private readonly Recorder _r;
    private readonly string _contactEmail;
    private string? _segmentId;
    private string? _topicId;
    private string? _contactId;
    private Recorder.Created? _segmentHandle;
    private Recorder.Created? _topicHandle;
    private string? _eventName;
    private string? _webhookId;
    private bool _dry => !_r.Live;

    public Scenarios(BillionSendClient client, Func<Action<BillionSendOptions>?, BillionSendClient> newClient, Recorder recorder)
    {
        _c = client;
        _newClient = newClient;
        _r = recorder;
        Rand = Guid.NewGuid().ToString("N")[..8];
        Tag = $"sdk-e2e-dotnet-{Rand}";
        _contactEmail = Addr("c1");
    }

    public string Rand { get; }

    public string Tag { get; }

    /// <summary>Only the simulator's success mailbox, with a plus alias that names the run.</summary>
    public string Addr(string suffix) => $"success+{Tag}-{suffix}@simulator.amazonses.com";

    private static string Id(JsonNode? answer) =>
        (string?)answer?["id"] ?? throw new InvalidOperationException("the answer has no id");

    private static string Text(JsonNode? node, string name) => (string?)node?[name] ?? string.Empty;

    private object Mail(string subject, string recipient, object? more = null)
    {
        var body = new JsonObject
        {
            ["from"] = Sender,
            ["to"] = new JsonArray(recipient),
            ["subject"] = subject,
            ["html"] = $"<p>{Tag}</p>",
        };
        if (more is JsonObject extra)
        {
            foreach (var pair in extra.ToList())
            {
                extra.Remove(pair.Key);
                body[pair.Key] = pair.Value;
            }
        }

        return body;
    }

    private async Task<T> Refused<T>(Func<Task> call) where T : BillionSendException
    {
        try
        {
            await call();
        }
        catch (T error)
        {
            return error;
        }

        // Against the contract mock (which answers only successes) there is nothing to refuse.
        return _dry ? (T)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(T)) : throw new InvalidOperationException($"expected {typeof(T).Name}, the call succeeded");
    }

    private async Task<BillionSendException> AnyRefusal(Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (BillionSendException error)
        {
            return error;
        }

        return _dry
            ? (BillionSendException)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BillionSendException))
            : throw new InvalidOperationException("expected a refusal, the call succeeded");
    }

    private static string RandomId() => Guid.NewGuid().ToString();

    private static bool Listed(JsonNode? page, string id) =>
        page?["data"] is JsonArray data && data.Any(item => (string?)item?["id"] == id);

    public async Task RunAsync(Func<bool> budgetLeft)
    {
        // Each scenario is a group of steps; one that cannot start for want of budget is said so.
        var groups = new (string Name, Func<Task> Run)[]
        {
            ("reads", ReadOnlyCalls),
            ("emails", Emails),
            ("segments, topics, properties, contacts", Audience),
            ("events and suppressions", EventsAndSuppressions),
            ("templates and broadcasts", TemplatesAndBroadcasts),
            ("webhooks and API keys", WebhooksAndKeys),
            ("automations", Automations),
            ("contact import", Import),
            ("errors and logs", Errors),
        };
        foreach (var (name, run) in groups)
        {
            if (!budgetLeft())
            {
                _r.Skip(name, "request budget spent");
                continue;
            }

            await run();
        }
    }

    // ---------------------------------------------------------------- read-only calls on what exists

    private async Task ReadOnlyCalls()
    {
        await _r.Step("usage.get", async () =>
        {
            var usage = await _c.Usage.GetAsync();
            _r.Expect(Text(usage, "object") == "usage", "usage.object");
        });
        await _r.Step("deliverability.get", async () => await _c.Deliverability.GetAsync());
        string? domainId = null;
        await _r.Step("domains.list", async () =>
        {
            var page = await _c.Domains.ListAsync(new Dictionary<string, object?> { ["limit"] = 5 });
            _r.Expect(page?["data"] is JsonArray, "domains.list data");
            domainId = (string?)page?["data"]?[0]?["id"];
        });
        if (domainId is not null)
        {
            await _r.Step("domains.get (an existing domain, read only)", async () =>
            {
                var domain = await _c.Domains.GetAsync(domainId);
                _r.Expect(Id(domain) == domainId, "domains.get id");
            });
        }

        await _r.Step("contacts.list (2 of the account's contacts)", async () =>
        {
            var page = await _c.Contacts.ListAsync(new Dictionary<string, object?> { ["limit"] = 2 });
            _r.Expect(page?["data"] is JsonArray { Count: <= 2 }, "contacts.list honours limit");
        });
        await _r.Step("contacts.ListAllAsync stops where the caller stops (README: Pagination)", async () =>
        {
            var seen = new HashSet<string>();
            var items = 0;
            await foreach (var contact in _c.Contacts.ListAllAsync(new Dictionary<string, object?> { ["limit"] = 100 }))
            {
                _r.Expect(!string.IsNullOrEmpty((string?)contact["id"]), "contact id");
                seen.Add((string?)contact["id"] ?? string.Empty);
                if (++items >= 150)
                {
                    break; // the README loops over the whole list; the account has thousands of contacts
                }
            }

            _r.Expect(items == 150, $"ListAllAsync walked past the first page ({items} items)");
            _r.Expect(seen.Count == 150, $"the second page repeats nothing of the first ({seen.Count} distinct of {items})");
        });
        await _r.Step("broadcasts.list", async () => await _c.Broadcasts.ListAsync(new Dictionary<string, object?> { ["limit"] = 2 }));
        await _r.Step("oauthGrants.list", async () => await _c.OauthGrants.ListAsync(new Dictionary<string, object?> { ["limit"] = 2 }));
        await _r.Step("verifications.availability", async () => await _c.Verifications.AvailabilityAsync());
        await _r.Step("emails.list (2 of the account's emails)", async () =>
        {
            var page = await _c.Emails.ListAsync(new Dictionary<string, object?> { ["limit"] = 2 });
            _r.Expect(page?["data"] is JsonArray { Count: <= 2 }, "emails.list honours limit");
        });
        var week = new Dictionary<string, object?>
        {
            ["start_date"] = DateTime.UtcNow.AddDays(-7).ToString("yyyy-MM-dd"),
            ["end_date"] = DateTime.UtcNow.ToString("yyyy-MM-dd"),
        };
        await _r.Step("emails.metrics (a list of metrics as one comma-separated parameter)", async () =>
        {
            var query = new Dictionary<string, object?>(week) { ["metrics"] = new[] { "sent", "delivered" }, ["granularity"] = "daily" };
            await _c.Emails.Metrics.GetAsync(query);
        });
    }

    // ---------------------------------------------------------------- sending (at most seven emails)

    private async Task Emails()
    {
        string? emailId = null;
        await _r.Step("README quick start: emails.send to the simulator, then emails.get", async () =>
        {
            // The README's example, with this suite's sender and the simulator's address.
            var sent = await _c.Emails.SendAsync(new
            {
                from = Sender,
                to = new[] { Addr("mail") },
                subject = $"{Tag} quick start",
                html = "<p>Glad you are here.</p>",
            });
            emailId = Id(sent);
            var email = await _c.Emails.GetAsync(emailId);
            _r.Expect(Id(email) == emailId, "emails.get returns the email sent");
            _r.Expect(Text(email, "subject") == $"{Tag} quick start", "emails.get subject");
        });

        await _r.Step("idempotency: the same key and payload answers the first result, another payload is a 409", async () =>
        {
            var key = $"{Tag}/idem";
            var body = Mail($"{Tag} idempotent", Addr("idem"));
            var first = await _c.Emails.SendAsync(body, new IdempotentRequestOptions { IdempotencyKey = key });
            var again = await _c.Emails.SendAsync(body, new IdempotentRequestOptions { IdempotencyKey = key });
            _r.Expect(Id(first) == Id(again), "a repeat with the same key sends nothing new");
            var conflict = await Refused<ConflictException>(async () =>
                await _c.Emails.SendAsync(Mail($"{Tag} idempotent, other payload", Addr("idem")), new IdempotentRequestOptions { IdempotencyKey = key }));
            _r.Expect(conflict.StatusCode == 409, "409 for a key reused with another payload");
        });

        await _r.Step("emails.batch sends two emails", async () =>
        {
            var batch = await _c.Emails.BatchAsync(new[] { Mail($"{Tag} batch 1", Addr("b1")), Mail($"{Tag} batch 2", Addr("b2")) });
            _r.Expect(batch?["data"] is JsonArray { Count: 2 }, "emails.batch answers one item per email");
        });

        await _r.Step("a scheduled email: emails.update, emails.cancel, emails.get, emails.delete", async () =>
        {
            var at = DateTimeOffset.UtcNow.AddHours(3);
            var scheduled = await _c.Emails.SendAsync(Mail($"{Tag} scheduled", Addr("sched"), new JsonObject { ["scheduled_at"] = at.ToString("o") }));
            var id = Id(scheduled);
            await _c.Emails.UpdateAsync(id, new { scheduled_at = at.AddHours(1).ToString("o") });
            await _c.Emails.CancelAsync(id);
            var cancelled = await _c.Emails.GetAsync(id);
            _r.Expect(Id(cancelled) == id, "a cancelled email can still be read");
            await _c.Emails.DeleteAsync(id);
        });

        await _r.Step("an email with an attachment: emails.attachments.list and get", async () =>
        {
            var content = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Tag}\n"));
            var sent = await _c.Emails.SendAsync(Mail(
                $"{Tag} attachment",
                Addr("att"),
                new JsonObject { ["attachments"] = new JsonArray(new JsonObject { ["filename"] = "sdk-e2e.txt", ["content"] = content }) }));
            var id = Id(sent);
            var list = await _c.Emails.Attachments.ListAsync(id);
            var attachmentId = (string?)list?["data"]?[0]?["id"];
            _r.Expect(attachmentId is not null, "the sent email lists its attachment");
            if (attachmentId is not null)
            {
                var one = await _c.Emails.Attachments.GetAsync(id, attachmentId);
                _r.Expect(Id(one) == attachmentId, "emails.attachments.get returns the attachment");
            }
        });

        if (emailId is not null)
        {
            var shared = emailId;
            await _r.Step("emails.share and emails.unshare", async () =>
            {
                var link = await _c.Emails.ShareAsync(shared);
                _r.Expect(Text(link, "url").StartsWith("https://", StringComparison.Ordinal), "a share link");
                await _c.Emails.UnshareAsync(shared);
            });
        }

        await _r.Step("README retries: a client with its own MaxRetries and Timeout, a send with MaxRetries = 0", async () =>
        {
            using var client = _newClient(o =>
            {
                o.MaxRetries = 4;
                o.Timeout = TimeSpan.FromSeconds(30);
            });
            var sent = await client.Emails.SendAsync(Mail($"{Tag} retries", Addr("retry")), new IdempotentRequestOptions { MaxRetries = 0 });
            _r.Expect(!string.IsNullOrEmpty(Id(sent)), "the send is queued");
        });

        await _r.Step("emails.send refuses an invalid recipient as a ValidationException and sends nothing", async () =>
        {
            var error = await Refused<ValidationException>(async () =>
                await _c.Emails.SendAsync(new { from = Sender, to = "not an address", subject = Tag, text = "x" }));
            _r.Expect(error.StatusCode is 400 or 422, "a 4xx for an invalid recipient");
            _r.Expect(!string.IsNullOrEmpty(error.RequestId), "the refusal carries the request id");
        });
    }

    // ---------------------------------------------------------------- segments, topics, properties, contacts

    private async Task Audience()
    {
        await _r.Step("segments: create, get, update, list", async () =>
        {
            var made = await _c.Segments.CreateAsync(new { name = $"{Tag} segment" });
            var id = Id(made);
            _segmentId = id;
            _segmentHandle = _r.Track($"segment {id}", async () => await _c.Segments.DeleteAsync(id));
            var got = await _c.Segments.GetAsync(id);
            _r.Expect(Text(got, "name") == $"{Tag} segment", "segments.get name");
            await _c.Segments.UpdateAsync(id, new { name = $"{Tag} segment renamed" });
            var list = await _c.Segments.ListAsync(new Dictionary<string, object?> { ["limit"] = 100 });
            _r.Expect(Listed(list, id), "segments.list shows the new segment");
        });

        await _r.Step("topics: create, get, update, list", async () =>
        {
            var made = await _c.Topics.CreateAsync(new { name = $"{Tag} topic", default_subscription = "opt_in" });
            var id = Id(made);
            _topicId = id;
            _topicHandle = _r.Track($"topic {id}", async () => await _c.Topics.DeleteAsync(id));
            var got = await _c.Topics.GetAsync(id);
            _r.Expect(Text(got, "name") == $"{Tag} topic", "topics.get name");
            await _c.Topics.UpdateAsync(id, new { description = "sdk e2e" });
            var list = await _c.Topics.ListAsync();
            _r.Expect(Listed(list, id), "topics.list shows the new topic");
        });

        await _r.Step("contact properties: create, get, update, list, delete", async () =>
        {
            var key = $"sdk_e2e_{Rand}";
            var made = await _c.ContactProperties.CreateAsync(new { key, type = "string", fallback_value = "a" });
            var id = Id(made);
            var created = _r.Track($"contact property {id}", async () => await _c.ContactProperties.DeleteAsync(id));
            var got = await _c.ContactProperties.GetAsync(id);
            _r.Expect(Text(got, "key") == key, "contactProperties.get key");
            await _c.ContactProperties.UpdateAsync(id, new { fallback_value = "b" });
            var list = await _c.ContactProperties.ListAsync(new Dictionary<string, object?> { ["limit"] = 100 });
            _r.Expect(Listed(list, id), "contactProperties.list shows the new property");
            await _c.ContactProperties.DeleteAsync(id);
            created.Done = true;
        });

        await _r.Step("contacts: create, update, get", async () =>
        {
            var made = await _c.Contacts.CreateAsync(new { email = _contactEmail, first_name = "Sdk", last_name = "E2e" });
            var id = Id(made);
            _contactId = id;
            _r.Track($"contact {id}", async () => await _c.Contacts.DeleteAsync(id));
            await _c.Contacts.UpdateAsync(id, new { first_name = "Changed" });
            var got = await _c.Contacts.GetAsync(id);
            _r.Expect(Text(got, "first_name") == "Changed", "contacts.update is visible in contacts.get");
            _r.Expect(string.Equals(Text(got, "email"), _contactEmail, StringComparison.OrdinalIgnoreCase), "contacts.get email");
            _r.Expect(got?["unsubscribed"]?.GetValue<bool>() == false, "a new contact is subscribed");
        });


        if (_contactId is not null && _segmentId is not null)
        {
            var contact = _contactId;
            var seg = _segmentId;
            await _r.Step("contacts.segments: add, list, remove; segments.contacts.list", async () =>
            {
                await _c.Contacts.Segments.AddAsync(contact, seg);
                var mine = await _c.Contacts.Segments.ListAsync(contact);
                _r.Expect(Listed(mine, seg), "the contact lists its segment");
                var members = await _c.Segments.Contacts.ListAsync(seg);
                _r.Expect(Listed(members, contact), "the segment lists its contact");
                await _c.Contacts.Segments.RemoveAsync(contact, seg);
            });
        }

        if (_contactId is not null && _topicId is not null)
        {
            var contact = _contactId;
            var top = _topicId;
            await _r.Step("contacts.topics: update (opt_in only), list", async () =>
            {
                await _c.Contacts.Topics.UpdateAsync(contact, new[] { new { id = top, subscription = "opt_in" } });
                var topics = await _c.Contacts.Topics.ListAsync(contact);
                _r.Expect(topics?["data"] is JsonArray, "contacts.topics.list");
            });
        }

        if (_segmentId is not null)
        {
            var audience = _segmentId;
            await _r.Step("audiences.contacts (an audience is a segment): create, get, update, delete", async () =>
            {
                var made = await _c.Audiences.Contacts.CreateAsync(audience, new { email = Addr("aud") });
                var id = Id(made);
                _r.Track($"contact {id}", async () => await _c.Contacts.DeleteAsync(id));
                var got = await _c.Audiences.Contacts.GetAsync(audience, id);
                _r.Expect(Id(got) == id, "audiences.contacts.get");
                await _c.Audiences.Contacts.UpdateAsync(audience, id, new { first_name = "Aud" });
                await _c.Audiences.Contacts.DeleteAsync(audience, id);
                _r.CreatedObjects[^1].Done = true;
            });
        }

        await _r.Step("contacts.batch: create, get, remove", async () =>
        {
            var one = Addr("bc1");
            var two = Addr("bc2");
            var made = await _c.Contacts.Batch.CreateAsync(new[] { new { email = one }, new { email = two } });
            _r.Track("batch contacts", async () => await _c.Contacts.Batch.RemoveAsync(new { emails = new[] { one, two } }));
            _r.Expect(made is not null, "contacts.batch.create answers");
            var got = await _c.Contacts.Batch.GetAsync(new { contacts = new[] { new { email = one }, new { email = two } } });
            _r.Expect(got?["data"] is JsonArray { Count: 2 }, "contacts.batch.get returns both contacts");
            await _c.Contacts.Batch.RemoveAsync(new { emails = new[] { one, two } });
            _r.CreatedObjects[^1].Done = true;
        });

        if (_segmentId is not null)
        {
            var estimateSegment = _segmentId;
            await _r.Step("verifications.estimate (changes nothing) on the suite's own segment", async () =>
            {
                var estimate = await _c.Verifications.EstimateAsync(new { target = new { kind = "segment", segment_id = estimateSegment } });
                _r.Expect(estimate?["total"] is not null, "verifications.estimate total");
            });
        }

        // The contact, the topic and the segment stay until the end: later scenarios use them.
    }

    // ---------------------------------------------------------------- events and suppressions

    private async Task EventsAndSuppressions()
    {
        await _r.Step("events: create, get by name, update, list, send twice with one key, remove", async () =>
        {
            var name = $"{Tag}.created";
            var made = await _c.Events.CreateAsync(new { name, schema = new { amount = "number" } });
            var created = _r.Track($"event {name}", async () => await _c.Events.RemoveAsync(name));
            _r.Expect(!string.IsNullOrEmpty(Id(made)) || made is not null, "events.create answers");
            var got = await _c.Events.GetAsync(name);
            _r.Expect(Text(got, "name") == name, "events.get by name");
            await _c.Events.UpdateAsync(name, new { schema = new { amount = "number", plan = "string" } });
            var list = await _c.Events.ListAsync(new Dictionary<string, object?> { ["limit"] = 100 });
            _r.Expect(list?["data"] is JsonArray data && data.Any(e => (string?)e?["name"] == name), "events.list shows the new event");
            if (_contactId is { } contact)
            {
                var key = $"{Tag}/event";
                var first = await _c.Events.SendAsync(new { @event = name, contact_id = contact, payload = new { amount = 5 } }, new IdempotentRequestOptions { IdempotencyKey = key });
                var again = await _c.Events.SendAsync(new { @event = name, contact_id = contact, payload = new { amount = 5 } }, new IdempotentRequestOptions { IdempotencyKey = key });
                _r.Expect(JsonNode.DeepEquals(first, again), "events.send with one key answers the same twice");
            }

            await _c.Events.RemoveAsync(name);
            created.Done = true;
            _eventName = name;
        });

        await _r.Step("suppressions: create (manual), get, list, delete; batch add and remove", async () =>
        {
            var single = Addr("sup1");
            var made = await _c.Suppressions.CreateAsync(new { email = single, origin = "manual" });
            var id = Id(made);
            var created = _r.Track($"suppression {id}", async () => await _c.Suppressions.DeleteAsync(id));
            var got = await _c.Suppressions.GetAsync(id);
            _r.Expect(string.Equals(Text(got, "email"), single, StringComparison.OrdinalIgnoreCase), "suppressions.get email");
            var list = await _c.Suppressions.ListAsync(new Dictionary<string, object?> { ["limit"] = 100 });
            _r.Expect(Listed(list, id), "suppressions.list shows the new entry");
            await _c.Suppressions.DeleteAsync(id);
            created.Done = true;

            var one = Addr("sup2");
            var two = Addr("sup3");
            await _c.Suppressions.Batch.AddAsync(new { emails = new[] { one, two }, origin = "manual" });
            var batch = _r.Track("batch suppressions", async () => await _c.Suppressions.Batch.RemoveAsync(new { emails = new[] { one, two } }));
            await _c.Suppressions.Batch.RemoveAsync(new { emails = new[] { one, two } });
            batch.Done = true;
        });
    }

    // ---------------------------------------------------------------- templates and broadcasts (drafts only)

    private async Task TemplatesAndBroadcasts()
    {
        string? templateId = null;
        await _r.Step("templates: create, get, update, publish, duplicate, list, delete both", async () =>
        {
            var made = await _c.Templates.CreateAsync(new { name = $"{Tag} template", html = "<p>Hello {{{first_name}}}</p>", subject = "Hello" });
            templateId = Id(made);
            var original = templateId;
            var created = _r.Track($"template {original}", async () => await _c.Templates.DeleteAsync(original));
            var got = await _c.Templates.GetAsync(original);
            _r.Expect(Text(got, "name") == $"{Tag} template", "templates.get name");
            await _c.Templates.UpdateAsync(original, new { subject = "Hello again" });
            await _c.Templates.PublishAsync(original);
            var copy = await _c.Templates.DuplicateAsync(original);
            var copyId = Id(copy);
            var copyHandle = _r.Track($"template {copyId}", async () => await _c.Templates.DeleteAsync(copyId));
            var list = await _c.Templates.ListAsync(new Dictionary<string, object?> { ["limit"] = 100 });
            _r.Expect(Listed(list, original) && Listed(list, copyId), "templates.list shows both");
            await _c.Templates.DeleteAsync(copyId);
            copyHandle.Done = true;
            await _c.Templates.DeleteAsync(original);
            created.Done = true;
        });

        if (_segmentId is not null)
        {
            await _r.Step("broadcasts (drafts, never sent): create, get, update, duplicate, list, recipients, clickedLinks, cancel, delete", async () =>
            {
                var segmentId = _segmentId;
                var made = await _c.Broadcasts.CreateAsync(new { name = $"{Tag} broadcast", segment_id = segmentId, from = Sender, subject = $"{Tag} draft", html = "<p>draft</p>" });
                var id = Id(made);
                var created = _r.Track($"broadcast {id}", async () => await _c.Broadcasts.DeleteAsync(id));
                var got = await _c.Broadcasts.GetAsync(id);
                _r.Expect(Id(got) == id, "broadcasts.get");
                await _c.Broadcasts.UpdateAsync(id, new { subject = $"{Tag} draft, edited" });
                var copy = await _c.Broadcasts.DuplicateAsync(id);
                var copyId = Id(copy);
                var copyHandle = _r.Track($"broadcast {copyId}", async () => await _c.Broadcasts.DeleteAsync(copyId));
                var list = await _c.Broadcasts.ListAsync(new Dictionary<string, object?> { ["limit"] = 100 });
                _r.Expect(Listed(list, id), "broadcasts.list shows the draft");
                var recipients = await _c.Broadcasts.RecipientsAsync(id, new Dictionary<string, object?> { ["type"] = "sent" });
                _r.Expect(recipients?["data"] is JsonArray, "broadcasts.recipients");
                await _c.Broadcasts.ClickedLinksAsync(id);
                var refused = await AnyRefusal(async () => await _c.Broadcasts.CancelAsync(id));
                _r.Expect(refused.StatusCode is >= 400 and < 500, "a draft cannot be cancelled: a 4xx");
                await _c.Broadcasts.DeleteAsync(copyId);
                copyHandle.Done = true;
                await _c.Broadcasts.DeleteAsync(id);
                created.Done = true;
            });
        }
    }

    // ---------------------------------------------------------------- webhooks and API keys

    private async Task WebhooksAndKeys()
    {
        await _r.Step("webhooks: create, get, update, list, a delivery signed with its secret, events, delete", async () =>
        {
            // An event that almost never fires, and an endpoint that is nobody's.
            var made = await _c.Webhooks.CreateAsync(new { endpoint = "https://example.com/sdk-e2e", events = new[] { "inbox.expired" } });
            var id = Id(made);
            _webhookId = id;
            var created = _r.Track($"webhook {id}", async () => await _c.Webhooks.DeleteAsync(id));
            var secret = Text(made, "signing_secret");
            _r.Expect(secret.StartsWith("whsec_", StringComparison.Ordinal), "the signing secret is a whsec_ secret");
            await _c.Webhooks.GetAsync(id);
            await _c.Webhooks.UpdateAsync(id, new { events = new[] { "inbox.expired", "inbox.deleted" } });
            var list = await _c.Webhooks.ListAsync(new Dictionary<string, object?> { ["limit"] = 100 });
            _r.Expect(Listed(list, id), "webhooks.list shows the new webhook");

            // The secret the API handed out verifies a delivery signed the way the API signs one.
            if (_r.Live)
            {
                VerifyDelivery(secret);
            }

            // Rotating a signing secret is left to the offline tests: the suite changes no secret.
            var events = await _c.Webhooks.Events.ListAsync(id);
            _r.Expect(events?["data"] is JsonArray, "webhooks.events.list");
            var missing = await Refused<NotFoundException>(async () => await _c.Webhooks.Events.GetAsync(id, RandomId()));
            _r.Expect(missing.StatusCode == 404, "webhooks.events.get of an unknown event is a 404");
            await _c.Webhooks.DeleteAsync(id);
            created.Done = true;
        });

        await _r.Step("API keys: create a sending-only key, list, rename, refuse it a contacts call (403), delete", async () =>
        {
            var made = await _c.ApiKeys.CreateAsync(new { name = $"{Tag} key", permission = "sending_access" });
            var id = Id(made);
            var token = Text(made, "token");
            var created = _r.Track($"API key {id}", async () => await _c.ApiKeys.DeleteAsync(id));
            _r.Expect(token.StartsWith("bs_", StringComparison.Ordinal), "a bs_ key");
            var list = await _c.ApiKeys.ListAsync(new Dictionary<string, object?> { ["limit"] = 100 });
            _r.Expect(Listed(list, id), "apiKeys.list shows the new key");
            await _c.ApiKeys.UpdateAsync(id, new { name = $"{Tag} key renamed" });
            if (_r.Live)
            {
                using var restricted = _newClient(o => o.ApiKey = token);
                var denied = await Refused<PermissionDeniedException>(async () => await restricted.Contacts.ListAsync());
                _r.Expect(denied.StatusCode == 403, "a sending-only key may not read contacts");
            }

            await _c.ApiKeys.DeleteAsync(id);
            created.Done = true;
        });
    }

    private void VerifyDelivery(string secret)
    {
        var key = Convert.FromBase64String(secret["whsec_".Length..]);
        var body = $"{{\"type\":\"inbox.expired\",\"created_at\":\"{DateTime.UtcNow:o}\",\"data\":{{}}}}";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = "v1," + Convert.ToBase64String(System.Security.Cryptography.HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"msg_e2e.{timestamp}.{body}")));
        var headers = new Dictionary<string, string> { ["webhook-id"] = "msg_e2e", ["webhook-timestamp"] = timestamp, ["webhook-signature"] = signature };
        var evt = Webhook.Verify(body, headers, secret);
        _r.Expect((string?)evt["type"] == "inbox.expired", "Webhook.Verify accepts a delivery signed with the API's secret");
        Assert.Throws<WebhookVerificationException>(() => Webhook.Verify(body + " ", headers, secret));
    }

    // ---------------------------------------------------------------- automations (disabled, never started)

    private async Task Automations()
    {
        var trigger = _eventName ?? $"{Tag}.created";
        JsonObject Graph(string name) => new()
        {
            ["name"] = name,
            ["status"] = "disabled",
            ["steps"] = new JsonArray(
                new JsonObject { ["key"] = "start", ["type"] = "trigger", ["config"] = new JsonObject { ["event_name"] = trigger } },
                new JsonObject { ["key"] = "wait", ["type"] = "delay", ["config"] = new JsonObject { ["duration"] = "1 hour" } }),
            ["connections"] = new JsonArray(new JsonObject { ["from"] = "start", ["to"] = "wait" }),
        };

        await _r.Step("automations: validate, create (disabled), get, update, duplicate, simulate, stop, runs, metrics, list, remove", async () =>
        {
            var document = Graph("x");
            var verdict = await _c.Automations.ValidateAsync(new { steps = document["steps"], connections = document["connections"] });
            _r.Expect(verdict?["valid"]?.GetValue<bool>() == true, "a trigger and a delay validate");
            var made = await _c.Automations.CreateAsync(Graph($"{Tag} automation"));
            var id = Id(made);
            var created = _r.Track($"automation {id}", async () => await _c.Automations.RemoveAsync(id));
            var got = await _c.Automations.GetAsync(id);
            _r.Expect(Text(got, "status") == "disabled", "a new automation is disabled");
            await _c.Automations.UpdateAsync(id, new { name = $"{Tag} automation renamed" });
            var copy = await _c.Automations.DuplicateAsync(id);
            var copyId = Id(copy);
            var copyHandle = _r.Track($"automation {copyId}", async () => await _c.Automations.RemoveAsync(copyId));
            var simulation = await _c.Automations.SimulateAsync(id, new { contact = new { email = Addr("sim") }, payload = new { }, events = Array.Empty<object>() });
            _r.Expect(simulation is not null, "automations.simulate answers");
            await _c.Automations.StopAsync(id);
            var runs = await _c.Automations.Runs.ListAsync(id);
            _r.Expect(runs?["data"] is JsonArray { Count: 0 }, "a disabled automation has no runs");
            var noRun = await Refused<NotFoundException>(async () => await _c.Automations.Runs.GetAsync(id, RandomId()));
            _r.Expect(noRun.StatusCode == 404, "automations.runs.get of an unknown run is a 404");
            var noCancel = await AnyRefusal(async () => await _c.Automations.Runs.CancelAsync(id, RandomId()));
            _r.Expect(noCancel.StatusCode is 404 or 409 or 422, "automations.runs.cancel of an unknown run is refused");
            await _c.Automations.MetricsAsync(id);
            var list = await _c.Automations.ListAsync(new Dictionary<string, object?> { ["limit"] = 100 });
            _r.Expect(Listed(list, id) && Listed(list, copyId), "automations.list shows both");
            await _c.Automations.RemoveAsync(copyId);
            copyHandle.Done = true;
            await _c.Automations.RemoveAsync(id);
            created.Done = true;
        });
    }

    // ---------------------------------------------------------------- contact import (README: Import contacts from a CSV)

    private async Task Import()
    {
        var one = Addr("imp1");
        var path = Path.Combine(Path.GetTempPath(), $"{Tag}-联系人.csv"); // a non-ASCII file name goes on the wire as it is
        await File.WriteAllTextAsync(path, $"Email,First Name\n{one},Imported\n");
        try
        {
            await _r.Step("README import: FileUpload.FromFileAsync, contacts.imports.create, get, list", async () =>
            {
                var file = await FileUpload.FromFileAsync(path);
                var queued = await _c.Contacts.Imports.CreateAsync(new Dictionary<string, object?>
                {
                    ["file"] = file,
                    ["column_map"] = new { email = "Email", first_name = "First Name" },
                    ["on_conflict"] = "skip",
                });
                var id = Id(queued);
                _r.Track("imported contact", async () => await _c.Contacts.Batch.RemoveAsync(new { emails = new[] { one } }));
                JsonNode? import = null;
                for (var poll = 0; poll < 4; poll++)
                {
                    import = await _c.Contacts.Imports.GetAsync(id);
                    if (Text(import, "status") is "completed" or "failed")
                    {
                        break;
                    }
                }

                _r.Expect(Text(import, "status") == "completed", $"the import completed (status {Text(import, "status")})");
                var list = await _c.Contacts.Imports.ListAsync(new Dictionary<string, object?> { ["limit"] = 5 });
                _r.Expect(Listed(list, id), "contacts.imports.list shows the import");
                await _c.Contacts.Batch.RemoveAsync(new { emails = new[] { one } });
                _r.CreatedObjects[^1].Done = true;
            });
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---------------------------------------------------------------- errors, timeouts, logs

    private async Task Errors()
    {
        string? requestId = null;
        await _r.Step("README errors: an unknown email is a NotFoundException with a request id", async () =>
        {
            try
            {
                await _c.Emails.GetAsync(RandomId());
                _r.Expect(false, "expected a 404");
            }
            catch (NotFoundException error)
            {
                requestId = error.RequestId;
                _r.Expect(error.StatusCode == 404 && error.Name == "not_found", $"404 not_found, got {error.StatusCode} {error.Name}");
                _r.Expect(!string.IsNullOrEmpty(error.RequestId), "the 404 carries the request id");
            }
        });

        await _r.Step("a key that does not exist is an AuthenticationException (401)", async () =>
        {
            using var stranger = _newClient(o =>
            {
                o.ApiKey = $"bs_{Guid.NewGuid():N}{Guid.NewGuid():N}";
                o.MaxRetries = 0;
            });
            var error = await Refused<AuthenticationException>(async () => await stranger.Usage.GetAsync());
            _r.Expect(error.StatusCode == 401, "401 for an unknown key");
        });

        await _r.Step("a timeout is an ApiTimeoutException", async () =>
        {
            // Wait out the suite's own pause between requests first, so the timeout cuts the request itself.
            await Task.Delay(1300);
            var error = await Refused<ApiTimeoutException>(async () =>
                await _c.Usage.GetAsync(new RequestOptions { Timeout = TimeSpan.FromMilliseconds(1), MaxRetries = 0 }));
            _r.Expect(error.StatusCode is null, "a timeout has no status");
        });

        if (requestId is not null)
        {
            var wanted = requestId;
            await _r.Step("logs.get: the request id of an error is the id of its log row, and it records the SDK's user agent", async () =>
            {
                JsonNode? log = null;
                for (var attempt = 0; attempt < 3 && log is null; attempt++)
                {
                    try
                    {
                        log = await _c.Logs.GetAsync(wanted);
                    }
                    catch (NotFoundException) when (attempt < 2)
                    {
                        // The row is written just after the answer goes out.
                    }
                }

                _r.Expect(Id(log) == wanted, "logs.get returns the row the request id names");
                _r.Expect(Text(log, "user_agent") == $"billionsend-dotnet/{BillionSendClient.SdkVersion}", "the log row records the SDK's user agent");
                _r.Expect(Text(log, "method") == "GET", "the log row's method");
            });
        }
    }

    // ---------------------------------------------------------------- the end

    /// <summary>Deletes the contact the later scenarios used (its own delete is a step), then whatever is left.</summary>
    public async Task FinishAsync()
    {
        if (_contactId is { } contact)
        {
            await _r.Step("contacts.delete", async () =>
            {
                await _c.Contacts.DeleteAsync(contact);
                _r.CreatedObjects.First(c => c.Description == $"contact {contact}").Done = true;
            });
        }

        if (_topicHandle is { } topic)
        {
            await _r.Step("topics.delete", async () =>
            {
                await topic.Undo();
                topic.Done = true;
            });
        }

        if (_segmentHandle is { } segment)
        {
            await _r.Step("segments.delete", async () =>
            {
                await segment.Undo();
                segment.Done = true;
            });
        }
    }

    /// <summary>After the clean-up: nothing this run created is still listed, and the contact is gone.</summary>
    public async Task VerifyGoneAsync()
    {
        var all = new Dictionary<string, object?> { ["limit"] = 100 };
        var lists = new (string Name, Func<Task<JsonNode?>> Read)[]
        {
            ("segments", () => _c.Segments.ListAsync(all)),
            ("topics", () => _c.Topics.ListAsync()),
            ("templates", () => _c.Templates.ListAsync(all)),
            ("broadcasts", () => _c.Broadcasts.ListAsync(all)),
            ("webhooks", () => _c.Webhooks.ListAsync(all)),
            ("API keys", () => _c.ApiKeys.ListAsync(all)),
            ("automations", () => _c.Automations.ListAsync(all)),
        };
        foreach (var (name, read) in lists)
        {
            await _r.Step($"nothing of this run is left in {name}", async () =>
            {
                var text = (await read())?.ToJsonString() ?? string.Empty;
                _r.Expect(!text.Contains(Tag, StringComparison.Ordinal) && (_webhookId is null || !text.Contains(_webhookId, StringComparison.Ordinal)), $"{name} still lists something of this run");
            });
        }

        if (_contactId is { } contact)
        {
            await _r.Step("the contact is gone (404)", async () =>
            {
                var gone = await Refused<NotFoundException>(async () => await _c.Contacts.GetAsync(contact));
                _r.Expect(gone.StatusCode == 404, "contacts.get of the deleted contact");
            });
        }

        await _r.Step("none of the run's other contacts is left (batch get by email)", async () =>
        {
            var emails = new[] { Addr("bc1"), Addr("bc2"), Addr("aud"), Addr("imp1"), _contactEmail };
            var left = await _c.Contacts.Batch.GetAsync(new { contacts = emails.Select(email => new { email }).ToArray() });
            _r.Expect(left?["data"] is JsonArray { Count: 0 }, "contacts.batch.get still finds a contact of this run");
        });
    }
}
