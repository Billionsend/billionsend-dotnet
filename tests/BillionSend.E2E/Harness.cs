using System.Diagnostics;
using System.Text;
using BillionSend.Tests.Support;

namespace BillionSend.E2E;

/// <summary>A test that runs only when <c>BILLIONSEND_E2E_API_KEY</c> is set.</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public const string KeyVariable = "BILLIONSEND_E2E_API_KEY";

    public LiveFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(KeyVariable)))
        {
            Skip = $"Set {KeyVariable} to run the tests against the live API";
        }
    }
}

/// <summary>A request broke a safety rule of the live run and was not sent.</summary>
public sealed class LiveGuardException(string message) : Exception(message);

/// <summary>
/// Two rules the live run holds in code, whatever a scenario writes: mail goes only to the SES
/// mailbox simulator's success address (or a plus alias of it), so no real person and not even the
/// simulator's bounce, complaint or suppression-list mailboxes are ever written to; and an address
/// the run has put on the suppression list is never a recipient (<c>to</c>, <c>cc</c>, <c>bcc</c>)
/// of any send afterwards, deleted from the list or not.
/// </summary>
public sealed class SendGuard
{
    private static readonly System.Text.RegularExpressions.Regex Success =
        new(@"^success(\+[a-z0-9._-]+)?@simulator\.amazonses\.com$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private readonly HashSet<string> _suppressed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every address this run added to the suppression list.</summary>
    public IReadOnlyCollection<string> Suppressed => _suppressed;

    /// <summary>The address in <c>Name &lt;address&gt;</c> or in a bare address, trimmed.</summary>
    public static string Bare(string recipient)
    {
        var text = recipient.Trim();
        var open = text.LastIndexOf('<');
        var close = text.LastIndexOf('>');
        return open >= 0 && close > open ? text[(open + 1)..close].Trim() : text;
    }

    /// <summary>
    /// Throws <see cref="LiveGuardException"/> when the request breaks a rule; otherwise notes what
    /// it suppresses. A body of a send or of a suppression that cannot be read as JSON, and a
    /// recipient that is not text, cannot be checked and so are refused too.
    /// </summary>
    public void Check(Operation? operation, string? body)
    {
        var key = operation?.Key;
        var suppressing = key is "POST /suppressions" or "POST /suppressions/batch/add";
        // Sending operations carry an Idempotency-Key; events.send has no recipients, only a payload
        // of the caller's own keys. A route the document does not know is checked as a send.
        var sending = operation is null || (operation.Idempotent && key != "POST /events/send");
        if ((!suppressing && !sending) || string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        JsonNode? json;
        try
        {
            json = JsonNode.Parse(body);
        }
        catch (System.Text.Json.JsonException)
        {
            throw new LiveGuardException("the body cannot be read as JSON, so its recipients cannot be checked");
        }

        if (json is not JsonObject and not JsonArray)
        {
            throw new LiveGuardException("the body is neither an object nor a list, so its recipients cannot be checked");
        }

        if (suppressing)
        {
            Note(json);
            return;
        }

        foreach (var recipient in Recipients(json))
        {
            var address = Bare(recipient);
            if (!Success.IsMatch(address))
            {
                throw new LiveGuardException($"a recipient is not the simulator's success mailbox: {address.Split('@')[0]}@…");
            }

            if (_suppressed.Contains(address))
            {
                throw new LiveGuardException($"a recipient was put on the suppression list by this run: {address}");
            }
        }
    }

    private void Note(JsonNode? json)
    {
        if (json is not JsonObject body)
        {
            return;
        }

        if (body["email"] is JsonValue one && one.TryGetValue<string>(out var email))
        {
            _suppressed.Add(Bare(email));
        }

        if (body["emails"] is JsonArray many)
        {
            foreach (var item in many)
            {
                if (item is JsonValue value && value.TryGetValue<string>(out var text))
                {
                    _suppressed.Add(Bare(text));
                }
            }
        }
    }

    /// <summary>Every value under a <c>to</c>, <c>cc</c> or <c>bcc</c> key of the body, or of each item of a batch; a value that is not text cannot be checked and is returned as an empty address.</summary>
    private static IEnumerable<string> Recipients(JsonNode? json)
    {
        var items = json is JsonArray batch ? batch.ToList() : [json];
        foreach (var item in items)
        {
            if (item is not JsonObject mail)
            {
                yield return string.Empty;
                continue;
            }

            foreach (var field in new[] { "to", "cc", "bcc" })
            {
                switch (mail[field])
                {
                    case null:
                        break;
                    case JsonValue single when single.TryGetValue<string>(out var text):
                        yield return text;
                        break;
                    case JsonArray list:
                        foreach (var entry in list)
                        {
                            yield return entry is JsonValue v && v.TryGetValue<string>(out var address) ? address : string.Empty;
                        }

                        break;
                    default:
                        yield return string.Empty;
                        break;
                }
            }
        }
    }
}

/// <summary>The suite was stopped on purpose: two 429s in a row, or the request budget is spent.</summary>
public sealed class LiveAbort(string message) : Exception(message);

/// <summary>
/// Sits between the SDK and the network for the live run. It keeps the suite polite (at least a
/// second between requests, a hard cap on the total, a stop after two 429s in a row) and holds the
/// SDK to the published document both ways: every request it sends and every answer the API gives
/// is checked against the operation's schemas, and what does not match is recorded as a finding.
/// Nothing but the operation's template, its status and the findings is ever written down: no key,
/// no body, no address.
/// </summary>
public sealed class LiveHandler(HttpMessageHandler inner, TimeSpan gap, int scenarioBudget, int totalBudget) : DelegatingHandler(inner)
{
    private DateTime _last = DateTime.MinValue;
    private int _consecutive429;

    public int Count { get; private set; }

    public bool Aborted { get; private set; }

    /// <summary>Cleanup may spend what the scenarios left of the total.</summary>
    public bool Cleaning { get; set; }

    public List<string> Log { get; } = [];

    public List<string> Findings { get; } = [];

    /// <summary>The recipient rules, kept for the whole run: what it suppressed is never mailed afterwards.</summary>
    public SendGuard Guard { get; } = new();

    /// <summary>Requests the guard refused (none was sent); a live run with any is a failed run.</summary>
    public List<string> Refused { get; } = [];

    /// <summary>The status of the last answer, for a scenario that wants to say what it saw.</summary>
    public int LastStatus { get; private set; }

    public bool BudgetLeft => Count < scenarioBudget;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Aborted)
        {
            throw new LiveAbort("The suite stopped after two 429 answers in a row");
        }

        if (Count >= (Cleaning ? totalBudget : scenarioBudget))
        {
            throw new LiveAbort($"The request budget of {(Cleaning ? totalBudget : scenarioBudget)} is spent");
        }

        string? body = null;
        var headers = new Dictionary<string, string>();
        foreach (var header in request.Headers)
        {
            headers[header.Key.ToLowerInvariant()] = string.Join(", ", header.Value);
        }

        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
            {
                headers[header.Key.ToLowerInvariant()] = string.Join(", ", header.Value);
            }

            body = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        var uri = request.RequestUri!;
        var (operation, pathParams) = ContractMock.Match(request.Method.Method, uri.AbsolutePath);

        // Before anything is sent or counted: a request that would mail an address the suite may
        // not mail, or one it suppressed, never leaves.
        try
        {
            Guard.Check(operation, body);
        }
        catch (LiveGuardException refusal)
        {
            Refused.Add($"{operation?.Key ?? $"{request.Method} {uri.AbsolutePath}"}: {refusal.Message}");
            throw;
        }

        var wait = gap - (DateTime.UtcNow - _last);
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, cancellationToken);
        }

        _last = DateTime.UtcNow;
        Count++;

        var label = operation?.Key ?? $"{request.Method} (not in the document)";
        if (operation is null)
        {
            Findings.Add($"{request.Method} {uri.AbsolutePath}: the SDK called a route the document does not have");
        }
        else
        {
            // The key the request carries is the one the client was built with; it is compared with
            // itself so the check does not need to know it (and never prints it).
            var bearer = headers.TryGetValue("authorization", out var auth) && auth.StartsWith("Bearer ", StringComparison.Ordinal) ? auth["Bearer ".Length..] : "";
            foreach (var problem in ContractMock.Check(operation, headers, ContractMock.ParseQuery(uri.Query, out _), pathParams, body, bearer))
            {
                Findings.Add($"{label}: request: {problem}");
            }
        }

        var response = await base.SendAsync(request, cancellationToken);
        await response.Content.LoadIntoBufferAsync();
        var status = (int)response.StatusCode;
        LastStatus = status;
        Log.Add($"{Count:000} {label} -> {status}");

        if (status == 429)
        {
            if (++_consecutive429 >= 2)
            {
                Aborted = true;
            }
        }
        else
        {
            _consecutive429 = 0;
        }

        if (operation is not null && !operation.Stream)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            CheckAnswer(operation, status, text, label);
        }

        return response;
    }

    private void CheckAnswer(Operation operation, int status, string text, string label)
    {
        var responses = (JsonObject)operation.Node["responses"]!;
        var declared = responses[status.ToString()] as JsonObject;
        if (declared is null && status is >= 200 and < 300)
        {
            Findings.Add($"{label}: the API answered {status}, which the document does not declare");
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            Findings.Add($"{label}: the {status} answer is not JSON");
            return;
        }

        var schema = declared?["content"]?["application/json"]?["schema"] ?? Spec.Document["components"]!["schemas"]!["ErrorResponse"];
        foreach (var problem in Spec.Validate(parsed, schema, $"answer {status}"))
        {
            Findings.Add($"{label}: {problem}");
        }
    }
}

