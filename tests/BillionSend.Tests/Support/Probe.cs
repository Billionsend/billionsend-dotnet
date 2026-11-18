using System.Net;
using System.Text;

namespace BillionSend.Tests.Support;

/// <summary>
/// A bare HTTP handler for tests about the wire itself (encoding, bytes, headers): it records what
/// was sent and answers what the test says. The contract mock is the place for tests about the API.
/// </summary>
public sealed class Probe : HttpMessageHandler
{
    public sealed record Seen(string Method, string Target, IReadOnlyDictionary<string, string> Headers, byte[]? Body)
    {
        public string? Header(string name) => Headers.TryGetValue(name.ToLowerInvariant(), out var value) ? value : null;

        public string Text => Body is null ? string.Empty : Encoding.UTF8.GetString(Body);
    }

    private readonly List<Seen> _requests = [];

    public IReadOnlyList<Seen> Requests => _requests;

    /// <summary>What to answer; a 200 with <c>{}</c> by default.</summary>
    public Func<Seen, HttpResponseMessage> Answer { get; set; } = _ => Reply(200, "{}");

    public static HttpResponseMessage Reply(int status, string json)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        response.Headers.TryAddWithoutValidation("x-request-id", "req_probe");
        return response;
    }

    public BillionSendClient Client(Action<BillionSendOptions>? configure = null)
    {
        var options = new BillionSendOptions
        {
            ApiKey = "bs_probe",
            BaseUrl = "https://probe.test",
            HttpClient = new HttpClient(this, disposeHandler: false),
        };
        configure?.Invoke(options);
        var client = new BillionSendClient(options);
        client.Api.Delay = (_, _) => Task.CompletedTask;
        return client;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>();
        foreach (var header in request.Headers)
        {
            headers[header.Key.ToLowerInvariant()] = string.Join(", ", header.Value);
        }

        byte[]? body = null;
        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
            {
                headers[header.Key.ToLowerInvariant()] = string.Join(", ", header.Value);
            }

            if (request.Content.Headers.ContentLength is { } length)
            {
                headers["content-length"] = length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            body = await request.Content.ReadAsByteArrayAsync(cancellationToken);
        }

        var seen = new Seen(request.Method.Method, request.RequestUri!.PathAndQuery, headers, body);
        _requests.Add(seen);
        return Answer(seen);
    }
}
