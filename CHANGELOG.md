# Changelog

## 0.1.0 (2026-11-17)

- First version of the BillionSend SDK for .NET: every operation of the BillionSend API (139), grouped by resource and named after each operation's `operationId` (`client.Emails.Receiving.Attachments.ListAsync`), generated from the OpenAPI document with its descriptions as doc comments. `emails.metrics`, which is also the group of `emails.metrics.links` / `countries` / `clients`, is `client.Emails.Metrics.GetAsync`.
- Async methods that take any serializable request body and return the answer as a `JsonNode`; query parameters as a dictionary, lists sent comma-separated in one parameter.
- Errors as exceptions: `ValidationException`, `AuthenticationException`, `PermissionDeniedException`, `NotFoundException`, `ConflictException`, `RateLimitException` (with `RetryAfter`), `ApiException`, `ApiConnectionException` and `ApiTimeoutException`, all `BillionSendException`s with `StatusCode`, `Name`, `Message` and `RequestId` (the answer's `x-request-id`).
- The sending operations always carry an `Idempotency-Key`: the caller's, or a new one per call that its retries reuse.
- Retries for 429, 5xx, timeouts, network failures and `409 concurrent_idempotent_requests`, on requests safe to repeat; `Retry-After` honoured, exponential backoff otherwise; per-attempt timeouts; your own `HttpClient` if you pass one.
- `ListAllAsync` / `SearchAllAsync` iterators (`IAsyncEnumerable<JsonNode>`) over the 21 cursor-paginated lists.
- Inbox event streams (server-sent events) as `IAsyncEnumerable` of `InboxStreamEvent` and `StreamGapEvent`, resuming with `Last-Event-ID` after a dropped connection; `stream.gap` handed out once per position; one inbox's stream ends after its `inbox.deleted` or `inbox.expired`.
- `Webhook.Verify` checks a webhook delivery's signature and timestamp and returns the parsed event.
- `BILLIONSEND_API_KEY` and `BILLIONSEND_BASE_URL`; no dependencies beyond the .NET runtime; .NET 8 or later.
