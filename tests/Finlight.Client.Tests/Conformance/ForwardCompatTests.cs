using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Finlight.Tests.Support;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Time.Testing;

namespace Finlight.Tests.Conformance;

/// <summary>F-01 to F-04: unknown keys and values at every level.</summary>
public sealed class ForwardCompatTests
{
    private const string Secret = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    public static TheoryData<string> Cases => ["F-01", "F-02", "F-03", "F-04"];

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Case(string id)
    {
        switch (id)
        {
            case "F-01":
                {
                    var fixture = Kit.Obj("fixtures/forward-compat/unknown-fields.json");
                    var article = Presence.Article(fixture["wire"]!);
                    Assert.Equal(Kit.Canonical(fixture["known"]), Kit.Canonical(Presence.Roundtrip(article)));
                    var raw = JsonNode.Parse(article.GetRawJson()!)!;
                    Assert.NotNull(raw["wordCount"]);
                    Assert.NotNull(raw["companies"]![0]!["primaryListing"]!["figi"]);
                    Assert.NotNull(raw["events"]![0]!["subtype"]);
                    break;
                }

            case "F-02":
                {
                    var fixture = Kit.Obj("fixtures/forward-compat/unknown-enum-values.json");
                    var article = Presence.Article(fixture["wire"]!);
                    Assert.Equal("wire_service", article.Source.Type!.Value.Value);
                    Assert.False(article.Source.Type!.Value.IsKnown);
                    Assert.Equal("mixed", article.Sentiment.Value!.Label!.Value.Value);
                    Assert.False(article.Sentiment.Value!.Label!.Value.IsKnown);
                    Assert.Equal("guarantor", article.Companies.Value![0].Role!.Value.Value);
                    Assert.Equal("esg_controversy", article.Events.Value![0].Type.Value);
                    Assert.Equal("mixed", article.Events.Value![0].Direction!.Value.Value);
                    Assert.Equal("extreme", article.Events.Value![0].Importance!.Value.Value);
                    Assert.Equal("synthetic-new-category", article.Categories[1]);
                    Assert.Equal(Kit.Canonical(fixture["wire"]), Kit.Canonical(Presence.Roundtrip(article)));
                    break;
                }

            case "F-03":
                await EnvelopesAsync();
                break;
            case "F-04":
                await UnknownEventsAndCodesAsync();
                break;
        }
    }

    private static async Task EnvelopesAsync()
    {
        var fixture = Kit.Obj("fixtures/forward-compat/envelope-keys.json");
        foreach (var envelope in fixture["envelopes"]!.AsArray())
        {
            var wire = envelope!["wire"]!;
            switch (envelope["kind"]!.GetValue<string>())
            {
                case "rest.search":
                    {
                        await using var rest = await Rest.StartAsync();
                        rest.Mock.Enqueue(MockResponse.Json(200, wire.ToJsonString()));
                        var page = await rest.Client.Articles.SearchAsync(new SearchParams());
                        Assert.Equal(wire["articles"]!.AsArray().Count, page.Articles.Count);
                        Assert.Equal(wire["pagination"]!["hasMore"]!.GetValue<bool>(), page.Pagination.HasMore);
                        break;
                    }

                case "rest.article":
                    {
                        await using var rest = await Rest.StartAsync();
                        rest.Mock.Enqueue(MockResponse.Json(200, wire.ToJsonString()));
                        var article = await rest.Client.Articles.GetAsync("EU8JwpsZ8iJehuyPZjMqRz");
                        Assert.Equal(wire["article"]!["id"]!.GetValue<string>(), article.Id);
                        break;
                    }

                case "webhook":
                    {
                        var e = Assert.IsType<WebhookEvent>(Signed(wire.ToJsonString()));
                        Assert.Equal(wire["eventId"]!.GetValue<string>(), e.EventId);
                        Assert.NotNull(JsonNode.Parse(e.GetRawJson()!)!["deliveryAttempt"]);
                        break;
                    }

                case "ws.article":
                case "ws.control":
                    await StreamFrameAsync(wire);
                    break;
                default:
                    throw new InvalidOperationException(envelope["kind"]!.ToString());
            }
        }
    }

