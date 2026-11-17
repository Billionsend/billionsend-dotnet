using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using BillionSend.Internal;

namespace BillionSend
{
    /// <summary>
    /// What iterating an inbox <see cref="EventStream"/> gives: an <see cref="InboxStreamEvent"/>
    /// or a <see cref="StreamGapEvent"/>. Tell them apart with a type pattern, or on <see cref="Event"/>.
    /// </summary>
    public abstract class InboxStreamItem
    {
        private protected InboxStreamItem(string @event, JsonNode? data)
        {
            Event = @event;
            Data = data;
        }

        /// <summary>The stream position of an event, or <see langword="null"/> for a gap.</summary>
        public abstract string? Id { get; }

        /// <summary>The event name: a webhook event name such as <c>inbox.email.received</c>, or <c>stream.gap</c>.</summary>
        public string Event { get; }

        /// <summary>The event's JSON payload.</summary>
        public JsonNode? Data { get; }
    }

    /// <summary>
    /// One inbox event. <see cref="Id"/> is its stream position: keep the last one to resume with
    /// <see cref="EventStreamOptions.LastEventId"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="InboxStreamItem.Data"/> is <c>{ type, created_at, inbox_id, data }</c>: the
    /// webhook payload's metadata (never a body, code or link), whose inner <c>data</c> carries the
    /// ids to read next with the REST methods (<c>thread_id</c>, <c>email_id</c>, <c>draft_id</c>).
    /// </remarks>
    public sealed class InboxStreamEvent : InboxStreamItem
    {
        internal InboxStreamEvent(string id, string @event, JsonNode? data)
            : base(@event, data)
        {
            Id = id;
        }

        /// <inheritdoc/>
        public override string Id { get; }

        /// <summary>The inner <c>data</c> object of the payload (the ids and metadata), or <see langword="null"/>.</summary>
        public JsonObject? Payload => Data is JsonObject outer ? outer["data"] as JsonObject : null;
    }

    /// <summary>
    /// The stream resumed from a position older than the server keeps (24 hours): events may be
    /// missing, so read the REST lists again. It has no id and does not move the stream position.
    /// One stream hands it out once per position: a later reconnect from the same position, which
    /// the server answers with another gap, does not repeat it.
    /// </summary>
    public sealed class StreamGapEvent : InboxStreamItem
    {
        /// <summary>The event name of a gap.</summary>
        public const string EventName = "stream.gap";

        internal StreamGapEvent(JsonNode? data)
            : base(EventName, data)
        {
        }

        /// <inheritdoc/>
        public override string? Id => null;

        /// <summary>Why events may be missing (<c>data.reason</c>).</summary>
        public string? Reason =>
            Data is JsonObject body && body["reason"] is JsonValue value && value.TryGetValue<string>(out var reason)
                ? reason
                : null;
    }

    /// <summary>
    /// A live inbox event stream. Iterate it with <c>await foreach</c>; disposing it, or calling
    /// <see cref="Close"/>, closes the connection and ends a running loop without an error.
    /// </summary>
    /// <remarks>
    /// When the connection drops (the server also closes every stream about every ten minutes) it
    /// reconnects with <c>Last-Event-ID</c>, and the events in between are replayed first, unless
    /// <see cref="EventStreamOptions.Reconnect"/> is false. After
    /// <see cref="EventStreamOptions.MaxReconnects"/> failures in a row it throws an
    /// <see cref="ApiConnectionException"/>; a refusal a retry cannot fix (a revoked key, say) is
    /// thrown as its own <see cref="BillionSendException"/> type. Cancelling the token given to
    /// the method that opened the stream, or to the loop (<c>WithCancellation</c>), throws
    /// <see cref="OperationCanceledException"/>. A stream can be iterated once.
    /// </remarks>
    public sealed class EventStream : IAsyncEnumerable<InboxStreamItem>, IAsyncDisposable, IDisposable
    {
        private readonly Func<string?, CancellationToken, Task<StreamConnection>> _connect;
        private readonly EventStreamOptions _options;
        private readonly HashSet<string> _endsOn;
        private readonly CancellationToken _openToken;
        private readonly CancellationTokenSource _closing = new();
        private readonly object _gate = new();
        private StreamConnection? _connection;
        private TimeSpan _delay;
        private bool _closed;
        private int _started;

        internal EventStream(
            StreamConnection connection,
            Func<string?, CancellationToken, Task<StreamConnection>> connect,
            EventStreamOptions options,
            IReadOnlyCollection<string> endsOn,
            CancellationToken openToken)
        {
            _connection = connection;
            _connect = connect;
            _options = options;
            _endsOn = new HashSet<string>(endsOn, StringComparer.Ordinal);
            _openToken = openToken;
            _delay = options.ReconnectDelay;
            LastEventId = options.LastEventId;
        }

