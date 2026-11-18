# Changelog

## 0.2.0 (2026-11-19)

- The OpenAPI snapshot is the published document again (180 operations; `scripts/sync_openapi.py --check` fails CI when they differ, on every push and daily), so the methods and the tests follow the API that is live: `GET /usage` answers `emails.daily` / `emails.monthly`, `contacts`, `domains` and the rest instead of `limits`.
- New operations: `Automations` (create, list, get, update, remove, validate, duplicate, simulate, stop, metrics, `Runs` list / get / cancel), `Events` (create, list, get, update, remove, send), `Contacts.Imports` (create, list, get) and `Contacts.Segments.ListAsync`, `Domains.Claims` (create, get, verify), `Emails.ShareAsync` / `UnshareAsync`, `Emails.Attachments` (list, get), `Broadcasts.ClickedLinksAsync` / `RecipientsAsync`, `Logs` (list, get), `OauthGrants` (list, revoke), `Webhooks.Events` (list, get, replay, `Attempts.ListAsync`), `Webhooks.RotateSigningSecretAsync`, `ApiKeys.UpdateAsync` and `Inboxes.Agent.UpdateAsync`.
- `FileUpload` and `multipart/form-data` bodies for `Contacts.Imports.CreateAsync` (a file part, text fields, and objects or lists as their JSON text); a required query parameter (`Broadcasts.RecipientsAsync`'s `type`) is a required argument.
- Removed: `Unsubscribes.DeleteAsync` (a recipient's opt-out is permanent) and `Webhooks.RotateAsync`, replaced by `Webhooks.RotateSigningSecretAsync`.
- `ListAllAsync` iterators now cover all 33 cursor-paginated lists.

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
