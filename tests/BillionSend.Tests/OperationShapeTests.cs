// Every operation of the snapshot, called by reflection with arguments built from its own
// parameters and schemas, so nothing in this file is written by hand per operation: each path
// parameter gets a distinct value (a swapped pair of arguments fails), the body and the query are
// the schemas' own samples, and the request that reaches the mock is compared with them piece by
// piece, not only validated. The answer the SDK returns must be the answer the mock sent and must
// itself match the operation's response schema.
using System.Globalization;
using System.Reflection;
using BillionSend.Tests.Support;

namespace BillionSend.Tests;

public sealed class OperationShapeTests : MockTest
{
    public static TheoryData<string, bool> Keys()
    {
        var data = new TheoryData<string, bool>();
        foreach (var operation in Spec.Operations.Where(o => !o.Multipart).OrderBy(o => o.Key, StringComparer.Ordinal))
        {
            data.Add(operation.Key, false);
            data.Add(operation.Key, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public async Task SendsExactlyWhatItWasGivenAndReturnsExactlyWhatTheApiSent(string key, bool minimalAnswers)
    {
        Mock.Minimal = minimalAnswers;
        var operation = Spec.Get(key);
        using var client = Client();
        var method = Find(client, operation.OperationId, out var target);

        var call = BuildCall(operation, method);
        var (pathNames, pathValues, body, query, arguments) = (call.PathNames, call.PathValues, call.Body, call.Query, call.Arguments);

        object? returned = await (dynamic)method.Invoke(target, arguments.ToArray())!;
        var hit = Assert.Single(Mock.Hits);

        // The request: method, path (each value percent-encoded as one segment), query, headers, body.
        Assert.Equal(key, hit.Key);
        var expectedPath = pathNames.Zip(pathValues).Aggregate(operation.Path, (path, pair) => path.Replace("{" + pair.First + "}", Uri.EscapeDataString(pair.Second)));
        Assert.Equal(expectedPath, hit.Url.AbsolutePath);
        Assert.Equal(pathValues, pathNames.Select(name => hit.PathParams[name]));
        Assert.Equal(
            query.ToDictionary(pair => pair.Key, pair => Wire(pair.Value)),
            hit.Query);
        if (body is not null)
        {
            Assert.True(JsonNode.DeepEquals(body, hit.Json), $"{key}: the body on the wire differs from the body given");
            Assert.Equal("application/json", hit.Header("content-type"));
        }
        else
        {
            Assert.Null(hit.Body);
        }

        Assert.Equal(operation.TakesApiKey ? $"Bearer {ContractMock.ApiKey}" : null, hit.Header("authorization"));
        Assert.Equal(operation.Idempotent, hit.Header("idempotency-key") is not null);
        Assert.Equal($"billionsend-dotnet/{BillionSendClient.SdkVersion}", hit.Header("user-agent"));

        // The answer.
        if (operation.Stream)
        {
            await using var stream = Assert.IsType<EventStream>(returned);
            var seen = new List<InboxStreamItem>();
            await foreach (var item in stream)
            {
                seen.Add(item);
            }

            Assert.Equal("inbox.email.received", Assert.IsType<InboxStreamEvent>(Assert.Single(seen)).Event);
            return;
        }

        var answer = (JsonNode?)returned;
        Assert.NotNull(hit.Response);
        Assert.True(JsonNode.DeepEquals(hit.Response, answer), $"{key}: the answer differs from the body the API sent");
        Assert.Empty(Spec.Validate(answer, operation.ResponseSchema, "returned"));
    }

    public static TheoryData<string> KeysWithErrors()
    {
        var data = new TheoryData<string>();
        foreach (var operation in Spec.Operations.Where(o => !o.Multipart).OrderBy(o => o.Key, StringComparer.Ordinal))
        {
            data.Add(operation.Key);
        }

        return data;
    }

    /// <summary>Each error status an operation declares is thrown as its own type, with the API's name, message and request id.</summary>
    [Theory]
    [MemberData(nameof(KeysWithErrors))]
    public async Task ThrowsEachErrorTheOperationDeclaresAsItsTypeWithTheApisWords(string key)
    {
        var operation = Spec.Get(key);
        using var client = Client(o => o.MaxRetries = 0);
        var method = Find(client, operation.OperationId, out var target);
        var statuses = ((JsonObject)operation.Node["responses"]!).Select(r => int.Parse(r.Key)).Where(code => code >= 400).ToList();
        Assert.NotEmpty(statuses);
        foreach (var status in statuses)
        {
            Mock.Reset();
            Mock.Error(key, status, $"name_{status}", $"message {status}", new Dictionary<string, string> { ["x-request-id"] = $"req_{status}" });
            var arguments = BuildCall(operation, method).Arguments;
            BillionSendException error;
            try
            {
                object? answered = await (dynamic)method.Invoke(target, arguments.ToArray())!;
                if (answered is EventStream opened)
                {
                    await opened.DisposeAsync();
                }

                throw new Xunit.Sdk.XunitException($"{key}: status {status} did not throw");
            }
            catch (BillionSendException thrown)
            {
                error = thrown;
            }

            Assert.Equal(status, error.StatusCode);
            Assert.Equal($"name_{status}", error.Name);
            Assert.Equal($"message {status}", error.Message);
            Assert.Equal($"req_{status}", error.RequestId);
            Assert.Equal(
                status switch
                {
                    400 or 422 => typeof(ValidationException),
                    401 => typeof(AuthenticationException),
                    403 => typeof(PermissionDeniedException),
                    404 => typeof(NotFoundException),
                    409 => typeof(ConflictException),
                    429 => typeof(RateLimitException),
                    >= 500 => typeof(ApiException),
                    _ => typeof(BillionSendException),
                },
                error.GetType());
        }
    }

    /// <summary>
    /// The API answers a 429 before it writes anything, so every operation, whatever its method and
    /// with or without an idempotency key, is repeated after the wait <c>Retry-After</c> names,
    /// with the same request.
    /// </summary>
    [Theory]
    [MemberData(nameof(KeysWithErrors))]
    public async Task RepeatsARateLimitedRequestOfAnyOperationAfterItsRetryAfter(string key)
    {
        var operation = Spec.Get(key);
        using var client = Client();
        var delays = new List<TimeSpan>();
        client.Api.Delay = (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        };
        var method = Find(client, operation.OperationId, out var target);
        var arguments = BuildCall(operation, method).Arguments;
        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] is EventStreamOptions)
            {
                arguments[i] = new EventStreamOptions { Reconnect = false };
            }
        }

        Mock.Error(key, 429, "rate_limit_exceeded", "Slow down", new Dictionary<string, string> { ["retry-after"] = "2" });
        object? returned = await (dynamic)method.Invoke(target, arguments.ToArray())!;
        if (returned is EventStream stream)
        {
            await stream.DisposeAsync();
        }

        var hits = Mock.HitsFor(key);
        Assert.Equal(2, hits.Count);
        Assert.Equal([TimeSpan.FromSeconds(2)], delays);
        Assert.Equal(hits[0].Url, hits[1].Url);
        Assert.Equal(hits[0].Body, hits[1].Body);
        Assert.Equal(hits[0].Header("idempotency-key"), hits[1].Header("idempotency-key"));
    }

