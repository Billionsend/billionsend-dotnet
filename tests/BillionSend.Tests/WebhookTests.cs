using System.Security.Cryptography;
using System.Text;

namespace BillionSend.Tests;

public sealed class WebhookTests
{
    // The known-answer vector the API's own signing test pins.
    private const string Secret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";
    private const string MessageId = "msg_p5jXN8AQM9LWM0D4loKWxJek";
    private const string Timestamp = "1614265330";
    private const string Payload = "{\"test\": 2432232314}";
    private const string Signature = "v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=";

    private static readonly DateTimeOffset At = DateTimeOffset.FromUnixTimeSeconds(long.Parse(Timestamp));

    private static readonly WebhookVerifyOptions AtVector = new() { Now = At };

    private static Dictionary<string, string> Headers(string signature = Signature, string timestamp = Timestamp) => new()
    {
        ["webhook-id"] = MessageId,
        ["webhook-timestamp"] = timestamp,
        ["webhook-signature"] = signature,
    };

    /// <summary>Signs as the API does, with the platform's HMAC as an independent check.</summary>
    private static string Sign(string secret, string id, string timestamp, string payload)
    {
        var key = Convert.FromBase64String(secret["whsec_".Length..]);
        using var hmac = new HMACSHA256(key);
        return "v1," + Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{id}.{timestamp}.{payload}")));
    }

    private static string NewSecret(int bytes) => "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes));

    private static WebhookVerificationException Failure(Action verify)
    {
        var error = Assert.Throws<WebhookVerificationException>(verify);
        Assert.IsAssignableFrom<BillionSendException>(error);
        return error;
    }

    [Fact]
    public void VerifiesThePublishedVectorAndReturnsTheParsedBody()
    {
        var body = Webhook.Verify(Payload, Headers(), Secret, AtVector);
        Assert.Equal(2432232314L, (long)body["test"]!);
    }

    [Fact]
    public void TakesTheBodyAsBytesAndHeadersInAnyCaseOrThroughALookup()
    {
        var headers = new Dictionary<string, string>
        {
            ["Webhook-Id"] = MessageId,
            ["WEBHOOK-TIMESTAMP"] = Timestamp,
            ["webhook-signature"] = Signature,
        };
        var bytes = Encoding.UTF8.GetBytes(Payload);
        Assert.Equal(2432232314L, (long)Webhook.Verify(bytes, headers, Secret, AtVector)["test"]!);
        Assert.Equal(2432232314L, (long)Webhook.Verify(Payload, name => Headers()[name], Secret, AtVector)["test"]!);
        Assert.Equal(2432232314L, (long)Webhook.Verify(bytes, name => headers.First(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value, Secret, AtVector)["test"]!);
    }

    [Fact]
    public void AcceptsAnyOfTheSignaturesARotationSendsAndRefusesAV2OrAWrongOne()
    {
        var other = NewSecret(32);
        var rotated = $"{Sign(other, MessageId, Timestamp, Payload)} {Signature}";
        Assert.NotNull(Webhook.Verify(Payload, Headers(rotated), Secret, AtVector));
        Assert.NotNull(Webhook.Verify(Payload, Headers(rotated), other, AtVector));

        var wrongVersion = Signature.Replace("v1,", "v2,");
        Assert.Equal("No webhook signature matches", Failure(() => Webhook.Verify(Payload, Headers(wrongVersion), Secret, AtVector)).Message);
        Failure(() => Webhook.Verify(Payload + " ", Headers(), Secret, AtVector));
        Failure(() => Webhook.Verify(Payload, Headers(), other, AtVector));
        Failure(() => Webhook.Verify(Payload, Headers("v1,not base64!"), Secret, AtVector));
    }

    [Fact]
    public void RefusesATimestampOutsideTheToleranceEitherWay()
    {
        var late = new WebhookVerifyOptions { Now = At.AddSeconds(301) };
        var early = new WebhookVerifyOptions { Now = At.AddSeconds(-301) };
        Assert.Contains("too old", Failure(() => Webhook.Verify(Payload, Headers(), Secret, late)).Message);
        Assert.Contains("too new", Failure(() => Webhook.Verify(Payload, Headers(), Secret, early)).Message);
        Assert.NotNull(Webhook.Verify(Payload, Headers(), Secret, new WebhookVerifyOptions { Now = late.Now, Tolerance = TimeSpan.FromSeconds(600) }));
        Assert.NotNull(Webhook.Verify(Payload, Headers(), Secret, new WebhookVerifyOptions { Now = At.AddSeconds(300) }));
        Assert.Equal("Invalid webhook-timestamp header", Failure(() => Webhook.Verify(Payload, Headers(Signature, "16142e5"), Secret, AtVector)).Message);
    }

    [Fact]
    public void VerifiesAgainstTheCurrentTimeByDefault()
    {
        var secret = NewSecret(24);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        const string payload = "{\"type\":\"inbox.email.received\",\"created_at\":\"2026-11-16T00:00:00.000Z\",\"data\":{}}";
        var headers = new Dictionary<string, string>
        {
            ["webhook-id"] = "msg_1",
            ["webhook-timestamp"] = timestamp,
            ["webhook-signature"] = Sign(secret, "msg_1", timestamp, payload),
        };
        Assert.Equal("inbox.email.received", (string?)Webhook.Verify(payload, headers, secret)["type"]);
    }

    [Fact]
    public void RefusesMissingHeadersAMalformedSecretAndABodyThatIsNotJson()
    {
        var unsigned = Headers();
        unsigned.Remove("webhook-signature");
        Failure(() => Webhook.Verify(Payload, unsigned, Secret, AtVector));
        foreach (var secret in new[]
        {
            "MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw",
            "whsec_short",
            NewSecret(65),
            "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaS",
            "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw ",
        })
        {
            Assert.Equal("The webhook secret is not a whsec_ secret", Failure(() => Webhook.Verify(Payload, Headers(), secret, AtVector)).Message);
        }

        const string text = "not json";
        var error = Failure(() => Webhook.Verify(text, Headers(Sign(Secret, MessageId, Timestamp, text)), Secret, AtVector));
        Assert.Equal("The webhook body is not JSON", error.Message);
        Assert.Equal("webhook_verification_error", error.Name);
        Assert.Null(error.StatusCode);
        Assert.Null(error.RequestId);
    }
}
