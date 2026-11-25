using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Finlight.Internal;

/// <summary>Reads envelopes and entries tolerantly (R-MODELS-1, R-MODELS-3).</summary>
internal static class Decode
{
    public static JsonDocument Parse(byte[] body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException exception)
        {
            throw new FinlightDecodeException("the response is not valid JSON", null, null, exception);
        }
    }

    public static JsonElement Field(JsonElement root, string key, JsonValueKind kind)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new FinlightDecodeException("the response is not a JSON object", "$", Truncate(root.GetRawText()));
        }

        if (!root.TryGetProperty(key, out var value) || value.ValueKind != kind)
        {
            throw new FinlightDecodeException(
                $"the response has no {kind.ToString().ToLowerInvariant()} '{key}'", "$." + key, null);
        }

        return value;
    }

    /// <summary>Decodes one article; a broken one is a <see cref="FinlightDecodeException"/>.</summary>
    public static Article Article(JsonElement element, string path)
    {
        var article = One<Article>(element, path);
        article.RawJson = element.GetRawText();
        return article;
    }

    public static CompanyRecord CompanyRecord(JsonElement element, string path)
    {
        var record = One<CompanyRecord>(element, path);
        record.RawJson = element.GetRawText();
        return record;
    }

    public static Source Source(JsonElement element, string path)
    {
        var source = One<Source>(element, path);
        source.RawJson = element.GetRawText();
        return source;
    }

    public static T One<T>(JsonElement element, string path)
        where T : class
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new FinlightDecodeException($"{path}: expected an object, got {element.ValueKind}", path, Truncate(element.GetRawText()));
        }

        try
        {
            return element.Deserialize<T>(FinlightJson.Options)
                ?? throw new FinlightDecodeException($"{path}: expected an object", path, Truncate(element.GetRawText()));
        }
        catch (JsonException exception)
        {
            var at = exception.Path is { Length: > 1 } inner ? path + inner[1..] : path;
            throw new FinlightDecodeException($"{path}: {exception.Message}", at, Truncate(element.GetRawText()), exception);
        }
    }

    /// <summary>Decodes a list; broken entries are skipped, logged and reported in <c>skipped</c>.</summary>
    public static (List<T> Items, List<SkippedEntry> Skipped) Entries<T>(
        JsonElement array,
        string path,
        Func<JsonElement, string, T> decode,
        ILogger log)
    {
        var items = new List<T>();
        var skipped = new List<SkippedEntry>();
        var index = 0;
        foreach (var element in array.EnumerateArray())
        {
            try
            {
                items.Add(decode(element, $"{path}[{index}]"));
            }
            catch (FinlightDecodeException exception)
            {
                log.LogWarning("skipped {Path}[{Index}]: {Reason}", path, index, exception.Message);
                skipped.Add(new SkippedEntry(index, element.GetRawText(), exception.Message));
            }

            index++;
        }

        return (items, skipped);
    }

    public static ArticlePage Page(byte[] body, ILogger log)
    {
        using var doc = Parse(body);
        var root = doc.RootElement;
        var articles = Field(root, "articles", JsonValueKind.Array);
        var paginationElement = Field(root, "pagination", JsonValueKind.Object);
        var pagination = One<Pagination>(paginationElement, "$.pagination");
        var (items, skipped) = Entries(articles, "$.articles", Article, log);
        return new ArticlePage { Articles = items, Pagination = pagination, Skipped = skipped };
    }

    public static string Truncate(string text) => text.Length <= 4096 ? text : text[..4096];
}
