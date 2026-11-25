using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Finlight.Tests.Support;

/// <summary>Access to the vendored contract kit in ./conformance.</summary>
internal static partial class Kit
{
    public const string ApiKey = "sk_0000000000000000000000000000000000000000000000000000000000000001";

    private static readonly Lazy<string> RootDir = new(FindRoot);

    public static string Root => RootDir.Value;

    public static string RepoRoot => Path.GetDirectoryName(Root)!;

    public static JsonNode Load(string relative)
        => JsonNode.Parse(File.ReadAllText(Path.Combine(Root, relative)))
            ?? throw new InvalidOperationException($"empty fixture {relative}");

    public static JsonObject Obj(string relative) => Load(relative).AsObject();

    public static string Text(JsonNode? node) => node?.ToJsonString() ?? "null";

    /// <summary>Every case id listed in conformance.md, in order.</summary>
    public static IReadOnlyList<string> CaseIds()
    {
        var text = File.ReadAllText(Path.Combine(Root, "conformance.md"));
        return CaseRow().Matches(text).Select(m => m.Groups[1].Value).ToList();
    }

    public static int DeclaredTotal()
    {
        var text = File.ReadAllText(Path.Combine(Root, "conformance.md"));
        return int.Parse(TotalLine().Match(text).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Canonical JSON: sorted keys, compact; numbers compared by value.</summary>
    public static string Canonical(JsonNode? node)
    {
        var builder = new StringBuilder();
        Write(node, builder);
        return builder.ToString();
    }

    public static string Canonical(string json) => Canonical(JsonNode.Parse(json));

    private static void Write(JsonNode? node, StringBuilder builder)
    {
        switch (node)
        {
            case null:
                builder.Append("null");
                break;
            case JsonObject obj:
                builder.Append('{');
                var first = true;
                foreach (var (key, value) in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;
                    builder.Append(JsonSerializer.Serialize(key)).Append(':');
                    Write(value, builder);
                }

                builder.Append('}');
                break;
            case JsonArray array:
                builder.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    Write(array[i], builder);
                }

                builder.Append(']');
                break;
            default:
                using (var doc = JsonDocument.Parse(node.ToJsonString()))
                {
                    var element = doc.RootElement;
                    if (element.ValueKind == JsonValueKind.Number)
                    {
                        builder.Append(element.GetDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(element.GetRawText());
                    }
                }

                break;
        }
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "conformance", "CONTRACT_VERSION");
            if (File.Exists(candidate))
            {
                return Path.Combine(dir.FullName, "conformance");
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("conformance/ not found; run scripts/sync-conformance.sh");
    }

    [GeneratedRegex(@"^\| ([A-Z]-\d{2}) \|", RegexOptions.Multiline)]
    private static partial Regex CaseRow();

    [GeneratedRegex(@"^Total: (\d+) cases\.", RegexOptions.Multiline)]
    private static partial Regex TotalLine();
}