/// <summary>Takes anything that looks like a secret out of text before it is shown or written.</summary>
public static class Redact
{
    private static readonly System.Text.RegularExpressions.Regex Secrets =
        new(@"(bs_[A-Za-z0-9_]{6,}|whsec_[A-Za-z0-9+/=]{8,}|Bearer\s+\S+)", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static string Text(string text)
    {
        var key = Environment.GetEnvironmentVariable(LiveFactAttribute.KeyVariable);
        if (!string.IsNullOrEmpty(key))
        {
            text = text.Replace(key, "[redacted]", StringComparison.Ordinal);
        }

        return Secrets.Replace(text, "[redacted]");
    }
}

/// <summary>What a live run did: its steps, what it left behind, what it had to give up.</summary>
public sealed class Recorder(bool live)
{
    public sealed record Result(string Name, string Outcome, string Detail);

    public sealed class Created(string description, Func<Task> undo)
    {
        public string Description { get; } = description;

        public Func<Task> Undo { get; } = undo;

        /// <summary>The scenario deleted it itself.</summary>
        public bool Done { get; set; }
    }

    private readonly List<Created> _created = [];

    public bool Live { get; } = live;

    public List<Result> Results { get; } = [];

    public List<string> Leftovers { get; } = [];

    public IReadOnlyList<Created> CreatedObjects => _created;