        /// <summary>The id of the last event received: pass it as <see cref="EventStreamOptions.LastEventId"/> to resume later.</summary>
        public string? LastEventId { get; private set; }

        /// <summary>Closes the connection; a running loop ends without an error.</summary>
        public void Close()
        {
            StreamConnection? connection;
            lock (_gate)
            {
                if (_closed)
                {
                    return;
                }

                _closed = true;
                connection = _connection;
                _connection = null;
            }

            try
            {
                _closing.Cancel();
            }
            catch (AggregateException)
            {
                // A callback failed while cancelling; the stream is closed regardless.
            }

            connection?.Dispose();
        }

        /// <inheritdoc/>
        public void Dispose() => Close();

        /// <inheritdoc/>
        public ValueTask DisposeAsync()
        {
            Close();
            return default;
        }

        /// <inheritdoc/>
        public IAsyncEnumerator<InboxStreamItem> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                throw new InvalidOperationException("An EventStream can be iterated only once");
            }

            return EventsAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        }

        private bool IsClosed
        {
            get
            {
                lock (_gate)
                {
                    return _closed;
                }
            }
        }

        private async IAsyncEnumerable<InboxStreamItem> EventsAsync([EnumeratorCancellation] CancellationToken loopToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_openToken, loopToken, _closing.Token);
            var token = linked.Token;
            var failures = 0;
            // The position a gap was last handed out for; the server repeats the gap on every
            // reconnect from that position until a newer event moves it.
            var gapHandedOut = false;
            string? gapAt = null;
            try
            {
                while (!IsClosed)
                {
                    StreamConnection? connection;
                    lock (_gate)
                    {
                        connection = _connection;
                    }

                    if (connection is not null)
                    {
                        var events = ServerSentEvents
                            .ParseAsync(connection.Body, ms => _delay = TimeSpan.FromMilliseconds(ms), LastEventId, token)
                            .GetAsyncEnumerator(token);
                        try
                        {
                            while (true)
                            {
                                ServerSentEvent message;
                                try
                                {
                                    if (!await events.MoveNextAsync().ConfigureAwait(false))
                                    {
                                        break;
                                    }

                                    message = events.Current;
                                }
                                catch (Exception) when (!IsCallerCancellation(loopToken))
                                {
                                    // The connection dropped (or the stream was closed): reconnect below.
                                    break;
                                }

                                failures = 0;
                                if (message.Event == StreamGapEvent.EventName)
                                {
                                    if (gapHandedOut && gapAt == LastEventId)
                                    {
                                        continue;
                                    }

                                    gapHandedOut = true;
                                    gapAt = LastEventId;
                                    yield return new StreamGapEvent(message.Data);
                                    continue;
                                }

                                LastEventId = message.Id;
                                yield return new InboxStreamEvent(message.Id ?? string.Empty, message.Event, message.Data);
                                if (_endsOn.Contains(message.Event))
                                {
                                    yield break;
                                }
                            }
                        }
                        finally
                        {
                            await events.DisposeAsync().ConfigureAwait(false);
                        }

                        lock (_gate)
                        {
                            if (ReferenceEquals(_connection, connection))
                            {
                                _connection = null;
                            }
                        }

                        connection.Dispose();
                    }

                    ThrowIfCallerCancelled(loopToken);
                    if (IsClosed || !_options.Reconnect)
                    {
                        yield break;
                    }

                    failures++;
                    if (failures > _options.MaxReconnects)
                    {
                        throw new ApiConnectionException(
                            $"The event stream disconnected; gave up after {_options.MaxReconnects} reconnects");
                    }

                    var next = await ReconnectAsync(token, loopToken).ConfigureAwait(false);
                    if (next is null)
                    {
                        if (IsClosed)
                        {
                            yield break;
                        }

                        continue;
                    }

                    lock (_gate)
                    {
                        if (_closed)
                        {
                            next.Dispose();
                            yield break;
                        }

                        _connection = next;
                    }
                }
            }
            finally
            {
                Close();
            }
        }

        /// <summary>
        /// Waits, then opens the next connection. <see langword="null"/> when closed meanwhile, or
        /// when the attempt failed in a way worth another try.
        /// </summary>
        private async Task<StreamConnection?> ReconnectAsync(CancellationToken token, CancellationToken loopToken)
        {
            try
            {
                await Task.Delay(_delay, token).ConfigureAwait(false);
                return await _connect(LastEventId, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!IsCallerCancellation(loopToken))
            {
                return null;
            }
            catch (BillionSendException error) when (Retry.IsRetryable(error, keyed: false))
            {
                return null;
            }
        }

        private bool IsCallerCancellation(CancellationToken loopToken) =>
            !IsClosed && (_openToken.IsCancellationRequested || loopToken.IsCancellationRequested);

        private void ThrowIfCallerCancelled(CancellationToken loopToken)
        {
            if (IsCallerCancellation(loopToken))
            {
                _openToken.ThrowIfCancellationRequested();
                loopToken.ThrowIfCancellationRequested();
            }
        }
    }

    /// <summary>One server-sent event, as the parser reads it.</summary>
    internal sealed class ServerSentEvent
    {
        public ServerSentEvent(string? id, string @event, JsonNode? data, string raw)
        {
            Id = id;
            Event = @event;
            Data = data;
            Raw = raw;
        }

        /// <summary>The stream position after this event (the last <c>id</c> field seen), used to resume.</summary>
        public string? Id { get; }

        /// <summary>The event name (<c>message</c> when the server sent none).</summary>
        public string Event { get; }

        /// <summary><c>data</c> parsed as JSON; the raw text as a JSON string when it is not JSON.</summary>
        public JsonNode? Data { get; }

        /// <summary>The raw <c>data</c> text.</summary>
        public string Raw { get; }
    }

    /// <summary>Parses a <c>text/event-stream</c> body (UTF-8) into events, following the WHATWG rules.</summary>
    internal static class ServerSentEvents
    {
        public static async IAsyncEnumerable<ServerSentEvent> ParseAsync(
            Stream body,
            Action<int> onRetry,
            string? initialId,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var decoder = new UTF8Encoding(false).GetDecoder();
            var bytes = new byte[8192];
            var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length) + 1];
            var buffer = new StringBuilder();
            var first = true;
            var parser = new LineParser(initialId, onRetry);
            while (true)
            {
                var read = await body.ReadAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
                var final = read == 0;
                var count = decoder.GetChars(bytes, 0, read, chars, 0, flush: final);
                buffer.Append(chars, 0, count);
                if (first && buffer.Length > 0)
                {
                    if (buffer[0] == '\uFEFF')
                    {
                        buffer.Remove(0, 1);
                    }

                    first = false;
                }

                foreach (var line in TakeLines(buffer, final))
                {
                    if (parser.Process(line) is { } message)
                    {
                        yield return message;
                    }
                }

                if (final)
                {
                    // An event not ended by a blank line is discarded, as the spec says.
                    yield break;
                }
            }
        }

        private static List<string> TakeLines(StringBuilder buffer, bool final)
        {
            var lines = new List<string>();
            var start = 0;
            var i = 0;
            while (i < buffer.Length)
            {
                var c = buffer[i];
                if (c == '\n')
                {
                    lines.Add(buffer.ToString(start, i - start));
                    i++;
                    start = i;
                }
                else if (c == '\r')
                {
                    if (i + 1 < buffer.Length)
                    {
                        lines.Add(buffer.ToString(start, i - start));
                        i += buffer[i + 1] == '\n' ? 2 : 1;
                        start = i;
                    }
                    else if (final)
                    {
                        lines.Add(buffer.ToString(start, i - start));
                        i++;
                        start = i;
                    }
                    else
                    {
                        // A lone "\r" at the end of the buffer may be the first half of "\r\n".
                        break;
                    }
                }
                else
                {
                    i++;
                }
            }

            buffer.Remove(0, start);
            return lines;
        }

        private sealed class LineParser
        {
            private readonly Action<int> _onRetry;
            private readonly List<string> _data = new();
            private string? _lastId;
            private string _eventName = string.Empty;
            private bool _hasData;

            public LineParser(string? initialId, Action<int> onRetry)
            {
                _lastId = initialId;
                _onRetry = onRetry;
            }

            public ServerSentEvent? Process(string line)
            {
                if (line.Length == 0)
                {
                    if (!_hasData)
                    {
                        _eventName = string.Empty;
                        return null;
                    }

                    var raw = string.Join("\n", _data);
                    JsonNode? data;
                    try
                    {
                        data = JsonNode.Parse(raw);
                    }
                    catch (JsonException)
                    {
                        data = JsonValue.Create(raw);
                    }

                    var message = new ServerSentEvent(_lastId, _eventName.Length > 0 ? _eventName : "message", data, raw);
                    _eventName = string.Empty;
                    _data.Clear();
                    _hasData = false;
                    return message;
                }

                if (line[0] == ':')
                {
                    return null;
                }

                var colon = line.IndexOf(':');
                var field = colon == -1 ? line : line.Substring(0, colon);
                var value = colon == -1 ? string.Empty : line.Substring(colon + 1);
                if (value.StartsWith(' '))
                {
                    value = value.Substring(1);
                }

                switch (field)
                {
                    case "event":
                        _eventName = value;
                        break;
                    case "data":
                        _data.Add(value);
                        _hasData = true;
                        break;
                    case "id":
                        if (!value.Contains('\0'))
                        {
                            _lastId = value;
                        }

                        break;
                    case "retry":
                        if (value.Length > 0 && IsDigits(value) && int.TryParse(value, out var ms))
                        {
                            _onRetry(ms);
                        }

                        break;
                }

                return null;
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
        }
    }
}
