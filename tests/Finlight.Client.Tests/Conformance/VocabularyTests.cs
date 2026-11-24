using System.Text.Json;
using System.Text.Json.Nodes;
using Finlight.Tests.Support;

namespace Finlight.Tests.Conformance;

/// <summary>V-01: the shipped constants equal vocab.json; values stay open.</summary>
public sealed class VocabularyTests
{
    public static TheoryData<string> Cases => ["V-01"];

    [Theory]
    [MemberData(nameof(Cases))]
    public void Case(string id)
    {
        Assert.Equal("V-01", id);
        var vocab = Kit.Obj("vocab.json");
        static List<string> Values(JsonNode? node) => [.. node!["values"]!.AsArray().Select(v => v!.GetValue<string>())];

        Assert.Equal(Values(vocab["request"]!["include"]), IncludeBlockValues.All);
        Assert.Equal(Values(vocab["request"]!["orderBy"]), OrderByValues.All);
        Assert.Equal(Values(vocab["request"]!["order"]), SortOrderValues.All);
        Assert.Equal(Values(vocab["request"]!["roles"]), CompanyRoleValues.All);
        Assert.Equal(Values(vocab["response"]!["roles"]), CompanyRoleValues.All);
        Assert.Equal(Values(vocab["request"]!["eventTypes"]), EventTypeValues.All);
        Assert.Equal(Values(vocab["response"]!["eventTypes"]), EventTypeValues.All);
        Assert.Equal(Values(vocab["request"]!["eventDirections"]), EventDirectionValues.All);
        Assert.Equal(Values(vocab["response"]!["eventDirections"]), EventDirectionValues.All);
        Assert.Equal(Values(vocab["request"]!["minImportance"]), ImportanceValues.All);
        Assert.Equal(Values(vocab["response"]!["importance"]), ImportanceValues.All);
        Assert.Equal(Values(vocab["response"]!["sentimentLabels"]), SentimentLabelValues.All);
        Assert.Equal(Values(vocab["response"]!["sourceTypes"]), SourceTypeValues.All);
        Assert.Equal(Values(vocab["websocket"]!["events"]), StreamEventNameValues.All);
        Assert.Equal(Values(vocab["websocket"]!["articleEvents"]), StreamEventNameValues.ArticleEvents);
        Assert.Equal(Values(vocab["websocket"]!["connectionEvents"]), StreamEventNameValues.ConnectionEvents);
        Assert.Equal(Values(vocab["websocket"]!["connectionErrorCodes"]), ConnectionErrorCodeValues.All);
        Assert.Equal(Values(vocab["websocket"]!["preemptReasons"]), PreemptReasonValues.All);
        Assert.Equal(Values(vocab["webhook"]!["events"]), WebhookEventNameValues.All);
        Assert.Equal(
            vocab["problemCodes"]!["values"]!.AsArray().Select(v => v!["code"]!.GetValue<string>()),
            ProblemCodeValues.All);
        Assert.Equal([.. Values(vocab["limitTypes"]), "unknown"], RateLimitTypeValues.All);
        Assert.Equal(3, vocab["payloadVersion"]!.GetValue<int>());
        Assert.Equal(3, FinlightClient.PayloadVersion);

        // The problem type URIs follow the code.
        foreach (var entry in vocab["problemCodes"]!["values"]!.AsArray())
        {
            var code = entry!["code"]!.GetValue<string>();
            Assert.Equal("https://docs.finlight.me/errors/" + code.ToLowerInvariant().Replace('_', '-'), entry["type"]!.GetValue<string>());
        }

        // Open: unknown strings are kept as sent, compare by value, and write back unchanged.
        var role = JsonSerializer.Deserialize<CompanyRole>("\"guarantor\"");
        Assert.Equal("guarantor", role.Value);
        Assert.False(role.IsKnown);
        Assert.True(CompanyRole.Acquirer.IsKnown);
        Assert.Equal(CompanyRole.Acquirer, new CompanyRole("acquirer"));
        Assert.True(CompanyRole.Acquirer == "acquirer");
        Assert.Equal("\"guarantor\"", JsonSerializer.Serialize(role));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CompanyRole>("1"));
        switch (CompanyRole.Target.Value)
        {
            case CompanyRoleValues.Target:
                break;
            default:
                Assert.Fail("const strings work in switch");
                break;
        }
    }
}
