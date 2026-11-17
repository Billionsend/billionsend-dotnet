using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BillionSend
{
    /// <summary>Options of <see cref="Webhook.Verify(string, IEnumerable{KeyValuePair{string, string}}, string, WebhookVerifyOptions?)"/>.</summary>
    public sealed class WebhookVerifyOptions
    {
        /// <summary>How far the <c>webhook-timestamp</c> may be from now (default 300 seconds).</summary>
        public TimeSpan? Tolerance { get; set; }

        /// <summary>The time to check the timestamp against (default: now).</summary>
        public DateTimeOffset? Now { get; set; }
    }

    /// <summary>Verifies webhook deliveries.</summary>
    /// <remarks>
    /// The signature is HMAC-SHA256 over <c>&lt;webhook-id&gt;.&lt;webhook-timestamp&gt;.&lt;body&gt;</c>
    /// with the endpoint's <c>whsec_</c> secret, sent as <c>v1,&lt;base64&gt;</c> in
    /// <c>webhook-signature</c>; during a secret rotation the header carries one signature per
    /// secret, space-separated, and any of them matching is enough. The timestamp (Unix seconds)
    /// must be within the tolerance of now, so a captured delivery cannot be replayed later.
    /// Pass the body exactly as it arrived (the raw text or bytes, before any JSON parsing):
    /// re-serialized JSON does not verify.
    /// </remarks>
    public static class Webhook
    {
        /// <summary>The default tolerance, in seconds.</summary>
        public const int DefaultToleranceSeconds = 300;

        private const string SecretPrefix = "whsec_";
        private const int SecretMinBytes = 24;
        private const int SecretMaxBytes = 64;

        /// <summary>Verifies a webhook delivery and returns its parsed body.</summary>
        /// <param name="payload">The raw request body.</param>
        /// <param name="headers">The request headers (any case).</param>
        /// <param name="secret">The endpoint's <c>whsec_</c> secret.</param>
        /// <param name="options">The tolerance and the clock, or <see langword="null"/>.</param>
        /// <returns>The parsed event: <c>{ type, created_at, data }</c>.</returns>
        /// <exception cref="WebhookVerificationException">A header is missing, the timestamp is out of tolerance, no signature matches, the secret is malformed, or the body is not JSON.</exception>
        public static JsonNode Verify(
            string payload,
            IEnumerable<KeyValuePair<string, string>> headers,
            string secret,
            WebhookVerifyOptions? options = null) =>
            Verify(Encoding.UTF8.GetBytes(payload ?? throw new ArgumentNullException(nameof(payload))), Lookup(headers), secret, options);

        /// <inheritdoc cref="Verify(string, IEnumerable{KeyValuePair{string, string}}, string, WebhookVerifyOptions?)"/>
        public static JsonNode Verify(
            byte[] payload,
            IEnumerable<KeyValuePair<string, string>> headers,
            string secret,
            WebhookVerifyOptions? options = null) =>
            Verify(payload, Lookup(headers), secret, options);

        /// <summary>
        /// Verifies a webhook delivery and returns its parsed body, reading headers through a
        /// lookup: in ASP.NET Core, <c>name =&gt; Request.Headers[name]</c>.
        /// </summary>
        /// <param name="payload">The raw request body.</param>
        /// <param name="header">Returns a request header's value by name, or <see langword="null"/>.</param>
        /// <param name="secret">The endpoint's <c>whsec_</c> secret.</param>
        /// <param name="options">The tolerance and the clock, or <see langword="null"/>.</param>
        /// <returns>The parsed event: <c>{ type, created_at, data }</c>.</returns>
        /// <exception cref="WebhookVerificationException">A header is missing, the timestamp is out of tolerance, no signature matches, the secret is malformed, or the body is not JSON.</exception>
        public static JsonNode Verify(
            string payload,
            Func<string, string?> header,
            string secret,
            WebhookVerifyOptions? options = null) =>
            Verify(Encoding.UTF8.GetBytes(payload ?? throw new ArgumentNullException(nameof(payload))), header, secret, options);

        /// <inheritdoc cref="Verify(string, Func{string, string?}, string, WebhookVerifyOptions?)"/>
        public static JsonNode Verify(
            byte[] payload,
            Func<string, string?> header,
            string secret,
            WebhookVerifyOptions? options = null)
        {
            if (payload is null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            if (header is null)
            {
                throw new ArgumentNullException(nameof(header));
            }

            var id = header("webhook-id");
            var timestamp = header("webhook-timestamp");
            var signature = header("webhook-signature");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(signature))
            {
                throw new WebhookVerificationException(
                    "Missing the webhook-id, webhook-timestamp or webhook-signature header");
            }

            if (!IsDigits(timestamp) || !long.TryParse(timestamp, out var seconds))
            {
                throw new WebhookVerificationException("Invalid webhook-timestamp header");
            }

            var tolerance = (long)(options?.Tolerance ?? TimeSpan.FromSeconds(DefaultToleranceSeconds)).TotalSeconds;
            var now = (options?.Now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
            if (now - seconds > tolerance)
            {
                throw new WebhookVerificationException("The webhook timestamp is too old");
            }

            if (seconds - now > tolerance)
            {
                throw new WebhookVerificationException("The webhook timestamp is too new");
            }

            var prefix = Encoding.UTF8.GetBytes($"{id}.{timestamp}.");
            var signed = new byte[prefix.Length + payload.Length];
            prefix.CopyTo(signed, 0);
            payload.CopyTo(signed, prefix.Length);
            var expected = HMACSHA256.HashData(SecretKey(secret), signed);

            var matched = false;
            foreach (var candidate in signature.Split(' '))
            {
                var comma = candidate.IndexOf(',');
                if (comma == -1 || candidate.Substring(0, comma) != "v1")
                {
                    continue;
                }

                var given = DecodeBase64(candidate.Substring(comma + 1));
                if (given is not null && CryptographicOperations.FixedTimeEquals(given, expected))
                {
                    matched = true;
                }
            }

            if (!matched)
            {
                throw new WebhookVerificationException("No webhook signature matches");
            }

            try
            {
                return JsonNode.Parse(payload) ?? throw new WebhookVerificationException("The webhook body is not JSON");
            }
            catch (JsonException)
            {
                throw new WebhookVerificationException("The webhook body is not JSON");
            }
        }

        /// <summary>The key of a <c>whsec_</c> secret: canonical base64 of 24 to 64 bytes, as the dashboard and the API hand it out.</summary>
        private static byte[] SecretKey(string secret)
        {
            var encoded = secret is not null && secret.StartsWith(SecretPrefix, StringComparison.Ordinal)
                ? secret.Substring(SecretPrefix.Length)
                : null;
            var key = encoded is null ? null : DecodeBase64(encoded);
            if (encoded is null ||
                key is null ||
                key.Length < SecretMinBytes ||
                key.Length > SecretMaxBytes ||
                Convert.ToBase64String(key) != encoded)
            {
                throw new WebhookVerificationException("The webhook secret is not a whsec_ secret");
            }

            return key;
        }

        private static byte[]? DecodeBase64(string text)
        {
            var buffer = new byte[((text.Length + 3) / 4) * 3];
            return Convert.TryFromBase64String(text, buffer, out var written) ? buffer.AsSpan(0, written).ToArray() : null;
        }

        private static bool IsDigits(string value)
        {
            foreach (var c in value)
            {
                if (c < '0' || c > '9')
                {
                    return false;
                }
            }

            return true;
        }

        private static Func<string, string?> Lookup(IEnumerable<KeyValuePair<string, string>> headers)
        {
            if (headers is null)
            {
                throw new ArgumentNullException(nameof(headers));
            }

            return name =>
            {
                foreach (var pair in headers)
                {
                    if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return pair.Value;
                    }
                }

                return null;
            };
        }
    }
}
