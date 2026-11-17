using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using BillionSend.Internal;

namespace BillionSend
{
    /// <summary>Walks cursor-paginated lists.</summary>
    public static class Pagination
    {
        private static readonly IReadOnlyDictionary<string, object?> NoQuery = new Dictionary<string, object?>();

        /// <summary>
        /// Walks a cursor-paginated list one item at a time, fetching pages on demand. Every list
        /// method with cursors has one of these beside it (<c>ListAllAsync</c>,
        /// <c>SearchAllAsync</c>); this builds the same iterator over any page fetcher.
        /// </summary>
        /// <remarks>
        /// A page is a JSON object with a <c>data</c> array of items that carry an <c>id</c>, and
        /// <c>has_more</c>. Starting with <c>before</c> set walks backward (using the first id of
        /// each page); otherwise it walks forward from <c>after</c> (using the last id). A failed
        /// page is thrown as its <see cref="BillionSendException"/>, and so is an answer that is
        /// not a list page (no <c>data</c> array, or an item to continue from without an <c>id</c>),
        /// with the name <c>application_error</c>. The items are detached from
        /// their page, so they can be added to other JSON trees.
        /// </remarks>
        /// <param name="fetchPage">Fetches one page for the given query (the start query with the cursor set).</param>
        /// <param name="query">The query of the first page, or <see langword="null"/>.</param>
        /// <param name="cancellationToken">Stops the iteration.</param>
        /// <returns>The items, one at a time.</returns>
        public static async IAsyncEnumerable<JsonNode> IterateAsync(
            Func<IReadOnlyDictionary<string, object?>, CancellationToken, Task<JsonNode?>> fetchPage,
            IReadOnlyDictionary<string, object?>? query = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (fetchPage is null)
            {
                throw new ArgumentNullException(nameof(fetchPage));
            }

            var start = query ?? NoQuery;
            var backward = start.TryGetValue("before", out var before) && before is not null;
            string? cursor = null;
            if (backward)
            {
                cursor = Urls.Format(before!);
            }
            else if (start.TryGetValue("after", out var after) && after is not null)
            {
                cursor = Urls.Format(after);
            }

            while (true)
            {
                var page = new Dictionary<string, object?>(start);
                if (cursor is not null)
                {
                    page[backward ? "before" : "after"] = cursor;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var answer = await fetchPage(page, cancellationToken).ConfigureAwait(false);
                // A page that is not a list page is an error, as in the other clients: ending
                // quietly would pass off a truncated walk as the whole list.
                if (answer is not JsonObject body || body["data"] is not JsonArray data)
                {
                    throw new BillionSendException("The list page has no data array");
                }

                var items = data.ToList();
                data.Clear();
                foreach (var item in items)
                {
                    if (item is not null)
                    {
                        yield return item;
                    }
                }

                var hasMore = body["has_more"] is JsonValue more && more.TryGetValue<bool>(out var flag) && flag;
                if (!hasMore || items.Count == 0)
                {
                    yield break;
                }

                var edge = backward ? items[0] : items[items.Count - 1];
                if (edge?["id"] is not JsonNode id)
                {
                    throw new BillionSendException("The last item of the list page has no id to continue from");
                }

                cursor = Urls.Format(id);
            }
        }
    }
}
