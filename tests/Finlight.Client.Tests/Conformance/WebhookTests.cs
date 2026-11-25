using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Finlight.Tests.Support;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Time.Testing;

namespace Finlight.Tests.Conformance;

/// <summary>W-01 to W-16: signature, timestamp, payload version and envelope.</summary>
public sealed class WebhookTests
{
    private static readonly Dictionary<string, string> Files = new()
    {
        ["W-01"] = "created-enriched.json",
        ["W-02"] = "updated-with-changed-fields.json",
        ["W-03"] = "updated-content-not-entitled.json",
        ["W-04"] = "created-raw.json",
        ["W-05"] = "test-webhook.json",
        ["W-06"] = "v2-body.json",
        ["W-07"] = "neg-tampered-body.json",
        ["W-08"] = "neg-stale-timestamp.json",
        ["W-09"] = "neg-future-timestamp.json",
        ["W-10"] = "neg-unparseable-timestamp.json",
        ["W-11"] = "neg-unix-seconds-timestamp.json",
        ["W-12"] = "neg-missing-timestamp.json",
        ["W-13"] = "neg-missing-signature.json",
        ["W-14"] = "neg-wrong-prefix.json",
        ["W-15"] = "neg-secret-hex-decoded.json",
        ["W-16"] = "lowercase-headers.json",
    };

    public static TheoryData<string> Cases => [.. Files.Keys];

    [Theory]
    [MemberData(nameof(Cases))]
    public void Case(string id)
    {
        var fixture = Kit.Obj("fixtures/webhook/" + Files[id]);
        Assert.Equal(id, fixture["id"]!.GetValue<string>());
        var request = fixture["request"]!;
        var body = Convert.FromBase64String(request["rawBodyBase64"]!.GetValue<string>());
        Assert.Equal(request["rawBody"]!.GetValue<string>(), Encoding.UTF8.GetString(body));
        var headers = request["headers"]!.AsObject()
            .Select(h => new KeyValuePair<string, StringValues>(h.Key, h.Value!.GetValue<string>()))
            .ToList();
        var secret = fixture["secret"]!.GetValue<string>();
        var options = new WebhookVerifyOptions
        {
            Tolerance = TimeSpan.FromSeconds(fixture["verify"]!["toleranceSeconds"]!.GetValue<int>()),
            Now = new FakeTimeProvider(DateTimeOffset.Parse(fixture["verify"]!["now"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture)),
        };
        var expect = fixture["expect"]!;

        // Every header form and the bytes and string variants must agree.
        var httpHeaders = new HttpRequestMessage().Headers;
        foreach (var (name, value) in headers)
        {
            httpHeaders.TryAddWithoutValidation(name, value.ToString());
        }

        var signature = headers.FirstOrDefault(h => h.Key.Equals("X-Webhook-Signature", StringComparison.OrdinalIgnoreCase)).Value.ToString();
        var timestamp = headers.FirstOrDefault(h => h.Key.Equals("X-Webhook-Timestamp", StringComparison.OrdinalIgnoreCase)).Value.ToString();
        var attempts = new List<Func<WebhookPayload>>
        {
            () => Webhooks.ConstructEvent(body, headers, secret, options),
            () => Webhooks.ConstructEvent(Encoding.UTF8.GetString(body), headers, secret, options),
            () => Webhooks.ConstructEvent(body, httpHeaders, secret, options),
            () => Webhooks.ConstructEvent(body, NullIfEmpty(signature), NullIfEmpty(timestamp), secret, options),
        };

        foreach (var attempt in attempts)
        {
            switch (expect["outcome"]!.GetValue<string>())
            {
                case "event":
                    CheckEvent(id, Assert.IsType<WebhookEvent>(attempt()), expect["event"]!, fixture);
                    break;
                case "wrongPayloadVersion":
                    var wrong = Assert.IsType<WrongPayloadVersion>(attempt());
                    Assert.Equal(expect["detectedVersion"]!.GetValue<int>(), wrong.DetectedVersion);
                    Assert.Equal(expect["payloadVersionPresent"]!.GetValue<bool>(), wrong.PayloadVersionPresent);
                    Assert.Null(wrong.PayloadVersion);
                    Assert.Equal(expect["link"]!.GetValue<string>(), wrong.Link);
                    Assert.Equal(Kit.Canonical(request["rawBody"]!.GetValue<string>()), Kit.Canonical(wrong.Raw));
                    break;
                case "error":
                    var error = Assert.Throws<WebhookVerificationException>(() => attempt());
                    Assert.Equal(expect["reason"]!.GetValue<string>(), error.Reason.Value);
                    Assert.Equal("webhook_verification", error.Kind.Value);
                    Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
                    break;
                default:
                    throw new InvalidOperationException(expect["outcome"]!.ToString());
            }
        }

        if (expect["signatureValid"]!.GetValue<bool>() && expect["outcome"]!.GetValue<string>() != "error")
        {
            // A changed body after a valid signature fails with the signature error first.
            var tampered = (byte[])body.Clone();
            tampered[^2] ^= 0x01;
            Assert.Equal(
                WebhookVerificationReason.SignatureMismatch,
                Assert.Throws<WebhookVerificationException>(() => Webhooks.ConstructEvent(tampered, headers, secret, options)).Reason);
        }
    }

    private static void CheckEvent(string id, WebhookEvent e, JsonNode expected, JsonObject fixture)
    {
        Assert.Equal(expected["eventId"]!.GetValue<string>(), e.EventId);
        Assert.Equal(expected["event"]!.GetValue<string>(), e.Event.Value);
        Assert.Equal(expected["occurredAt"]!.GetValue<string>(), e.OccurredAt);
        Assert.NotNull(e.OccurredAtTime);
        Assert.Equal(expected["subscriptionId"]!.GetValue<string>(), e.SubscriptionId);
        Assert.Equal(expected["isTest"]!.GetValue<bool>(), e.IsTest);
        Assert.Equal(expected["link"]!.GetValue<string>(), e.Data.Link);
        Assert.Equal(3, e.PayloadVersion);
        var changed = expected["changedFields"]?.AsArray().Select(v => v!.GetValue<string>()).ToList();
        Assert.Equal(changed, e.ChangedFields);
        Assert.Equal(
            Kit.Canonical(fixture["request"]!["rawBody"]!.GetValue<string>()),
            Kit.Canonical(e.GetRawJson()!));
        switch (id)
        {
            case "W-03":
                Assert.False(e.Data.ContentPresent);
                break;
            case "W-04":
                Assert.False(e.Data.ContentPresent || e.Data.SentimentPresent || e.Data.CompaniesPresent || e.Data.EventsPresent);
                Assert.Null(e.Data.AvailableAt);
                Assert.Equal(e.Data.FirstSeenAt, e.OccurredAt);
                break;
            case "W-01":
            case "W-16":
                Assert.True(e.Data.ContentPresent && e.Data.CompaniesPresent && e.Data.EventsPresent);
                break;
        }
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
