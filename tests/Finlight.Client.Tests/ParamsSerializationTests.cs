using System.Text.Json;
using Finlight.Json;

namespace Finlight.Tests;

public class ParamsSerializationTests
{
    private static JsonElement Serialize(object value)
        => JsonSerializer.SerializeToElement(value, value.GetType(), FinlightJson.Options);

    [Fact]
    public void GetArticlesParams_SetFields_SerializeWithWireNames()
    {
        var json = Serialize(new GetArticlesParams
        {
            Query = "nvidia",
            From = "2024-01-01",
            To = "2024-02-01",
            OrderBy = ArticleOrderBy.PublishDate,
            Order = SortOrder.Desc,
            PageSize = 50,
            IncludeContent = true,
            Categories = [Category.Markets, Category.Crypto],
            Tickers = ["NVDA"],
        });

        Assert.Equal("nvidia", json.GetProperty("query").GetString());
        Assert.Equal("2024-01-01", json.GetProperty("from").GetString());
        Assert.Equal("2024-02-01", json.GetProperty("to").GetString());
        Assert.Equal("publishDate", json.GetProperty("orderBy").GetString());
        Assert.Equal("DESC", json.GetProperty("order").GetString());
        Assert.Equal(50, json.GetProperty("pageSize").GetInt32());
        Assert.True(json.GetProperty("includeContent").GetBoolean());
        Assert.Equal(["markets", "crypto"], json.GetProperty("categories").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["NVDA"], json.GetProperty("tickers").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void GetArticlesParams_UnsetFields_AreOmitted()
    {
        var json = Serialize(new GetArticlesParams { Query = "nvidia" });

        Assert.Single(json.EnumerateObject());
    }

    [Fact]
    public void SortOrder_Ascending_SerializesUppercase()
    {
        var json = Serialize(new GetArticlesParams { Order = SortOrder.Asc });

        Assert.Equal("ASC", json.GetProperty("order").GetString());
    }

    [Fact]
    public void WebSocketParams_SetFields_SerializeWithWireNames()
    {
        var json = Serialize(new GetArticlesWebSocketParams
        {
            Query = "ticker:NVDA",
            IncludeUpdates = true,
            Categories = [Category.Technology],
        });

        Assert.Equal("ticker:NVDA", json.GetProperty("query").GetString());
        Assert.True(json.GetProperty("includeUpdates").GetBoolean());
        Assert.Equal(["technology"], json.GetProperty("categories").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(3, json.EnumerateObject().Count());
    }

    [Fact]
    public void RawWebSocketParams_SetFields_SerializeWithWireNames()
    {
        var json = Serialize(new GetRawArticlesWebSocketParams
        {
            Sources = ["example.com"],
            OptInSources = ["opt.example.com"],
            IncludeUpdates = false,
        });

        Assert.Equal(["example.com"], json.GetProperty("sources").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["opt.example.com"], json.GetProperty("optInSources").EnumerateArray().Select(e => e.GetString()));
        Assert.False(json.GetProperty("includeUpdates").GetBoolean());
    }
}
