using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace BillionSend.Internal
{
    /// <summary>The HTTP layer shared by every resource: headers, retries, timeouts and errors.</summary>
    internal sealed class ApiClient : IDisposable
    {
        internal const string UserAgent = "billionsend-dotnet/" + BillionSendClient.SdkVersion;

        private static readonly JsonSerializerOptions DefaultJson = new(JsonSerializerDefaults.General)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        };

        private readonly string? _apiKey;
        private readonly HttpClient _http;
        private readonly bool _ownsHttp;
        private readonly Dictionary<string, string> _headers;
        private readonly JsonSerializerOptions _json;

        public ApiClient(BillionSendOptions options)
        {
            _apiKey = NonEmpty(options.ApiKey) ?? NonEmpty(Environment.GetEnvironmentVariable("BILLIONSEND_API_KEY"));
            var baseUrl = NonEmpty(options.BaseUrl) ??
                NonEmpty(Environment.GetEnvironmentVariable("BILLIONSEND_BASE_URL")) ??
                BillionSendClient.DefaultBaseUrl;
            BaseUrl = baseUrl.TrimEnd('/');
            if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var parsed) ||
                (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
            {
                throw new ArgumentException($"The base URL is not an http(s) URL: {baseUrl}", nameof(options));
            }

            Timeout = options.Timeout ?? BillionSendClient.DefaultTimeout;
            if (Timeout <= TimeSpan.Zero || Timeout.TotalMilliseconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "Timeout must be positive");
            }

            MaxRetries = options.MaxRetries ?? BillionSendClient.DefaultMaxRetries;
            if (MaxRetries < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "MaxRetries must be 0 or more");
            }

            _headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Merge(_headers, options.Headers);
            _json = options.JsonSerializerOptions ?? DefaultJson;
            if (options.HttpClient is null)
            {
                _http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
                {
                    // Each attempt is timed by the client itself, and an open event stream is not timed at all.
                    Timeout = System.Threading.Timeout.InfiniteTimeSpan,
                };
                _ownsHttp = true;
            }
            else
            {
                _http = options.HttpClient;
            }
        }

        public string BaseUrl { get; }

        public TimeSpan Timeout { get; }

        public int MaxRetries { get; }

        public bool HasApiKey => _apiKey is not null;

        // Seams for the tests: waiting, jitter, the clock and new keys.
        internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

        internal Func<double> Random { get; set; } = System.Random.Shared.NextDouble;

        internal Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

        internal static string NewIdempotencyKey() => Guid.NewGuid().ToString();

        /// <summary>Calls one operation and parses its JSON answer.</summary>
        public async Task<JsonNode?> RequestAsync(ApiCall call, RequestOptions? options, CancellationToken cancellationToken)
        {
            var prepared = Prepare(call, options, stream: false, lastEventId: null);
            var maxRetries = options?.MaxRetries ?? MaxRetries;
            var timeout = options?.Timeout ?? Timeout;
            for (var attempt = 0; ; attempt++)
            {
                BillionSendException error;
                try
                {
                    return await JsonAttemptAsync(prepared, timeout, cancellationToken).ConfigureAwait(false);
                }
                catch (BillionSendException exception)
                {
                    error = exception;
                }

                if (!await ShouldRetryAsync(prepared, error, attempt, maxRetries, cancellationToken).ConfigureAwait(false))
                {
                    ExceptionDispatchInfo.Capture(error).Throw();
                }
            }
        }

        /// <summary>
        /// Opens an event stream, resuming after <paramref name="lastEventId"/>; the connection is
        /// timed only until the response headers arrive.
        /// </summary>
        public async Task<StreamConnection> OpenStreamAsync(
            ApiCall call,
            string? lastEventId,
            RequestOptions? options,
            int maxRetries,
            CancellationToken cancellationToken)
        {
            var prepared = Prepare(call, options, stream: true, lastEventId);
            var timeout = options?.Timeout ?? Timeout;
            for (var attempt = 0; ; attempt++)
            {
                BillionSendException error;
                try
                {
                    return await StreamAttemptAsync(prepared, timeout, cancellationToken).ConfigureAwait(false);
                }
                catch (BillionSendException exception)
                {
                    error = exception;
                }

                if (!await ShouldRetryAsync(prepared, error, attempt, maxRetries, cancellationToken).ConfigureAwait(false))
                {
                    ExceptionDispatchInfo.Capture(error).Throw();
                }
            }
        }

        /// <summary>Opens an inbox event stream for a generated resource method.</summary>
        public async Task<EventStream> OpenEventStreamAsync(
            ApiCall call,
            IReadOnlyCollection<string> endsOn,
            EventStreamOptions? options,
            CancellationToken cancellationToken)
        {
            options ??= new EventStreamOptions();
            var first = await OpenStreamAsync(
                call,
                options.LastEventId,
                options,
                options.MaxRetries ?? MaxRetries,
                cancellationToken).ConfigureAwait(false);
            return new EventStream(
                first,
                (lastEventId, token) => OpenStreamAsync(call, lastEventId, options, 0, token),
                options,
                endsOn,
                cancellationToken);
        }

        public void Dispose()
        {
            if (_ownsHttp)
            {
                _http.Dispose();
            }
        }

        private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

        private static void Merge(Dictionary<string, string> into, IDictionary<string, string>? from)
        {
            if (from is null)
            {
                return;
            }

            foreach (var pair in from)
            {
                into[pair.Key] = pair.Value;
            }
        }

        private async Task<bool> ShouldRetryAsync(
            Prepared prepared,
            BillionSendException error,
            int attempt,
            int maxRetries,
            CancellationToken cancellationToken)
        {
            if (!prepared.Retryable || attempt >= maxRetries || !Retry.IsRetryable(error, prepared.Keyed))
            {
                return false;
            }

            var delay = Retry.DelayFor(attempt, error.RetryAfterHeader, Now(), Random);
            if (delay is null)
            {
                return false;
            }

            await Delay(delay.Value, cancellationToken).ConfigureAwait(false);
            return true;
        }

        private Prepared Prepare(ApiCall call, RequestOptions? options, bool stream, string? lastEventId)
        {
            if (call.Auth && _apiKey is null)
            {
                throw new AuthenticationException(
                    "No API key: pass one to new BillionSendClient() or set BILLIONSEND_API_KEY",
                    "missing_api_key");
            }

            var url = new Uri(BaseUrl + Urls.FillPath(call.PathTemplate, call.PathValues) + Urls.QueryString(call.Query));
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Accept"] = stream ? "text/event-stream" : "application/json",
                ["User-Agent"] = UserAgent,
            };
            Merge(headers, _headers);
            Merge(headers, options?.Headers);
            if (call.BatchValidation is BatchValidation validation)
            {
                headers["x-batch-validation"] = validation == BatchValidation.Permissive ? "permissive" : "strict";
            }

            if (lastEventId is not null)
            {
                headers["Last-Event-ID"] = lastEventId;
            }

            byte[]? body = null;
            string contentType = "application/json";
            if (call.Body is not null && call.Multipart)
            {
                (body, contentType) = Multipart.Encode(call.Body, _json);
            }
            else if (call.Body is not null)
            {
                body = JsonSerializer.SerializeToUtf8Bytes(call.Body, call.Body.GetType(), _json);
            }

            if (options is IdempotentRequestOptions { IdempotencyKey: { } key })
            {
                headers["Idempotency-Key"] = key;
            }
            else if (call.Idempotent && !headers.ContainsKey("Idempotency-Key"))
            {
                headers["Idempotency-Key"] = NewIdempotencyKey();
            }

            if (call.Auth)
            {
                headers["Authorization"] = $"Bearer {_apiKey}";
            }
            else
            {
                headers.Remove("Authorization");
            }

            var keyed = headers.ContainsKey("Idempotency-Key");
            return new Prepared(
                new HttpMethod(call.Method),
                url,
                headers,
                body,
                contentType,
                keyed,
                Retry.IdempotentMethods.Contains(call.Method) || keyed);
        }

        private static HttpRequestMessage BuildRequest(Prepared prepared)
        {
            var request = new HttpRequestMessage(prepared.Method, prepared.Url);
            if (prepared.Body is not null)
            {
                request.Content = new ByteArrayContent(prepared.Body);
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(prepared.ContentType);
            }

            foreach (var pair in prepared.Headers)
            {
                if (request.Headers.TryAddWithoutValidation(pair.Key, pair.Value))
                {
                    continue;
                }

                if (request.Content is not null)
                {
                    request.Content.Headers.Remove(pair.Key);
                    request.Content.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
                }
            }

            return request;
        }

        private async Task<JsonNode?> JsonAttemptAsync(Prepared prepared, TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = BuildRequest(prepared);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            try
            {
                using var response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                var requestId = Header(response, "x-request-id");
                if (!response.IsSuccessStatusCode)
                {
                    throw Errors.FromResponse(
                        (int)response.StatusCode,
                        response.ReasonPhrase,
                        text,
                        requestId,
                        Header(response, "Retry-After"),
                        Now());
                }

                try
                {
                    return JsonNode.Parse(text);
                }
                catch (JsonException exception)
                {
                    throw new BillionSendException(
                        "The response body is not valid JSON",
                        statusCode: (int)response.StatusCode,
                        requestId: requestId,
                        innerException: exception);
                }
            }
            catch (Exception exception) when (exception is not BillionSendException)
            {
                throw Translate(exception, timeout, cancellationToken);
            }
        }

        private async Task<StreamConnection> StreamAttemptAsync(Prepared prepared, TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = BuildRequest(prepared);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            HttpResponseMessage? response = null;
            try
            {
                cts.CancelAfter(timeout);
                response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);
                var requestId = Header(response, "x-request-id");
                if (!response.IsSuccessStatusCode)
                {
                    var text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                    throw Errors.FromResponse(
                        (int)response.StatusCode,
                        response.ReasonPhrase,
                        text,
                        requestId,
                        Header(response, "Retry-After"),
                        Now());
                }

                var contentType = response.Content.Headers.ContentType?.MediaType;
                if (!string.Equals(contentType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
                {
                    throw new BillionSendException(
                        $"Expected an event stream, got {contentType ?? "no content type"}",
                        statusCode: (int)response.StatusCode,
                        requestId: requestId);
                }

                // Open: from here on only the caller (or closing the stream) ends it.
                cts.CancelAfter(System.Threading.Timeout.InfiniteTimeSpan);
                var body = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                var connection = new StreamConnection(response, body, cts);
                response = null;
                cts = null;
                return connection;
            }
            catch (Exception exception) when (exception is not BillionSendException)
            {
                throw Translate(exception, timeout, cancellationToken);
            }
            finally
            {
                response?.Dispose();
                cts?.Dispose();
            }
        }

        /// <summary>
        /// The caller's cancellation goes through as is; a timeout becomes
        /// <see cref="ApiTimeoutException"/> and a transport failure <see cref="ApiConnectionException"/>.
        /// </summary>
        private static Exception Translate(Exception exception, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return exception as OperationCanceledException ?? new OperationCanceledException(cancellationToken);
            }

            return exception switch
            {
                OperationCanceledException => new ApiTimeoutException(
                    $"Request timed out after {timeout.TotalMilliseconds:0} ms",
                    exception),
                HttpRequestException or IOException => new ApiConnectionException(
                    $"Network error: {Describe(exception)}",
                    exception),
                _ => exception,
            };
        }

        private static string Describe(Exception exception) =>
            exception.InnerException is { } inner ? $"{exception.Message} ({inner.Message})" : exception.Message;

        private static string? Header(HttpResponseMessage response, string name)
        {
            if (response.Headers.TryGetValues(name, out var values) ||
                response.Content.Headers.TryGetValues(name, out values))
            {
                return values.FirstOrDefault();
            }

            return null;
        }

        private sealed record Prepared(
            HttpMethod Method,
            Uri Url,
            IReadOnlyDictionary<string, string> Headers,
            byte[]? Body,
            string ContentType,
            bool Keyed,
            bool Retryable);
    }

    /// <summary>An open <c>text/event-stream</c> response.</summary>
    internal sealed class StreamConnection : IDisposable
    {
        private readonly HttpResponseMessage _response;
        private readonly CancellationTokenSource _lifetime;
        private int _closed;

        public StreamConnection(HttpResponseMessage response, Stream body, CancellationTokenSource lifetime)
        {
            _response = response;
            Body = body;
            _lifetime = lifetime;
        }

        public Stream Body { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 1)
            {
                return;
            }

            try
            {
                _lifetime.Cancel();
            }
            catch (AggregateException)
            {
                // A callback failed while cancelling; the connection is closed regardless.
            }

            Body.Dispose();
            _response.Dispose();
            _lifetime.Dispose();
        }
    }
}