    [Fact]
    public async Task RepeatsARateLimitedMultipartUploadWithTheSameForm()
    {
        const string key = "POST /contacts/imports";
        using var client = Client();
        client.Api.Delay = (_, _) => Task.CompletedTask;
        Mock.Error(key, 429, "rate_limit_exceeded", "Slow down", new Dictionary<string, string> { ["retry-after"] = "0" });
        await client.Contacts.Imports.CreateAsync(new Dictionary<string, object?>
        {
            ["file"] = new FileUpload("people.csv", System.Text.Encoding.UTF8.GetBytes("Email\nana@example.com\n")),
            ["column_map"] = new { email = "Email" },
        });
        var hits = Mock.HitsFor(key);
        Assert.Equal(2, hits.Count);
        Assert.Equal(hits[0].Body, hits[1].Body);
        Assert.Contains("ana@example.com", hits[1].Body);
    }

    private sealed record BuiltCall(List<string> PathNames, List<string> PathValues, JsonNode? Body, Dictionary<string, JsonNode?> Query, List<object?> Arguments);

    /// <summary>The arguments for an operation's method: distinct path values, the body and the query from the schemas.</summary>
    private static BuiltCall BuildCall(Operation operation, MethodInfo method)
    {
        var pathNames = Regex().Matches(operation.Path).Select(m => m.Groups[1].Value).ToList();
        var pathValues = pathNames.Select((name, index) => name == "inbox_id" ? Inbox : $"00000000-0000-4000-8000-{index + 1:D12}").ToList();
        var body = operation.RequestSchema is null ? null : Spec.Example(operation.RequestSchema, full: true);
        var query = operation.Parameters("query").ToDictionary(p => (string)p["name"]!, p => Spec.Example(p["schema"], full: true));

        var arguments = new List<object?>();
        var nextPath = 0;
        foreach (var parameter in method.GetParameters())
        {
            if (parameter.ParameterType == typeof(string))
            {
                arguments.Add(pathValues[nextPath++]);
            }
            else if (parameter.ParameterType == typeof(object))
            {
                Assert.NotNull(body);
                arguments.Add(body);
            }
            else if (parameter.ParameterType == typeof(IReadOnlyDictionary<string, object?>))
            {
                arguments.Add(query.ToDictionary(pair => pair.Key, pair => ToClr(pair.Value)));
            }
            else if (parameter.ParameterType == typeof(EventStreamOptions))
            {
                arguments.Add(new EventStreamOptions { Reconnect = false, MaxRetries = 0 });
            }
            else if (parameter.ParameterType == typeof(CancellationToken))
            {
                arguments.Add(CancellationToken.None);
            }
            else
            {
                Assert.True(typeof(RequestOptions).IsAssignableFrom(parameter.ParameterType), $"{operation.Key}: unexpected parameter {parameter.Name}");
                arguments.Add(null);
            }
        }

        Assert.Equal(pathNames.Count, nextPath);
        Assert.Equal(body is null, !method.GetParameters().Any(p => p.ParameterType == typeof(object)));
        Assert.Equal(query.Count > 0, method.GetParameters().Any(p => p.ParameterType == typeof(IReadOnlyDictionary<string, object?>)));
        return new BuiltCall(pathNames, pathValues, body, query, arguments);
    }