    private static async Task StreamFrameAsync(JsonNode wire)
    {
        await using var ws = await WsMock.StartAsync();
        var probe = new StreamProbe(ws).Start(false, new JsonObject { ["query"] = "ticker:XMPL" }, Kit.ApiKey, "c-kit-1");
        try
        {
            var dial = await ws.NextDialAsync();
            await dial.Conn!.NextAsync("subscribe");
            await dial.Conn.SendAsync(wire);
            await Wait.ForAsync(() => probe.Frames == 1, "the frame");
            await probe.SettleAsync();
            Assert.Null(probe.Terminal);
            Assert.Empty(probe.Errors);
            if (wire["event"]!.GetValue<string>() == "article.created")
            {
                // The frame is counted before the consumer loop has taken the item from the stream.
                await Wait.ForAsync(() => probe.Items.Count == 1, "the item");
                var item = Assert.Single(probe.Items);
                Assert.Equal(StreamEventKind.Created, item.Kind);
                Assert.NotNull(JsonNode.Parse(item.GetRawJson())!["sequence"]);
            }
            else
            {
                Assert.Equal(1, probe.Pongs);
                Assert.Empty(probe.Items);
            }
        }
        finally
        {
            await probe.DisposeAsync();
        }
    }

    private static async Task UnknownEventsAndCodesAsync()
    {
        var fixture = Kit.Obj("fixtures/forward-compat/unknown-events-and-codes.json");
        foreach (var item in fixture["items"]!.AsArray())
        {
            var wire = item!["wire"]!;
            switch (item["kind"]!.GetValue<string>())
            {
                case "webhook.event":
                    {
                        var e = Assert.IsType<WebhookEvent>(Signed(wire.ToJsonString()));
                        Assert.Equal("article.retracted", e.Event.Value);
                        Assert.False(e.Event.IsKnown);
                        break;
                    }

                case "problem.code":
                case "problem.limitType":
                    {
                        await using var rest = await Rest.StartAsync();
                        var contentType = item["headers"]!["content-type"]!.GetValue<string>();
                        rest.Mock.Enqueue(MockResponse.Json(item["status"]!.GetValue<int>(), wire.ToJsonString(), contentType));
                        var error = await Assert.ThrowsAnyAsync<FinlightApiException>(() => rest.Client.Articles.SearchAsync(new SearchParams()));
                        Assert.Single(rest.Mock.Requests);
                        Assert.Equal(wire["status"]!.GetValue<int>(), error.Status);
                        Assert.Equal(wire["code"]!.GetValue<string>(), error.Code);
                        Assert.Equal(wire["requestId"]!.GetValue<string>(), error.RequestId);
                        Assert.Equal(wire["detail"]!.GetValue<string>(), error.Detail);
                        if (item["kind"]!.GetValue<string>() == "problem.code")
                        {
                            Assert.Equal(typeof(FinlightApiException), error.GetType());
                            Assert.Equal(ApiErrorKind.Other, error.ApiKind);
                        }
                        else
                        {
                            var limited = Assert.IsType<RateLimitedException>(error);
                            Assert.Equal("daily", limited.LimitType.Value);
                            Assert.False(limited.LimitType.IsKnown);
                        }

                        break;
                    }

                default:
                    throw new InvalidOperationException(item["kind"]!.ToString());
            }
        }
    }

    /// <summary>Signs a body like the server does and verifies it.</summary>
    internal static WebhookPayload Signed(string body, string? timestamp = null)
    {
        timestamp ??= "2026-11-23T21:04:05.123Z";
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(timestamp + "." + body));
        var headers = new Dictionary<string, StringValues>
        {
            ["X-Webhook-Signature"] = "sha256=" + Convert.ToHexString(mac).ToLowerInvariant(),
            ["X-Webhook-Timestamp"] = timestamp,
        };
        var now = new FakeTimeProvider(DateTimeOffset.Parse(timestamp, System.Globalization.CultureInfo.InvariantCulture));
        return Webhooks.ConstructEvent(Encoding.UTF8.GetBytes(body), headers, Secret, new WebhookVerifyOptions { Now = now });
    }
}
