using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BillionSend.Internal
{
    /// <summary>One operation call, as the generated resource methods describe it.</summary>
    internal sealed class ApiCall
    {
        public static readonly IReadOnlyList<string?> NoPath = Array.Empty<string?>();

        public ApiCall(string method, string pathTemplate, IReadOnlyList<string?> pathValues)
        {
            Method = method;
            PathTemplate = pathTemplate;
            PathValues = pathValues;
        }

        public string Method { get; }

        public string PathTemplate { get; }

        public IReadOnlyList<string?> PathValues { get; }

        /// <summary>The JSON body, or <see langword="null"/> for none.</summary>
        public object? Body { get; init; }

        /// <summary>The body is a <c>multipart/form-data</c> form: a dictionary of fields and files.</summary>
        public bool Multipart { get; init; }

        public IReadOnlyDictionary<string, object?>? Query { get; init; }

        /// <summary>The operation honours <c>Idempotency-Key</c>: one is always sent.</summary>
        public bool Idempotent { get; init; }

        /// <summary><see langword="false"/> for the few operations that take no API key.</summary>
        public bool Auth { get; init; } = true;

        public BatchValidation? BatchValidation { get; init; }
    }

    /// <summary>Path and query encoding.</summary>
    internal static class Urls
    {
        private static readonly Regex Placeholder = new(@"\{([^}]+)\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>Fills <c>{name}</c> placeholders in order, or explains which parameter is missing or empty.</summary>
        public static string FillPath(string template, IReadOnlyList<string?> values)
        {
            var index = 0;
            string? missing = null;
            string? invalid = null;
            var filled = Placeholder.Replace(template, match =>
            {
                var value = index < values.Count ? values[index] : null;
                index++;
                if (string.IsNullOrEmpty(value))
                {
                    missing ??= match.Groups[1].Value;
                    return string.Empty;
                }

                // "." and ".." would be folded away by the URL parser, and so call another route.
                if (value is "." or "..")
                {
                    invalid ??= match.Groups[1].Value;
                    return string.Empty;
                }

                return Uri.EscapeDataString(value);
            });
            if (missing is not null)
            {
                throw new BillionSendException($"Missing path parameter \"{missing}\"");
            }

            if (invalid is not null)
            {
                throw new BillionSendException($"Invalid path parameter \"{invalid}\": a dot segment");
            }

            return filled;
        }

        /// <summary>
        /// The query string with its leading <c>?</c>, or empty. Unset values are dropped and lists
        /// go comma-separated in one key (style form, explode false), as the document declares them.
        /// </summary>
        public static string QueryString(IReadOnlyDictionary<string, object?>? query)
        {
            if (query is null || query.Count == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            foreach (var pair in query)
            {
                if (pair.Value is null)
                {
                    continue;
                }

                string text;
                if (pair.Value is not string && pair.Value is not JsonNode && pair.Value is IEnumerable list)
                {
                    text = string.Join(",", list.Cast<object?>().Where(item => item is not null).Select(item => Format(item!)));
                }
                else
                {
                    text = Format(pair.Value);
                }

                builder.Append(builder.Length == 0 ? '?' : '&');
                builder.Append(Uri.EscapeDataString(pair.Key)).Append('=').Append(Uri.EscapeDataString(text));
            }

            return builder.ToString();
        }

        /// <summary>One query value as text: booleans as <c>true</c> / <c>false</c>, numbers and dates invariant.</summary>
        public static string Format(object value)
        {
            switch (value)
            {
                case string text:
                    return text;
                case bool flag:
                    return flag ? "true" : "false";
                case DateTimeOffset at:
                    return at.ToString("O", CultureInfo.InvariantCulture);
                case DateTime at:
                    return at.ToString("O", CultureInfo.InvariantCulture);
                case Enum named:
                    return named.ToString();
                case JsonValue json:
                    return json.TryGetValue<string>(out var inner) ? inner : json.ToJsonString();
                case JsonNode node:
                    return node.ToJsonString();
                case IFormattable formattable:
                    return formattable.ToString(null, CultureInfo.InvariantCulture);
                default:
                    return value.ToString() ?? string.Empty;
            }
        }
    }

    /// <summary>When and how long to wait before retrying.</summary>
    internal static class Retry
    {
        /// <summary>A <c>retry-after</c> longer than this is not waited for: the error is thrown instead.</summary>
        public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

        private const double BaseDelayMs = 500;
        private const double MaxBackoffMs = 8_000;

        /// <summary>Methods safe to repeat without an <c>Idempotency-Key</c>.</summary>
        public static readonly IReadOnlySet<string> IdempotentMethods =
            new HashSet<string>(StringComparer.Ordinal) { "GET", "HEAD", "OPTIONS", "PUT", "DELETE" };

        /// <summary>
        /// How long to wait before retry number <paramref name="attempt"/> + 1: the server's
        /// <c>retry-after</c> (seconds or an HTTP date) when it sent one, otherwise an exponential
        /// backoff with jitter. <see langword="null"/> means "do not retry", which is the answer for
        /// a <c>retry-after</c> longer than <see cref="MaxRetryAfter"/>.
        /// </summary>
        public static TimeSpan? DelayFor(int attempt, string? retryAfter, DateTimeOffset now, Func<double> random)
        {
            var seconds = ParseRetryAfter(retryAfter, now);
            if (seconds is not null)
            {
                var ms = Math.Ceiling(seconds.Value * 1000);
                return ms > MaxRetryAfter.TotalMilliseconds ? null : TimeSpan.FromMilliseconds(ms);
            }

            var backoff = Math.Min(BaseDelayMs * Math.Pow(2, attempt), MaxBackoffMs);
            return TimeSpan.FromMilliseconds(Math.Round(backoff * (1 - (random() * 0.25))));
        }

        /// <summary>Seconds from a <c>retry-after</c> value (seconds or an HTTP date), or <see langword="null"/>.</summary>
        public static double? ParseRetryAfter(string? value, DateTimeOffset now)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
                double.IsFinite(seconds))
            {
                return Math.Max(0, seconds);
            }

            if (DateTimeOffset.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces,
                    out var at))
            {
                return Math.Max(0, (at - now).TotalSeconds);
            }

            return null;
        }

        /// <summary>
        /// Whether a failed attempt may be retried: a 429, a 5xx, a timeout or a network failure;
        /// and, for a request that carries an <c>Idempotency-Key</c>, the
        /// <c>409 concurrent_idempotent_requests</c> the API answers while the first attempt with
        /// that key is still being processed (other 409s are final).
        /// </summary>
        public static bool IsRetryable(BillionSendException error, bool keyed)
        {
            if (error is ApiConnectionException)
            {
                return true;
            }

            if (error.StatusCode is not int status)
            {
                return false;
            }

            if (status == 429 || status >= 500)
            {
                return true;
            }

            return keyed && status == 409 && error.Name == "concurrent_idempotent_requests";
        }
    }

    /// <summary>Maps an answer to its error type.</summary>
    internal static class Errors
    {
        public static BillionSendException FromResponse(
            int status,
            string? reasonPhrase,
            string text,
            string? requestId,
            string? retryAfter,
            DateTimeOffset now)
        {
            var name = BillionSendException.ApplicationError;
            string? message = null;
            try
            {
                if (text.Length > 0 &&
                    JsonNode.Parse(text) is JsonObject body &&
                    body["name"] is JsonValue nameValue && nameValue.TryGetValue<string>(out var apiName) &&
                    body["message"] is JsonValue messageValue && messageValue.TryGetValue<string>(out var apiMessage))
                {
                    name = apiName;
                    message = apiMessage;
                }
            }
            catch (JsonException)
            {
                // Not the API's error shape: described below.
            }

            if (message is null)
            {
                var detail = text.Trim();
                if (detail.Length > 200)
                {
                    detail = detail.Substring(0, 200);
                }

                var label = string.IsNullOrEmpty(reasonPhrase) ? $"{status}" : $"{status} {reasonPhrase}";
                message = detail.Length > 0 ? $"Unexpected {label} response: {detail}" : $"Unexpected {label} response";
            }

            BillionSendException error = status switch
            {
                400 or 422 => new ValidationException(message, name, status, requestId),
                401 => new AuthenticationException(message, name, status, requestId),
                403 => new PermissionDeniedException(message, name, status, requestId),
                404 => new NotFoundException(message, name, status, requestId),
                409 => new ConflictException(message, name, status, requestId),
                429 => new RateLimitException(message, name, status, requestId, Retry.ParseRetryAfter(retryAfter, now)),
                >= 500 => new ApiException(message, name, status, requestId),
                _ => new BillionSendException(message, name, status, requestId),
            };
            error.RetryAfterHeader = retryAfter;
            return error;
        }
    }
}
