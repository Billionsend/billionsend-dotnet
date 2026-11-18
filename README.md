# BillionSend .NET SDK

The official .NET client for the [BillionSend](https://billionsend.com) email API: send email, run inboxes for people and agents, stream inbox events, verify webhooks, and manage domains, contacts and broadcasts.

- Every API operation, one async method each, named after the BillionSend OpenAPI document.
- Answers as `System.Text.Json` trees (`JsonNode`); failures as exceptions you can tell apart by type.
- Safe sends: every send carries an `Idempotency-Key`, so retries after a timeout never send twice.
- `IAsyncEnumerable` for every cursor list and for live inbox events, with automatic resume.
- No dependencies beyond the .NET runtime. Targets .NET 8 and later.

Documentation: [billionsend.com/docs](https://billionsend.com/docs) · Source: [github.com/billionsend/billionsend-dotnet](https://github.com/billionsend/billionsend-dotnet)

## Install

```bash
dotnet add package BillionSend
```

## Quick start

```csharp
using BillionSend;

using var client = new BillionSendClient(); // reads BILLIONSEND_API_KEY

var sent = await client.Emails.SendAsync(new
{
    from = "Acme <hello@acme.dev>",
    to = new[] { "ana@example.com" },
    subject = "Welcome to Acme",
    html = "<p>Glad you are here.</p>",
});

Console.WriteLine($"queued {sent?["id"]}");
```

One client is safe to share across threads; create it once (or register it as a singleton) and keep it for the life of the application.

## Authentication

Create an API key on the dashboard (it starts with `bs_`) and pass it to the client, or set `BILLIONSEND_API_KEY`:

```csharp
var client = new BillionSendClient("bs_...");
// or: new BillionSendClient() with BILLIONSEND_API_KEY set
```

A key restricted to one inbox is what an agent should hold; see [API keys](https://billionsend.com/docs/api-keys). Calling an operation without any key configured throws an `AuthenticationException` named `missing_api_key` before anything is sent.

**Agent sign-up.** An agent can create its own account with no API key; the answer carries the new key and inbox:

```csharp
using var anonymous = new BillionSendClient();
var signup = await anonymous.Agent.SignupAsync(new
{
    human_email = "owner@acme.dev",
    name = "Support agent",
});

using var agent = new BillionSendClient((string)signup!["api_key"]!);
Console.WriteLine(signup["inbox"]?["email_address"]);
```

## Requests and answers

Methods follow the API's `operationId`s: `emails.receiving.attachments.list` is `client.Emails.Receiving.Attachments.ListAsync(...)`. One operation is also the group of others: `emails.metrics` is `client.Emails.Metrics.GetAsync(...)`, beside `emails.metrics.links` / `countries` / `clients` (`LinksAsync`, `CountriesAsync`, `ClientsAsync`). Arguments come in a fixed order: the path parameters (`emailId`, `inboxId`, …), the request body, the query parameters, the call options and a `CancellationToken`.

- **Bodies** are any object `System.Text.Json` can serialize: an anonymous object, a dictionary, a `JsonNode`, or your own type. Your own types' property names are written in snake_case (`ReplyTo` becomes `reply_to`), the way the API names its fields; anonymous objects written in snake_case, dictionary keys, `JsonNode` objects and `[JsonPropertyName]` names are sent as they are. Set `BillionSendOptions.JsonSerializerOptions` to change this.
- **Query parameters** are a dictionary. Lists (`metrics`, `domain_id`, …) go comma-separated in one parameter, booleans as `true` / `false`, `null` values are left out.
- **Answers** are the parsed JSON body as a `JsonNode`, with the API's own field names: read them with the indexer and a cast, or deserialize them into your own types with `answer.Deserialize<T>()`.

```csharp
var page = await client.Emails.Receiving.ListAsync(new Dictionary<string, object?>
{
    ["limit"] = 20,
    ["inbox_id"] = "6a1f1d38-3c55-4a4e-9a57-0f2b3c4d5e6f", // an inbox's id (not its address); unread needs one, or a key restricted to one
    ["unread"] = "true",
});

foreach (var email in page!["data"]!.AsArray())
{
    Console.WriteLine($"{email!["from"]}: {email["subject"]}");
}
```

## Import contacts from a CSV

`Contacts.Imports.CreateAsync` uploads a file as `multipart/form-data`. The body is a dictionary of form fields: a `FileUpload` becomes the file part, a string is sent as it is, and an object or a list (the column map, the segments, the topics) is sent as its JSON text, which is how the API takes them. The import runs in the background; poll `Contacts.Imports.GetAsync` for its `status` and `counts`:

```csharp
var file = await FileUpload.FromFileAsync("people.csv");
var queued = await client.Contacts.Imports.CreateAsync(new Dictionary<string, object?>
{
    ["file"] = file,
    ["column_map"] = new { email = "Email", first_name = "First Name" },
    ["on_conflict"] = "skip",
});

var import = await client.Contacts.Imports.GetAsync((string)queued!["id"]!);
Console.WriteLine($"{import!["status"]}");
```

## Send once: idempotency

The sending operations (`Emails.SendAsync`, `Emails.BatchAsync`, `Emails.Receiving.ReplyAsync` / `ForwardAsync`, `Inboxes.SendAsync`, `Inboxes.Drafts.SendAsync`, `Inboxes.Threads.Emails.ReplyAsync` / `ForwardAsync`, `Events.SendAsync`) always send an `Idempotency-Key` header. Without your own, each call gets a new random key (a UUID) and that call's retries reuse it, so a retry after a timeout cannot send twice. Pass your own key to make a send safe across calls and processes too; a repeat with the same key and payload returns the first result:

```csharp
await client.Emails.SendAsync(message, new IdempotentRequestOptions
{
    IdempotencyKey = $"welcome/{user.Id}",
});
```

## An agent's inbox: wait, read, reply

An agent holds a key restricted to its inbox, waits for new mail on the live event stream, reads it, and replies in the thread:

```csharp
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
```

Waiting for a sign-in code sent to the inbox:

```csharp
var code = await client.Inboxes.Codes.LatestAsync("support@acme.dev", new Dictionary<string, object?>
{
    ["from"] = "example.com",
    ["wait"] = 30,
});
```

## Live events

`client.Inboxes.Events.StreamAsync()` streams every inbox event of the team (it needs a team-wide key); `client.Inboxes.Events.StreamInboxAsync(inbox)` streams one inbox. Both return once the stream is open (or throw the error that refused it), and give an `EventStream` to read with `await foreach`:

- `InboxStreamEvent`: `Id`, `Event` and `Data`, where `Event` is the webhook event name (`inbox.email.received`, `inbox.thread.created`, `inbox.draft.sent`, `email.received`, …) and `Data` is `{ type, created_at, inbox_id, data }`, the webhook payload's metadata; `Payload` is its inner `data` with the ids to read next (`thread_id`, `email_id`, `draft_id`).
- `StreamGapEvent`: `Event` is `stream.gap`, `Id` is `null` and `Reason` says why: the stream resumed from further back than the server keeps (24 hours), so events may be missing and the lists should be read again. One stream hands it out once per position.

When the connection drops (the server also closes every stream about every ten minutes), the stream reconnects with `Last-Event-ID` and replays what it missed before the live events. A connection that sends nothing at all for `IdleTimeout` (default 45 seconds; the server writes a keepalive every 15) is taken for dead, as when a NAT or proxy drops it without a reset, and is reconnected the same way; `TimeSpan.Zero` turns this off. It gives up after `MaxReconnects` failures in a row (default 5) with an `ApiConnectionException`; a refusal a retry cannot fix (a revoked key) is thrown as its own type. One inbox's stream ends after that inbox's `inbox.deleted` or `inbox.expired`.

```csharp
await using var stream = await client.Inboxes.Events.StreamAsync(
    new Dictionary<string, object?> { ["types"] = new[] { "inbox.email.received", "inbox.thread.created" } },
    new EventStreamOptions { LastEventId = saved }); // resume where a previous run stopped

await foreach (var item in stream.WithCancellation(stopping))
{
    saved = stream.LastEventId;
    // break, stream.Close() or disposing the stream ends it
}
```

Leaving the loop, `Close()` or disposing the stream ends it quietly; cancelling the token throws `OperationCanceledException`.

## Pagination

List methods take the API's cursor parameters (`limit`, `after`, `before`) and return one page. Every cursor list also has an iterator that fetches pages as you go:

```csharp
await foreach (var contact in client.Contacts.ListAllAsync(new Dictionary<string, object?> { ["limit"] = 100 }))
{
    Console.WriteLine(contact["email"]);
}
```

Thread search has `SearchAllAsync`. Starting with `before` walks backward. `Pagination.IterateAsync(fetchPage)` builds the same iterator over any page fetcher.

## Errors

A refusal or a failure is thrown as a `BillionSendException` subclass:

| Type | When |
| --- | --- |
| `ValidationException` | `400`, `422`: the request is invalid |
| `AuthenticationException` | `401`, or no API key configured (`Name` is `missing_api_key`, nothing is sent) |
| `PermissionDeniedException` | `403`: the key, its scope, your role or your plan does not allow it |
| `NotFoundException` | `404` |
| `ConflictException` | `409`, such as an idempotency key reused with a different payload |
| `RateLimitException` | `429`; `RetryAfter` holds the server's hint in seconds |
| `ApiException` | `5xx` |
| `ApiConnectionException` | no answer: DNS, connection or TLS failure (`StatusCode` is `null`) |
| `ApiTimeoutException` | the request timed out (a subclass of `ApiConnectionException`) |
| `WebhookVerificationException` | a webhook delivery did not verify |

Each carries `StatusCode`, `Name` (the API's error name, such as `validation_error`; `application_error` when the API sent none), `Message` and `RequestId`, the answer's `x-request-id`: the same id the request has on the dashboard's Logs page, so quote it when you ask about a request.

```csharp
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
```

Cancelling a call's `CancellationToken` throws `OperationCanceledException`, as everywhere in .NET.

## Retries and timeouts

Failures worth retrying are retried up to `MaxRetries` times (default 2): `429`, `5xx`, timeouts and network errors, and the `409 concurrent_idempotent_requests` the API answers while the first attempt with the same key is still running. Only requests safe to repeat are retried: `GET`, `PUT`, `DELETE` and the sending operations above. The client waits as `Retry-After` asks (seconds or an HTTP date), or backs off exponentially (0.5 s, 1 s, 2 s … up to 8 s, with jitter); a `Retry-After` over a minute is not waited for, and the error is thrown instead.

Each attempt has its own timeout (default 60 seconds). An event stream is timed only until it opens.

```csharp
var client = new BillionSendClient(new BillionSendOptions
{
    ApiKey = "bs_...",
    MaxRetries = 4,
    Timeout = TimeSpan.FromSeconds(30),
});

await client.Emails.SendAsync(message, new IdempotentRequestOptions { MaxRetries = 0 }); // per call
```

## Verify webhooks

Every webhook delivery is signed with the endpoint's `whsec_` secret (shown when you create the endpoint, and by `Webhooks.RotateSigningSecretAsync`). `Webhook.Verify` checks the signature and the timestamp (within 5 minutes by default, so a captured delivery cannot be replayed) and returns the parsed event; anything wrong throws a `WebhookVerificationException`. Pass the body exactly as it arrived, before any JSON parsing. In ASP.NET Core:

```csharp
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
```

`Webhook.Verify` also takes the body as bytes and the headers as a dictionary (any case). During a secret rotation a delivery carries a signature for each live secret, and either secret verifies it.

## Configuration

```csharp
var client = new BillionSendClient(new BillionSendOptions
{
    ApiKey = "bs_...",                        // default: BILLIONSEND_API_KEY
    BaseUrl = "https://api.billionsend.com",  // default: BILLIONSEND_BASE_URL, then this
    Timeout = TimeSpan.FromSeconds(60),       // per attempt; event streams are timed only until they open
    MaxRetries = 2,                           // see Retries and timeouts
    HttpClient = httpClient,                  // default: one the client creates and disposes
    Headers = new Dictionary<string, string> { ["X-My-Header"] = "value" }, // sent with every request
});
```

Every method takes a `RequestOptions` (or its subclass for that operation) with `Headers`, `Timeout` and `MaxRetries` for that call, plus `IdempotencyKey` on the sending operations. `Emails.BatchAsync` and `Contacts.Batch.CreateAsync` take `BatchValidation` (`Strict` or `Permissive`, the `x-batch-validation` header). With your own `HttpClient` (from `IHttpClientFactory`, say), its `BaseAddress` is ignored, the client does not dispose it, and its own `Timeout` applies on top of the client's.

## Resources

| Property | Covers |
| --- | --- |
| `Emails` | send, batch, list, get, update, cancel, delete, insights, share, unshare, `Attachments` (list, get) |
| `Emails.Metrics` | `GetAsync` (the `emails.metrics` operation), links, countries, clients |
| `Emails.Receiving` | received mail: list, get, delete, reply, forward, `Attachments` (list, get, text) |
| `Domains` | create, list, get, update, delete, verify, `Claims` (create, get, verify) |
| `ApiKeys` | create, list, update, delete |
| `Contacts` | create, list, get, update, delete, preferences link, `Batch`, `Topics`, `Segments` (list, add, remove), `Imports` (create, list, get) |
| `Audiences.Contacts` | contacts addressed through an audience |
| `ContactProperties`, `Segments`, `Topics` | create, list, get, update, delete (segment contacts under `Segments.Contacts`) |
| `Broadcasts` | create, list, get, update, delete, send, duplicate, cancel, recipients, clicked links |
| `Templates` | create, list, get, update, delete, publish, duplicate |
| `Webhooks` | create, list, get, update, delete, rotate signing secret, `Events` (list, get, replay, `Attempts`) |
| `Automations` | create, list, get, update, remove, validate, duplicate, simulate, stop, metrics, `Runs` (list, get, cancel) |
| `Events` | create, list, get, update, remove, send (custom events that start automations) |
| `Suppressions` | create, list, get, delete, `Batch` add / remove |
| `Unsubscribes` | create, list |
| `Verifications` | availability, estimate, create, list, get |
| `Usage`, `Deliverability` | get |
| `Logs` | list, get |
| `OauthGrants` | list, revoke |
| `Inboxes` | create, list, get, update, delete, send, `Agent` (get, update), `Safety`, `Labels`, `Threads` (search, labels, emails, reply, forward), `Quarantine`, `Codes`, `Drafts`, `Events` |
| `Agent` | signup, status, claim email |

Each method's documentation names the API schema of its body and answer (`SendEmailRequest`, `Inbox`, …), described in the [API reference](https://billionsend.com/docs).

## Development

The build and the tests need the .NET 8 SDK (`global.json` pins the version); the generator needs Python 3 and nothing else.

```bash
python3 scripts/generate.py --check   # the generated code matches the OpenAPI snapshot
dotnet test                           # every offline test (no network)
dotnet pack src/BillionSend -c Release -o artifacts
```

`scripts/dotnet.sh` runs the same `dotnet` commands in the pinned SDK container image with rootless podman (`scripts/dotnet.sh test`).

CI also measures coverage and fails below 95% of lines and of branches in the hand-written runtime code (everything but `Generated/`) or when a generated method is never called:

```bash
dotnet test --settings tests/coverage.runsettings --collect:"XPlat Code Coverage" --results-directory TestResults
python3 scripts/check_coverage.py "TestResults/*/coverage.cobertura.xml"
```

Every answer the offline tests' fake API gives, and every request the SDK sends it, is validated against the schemas of the OpenAPI snapshot (`tests/BillionSend.Tests/Support/ContractMock.cs`), so a test fixture cannot drift from the API.

### Live API tests

`tests/BillionSend.E2E` runs the SDK against the real API: sends to the SES mailbox simulator, creates segments, topics, templates, webhooks, automations and the like with a `sdk-e2e-dotnet-` name, and deletes everything it made. Every request and every answer is checked against the OpenAPI document. It is not in `BillionSend.sln` and CI never runs it; without a key its live test is skipped:

```bash
BILLIONSEND_E2E_API_KEY=bs_... dotnet test tests/BillionSend.E2E
```

It needs a key with full access to an account that has a verified sending domain (the sender is `SDK E2E <sdk-e2e@mail.billionsend.dev>`), sends fewer than 20 emails, and makes at most 150 requests at one a second. Set `BILLIONSEND_OPENAPI_PATH` to check against another copy of the document than the snapshot.

The contract is `openapi/api.openapi.json`, a snapshot of the published OpenAPI document. `src/BillionSend/Generated/Resources.g.cs` is generated from it: one method per operation, an iterator per cursor list, and the operation's descriptions as doc comments. The tests run every SDK method against a local mock that checks each request against that snapshot and answers from it, and fail when an operation has no SDK method. When the API changes:

```bash
python3 scripts/sync_openapi.py               # or: --from path/to/api.openapi.json
python3 scripts/generate.py                   # regenerates src/BillionSend/Generated/Resources.g.cs
dotnet test                                   # what fails is what the SDK has to follow
```

## Releasing

Publishing is manual; nothing is published on push, and nothing is ever pushed from a laptop. The package is `BillionSend` on [nuget.org](https://www.nuget.org/packages/BillionSend), published by `.github/workflows/release.yml` with NuGet Trusted Publishing: the workflow trades its GitHub OIDC token for a short-lived API key (`NuGet/login`), so no long-lived key exists anywhere.

What it takes:

- The `BillionSend` package ID belongs to the nuget.org organization `BillionSend`; its Trusted Publishing policy (GitHub Actions, repository `billionsend/billionsend-dotnet`, workflow file `release.yml`) is active.
- The workflow logs in as the nuget.org user `ikbear`, a member of that organization, written in `release.yml` (`NuGet/login`'s `user`). It is a user name, not a key: no repository secret is needed. To publish as someone else, change that line. The `nuget` environment can require a reviewer's approval before the job runs.

Steps:

1. Set `<Version>` in `src/BillionSend/BillionSend.csproj` and `SdkVersion` in `src/BillionSend/BillionSendClient.cs` (a test keeps the two and the changelog in step), turn the changelog's `Unreleased` heading into that version, commit, then tag and push: `git tag v0.1.0 && git push origin main v0.1.0`.
2. Actions → `release` → Run workflow, on the version tag. The workflow refuses a branch or a tag that differs from the project's version, checks the generated code, runs every test, packs `BillionSend.<version>.nupkg` with its symbol package, logs in with `NuGet/login` and pushes both to nuget.org.

The first version goes the same way: a Trusted Publishing policy can be created before the package exists, and the first push creates the `BillionSend` ID under the policy's owner. If nuget.org ever refuses that (a policy it accepts only for an existing package), create the package once by hand: from a clean checkout of the tag run `dotnet pack src/BillionSend -c Release -o artifacts`, upload `artifacts/BillionSend.<version>.nupkg` on nuget.org (signed in as the owner) through **Upload package**, then add the Trusted Publishing policy; every later version goes through the workflow. nuget.org validates and indexes a new version within minutes; a pushed version can be unlisted but never replaced, so a fix ships as the next version.

## License

[MIT](LICENSE)
