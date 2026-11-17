using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace BillionSend.Tests.Support;

/// <summary>One request the mock received.</summary>
public sealed record Hit(
    string Key,
    string Method,
    Uri Url,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyDictionary<string, string> Query,
    IReadOnlyDictionary<string, string> PathParams,
    string? Body)
{
    /// <summary>The JSON the mock answered with, when it answered JSON.</summary>
    public JsonNode? Response { get; set; }

    public string? Header(string name) => Headers.TryGetValue(name.ToLowerInvariant(), out var value) ? value : null;

    public JsonNode? Json => Body is null ? null : JsonNode.Parse(Body);
}

/// <summary>
/// A contract mock of the BillionSend API built from the OpenAPI snapshot, at the transport layer
/// (an <see cref="HttpMessageHandler"/>). For every request it finds the operation (method and path
/// template) in the snapshot or records a violation; checks the API key header (absent only where
/// the operation's security is empty), the user agent, the idempotency key of the sending
/// operations, the query and header parameters and the JSON body against the operation's schemas;
/// and answers with a queued response, or else with an instance of the operation's success schema.
/// Every hit is recorded, so the coverage test can compare what the SDK called with what the
/// snapshot declares.
/// </summary>
public sealed class ContractMock : HttpMessageHandler
{
    public const string BaseUrl = "https://api.test.billionsend.com";
    public const string ApiKey = "bs_test_0123456789";
    public const string RequestId = "req_mock";

    private readonly ConcurrentQueue<Hit> _hits = new();
    private readonly ConcurrentQueue<string> _violations = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<Responder>> _queued = new();

    public delegate Task<HttpResponseMessage> Responder(Hit hit, CancellationToken cancellationToken);

    public IReadOnlyList<Hit> Hits => _hits.ToList();

    /// <summary>Generated answers carry only the required properties (otherwise every property).</summary>
    public bool Minimal { get; set; }

    public IReadOnlyList<string> Violations => _violations.ToList();

    public IReadOnlyList<Hit> HitsFor(string key) => _hits.Where(h => h.Key == key).ToList();

    public BillionSendClient Client(Action<BillionSendOptions>? configure = null)
    {
        var options = new BillionSendOptions { ApiKey = ApiKey, BaseUrl = BaseUrl, HttpClient = new HttpClient(this, disposeHandler: false) };
        configure?.Invoke(options);
        var client = new BillionSendClient(options);
        client.Api.Delay = (_, _) => Task.CompletedTask;
        return client;
    }

    /// <summary>Queues one answer for <paramref name="key"/> (<c>"GET /emails"</c>); queued answers are used in order.</summary>
    public void Enqueue(string key, Responder responder)
    {
        if (!Spec.OperationKeys.Contains(key))
        {
            throw new ArgumentException($"{key} is not an operation of the snapshot");
        }

        _queued.GetOrAdd(key, _ => new ConcurrentQueue<Responder>()).Enqueue(responder);
    }

    public void Reply(string key, int status, JsonNode? body = null, IDictionary<string, string>? headers = null) =>
        Enqueue(key, (_, _) => Task.FromResult(Json(status, body?.ToJsonString() ?? string.Empty, headers)));

