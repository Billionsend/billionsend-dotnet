using System.Text.RegularExpressions;
using BillionSend.Internal;

namespace BillionSend.Tests;

public sealed class RetryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-16T12:00:00Z");

    private static TimeSpan? Delay(int attempt, string? retryAfter, double random = 0) =>
        Retry.DelayFor(attempt, retryAfter, Now, () => random);

    [Fact]
    public void UsesRetryAfterSeconds()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(2000), Delay(0, "2"));
        Assert.Equal(TimeSpan.FromMilliseconds(250), Delay(0, "0.25"));
        Assert.Equal(TimeSpan.Zero, Delay(0, "0"));
    }

    [Fact]
    public void UsesARetryAfterHttpDate()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(5000), Delay(0, "Mon, 16 Nov 2026 12:00:05 GMT"));
        Assert.Equal(TimeSpan.Zero, Delay(0, "Mon, 16 Nov 2026 11:00:00 GMT"));
    }

    [Fact]
    public void GivesUpOnARetryAfterPastTheCap()
    {
        Assert.Null(Delay(0, "61"));
        Assert.Equal(TimeSpan.FromSeconds(60), Delay(0, "60"));
    }

    [Fact]
    public void BacksOffExponentiallyWithJitterWithoutAUsableRetryAfter()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(500), Delay(0, null));
        Assert.Equal(TimeSpan.FromMilliseconds(1000), Delay(1, ""));
        Assert.Equal(TimeSpan.FromMilliseconds(2000), Delay(2, "soon"));
        Assert.Equal(TimeSpan.FromMilliseconds(8000), Delay(10, null));
        Assert.Equal(TimeSpan.FromMilliseconds(375), Delay(0, null, random: 1));
    }

    [Fact]
    public void RetriesTimeoutsNetworkFailures429And5xxAnd409OnlyWhileAKeyedFirstAttemptRuns()
    {
        ConflictException Conflict(string name) => new("m", name, 409);
        Assert.True(Retry.IsRetryable(new RateLimitException("m", statusCode: 429), false));
        Assert.True(Retry.IsRetryable(new ApiException("m", statusCode: 502), false));
        Assert.True(Retry.IsRetryable(new ApiTimeoutException(), false));
        Assert.True(Retry.IsRetryable(new ApiConnectionException(), false));
        Assert.True(Retry.IsRetryable(Conflict("concurrent_idempotent_requests"), true));
        Assert.False(Retry.IsRetryable(Conflict("concurrent_idempotent_requests"), false));
        Assert.False(Retry.IsRetryable(Conflict("invalid_idempotent_request"), true));
        Assert.False(Retry.IsRetryable(new ValidationException("m", statusCode: 422), true));
        Assert.False(Retry.IsRetryable(new BillionSendException("Missing path parameter"), true));
    }

    [Fact]
    public void MakesAVersion4UuidNewEachTime()
    {
        var a = ApiClient.NewIdempotencyKey();
        Assert.Matches(new Regex("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$"), a);
        Assert.NotEqual(a, ApiClient.NewIdempotencyKey());
    }

    [Fact]
    public void WritesListsCommaSeparatedAndScalarsInvariantly()
    {
        var query = Urls.QueryString(new Dictionary<string, object?>
        {
            ["metrics"] = new[] { "sent", "delivered" },
            ["read"] = true,
            ["wait"] = 30,
            ["ratio"] = 0.5,
            ["after"] = null,
            ["q"] = "a b&c",
        });
        Assert.Equal("?metrics=sent%2Cdelivered&read=true&wait=30&ratio=0.5&q=a%20b%26c", query);
        Assert.Equal(string.Empty, Urls.QueryString(null));
    }
}
