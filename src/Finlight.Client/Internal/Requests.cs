using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Finlight.Internal;

/// <summary>
/// Builds request bodies, query strings and subscribe frames from a fixed list of keys (R-REQ-1)
/// and checks their shape before any network call (R-REQ-3).
/// </summary>
internal static class Requests
{
    public const int MaxInclude = 3;

    public static JsonObject SearchBody(SearchParams p)
    {
        NotNull(p, "parameters");
        var body = new JsonObject();
        AddString(body, "query", p.Query);
        AddList(body, "sources", p.Sources);
        AddList(body, "optInSources", p.OptInSources);
        AddList(body, "excludeSources", p.ExcludeSources);
        AddList(body, "sourceTypes", p.SourceTypes?.Select(r => r.Value).ToList());
        AddList(body, "excludeSourceTypes", p.ExcludeSourceTypes?.Select(r => r.Value).ToList());
        if (p.From is { } from)
        {
            body["from"] = from.Format("from");
        }

        if (p.To is { } to)
        {
            body["to"] = to.Format("to");
        }

        if (p.Languages is not null && p.Languages.Count == 0)
        {
            throw new FinlightValidationException("languages must not be empty (leave it unset for the server default)", "languages");
        }

        AddList(body, "languages", p.Languages);
        AddList(body, "tickers", p.Tickers);
        AddList(body, "countries", p.Countries);
        AddList(body, "categories", p.Categories);
        if (p.HasContent is { } hasContent)
        {
            body["hasContent"] = hasContent;
        }

        AddList(body, "roles", p.Roles?.Select(r => r.Value).ToList());
        if (p.MinRelevance is { } minRelevance)
        {
            if (double.IsNaN(minRelevance) || minRelevance < 0 || minRelevance > 1)
            {
                throw new FinlightValidationException("minRelevance must be between 0 and 1", "minRelevance");
            }

            body["minRelevance"] = minRelevance;
        }

        AddList(body, "eventTypes", p.EventTypes?.Select(r => r.Value).ToList());
        AddList(body, "eventDirections", p.EventDirections?.Select(r => r.Value).ToList());
        AddString(body, "minImportance", p.MinImportance?.Value);
        AddList(body, "sectors", p.Sectors);
        AddList(body, "companyCountries", p.CompanyCountries);
        AddList(body, "exchanges", p.Exchanges);
        AddString(body, "orderBy", p.OrderBy?.Value);
        AddString(body, "order", p.Order?.Value);
        if (p.Limit is { } limit)
        {
            if (limit is < 1 or > 100)
            {
                throw new FinlightValidationException("limit must be an integer from 1 to 100", "limit");
            }

            body["limit"] = limit;
        }

        AddString(body, "cursor", p.Cursor);
        AddInclude(body, p.Include);
        return body;
    }

    public static JsonObject StreamSubscribe(StreamParams p)
    {
        NotNull(p, "parameters");
        var frame = new JsonObject { ["payloadVersion"] = FinlightClient.PayloadVersion };
        AddInclude(frame, p.Include);
        AddString(frame, "query", p.Query);
        AddList(frame, "sources", p.Sources);
        AddList(frame, "excludeSources", p.ExcludeSources);
        AddList(frame, "optInSources", p.OptInSources);
        AddList(frame, "sourceTypes", p.SourceTypes?.Select(r => r.Value).ToList());
        AddList(frame, "excludeSourceTypes", p.ExcludeSourceTypes?.Select(r => r.Value).ToList());
        AddString(frame, "language", p.Language);
        AddList(frame, "tickers", p.Tickers);
        AddList(frame, "countries", p.Countries);
        AddList(frame, "categories", p.Categories);
        AddBool(frame, "excludeEmptyContent", p.ExcludeEmptyContent);
        AddBool(frame, "includeUpdates", p.IncludeUpdates);
        return frame;
    }