    [Fact]
    public void CoversEveryOperationButTheMultipartOnesWhichHaveTheirOwnTests()
    {
        Assert.Equal(
            ["POST /contacts/imports", "PUT /brand/logo", "PUT /domains/{id}/unsubscribe-page/logo"],
            Spec.Operations.Where(o => o.Multipart).Select(o => o.Key).OrderBy(key => key, StringComparer.Ordinal));
    }

    private static MethodInfo Find(BillionSendClient client, string operationId, out object target)
    {
        object? current = client;
        var parts = operationId.Split('.');
        foreach (var part in parts[..^1])
        {
            current = current!.GetType().GetProperty(Pascal(part))!.GetValue(current);
        }

        var method = current!.GetType().GetMethod(Pascal(parts[^1]) + "Async");
        if (method is null && current.GetType().GetProperty(Pascal(parts[^1]))?.GetValue(current) is { } group)
        {
            // An operation that is also the group of others is that group's GetAsync (emails.metrics).
            current = group;
            method = group.GetType().GetMethod("GetAsync");
        }

        target = current;
        return method ?? throw new MissingMethodException(operationId);
    }

    private static string Pascal(string name) => char.ToUpperInvariant(name[0]) + name[1..];

    private static System.Text.RegularExpressions.Regex Regex() => new(@"\{([^}]+)\}");

    private static object? ToClr(JsonNode? node) => node switch
    {
        null => null,
        JsonArray array => array.Select(ToClr).ToList(),
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
        JsonValue value when value.TryGetValue<long>(out var integer) => integer,
        JsonValue value => value.GetValue<double>(),
        _ => node,
    };

    /// <summary>A query value as it goes on the wire, written here from the standard's rules (style form, no explode).</summary>
    private static string Wire(JsonNode? node) => node switch
    {
        JsonArray array => string.Join(",", array.Select(Wire)),
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonValue value when value.TryGetValue<bool>(out var flag) => flag ? "true" : "false",
        JsonValue value when value.TryGetValue<long>(out var integer) => integer.ToString(CultureInfo.InvariantCulture),
        JsonValue value => value.GetValue<double>().ToString(CultureInfo.InvariantCulture),
        _ => node!.ToJsonString(),
    };
}
