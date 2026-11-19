using System;

namespace BillionSend
{
    /// <summary>
    /// Every error the SDK throws. Tell the kinds apart by type: <see cref="ValidationException"/>,
    /// <see cref="AuthenticationException"/>, <see cref="PermissionDeniedException"/>,
    /// <see cref="NotFoundException"/>, <see cref="ConflictException"/>,
    /// <see cref="RateLimitException"/>, <see cref="ApiException"/>,
    /// <see cref="ApiConnectionException"/>, <see cref="ApiTimeoutException"/> and
    /// <see cref="WebhookVerificationException"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="Name"/> is the API's error name (<c>validation_error</c>, <c>invalid_api_key</c>,
    /// …), not the type name; failures that never got an API answer use <c>application_error</c>,
    /// with <see cref="StatusCode"/> <see langword="null"/> when there was no HTTP response at all.
    /// <see cref="RequestId"/> is the answer's <c>x-request-id</c> (the id of the request on the
    /// dashboard's Logs page), <see langword="null"/> without one.
    /// </remarks>
    public class BillionSendException : Exception
    {
        /// <summary>The name used when the API sent none.</summary>
        public const string ApplicationError = "application_error";

        /// <summary>Creates an error.</summary>
        /// <param name="message">The message.</param>
        /// <param name="name">The API's error name; <c>application_error</c> when <see langword="null"/>.</param>
        /// <param name="statusCode">The HTTP status, or <see langword="null"/> without an answer.</param>
        /// <param name="requestId">The answer's <c>x-request-id</c>.</param>
        /// <param name="innerException">The cause, if any.</param>
        public BillionSendException(
            string message,
            string? name = null,
            int? statusCode = null,
            string? requestId = null,
            Exception? innerException = null)
            : base(message, innerException)
        {
            Name = name ?? ApplicationError;
            StatusCode = statusCode;
            RequestId = requestId;
        }

        /// <summary>The API's error name, such as <c>validation_error</c>; <c>application_error</c> when the API sent none.</summary>
        public string Name { get; }

        /// <summary>The HTTP status, or <see langword="null"/> when there was no answer.</summary>
        public int? StatusCode { get; }

        /// <summary>The answer's <c>x-request-id</c>, or <see langword="null"/>.</summary>
        public string? RequestId { get; }

        /// <summary>The raw <c>retry-after</c> of the answer, for the retry loop.</summary>
        internal string? RetryAfterHeader { get; set; }
    }

    /// <summary>400 or 422: the request was refused as invalid.</summary>
    public class ValidationException : BillionSendException
    {
        /// <inheritdoc cref="BillionSendException(string, string?, int?, string?, Exception?)"/>
        public ValidationException(string message, string? name = null, int? statusCode = null, string? requestId = null)
            : base(message, name, statusCode, requestId)
        {
        }
    }

    /// <summary>
    /// 401, or no API key configured (<c>missing_api_key</c>, <see cref="BillionSendException.StatusCode"/>
    /// <see langword="null"/>): the key is missing, malformed or revoked.
    /// </summary>
    public class AuthenticationException : BillionSendException
    {
        /// <inheritdoc cref="BillionSendException(string, string?, int?, string?, Exception?)"/>
        public AuthenticationException(string message, string? name = null, int? statusCode = null, string? requestId = null)
            : base(message, name, statusCode, requestId)
        {
        }
    }

    /// <summary>
    /// 403: the key, its scope, the role or the plan does not allow this. <see cref="BillionSendException.Name"/>
    /// tells which: <c>sending_paused</c> is the whole team paused by its deliverability,
    /// <c>domain_sending_paused</c> is one sending domain paused (the team's other domains keep
    /// sending), <c>restricted_api_key</c> a key that may not do this.
    /// </summary>
    public class PermissionDeniedException : BillionSendException
    {
        /// <inheritdoc cref="BillionSendException(string, string?, int?, string?, Exception?)"/>
        public PermissionDeniedException(string message, string? name = null, int? statusCode = null, string? requestId = null)
            : base(message, name, statusCode, requestId)
        {
        }
    }

    /// <summary>404: no such resource or endpoint.</summary>
    public class NotFoundException : BillionSendException
    {
        /// <inheritdoc cref="BillionSendException(string, string?, int?, string?, Exception?)"/>
        public NotFoundException(string message, string? name = null, int? statusCode = null, string? requestId = null)
            : base(message, name, statusCode, requestId)
        {
        }
    }

    /// <summary>409: the request conflicts with the current state, or with an earlier use of its idempotency key.</summary>
    public class ConflictException : BillionSendException
    {
        /// <inheritdoc cref="BillionSendException(string, string?, int?, string?, Exception?)"/>
        public ConflictException(string message, string? name = null, int? statusCode = null, string? requestId = null)
            : base(message, name, statusCode, requestId)
        {
        }
    }

    /// <summary>429: a rate limit, a sending quota or a stream cap.</summary>
    public class RateLimitException : BillionSendException
    {
        /// <summary>Creates a rate-limit error.</summary>
        /// <param name="message">The message.</param>
        /// <param name="name">The API's error name.</param>
        /// <param name="statusCode">The HTTP status.</param>
        /// <param name="requestId">The answer's <c>x-request-id</c>.</param>
        /// <param name="retryAfter">The server's hint, in seconds.</param>
        public RateLimitException(
            string message,
            string? name = null,
            int? statusCode = null,
            string? requestId = null,
            double? retryAfter = null)
            : base(message, name, statusCode, requestId)
        {
            RetryAfter = retryAfter;
        }

        /// <summary>The server's <c>retry-after</c> hint in seconds, or <see langword="null"/>.</summary>
        public double? RetryAfter { get; }
    }

    /// <summary>5xx: the API failed to answer the request.</summary>
    public class ApiException : BillionSendException
    {
        /// <inheritdoc cref="BillionSendException(string, string?, int?, string?, Exception?)"/>
        public ApiException(string message, string? name = null, int? statusCode = null, string? requestId = null)
            : base(message, name, statusCode, requestId)
        {
        }
    }

    /// <summary>
    /// The request never got an answer: DNS, connection, TLS or protocol failure.
    /// <see cref="BillionSendException.StatusCode"/> is <see langword="null"/>.
    /// </summary>
    public class ApiConnectionException : BillionSendException
    {
        /// <summary>Creates a connection error.</summary>
        /// <param name="message">The message.</param>
        /// <param name="innerException">The cause, if any.</param>
        public ApiConnectionException(string message = "Connection error", Exception? innerException = null)
            : base(message, innerException: innerException)
        {
        }
    }

    /// <summary>The request timed out before the answer arrived.</summary>
    public class ApiTimeoutException : ApiConnectionException
    {
        /// <summary>Creates a timeout error.</summary>
        /// <param name="message">The message.</param>
        /// <param name="innerException">The cause, if any.</param>
        public ApiTimeoutException(string message = "Request timed out", Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }

    /// <summary>A webhook whose signature, timestamp or body did not verify (<see cref="Webhook.Verify(string, System.Collections.Generic.IEnumerable{System.Collections.Generic.KeyValuePair{string, string}}, string, WebhookVerifyOptions?)"/>).</summary>
    public class WebhookVerificationException : BillionSendException
    {
        /// <summary>Creates a verification error, named <c>webhook_verification_error</c>.</summary>
        /// <param name="message">The message.</param>
        public WebhookVerificationException(string message)
            : base(message, "webhook_verification_error")
        {
        }
    }
}
