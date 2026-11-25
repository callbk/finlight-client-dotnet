using System.Security.Cryptography;
using System.Text;
using Finlight.Tests.Conformance;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Time.Testing;

namespace Finlight.Tests.Unit;

/// <summary>Webhook rules beyond the kit cases (R-HOOK-1, R-HOOK-2).</summary>
public sealed class WebhookRulesTests
{
    private const string Secret = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Timestamp = "2026-11-23T21:04:05.123Z";
    private const string Data = """{"id":"i","link":"https://x.example/a","source":{"domain":"x.example"},"title":"t"}""";

    private static string Sign(string body, string timestamp = Timestamp)
        => "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(timestamp + "." + body))).ToLowerInvariant();

    private static WebhookVerifyOptions Options => new() { Now = new FakeTimeProvider(DateTimeOffset.Parse(Timestamp, System.Globalization.CultureInfo.InvariantCulture)) };

    [Theory]
    [InlineData("3.0", true)]
    [InlineData("3", true)]
    [InlineData("\"3\"", false)]
    [InlineData("true", false)]
    [InlineData("2", false)]
    [InlineData("4", false)]
    public void PayloadVersion_OnlyTheNumberThreeIsV3(string version, bool v3)
    {
        var body = $$"""{"eventId":"evt_x","event":"article.created","occurredAt":"{{Timestamp}}","payloadVersion":{{version}},"subscriptionId":"s","data":{{Data}}}""";
        var payload = ForwardCompatTests.Signed(body);
        if (v3)
        {
            Assert.IsType<WebhookEvent>(payload);
            return;
        }

        var wrong = Assert.IsType<WrongPayloadVersion>(payload);
        Assert.True(wrong.PayloadVersionPresent);
        Assert.Equal(version, wrong.PayloadVersion!.Value.GetRawText());
        Assert.Equal(2, wrong.DetectedVersion);
        Assert.Equal("https://x.example/a", wrong.Link);
        Assert.IsNotType<WebhookVerificationException>((object)wrong);
    }

    [Fact]
    public void Body_ThatIsNotAnObjectAfterAValidSignatureIsADecodeError()
    {
        Assert.Throws<FinlightDecodeException>(() => ForwardCompatTests.Signed("[1,2]"));
        Assert.Throws<FinlightDecodeException>(() => ForwardCompatTests.Signed("not json"));
        Assert.Throws<FinlightDecodeException>(() => ForwardCompatTests.Signed("""{"payloadVersion":3,"eventId":"e","event":"article.created","data":{"id":"i"}}"""));
    }

    [Fact]
    public void Headers_DuplicateNonAsciiAndCgiForms()
    {
        var body = $$"""{"eventId":"evt_x","event":"article.created","payloadVersion":3,"data":{{Data}}}""";
        var bytes = Encoding.UTF8.GetBytes(body);
        var signature = Sign(body);

        var duplicate = new Dictionary<string, StringValues>
        {
            ["X-Webhook-Signature"] = new StringValues([signature, signature]),
            ["X-Webhook-Timestamp"] = Timestamp,
        };
        Assert.Equal(WebhookVerificationReason.DuplicateHeader, Assert.Throws<WebhookVerificationException>(() => Webhooks.ConstructEvent(bytes, duplicate, Secret, Options)).Reason);

        var twice = new List<KeyValuePair<string, StringValues>>
        {
            new("x-webhook-timestamp", Timestamp),
            new("X-Webhook-Timestamp", Timestamp),
            new("X-Webhook-Signature", signature),
        };
        Assert.Equal(WebhookVerificationReason.DuplicateHeader, Assert.Throws<WebhookVerificationException>(() => Webhooks.ConstructEvent(bytes, twice, Secret, Options)).Reason);

        Assert.Equal(
            WebhookVerificationReason.NonAsciiHeader,
            Assert.Throws<WebhookVerificationException>(() => Webhooks.ConstructEvent(bytes, signature, Timestamp + "é", Secret, Options)).Reason);

        var cgi = new Dictionary<string, StringValues> { ["HTTP_X_WEBHOOK_SIGNATURE"] = signature, ["HTTP_X_WEBHOOK_TIMESTAMP"] = Timestamp };
        Assert.IsType<WebhookEvent>(Webhooks.ConstructEvent(bytes, cgi, Secret, Options));

        Assert.Equal(WebhookVerificationReason.InvalidSecret, Assert.Throws<WebhookVerificationException>(() => Webhooks.ConstructEvent(bytes, signature, Timestamp, string.Empty, Options)).Reason);
        Assert.Equal(WebhookVerificationReason.InvalidRawBody, Assert.Throws<WebhookVerificationException>(() => Webhooks.ConstructEvent((byte[])null!, signature, Timestamp, Secret, Options)).Reason);

        // VerifySignature alone, and the default tolerance of 300 s.
        Webhooks.VerifySignature(bytes, signature, Timestamp, Secret, Options);
        Assert.Equal(TimeSpan.FromSeconds(300), Webhooks.DefaultTolerance);
        var late = new WebhookVerifyOptions { Now = new FakeTimeProvider(DateTimeOffset.Parse(Timestamp, System.Globalization.CultureInfo.InvariantCulture).AddSeconds(301)) };
        Assert.Equal(WebhookVerificationReason.TimestampOutsideTolerance, Assert.Throws<WebhookVerificationException>(() => Webhooks.VerifySignature(bytes, signature, Timestamp, Secret, late)).Reason);
    }

    [Fact]
    public void Event_WithoutChangedFieldsAndTheTestFlag()
    {
        var body = $$"""{"eventId":"evt_test0123","event":"article.updated","payloadVersion":3,"data":{{Data}}}""";
        var e = Assert.IsType<WebhookEvent>(ForwardCompatTests.Signed(body));
        Assert.Null(e.ChangedFields);
        Assert.True(e.IsTest);
        Assert.Equal(WebhookEventName.ArticleUpdated, e.Event);
    }
}
