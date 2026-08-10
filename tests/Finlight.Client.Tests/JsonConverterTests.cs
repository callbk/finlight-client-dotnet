using System.Text.Json;
using Finlight.Json;

namespace Finlight.Tests;

public class JsonConverterTests
{
    private static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, FinlightJson.Options);

    [Theory]
    [InlineData("\"2024-01-01T00:00:00Z\"")]
    [InlineData("\"2024-01-01T02:00:00+02:00\"")]
    [InlineData("\"2024-01-01T00:00:00\"")]
    [InlineData("\"2024-01-01 00:00:00\"")]
    public void Timestamp_CommonFormats_ParseToSameUtcInstant(string json)
    {
        var parsed = Deserialize<DateTimeOffset>(json);

        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), parsed);
    }

    [Fact]
    public void Timestamp_WithNanoseconds_TruncatesTo100NsTicks()
    {
        var parsed = Deserialize<DateTimeOffset>("\"2024-01-01T00:00:00.123456789Z\"");

        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(1234567), parsed);
    }

    [Fact]
    public void Timestamp_SpaceSeparatedWithNanoseconds_Parses()
    {
        var parsed = Deserialize<DateTimeOffset>("\"2024-01-02 03:04:05.123456789\"");

        Assert.Equal(new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero).AddTicks(1234567), parsed);
    }

    [Fact]
    public void Timestamp_DateOnly_ParsesToUtcMidnight()
    {
        var parsed = Deserialize<DateTimeOffset>("\"2024-01-02\"");

        Assert.Equal(new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero), parsed);
    }

    [Fact]
    public void Timestamp_Null_YieldsNullForNullableProperty()
    {
        Assert.Null(Deserialize<DateTimeOffset?>("null"));
    }

    [Fact]
    public void Timestamp_Garbage_Throws()
    {
        Assert.Throws<JsonException>(() => Deserialize<DateTimeOffset>("\"not-a-date\""));
    }

    [Theory]
    [InlineData("\"0.95\"")]
    [InlineData("0.95")]
    public void Confidence_StringOrNumber_ParsesToDouble(string json)
    {
        Assert.Equal(0.95, Deserialize<double>(json));
    }

    [Fact]
    public void Confidence_Null_YieldsNullForNullableProperty()
    {
        Assert.Null(Deserialize<double?>("null"));
    }

    [Fact]
    public void Confidence_GarbageString_Throws()
    {
        Assert.Throws<JsonException>(() => Deserialize<double>("\"high\""));
    }

    [Fact]
    public void Article_WireFormat_Deserializes()
    {
        var article = Deserialize<Article>("""
            {
                "link": "https://example.com/a",
                "title": "Title",
                "publishDate": "2024-01-01T00:00:00.000Z",
                "source": "example.com",
                "language": "en",
                "confidence": "0.95",
                "categories": ["markets", "some-future-category"],
                "companies": [{"companyId": 1, "name": "Apple Inc.", "ticker": "AAPL", "confidence": "0.90"}]
            }
            """);

        Assert.NotNull(article);
        Assert.Equal(0.95, article.Confidence);
        Assert.Equal(["markets", "some-future-category"], article.Categories);
        Assert.NotNull(article.Companies);
        Assert.Equal(0.90, article.Companies[0].Confidence);
        Assert.Null(article.Content);
    }
}