    public void ReplyText(string key, int status, string text, string contentType = "text/plain") =>
        Enqueue(key, (_, _) =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(text, Encoding.UTF8, contentType),
            };
            response.Headers.TryAddWithoutValidation("x-request-id", RequestId);
            return Task.FromResult(response);
        });

    public void Error(string key, int status, string name, string message, IDictionary<string, string>? headers = null) =>
        Reply(key, status, new JsonObject { ["statusCode"] = status, ["name"] = name, ["message"] = message }, headers);

    public void Fail(string key, Exception exception) =>
        Enqueue(key, (_, _) => Task.FromException<HttpResponseMessage>(exception));

    /// <summary>Answers only after <paramref name="delay"/> (or when the request is cancelled).</summary>
    public void Slow(string key, TimeSpan delay) =>
        Enqueue(key, async (hit, ct) =>
        {
            await Task.Delay(delay, ct);
            return DefaultAnswer(Spec.Get(hit.Key));
        });

    /// <summary>Answers with an open event stream that <paramref name="write"/> feeds.</summary>
    public void Stream(string key, Func<SseStream, Task> write) =>
        Enqueue(key, (_, _) =>
        {
            var stream = new SseStream();
            _ = Task.Run(async () =>
            {
                try
                {
                    await write(stream);
                }
                catch (Exception exception)
                {
                    _violations.Enqueue($"{key}: the stream writer failed: {exception.Message}");
                }
            });
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            response.Headers.TryAddWithoutValidation("x-request-id", RequestId);
            return Task.FromResult(response);
        });

    public void Reset()
    {
        _hits.Clear();
        _violations.Clear();
        _queued.Clear();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!;
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
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
        }

        var query = ParseQuery(url.Query, out var repeated);
        var (operation, pathParams) = Match(request.Method.Method, url.AbsolutePath);
        if (operation is null)
        {
            _violations.Enqueue($"{request.Method} {url.AbsolutePath}: not an operation of the snapshot");
            _hits.Enqueue(new Hit($"{request.Method} {url.AbsolutePath}", request.Method.Method, url, headers, query, pathParams, body));
            return Json(404, """{"statusCode":404,"name":"not_found","message":"No such operation"}""", null);
        }

        foreach (var name in repeated)
        {
            _violations.Enqueue($"{operation.Key}: query parameter {name} sent more than once");
        }

        foreach (var problem in Check(operation, headers, query, pathParams, body))
        {
            _violations.Enqueue($"{operation.Key}: {problem}");
        }

        Responder? responder = null;
        if (_queued.TryGetValue(operation.Key, out var queue))
        {
            queue.TryDequeue(out responder);
        }

        var hit = new Hit(operation.Key, request.Method.Method, url, headers, query, pathParams, body);
        _hits.Enqueue(hit);
        HttpResponseMessage response;
        if (responder is not null)
        {
            response = await responder(hit, cancellationToken);
        }
        else
        {
            response = DefaultAnswer(operation, Minimal);
            if (!operation.Stream)
            {
                var generated = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                foreach (var error in Spec.Validate(generated, operation.ResponseSchema, "answer"))
                {
                    _violations.Enqueue($"{operation.Key}: the generated answer is invalid: {error}");
                }
            }
        }

        if (response.Content.Headers.ContentType?.MediaType == "application/json" && response.Content is StringContent)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            hit.Response = text.Length == 0 ? null : JsonNode.Parse(text);
        }

        return response;
    }

    /// <summary>The operation's success answer: a sample of its schema, or a one-event stream.</summary>
    public static HttpResponseMessage DefaultAnswer(Operation operation, bool minimal = false)
    {
        if (operation.Stream)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "id: 1.1.1\nevent: inbox.email.received\ndata: {\"type\":\"inbox.email.received\",\"created_at\":\"2026-11-16T00:00:00.000Z\",\"inbox_id\":\"i\",\"data\":{\"inbox_id\":\"i\",\"thread_id\":\"t\",\"email_id\":\"e\"}}\n\n"),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            response.Headers.TryAddWithoutValidation("x-request-id", RequestId);
            return response;
        }

        var status = int.Parse(operation.SuccessStatus!);
        var example = operation.ResponseSchema is null ? new JsonObject() : Spec.Example(operation.ResponseSchema, full: !minimal);
        return Json(status, example?.ToJsonString() ?? "null", null);
    }

    private static HttpResponseMessage Json(int status, string text, IDictionary<string, string>? headers)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(text, Encoding.UTF8) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        response.Headers.TryAddWithoutValidation("x-request-id", RequestId);
        foreach (var pair in headers ?? new Dictionary<string, string>())
        {
            response.Headers.Remove(pair.Key);
            response.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
        }

        return response;
    }

    private static Dictionary<string, string> ParseQuery(string query, out List<string> repeated)
    {
        var result = new Dictionary<string, string>();
        repeated = [];
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=');
            var key = Uri.UnescapeDataString(equals == -1 ? part : part[..equals]);
            var value = equals == -1 ? string.Empty : Uri.UnescapeDataString(part[(equals + 1)..].Replace('+', ' '));
            if (!result.TryAdd(key, value))
            {
                repeated.Add(key);
            }
        }

        return result;
    }

    /// <summary>The operation a path belongs to: the template with the most literal segments wins.</summary>
    private static (Operation?, Dictionary<string, string>) Match(string method, string path)
    {
        var segments = path.Trim('/').Split('/');
        Operation? best = null;
        Dictionary<string, string> bestParams = new();
        var bestLiterals = -1;
        foreach (var operation in Spec.Operations.Where(o => o.Method == method))
        {
            var template = operation.Path.Trim('/').Split('/');
            if (template.Length != segments.Length)
            {
                continue;
            }

            var captured = new Dictionary<string, string>();
            var literals = 0;
            var ok = true;
            for (var i = 0; i < template.Length && ok; i++)
            {
                if (template[i].StartsWith('{'))
                {
                    captured[template[i].Trim('{', '}')] = Uri.UnescapeDataString(segments[i]);
                }
                else if (template[i] == segments[i])
                {
                    literals++;
                }
                else
                {
                    ok = false;
                }
            }

            if (ok && literals > bestLiterals)
            {
                best = operation;
                bestParams = captured;
                bestLiterals = literals;
            }
        }

        return (best, bestParams);
    }

    private static IEnumerable<string> Check(
        Operation operation,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyDictionary<string, string> query,
        IReadOnlyDictionary<string, string> pathParams,
        string? body)
    {
        headers.TryGetValue("authorization", out var authorization);
        if (operation.TakesApiKey && authorization != $"Bearer {ApiKey}")
        {
            yield return $"expected the API key, got {authorization ?? "no Authorization header"}";
        }

        if (!operation.TakesApiKey && authorization is not null)
        {
            yield return "sent an Authorization header to an operation that takes no API key";
        }

        if (!headers.TryGetValue("user-agent", out var agent) || agent != $"billionsend-dotnet/{BillionSendClient.SdkVersion}")
        {
            yield return $"unexpected user agent {agent}";
        }

        var expectedAccept = operation.Stream ? "text/event-stream" : "application/json";
        if (!headers.TryGetValue("accept", out var accept) || accept != expectedAccept)
        {
            yield return $"expected Accept {expectedAccept}, got {accept}";
        }

        if (operation.Idempotent && (!headers.TryGetValue("idempotency-key", out var key) || key.Length is < 1 or > 256))
        {
            yield return "a sending operation without a valid Idempotency-Key";
        }

        foreach (var parameter in operation.Parameters("path"))
        {
            var name = (string)parameter["name"]!;
            if (!pathParams.TryGetValue(name, out var value) || value.Length == 0)
            {
                yield return $"path parameter {name} missing or empty";
                continue;
            }

            foreach (var error in Spec.Validate(JsonValue.Create(value), parameter["schema"], $"path {name}"))
            {
                yield return error;
            }
        }

        var declared = operation.Parameters("query").ToDictionary(p => (string)p["name"]!);
        foreach (var name in query.Keys.Where(name => !declared.ContainsKey(name)))
        {
            yield return $"undeclared query parameter {name}";
        }

        foreach (var (name, parameter) in declared)
        {
            if (!query.TryGetValue(name, out var value))
            {
                if ((bool?)parameter["required"] == true)
                {
                    yield return $"required query parameter {name} missing";
                }

                continue;
            }

            foreach (var error in Spec.Validate(Spec.Coerce(value, parameter["schema"]!), parameter["schema"], $"query {name}"))
            {
                yield return error;
            }
        }

        foreach (var parameter in operation.Parameters("header"))
        {
            var name = ((string)parameter["name"]!).ToLowerInvariant();
            if (!headers.TryGetValue(name, out var value))
            {
                if ((bool?)parameter["required"] == true)
                {
                    yield return $"required header {name} missing";
                }

                continue;
            }

            foreach (var error in Spec.Validate(JsonValue.Create(value), parameter["schema"], $"header {name}"))
            {
                yield return error;
            }
        }

        var requestBody = operation.RequestBody;
        if (requestBody is null)
        {
            if (body is not null)
            {
                yield return "sent a body to an operation that takes none";
            }

            yield break;
        }

        if (body is null)
        {
            if ((bool?)requestBody["required"] == true)
            {
                yield return "the required body is missing";
            }

            yield break;
        }

        if (!headers.TryGetValue("content-type", out var contentType) || !contentType.StartsWith("application/json"))
        {
            yield return $"body sent as {contentType}";
        }

        JsonNode? json;
        string? parseError = null;
        try
        {
            json = JsonNode.Parse(body);
        }
        catch (Exception exception)
        {
            json = null;
            parseError = exception.Message;
        }

        if (parseError is not null)
        {
            yield return $"the body is not JSON: {parseError}";
            yield break;
        }

        foreach (var error in Spec.Validate(json, operation.RequestSchema, "body"))
        {
            yield return error;
        }
    }
}

/// <summary>The body of a mocked event stream: what the test writes, the client reads.</summary>
public sealed class SseStream : Stream
{
    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private byte[]? _current;
    private int _offset;

    /// <summary>Completes when the client closes the stream.</summary>
    public Task Closed => _closed.Task;

    public bool IsClosed => _closed.Task.IsCompleted;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public void Write(string text) => _chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(text));

    public void Write(byte[] bytes) => _chunks.Writer.TryWrite(bytes);

    /// <summary>Ends the response: the client sees the connection close.</summary>
    public void End() => _chunks.Writer.TryComplete();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_current is null || _offset >= _current.Length)
        {
            if (!await _chunks.Reader.WaitToReadAsync(cancellationToken))
            {
                return 0;
            }

            _chunks.Reader.TryRead(out _current);
            _offset = 0;
        }

        var count = Math.Min(buffer.Length, _current!.Length - _offset);
        _current.AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        _chunks.Writer.TryComplete();
        _closed.TrySetResult();
        base.Dispose(disposing);
    }
}
