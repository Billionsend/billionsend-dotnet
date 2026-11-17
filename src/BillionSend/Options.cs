using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;

namespace BillionSend
{
    /// <summary>
    /// Options of a <see cref="BillionSendClient"/>. Everything is optional.
    /// </summary>
    public sealed class BillionSendOptions
    {
        /// <summary>The API key (<c>bs_...</c>). Defaults to the <c>BILLIONSEND_API_KEY</c> environment variable.</summary>
        public string? ApiKey { get; set; }

        /// <summary>The API address. Defaults to <c>BILLIONSEND_BASE_URL</c>, then <c>https://api.billionsend.com</c>.</summary>
        public string? BaseUrl { get; set; }

        /// <summary>
        /// The timeout of each attempt (default 60 seconds). An event stream is timed only until it
        /// opens.
        /// </summary>
        public TimeSpan? Timeout { get; set; }

        /// <summary>
        /// Retries for 429, 5xx, timeouts and network failures (default 2). Only requests safe to
        /// repeat are retried: <c>GET</c>, <c>PUT</c> and <c>DELETE</c>, and the sending operations,
        /// which always carry an <c>Idempotency-Key</c>.
        /// </summary>
        public int? MaxRetries { get; set; }

        /// <summary>
        /// The <see cref="System.Net.Http.HttpClient"/> to send requests with (its
        /// <c>BaseAddress</c> is not used). The client does not dispose it. Its own
        /// <c>Timeout</c> also applies; leave it at least as long as <see cref="Timeout"/>.
        /// </summary>
        public HttpClient? HttpClient { get; set; }

        /// <summary>Extra headers sent with every request.</summary>
        public IDictionary<string, string>? Headers { get; set; }

        /// <summary>
        /// How request bodies are serialized. The default writes your own types' property names in
        /// snake_case (<c>ReplyTo</c> becomes <c>reply_to</c>), the way the API names its fields;
        /// dictionary keys, <see cref="System.Text.Json.Nodes.JsonNode"/> objects and
        /// <c>[JsonPropertyName]</c> names are sent as they are.
        /// </summary>
        public JsonSerializerOptions? JsonSerializerOptions { get; set; }
    }

    /// <summary>Options of one call.</summary>
    public class RequestOptions
    {
        /// <summary>Extra headers for this request.</summary>
        public IDictionary<string, string>? Headers { get; set; }

        /// <summary>Overrides the client's per-attempt timeout for this request.</summary>
        public TimeSpan? Timeout { get; set; }

        /// <summary>Overrides the client's retry count for this request.</summary>
        public int? MaxRetries { get; set; }
    }

    /// <summary>Options of a call to an operation that honours <c>Idempotency-Key</c> (the sending operations).</summary>
    public class IdempotentRequestOptions : RequestOptions
    {
        /// <summary>
        /// Sent as the <c>Idempotency-Key</c> header (1 to 256 characters): a repeat with the same
        /// key and payload answers the first result instead of sending again. Without one the
        /// client makes a new key for each call (a UUID), and that call's retries reuse it; pass
        /// your own to make the send safe across calls and processes too, such as
        /// <c>$"welcome/{user.Id}"</c>.
        /// </summary>
        public string? IdempotencyKey { get; set; }
    }

    /// <summary>How a batch treats an invalid item (the <c>x-batch-validation</c> header).</summary>
    public enum BatchValidation
    {
        /// <summary><c>strict</c> (the API's default): one invalid item fails the whole batch.</summary>
        Strict,

        /// <summary><c>permissive</c>: invalid items are listed in <c>errors</c> and the valid ones go through.</summary>
        Permissive,
    }

    /// <summary>Options of a batch write (<c>Contacts.Batch.CreateAsync</c>).</summary>
    public sealed class BatchRequestOptions : RequestOptions
    {
        /// <summary>Sent as the <c>x-batch-validation</c> header when set.</summary>
        public BatchValidation? BatchValidation { get; set; }
    }

    /// <summary>Options of a batch send (<c>Emails.BatchAsync</c>).</summary>
    public sealed class BatchSendOptions : IdempotentRequestOptions
    {
        /// <summary>Sent as the <c>x-batch-validation</c> header when set.</summary>
        public BatchValidation? BatchValidation { get; set; }
    }

    /// <summary>Options of an inbox event stream.</summary>
    /// <remarks>
    /// <see cref="RequestOptions.Timeout"/> times each connection attempt until the stream opens,
    /// and <see cref="RequestOptions.MaxRetries"/> retries the first connection on 429, 5xx and
    /// network failures.
    /// </remarks>
    public sealed class EventStreamOptions : RequestOptions
    {
        /// <summary>Resume after this event id (sent as the <c>Last-Event-ID</c> header).</summary>
        public string? LastEventId { get; set; }

        /// <summary>Reconnect with <c>Last-Event-ID</c> when the connection drops (default <see langword="true"/>).</summary>
        public bool Reconnect { get; set; } = true;

        /// <summary>Reconnects in a row that fail to open before giving up (default 5); one that opens resets the count.</summary>
        public int MaxReconnects { get; set; } = 5;

        /// <summary>The wait before reconnecting (default 1 second; a server <c>retry</c> field overrides it).</summary>
        public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(1);
    }
}