    /// <summary>Runs one named check; a failure is recorded and the run goes on, so one finding does not hide the rest.</summary>
    public async Task Step(string name, Func<Task> body)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            await body();
            Results.Add(new Result(name, "pass", $"{watch.ElapsedMilliseconds} ms"));
        }
        catch (LiveAbort)
        {
            throw;
        }
        catch (Exception exception)
        {
            Results.Add(new Result(name, "FAIL", $"{exception.GetType().Name}: {Trim(exception.Message)}"));
        }
    }

    public void Skip(string name, string reason) => Results.Add(new Result(name, "skipped", reason));

    /// <summary>An expectation about what the live API does; it only counts in the live run, where there is a real API to ask.</summary>
    public void Expect(bool condition, string message)
    {
        if (Live && !condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    /// <summary>Registers an object the run created, to be deleted when the run ends however it ends.</summary>
    public Created Track(string description, Func<Task> undo)
    {
        var created = new Created(description, undo);
        _created.Add(created);
        return created;
    }

    public async Task CleanUp()
    {
        foreach (var created in Enumerable.Reverse(_created).Where(c => !c.Done))
        {
            try
            {
                await created.Undo();
                created.Done = true;
            }
            catch (Exception exception)
            {
                Leftovers.Add($"{created.Description}: {exception.GetType().Name}: {Trim(exception.Message)}");
            }
        }
    }

    private static string Trim(string message) => Redact.Text(message.Length > 300 ? message[..300] + "…" : message);

    public string Table()
    {
        var text = new StringBuilder();
        foreach (var result in Results)
        {
            text.AppendLine($"{result.Outcome,-8} {result.Name}  {result.Detail}");
        }

        return text.ToString();
    }
}