    public static JsonObject RawSubscribe(RawStreamParams p)
    {
        NotNull(p, "parameters");
        var frame = new JsonObject { ["payloadVersion"] = FinlightClient.PayloadVersion };
        AddString(frame, "query", p.Query);
        AddList(frame, "sources", p.Sources);
        AddList(frame, "excludeSources", p.ExcludeSources);
        AddList(frame, "sourceTypes", p.SourceTypes?.Select(r => r.Value).ToList());
        AddList(frame, "excludeSourceTypes", p.ExcludeSourceTypes?.Select(r => r.Value).ToList());
        AddString(frame, "language", p.Language);
        AddBool(frame, "includeUpdates", p.IncludeUpdates);
        return frame;
    }

    /// <summary>The include value of a GET query: comma-joined, or null when empty.</summary>
    public static string? IncludeQuery(IReadOnlyList<IncludeBlock>? include)
    {
        if (include is null || include.Count == 0)
        {
            return null;
        }

        CheckInclude(include);
        return string.Join(',', include.Select(i => i.Value));
    }

    public static string RequireText(string? value, string param, int? maxLength = null)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new FinlightValidationException($"{param} must not be empty", param);
        }

        if (maxLength is { } max && value.Length > max)
        {
            throw new FinlightValidationException($"{param} must have at most {max} characters", param);
        }

        return value;
    }

    public static List<long> CompanyIds(IEnumerable<long> ids)
    {
        NotNull(ids, "ids");
        var list = ids.ToList();
        if (list.Count is < 1 or > 10)
        {
            throw new FinlightValidationException("ids must hold 1 to 10 company ids", "ids");
        }

        if (list.Any(id => id < 1))
        {
            throw new FinlightValidationException("company ids are integers from 1", "ids");
        }

        return list;
    }

    public static long CompanyId(long id)
    {
        if (id < 1)
        {
            throw new FinlightValidationException("companyId is an integer from 1", "companyId");
        }

        return id;
    }

    /// <summary>Encodes a query string; every value is percent-encoded exactly once.</summary>
    public static string Query(IEnumerable<KeyValuePair<string, string>> pairs)
    {
        var builder = new StringBuilder();
        foreach (var (key, value) in pairs)
        {
            builder.Append(builder.Length == 0 ? '?' : '&');
            builder.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
        }

        return builder.ToString();
    }

    /// <summary>A missing argument is a local check like any other (one error base, R-ERR-1).</summary>
    public static void NotNull(object? value, string param)
    {
        if (value is null)
        {
            throw new FinlightValidationException($"{param} must not be null", param);
        }
    }

    private static void CheckInclude(IReadOnlyList<IncludeBlock> include)
    {
        if (include.Count > MaxInclude)
        {
            throw new FinlightValidationException($"include takes at most {MaxInclude} values", "include");
        }

        if (include.Any(i => string.IsNullOrEmpty(i.Value)))
        {
            throw new FinlightValidationException("include values must not be empty", "include");
        }
    }

    private static void AddInclude(JsonObject target, IReadOnlyList<IncludeBlock>? include)
    {
        if (include is null || include.Count == 0)
        {
            return;
        }

        CheckInclude(include);
        AddList(target, "include", include.Select(i => i.Value).ToList());
    }

    private static void AddString(JsonObject target, string key, string? value)
    {
        if (value is not null)
        {
            target[key] = value;
        }
    }

    private static void AddBool(JsonObject target, string key, bool? value)
    {
        if (value is { } flag)
        {
            target[key] = flag;
        }
    }

    private static void AddList(JsonObject target, string key, IReadOnlyList<string>? values)
    {
        if (values is null)
        {
            return;
        }

        var array = new JsonArray();
        foreach (var value in values)
        {
            if (value is null)
            {
                throw new FinlightValidationException($"{key} must not contain null", key);
            }

            array.Add(value);
        }

        target[key] = array;
    }
}

/// <summary>Reads a <see cref="DateInput"/> from a JSON string (for callers that deserialize parameters).</summary>
internal sealed class DateInputConverter : JsonConverter<DateInput>
{
    public override DateInput Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String
            ? (DateInput)reader.GetString()!
            : throw new JsonException("a date is a JSON string");

    public override void Write(Utf8JsonWriter writer, DateInput value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Format("date"));
}
